#include "UEToVSLinkModule.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Common/TcpListener.h"
#include "ContentBrowserModule.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Editor.h"
#include "Framework/Docking/TabManager.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformTime.h"
#include "IContentBrowserSingleton.h"
#include "Interfaces/IPv4/IPv4Address.h"
#include "Interfaces/IPv4/IPv4Endpoint.h"
#include "Misc/App.h"
#include "Misc/AutomationTest.h"
#include "Misc/CoreMisc.h"
#include "Misc/EngineVersion.h"
#include "Misc/FileHelper.h"
#include "Misc/OutputDeviceRedirector.h"
#include "Misc/Paths.h"
#include "Misc/ScopeLock.h"
#include "Modules/ModuleManager.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/Archive.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "SocketSubsystem.h"
#include "Sockets.h"
#include "Subsystems/AssetEditorSubsystem.h"
#include "Widgets/SWindow.h"

#if UETOVS_WITH_LIVE_CODING
#include "ILiveCodingModule.h"
#endif

DEFINE_LOG_CATEGORY_STATIC(LogUEToVSLink, Log, All);

namespace UEToVSLink
{
	/** Protocol version spoken by this plugin; protocol-1 clients simply never send the new commands. */
	static constexpr int32 ProtocolVersion = 2;
	static const TCHAR* PluginVersion = TEXT("0.2.0");

	static constexpr int32 MaxSubscribers = 8;
	/** Log lines sent per tick (20 Hz -> at most ~10k lines/s); the rest is counted as dropped. */
	static constexpr int32 MaxLogLinesPerTick = 500;
	/** Outgoing buffer per subscriber: log batches are dropped above the soft cap, the subscriber is closed above the hard cap. */
	static constexpr int32 SoftOutCap = 4 * 1024 * 1024;
	static constexpr int32 HardOutCap = 16 * 1024 * 1024;
	static constexpr int32 MaxRunLogs = 5000;
	static constexpr int32 MaxTestLogs = 200;
	static constexpr int32 MaxTestMessageChars = 1000;
	static constexpr int32 MaxFinishedTests = 4000;
	static constexpr double TestIdleBeforeFirstSeconds = 90.0;
	static constexpr double TestIdleSeconds = 30.0;
	static constexpr int64 MaxConsoleTailBytes = 2 * 1024 * 1024;
	static constexpr int32 MaxConsoleTailLines = 4000;

	static FString ToJson(const TSharedRef<FJsonObject>& Object)
	{
		FString Out;
		TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Out);
		FJsonSerializer::Serialize(Object, Writer);
		return Out;
	}

	static FString Result(bool bOk, const FString& Message = FString())
	{
		TSharedRef<FJsonObject> Obj = MakeShared<FJsonObject>();
		Obj->SetBoolField(TEXT("ok"), bOk);
		if (!Message.IsEmpty())
		{
			Obj->SetStringField(TEXT("message"), Message);
		}
		return ToJson(Obj);
	}

	/** Appends Value as a JSON string literal (with quotes). Hand-written: the log path must not allocate a DOM per line. */
	static void AppendJsonString(FString& Out, const TCHAR* Value, int32 Len)
	{
		Out.AppendChar(TEXT('"'));
		for (int32 i = 0; i < Len; ++i)
		{
			const TCHAR C = Value[i];
			switch (C)
			{
			case TEXT('"'): Out.Append(TEXT("\\\"")); break;
			case TEXT('\\'): Out.Append(TEXT("\\\\")); break;
			case TEXT('\n'): Out.Append(TEXT("\\n")); break;
			case TEXT('\r'): Out.Append(TEXT("\\r")); break;
			case TEXT('\t'): Out.Append(TEXT("\\t")); break;
			default:
				if (C < 0x20)
				{
					Out.Appendf(TEXT("\\u%04x"), static_cast<uint32>(C));
				}
				else
				{
					Out.AppendChar(C);
				}
				break;
			}
		}
		Out.AppendChar(TEXT('"'));
	}

	static void AppendJsonString(FString& Out, const FString& Value)
	{
		AppendJsonString(Out, *Value, Value.Len());
	}

	static void AppendJsonStringArray(FString& Out, const TArray<FString>& Values)
	{
		Out.AppendChar(TEXT('['));
		for (int32 i = 0; i < Values.Num(); ++i)
		{
			if (i > 0)
			{
				Out.AppendChar(TEXT(','));
			}
			AppendJsonString(Out, Values[i]);
		}
		Out.AppendChar(TEXT(']'));
	}

	static FString Clip(const FString& Text, int32 MaxChars)
	{
		return Text.Len() <= MaxChars ? Text : Text.Left(MaxChars) + TEXT("...");
	}

	static void BringEditorToFront()
	{
		TSharedPtr<SWindow> Root = FGlobalTabmanager::Get()->GetRootWindow();
		if (Root.IsValid())
		{
			Root->HACK_ForceToFront();
		}
	}

	static FAssetData FindAsset(const FString& ObjectPath)
	{
		IAssetRegistry& Registry = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
#if ENGINE_MAJOR_VERSION == 5 && ENGINE_MINOR_VERSION < 1
		return Registry.GetAssetByObjectPath(FName(*ObjectPath));
#else
		return Registry.GetAssetByObjectPath(FSoftObjectPath(ObjectPath));
#endif
	}

	static TArray<FString> ReadTopics(const TSharedPtr<FJsonObject>& Request)
	{
		TArray<FString> Topics;
		const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
		if (Request.IsValid() && Request->TryGetArrayField(TEXT("topics"), Values) && Values != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& Value : *Values)
			{
				FString Topic;
				if (Value.IsValid() && Value->TryGetString(Topic))
				{
					Topics.Add(Topic);
				}
			}
		}
		return Topics;
	}

	static FString FirstLine(TArray<uint8>& Buffer, bool& bOutFound)
	{
		const int32 Newline = Buffer.Find(static_cast<uint8>('\n'));
		bOutFound = Newline != INDEX_NONE;
		if (!bOutFound)
		{
			return FString();
		}
		FUTF8ToTCHAR Converted(reinterpret_cast<const ANSICHAR*>(Buffer.GetData()), Newline);
		FString Line(Converted.Length(), Converted.Get());
		Buffer.RemoveAt(0, Newline + 1);
		return Line.TrimStartAndEnd();
	}

	/** The command line of "Automation RunTests" must stay one command: no separators, no line breaks. */
	static FString SanitizeTestFilter(const FString& Filter)
	{
		FString Out;
		Out.Reserve(Filter.Len());
		for (TCHAR C : Filter)
		{
			if (C == TEXT(';') || C == TEXT('|') || C == TEXT('\r') || C == TEXT('\n') || C == TEXT('"'))
			{
				continue;
			}
			Out.AppendChar(C);
		}
		return Out.TrimStartAndEnd();
	}

	static FString TestName(FAutomationTestBase* Test)
	{
		FString Name = Test->GetBeautifiedTestName();
		const FString Context = Test->GetTestContext();
		if (!Context.IsEmpty())
		{
			Name += TEXT(" ");
			Name += Context;
		}
		return Name;
	}
}

// ---------------------------------------------------------------------------------------------------------------------
// Log capture (any thread)

void FUEToVSLinkLogCapture::Serialize(const TCHAR* V, ELogVerbosity::Type Verbosity, const FName& Category)
{
	if (!bEnabled.load(std::memory_order_relaxed) || V == nullptr || Verbosity == ELogVerbosity::SetColor)
	{
		return;
	}
	const uint8 Level = static_cast<uint8>(Verbosity & ELogVerbosity::VerbosityMask);
	if (Level == ELogVerbosity::NoLogging)
	{
		return;
	}

	int32 Len = 0;
	while (Len < MaxMessageChars && V[Len] != 0)
	{
		++Len;
	}
	const bool bTruncated = Len == MaxMessageChars && V[Len] != 0;
	const double Now = FPlatformTime::Seconds() - StartSeconds;

	FScopeLock ScopeLock(&Lock);
	if (Pending.Num() >= MaxPending)
	{
		++Dropped;
		++Seq;
		return;
	}
	FUEToVSLinkLogEntry& Entry = Pending.AddDefaulted_GetRef();
	Entry.Message = FString(Len, V);
	if (bTruncated)
	{
		Entry.Message += TEXT("...");
	}
	Entry.Category = Category;
	Entry.Time = Now;
	Entry.Seq = Seq++;
	Entry.Verbosity = Level;
}

uint64 FUEToVSLinkLogCapture::NextSeq()
{
	FScopeLock ScopeLock(&Lock);
	return Seq;
}

void FUEToVSLinkLogCapture::Drain(TArray<FUEToVSLinkLogEntry>& Out, int64& OutDropped)
{
	Out.Reset();
	FScopeLock ScopeLock(&Lock);
	Swap(Out, Pending);
	OutDropped = Dropped;
	Dropped = 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// Module lifetime

void FUEToVSLinkModule::StartupModule()
{
	if (IsRunningCommandlet() || !GIsEditor)
	{
		return;
	}

	// Port 0: let the OS pick a free port; it is published in Saved/UEToVSLink/port.txt.
	FIPv4Endpoint Endpoint(FIPv4Address(127, 0, 0, 1), 0);
	Listener = MakeUnique<FTcpListener>(Endpoint, FTimespan::FromMilliseconds(100), false);
	Listener->OnConnectionAccepted().BindRaw(this, &FUEToVSLinkModule::OnConnection);

	int32 Port = 0;
	for (int32 Attempt = 0; Attempt < 20 && Port == 0; ++Attempt)
	{
		if (FSocket* Socket = Listener->GetSocket())
		{
			Port = Socket->GetPortNo();
		}
		if (Port == 0)
		{
			FPlatformProcess::Sleep(0.01f);
		}
	}
	if (Port == 0)
	{
		UE_LOG(LogUEToVSLink, Warning, TEXT("UEToVSLink: could not open a localhost port"));
		Listener.Reset();
		return;
	}

	// The capture device stays registered but copies nothing until a subscriber / test run enables it.
	Capture = MakeUnique<FUEToVSLinkLogCapture>();
	Capture->StartSeconds = FPlatformTime::Seconds();
	if (GLog != nullptr)
	{
		GLog->AddOutputDevice(Capture.Get());
	}

	FAutomationTestFramework& Framework = FAutomationTestFramework::Get();
	TestStartHandle = Framework.OnTestStartEvent.AddRaw(this, &FUEToVSLinkModule::OnTestStart);
	TestEndHandle = Framework.OnTestEndEvent.AddRaw(this, &FUEToVSLinkModule::OnTestEnd);

	FFileHelper::SaveStringToFile(FString::FromInt(Port), *PortFilePath());
	TickHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateRaw(this, &FUEToVSLinkModule::Tick), 0.05f);
	UE_LOG(LogUEToVSLink, Log, TEXT("UEToVSLink %s listening on 127.0.0.1:%d (protocol %d)"), UEToVSLink::PluginVersion, Port, UEToVSLink::ProtocolVersion);
}

void FUEToVSLinkModule::ShutdownModule()
{
	if (TickHandle.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(TickHandle);
		TickHandle.Reset();
	}
	Listener.Reset();

	if (TestStartHandle.IsValid() || TestEndHandle.IsValid())
	{
		FAutomationTestFramework& Framework = FAutomationTestFramework::Get();
		Framework.OnTestStartEvent.Remove(TestStartHandle);
		Framework.OnTestEndEvent.Remove(TestEndHandle);
		TestStartHandle.Reset();
		TestEndHandle.Reset();
	}
	UnbindLiveCoding();

	if (Capture.IsValid())
	{
		Capture->SetEnabled(false);
		if (GLog != nullptr)
		{
			GLog->RemoveOutputDevice(Capture.Get());
		}
		Capture.Reset();
	}

	ISocketSubsystem* Sockets = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
	FSocket* Socket = nullptr;
	while (Pending.Dequeue(Socket))
	{
		Socket->Close();
		Sockets->DestroySocket(Socket);
	}
	for (FClient& Client : Clients)
	{
		Client.Socket->Close();
		Sockets->DestroySocket(Client.Socket);
	}
	Clients.Empty();
	for (FSubscriber& Sub : Subscribers)
	{
		Sub.Socket->Close();
		Sockets->DestroySocket(Sub.Socket);
	}
	Subscribers.Empty();
	FTestEvent Ignored;
	while (TestEvents.Dequeue(Ignored))
	{
	}
	IFileManager::Get().Delete(*PortFilePath(), false, true, true);
}

FString FUEToVSLinkModule::PortFilePath() const
{
	return FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("UEToVSLink"), TEXT("port.txt"));
}

bool FUEToVSLinkModule::OnConnection(FSocket* Socket, const FIPv4Endpoint& Endpoint)
{
	// Listener thread: hand the socket to the game thread.
	Pending.Enqueue(Socket);
	return true;
}

// ---------------------------------------------------------------------------------------------------------------------
// Tick (game thread)

bool FUEToVSLinkModule::Tick(float DeltaTime)
{
	ISocketSubsystem* Sockets = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
	FSocket* NewSocket = nullptr;
	while (Pending.Dequeue(NewSocket))
	{
		NewSocket->SetNonBlocking(true);
		FClient Client;
		Client.Socket = NewSocket;
		Client.StartTime = FPlatformTime::Seconds();
		Clients.Add(MoveTemp(Client));
	}

	for (int32 i = Clients.Num() - 1; i >= 0; --i)
	{
		FClient& Client = Clients[i];
		uint8 Chunk[4096];
		int32 Read = 0;
		while (Client.Socket->Recv(Chunk, sizeof(Chunk), Read) && Read > 0)
		{
			Client.Buffer.Append(Chunk, Read);
		}

		const bool bHasLine = Client.Buffer.Find(static_cast<uint8>('\n')) != INDEX_NONE;
		const bool bTimedOut = FPlatformTime::Seconds() - Client.StartTime > 5.0;
		if (!bHasLine && !bTimedOut && Client.Buffer.Num() < 64 * 1024)
		{
			continue;
		}

		if (bHasLine)
		{
			bool bFound = false;
			const FString Line = UEToVSLink::FirstLine(Client.Buffer, bFound);
			bool bSubscribe = false;
			TArray<FString> Topics;
			const FString Response = Handle(Line, bSubscribe, Topics);
			if (bSubscribe)
			{
				// Protocol 2: the connection stays open; the socket moves to the subscribers.
				FSubscriber& Sub = Subscribers.AddDefaulted_GetRef();
				Sub.Socket = Client.Socket;
				Sub.In = MoveTemp(Client.Buffer);
				ApplyTopics(Sub, Topics);
				Enqueue(Sub, Response);
				Clients.RemoveAt(i);
				UE_LOG(LogUEToVSLink, Verbose, TEXT("UEToVSLink: subscriber connected (%d)"), Subscribers.Num());
				continue;
			}
			Reply(Client.Socket, Response);
		}
		Client.Socket->Close();
		Sockets->DestroySocket(Client.Socket);
		Clients.RemoveAt(i);
	}

	TArray<FUEToVSLinkLogEntry> Entries;
	int64 Dropped = 0;
	if (Capture.IsValid())
	{
		Capture->Drain(Entries, Dropped);
	}

	TickTests(Entries);
	ProcessLogs(Entries, Dropped);
	TickLiveCoding(Entries);
	TickSubscribers();
	UpdateCapture();
	return true;
}

void FUEToVSLinkModule::Reply(FSocket* Socket, const FString& Json)
{
	FTCHARToUTF8 Utf8(*(Json + TEXT("\n")));
	int32 Sent = 0;
	Socket->SetNonBlocking(false);
	Socket->Send(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length(), Sent);
}

FString FUEToVSLinkModule::Handle(const FString& Line, bool& bOutSubscribe, TArray<FString>& OutTopics)
{
	bOutSubscribe = false;
	TSharedPtr<FJsonObject> Request;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Line);
	if (!FJsonSerializer::Deserialize(Reader, Request) || !Request.IsValid())
	{
		return UEToVSLink::Result(false, TEXT("bad request"));
	}

	FString Cmd;
	Request->TryGetStringField(TEXT("cmd"), Cmd);
	if (Cmd == TEXT("ping"))
	{
		TSharedRef<FJsonObject> Obj = MakeShared<FJsonObject>();
		Obj->SetBoolField(TEXT("ok"), true);
		Obj->SetStringField(TEXT("project"), FApp::GetProjectName());
		Obj->SetStringField(TEXT("engine"), FEngineVersion::Current().ToString());
		// Protocol 2 additions (protocol-1 clients ignore unknown fields).
		Obj->SetNumberField(TEXT("protocol"), UEToVSLink::ProtocolVersion);
		Obj->SetStringField(TEXT("plugin"), UEToVSLink::PluginVersion);
		TArray<TSharedPtr<FJsonValue>> Features;
		Features.Add(MakeShared<FJsonValueString>(TEXT("log")));
		Features.Add(MakeShared<FJsonValueString>(TEXT("tests")));
#if UETOVS_WITH_LIVE_CODING
		Features.Add(MakeShared<FJsonValueString>(TEXT("liveCoding")));
#endif
		Obj->SetArrayField(TEXT("features"), Features);
		return UEToVSLink::ToJson(Obj);
	}

	if (Cmd == TEXT("subscribe"))
	{
		if (Subscribers.Num() >= UEToVSLink::MaxSubscribers)
		{
			return UEToVSLink::Result(false, TEXT("too many subscribers"));
		}
		bOutSubscribe = true;
		OutTopics = UEToVSLink::ReadTopics(Request);
		TSharedRef<FJsonObject> Obj = MakeShared<FJsonObject>();
		Obj->SetBoolField(TEXT("ok"), true);
		Obj->SetNumberField(TEXT("protocol"), UEToVSLink::ProtocolVersion);
		Obj->SetStringField(TEXT("plugin"), UEToVSLink::PluginVersion);
		return UEToVSLink::ToJson(Obj);
	}

	if (Cmd == TEXT("runTests"))
	{
		FString RunId;
		FString Filter;
		Request->TryGetStringField(TEXT("id"), RunId);
		Request->TryGetStringField(TEXT("filter"), Filter);
		return StartTests(RunId, Filter);
	}

	if (Cmd == TEXT("openAsset") || Cmd == TEXT("browseAsset"))
	{
		FString Path;
		if (!Request->TryGetStringField(TEXT("path"), Path) || Path.IsEmpty())
		{
			return UEToVSLink::Result(false, TEXT("missing path"));
		}
		FAssetData Asset = UEToVSLink::FindAsset(Path);
		if (!Asset.IsValid())
		{
			return UEToVSLink::Result(false, FString::Printf(TEXT("asset not found: %s"), *Path));
		}

		UEToVSLink::BringEditorToFront();
		if (Cmd == TEXT("browseAsset"))
		{
			FContentBrowserModule& ContentBrowser = FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
			ContentBrowser.Get().SyncBrowserToAssets(TArray<FAssetData>{ Asset });
			return UEToVSLink::Result(true);
		}

		UObject* Object = Asset.GetAsset();
		if (Object == nullptr || GEditor == nullptr)
		{
			return UEToVSLink::Result(false, TEXT("could not load asset"));
		}
		UAssetEditorSubsystem* Editors = GEditor->GetEditorSubsystem<UAssetEditorSubsystem>();
		const bool bOpened = Editors != nullptr && Editors->OpenEditorForAsset(Object);
		if (!bOpened)
		{
			// Maps and assets without an editor: at least show them in the Content Browser.
			FContentBrowserModule& ContentBrowser = FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
			ContentBrowser.Get().SyncBrowserToAssets(TArray<FAssetData>{ Asset });
		}
		return UEToVSLink::Result(true, bOpened ? FString() : TEXT("shown in Content Browser"));
	}

	if (Cmd == TEXT("liveCoding"))
	{
#if UETOVS_WITH_LIVE_CODING
		ILiveCodingModule* LiveCoding = FModuleManager::GetModulePtr<ILiveCodingModule>(LIVE_CODING_MODULE_NAME);
		if (LiveCoding == nullptr)
		{
			return UEToVSLink::Result(false, TEXT("Live Coding module is not loaded"));
		}
		if (!LiveCoding->IsEnabledByDefault() && !LiveCoding->IsEnabledForSession())
		{
			return UEToVSLink::Result(false, TEXT("Live Coding is disabled (Editor Preferences > Live Coding)"));
		}
		if (!LiveCoding->IsEnabledForSession())
		{
			LiveCoding->EnableForSession(true);
		}
		if (LiveCoding->IsCompiling())
		{
			return UEToVSLink::Result(true, TEXT("already compiling"));
		}
		LiveCoding->Compile();
		return UEToVSLink::Result(true);
#else
		return UEToVSLink::Result(false, TEXT("Live Coding is only available on Windows"));
#endif
	}

	return UEToVSLink::Result(false, FString::Printf(TEXT("unknown command: %s"), *Cmd));
}

// ---------------------------------------------------------------------------------------------------------------------
// Subscribers

void FUEToVSLinkModule::ApplyTopics(FSubscriber& Sub, const TArray<FString>& Topics)
{
	Sub.bLog = Topics.Contains(TEXT("log"));
	Sub.bTests = Topics.Contains(TEXT("tests"));
	Sub.bLiveCoding = Topics.Contains(TEXT("liveCoding"));
	UpdateCapture();
}

void FUEToVSLinkModule::HandleSubscriberLine(FSubscriber& Sub, const FString& Line)
{
	TSharedPtr<FJsonObject> Request;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Line);
	if (!FJsonSerializer::Deserialize(Reader, Request) || !Request.IsValid())
	{
		return;
	}
	FString Cmd;
	Request->TryGetStringField(TEXT("cmd"), Cmd);
	if (Cmd != TEXT("subscribe"))
	{
		return; // one-shot commands use their own connection
	}

	const TArray<FString> Topics = UEToVSLink::ReadTopics(Request);
	ApplyTopics(Sub, Topics);
	FString Json = TEXT("{\"event\":\"subscribed\",\"topics\":");
	UEToVSLink::AppendJsonStringArray(Json, Topics);
	Json.AppendChar(TEXT('}'));
	Enqueue(Sub, Json);
}

void FUEToVSLinkModule::Enqueue(FSubscriber& Sub, const FString& JsonLine)
{
	if (Sub.bDead)
	{
		return;
	}
	FTCHARToUTF8 Utf8(*JsonLine);
	Sub.Out.Append(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
	Sub.Out.Add(static_cast<uint8>('\n'));
	if (Sub.Out.Num() - Sub.OutOffset > UEToVSLink::HardOutCap)
	{
		// The client stopped reading: give up on it rather than growing without bound.
		Sub.bDead = true;
	}
}

void FUEToVSLinkModule::Broadcast(const FString& JsonLine, bool bTestsTopic, bool bLiveCodingTopic)
{
	for (FSubscriber& Sub : Subscribers)
	{
		if ((bTestsTopic && Sub.bTests) || (bLiveCodingTopic && Sub.bLiveCoding))
		{
			Enqueue(Sub, JsonLine);
		}
	}
}

void FUEToVSLinkModule::BroadcastLogs(const FString& LinesJson, int32 LineCount, int64 Dropped)
{
	for (FSubscriber& Sub : Subscribers)
	{
		if (!Sub.bLog || Sub.bDead)
		{
			continue;
		}
		if (Sub.Out.Num() - Sub.OutOffset > UEToVSLink::SoftOutCap)
		{
			Sub.Dropped += LineCount + Dropped;
			continue;
		}
		FString Json = TEXT("{\"event\":\"log\",\"dropped\":");
		Json.AppendInt(static_cast<int32>(FMath::Min<int64>(Sub.Dropped + Dropped, MAX_int32)));
		Json.Append(TEXT(",\"lines\":"));
		Json.Append(LinesJson);
		Json.AppendChar(TEXT('}'));
		Sub.Dropped = 0;
		Enqueue(Sub, Json);
	}
}

void FUEToVSLinkModule::Flush(FSubscriber& Sub)
{
	ISocketSubsystem* Sockets = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
	while (!Sub.bDead && Sub.OutOffset < Sub.Out.Num())
	{
		const int32 ToSend = FMath::Min(Sub.Out.Num() - Sub.OutOffset, 256 * 1024);
		int32 Sent = 0;
		if (!Sub.Socket->Send(Sub.Out.GetData() + Sub.OutOffset, ToSend, Sent))
		{
			if (Sockets->GetLastErrorCode() != SE_EWOULDBLOCK)
			{
				Sub.bDead = true;
			}
			break;
		}
		if (Sent <= 0)
		{
			break;
		}
		Sub.OutOffset += Sent;
	}
	if (Sub.OutOffset >= Sub.Out.Num())
	{
		Sub.Out.Reset();
		Sub.OutOffset = 0;
	}
	else if (Sub.OutOffset > 1024 * 1024)
	{
		Sub.Out.RemoveAt(0, Sub.OutOffset);
		Sub.OutOffset = 0;
	}
}

void FUEToVSLinkModule::TickSubscribers()
{
	ISocketSubsystem* Sockets = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
	for (int32 i = Subscribers.Num() - 1; i >= 0; --i)
	{
		FSubscriber& Sub = Subscribers[i];
		if (!Sub.bDead)
		{
			uint8 Chunk[4096];
			int32 Read = 0;
			for (;;)
			{
				if (!Sub.Socket->Recv(Chunk, sizeof(Chunk), Read))
				{
					Sub.bDead = true; // closed by the client (0 bytes) or a socket error
					break;
				}
				if (Read <= 0)
				{
					break; // would block
				}
				Sub.In.Append(Chunk, Read);
				if (Sub.In.Num() > 64 * 1024)
				{
					Sub.bDead = true;
					break;
				}
			}
		}
		while (!Sub.bDead)
		{
			bool bFound = false;
			const FString Line = UEToVSLink::FirstLine(Sub.In, bFound);
			if (!bFound)
			{
				break;
			}
			if (!Line.IsEmpty())
			{
				HandleSubscriberLine(Sub, Line);
			}
		}
		Flush(Sub);
		if (Sub.bDead)
		{
			Sub.Socket->Close();
			Sockets->DestroySocket(Sub.Socket);
			Subscribers.RemoveAt(i);
			UE_LOG(LogUEToVSLink, Verbose, TEXT("UEToVSLink: subscriber disconnected (%d left)"), Subscribers.Num());
		}
	}
}

void FUEToVSLinkModule::UpdateCapture()
{
	if (!Capture.IsValid())
	{
		return;
	}
	bool bWant = bTestRunActive || bLiveCodingCompiling;
	for (const FSubscriber& Sub : Subscribers)
	{
		bWant |= Sub.bLog && !Sub.bDead;
	}
	Capture->SetEnabled(bWant);
}

void FUEToVSLinkModule::ProcessLogs(const TArray<FUEToVSLinkLogEntry>& Entries, int64 Dropped)
{
	bool bAnyLog = false;
	for (const FSubscriber& Sub : Subscribers)
	{
		bAnyLog |= Sub.bLog && !Sub.bDead;
	}
	if (!bAnyLog || (Entries.Num() == 0 && Dropped == 0))
	{
		return;
	}

	// Keep the newest lines when a burst exceeds the per-tick budget.
	const int32 First = FMath::Max(0, Entries.Num() - UEToVSLink::MaxLogLinesPerTick);
	const int64 TotalDropped = Dropped + First;
	FString Lines;
	Lines.Reserve((Entries.Num() - First) * 96);
	Lines.AppendChar(TEXT('['));
	for (int32 i = First; i < Entries.Num(); ++i)
	{
		const FUEToVSLinkLogEntry& Entry = Entries[i];
		if (i > First)
		{
			Lines.AppendChar(TEXT(','));
		}
		Lines.Appendf(TEXT("{\"t\":%.3f,\"c\":"), Entry.Time);
		UEToVSLink::AppendJsonString(Lines, Entry.Category.ToString());
		Lines.Appendf(TEXT(",\"v\":%d,\"m\":"), static_cast<int32>(Entry.Verbosity));
		UEToVSLink::AppendJsonString(Lines, Entry.Message);
		Lines.AppendChar(TEXT('}'));
	}
	Lines.AppendChar(TEXT(']'));
	BroadcastLogs(Lines, Entries.Num() - First, TotalDropped);
}

// ---------------------------------------------------------------------------------------------------------------------
// Automation tests

FString FUEToVSLinkModule::StartTests(const FString& RunId, const FString& Filter)
{
	const FString Clean = UEToVSLink::SanitizeTestFilter(Filter);
	if (Clean.IsEmpty())
	{
		return UEToVSLink::Result(false, TEXT("missing filter"));
	}
	if (bTestRunActive)
	{
		return UEToVSLink::Result(false, TEXT("a test run is already active"));
	}
	if (GEngine == nullptr)
	{
		return UEToVSLink::Result(false, TEXT("engine is not ready"));
	}

	// "Automation RunTests" lives in the AutomationController module; the worker runs the tests in this process.
	FModuleManager::Get().LoadModule(TEXT("AutomationController"));
	FModuleManager::Get().LoadModule(TEXT("AutomationWorker"));

	bTestRunActive = true;
	TestRunId = RunId;
	TestFilter = Clean;
	TestRunStart = FPlatformTime::Seconds();
	TestLastActivity = TestRunStart;
	bTestStarted = false;
	bTestInProgress = false;
	TestsPassed = 0;
	TestsFailed = 0;
	RunLogs.Reset();
	FinishedTests.Reset();
	UpdateCapture();

	FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"runStarted\",\"id\":");
	UEToVSLink::AppendJsonString(Json, TestRunId);
	Json.Append(TEXT(",\"filter\":"));
	UEToVSLink::AppendJsonString(Json, TestFilter);
	Json.AppendChar(TEXT('}'));
	Broadcast(Json, true, false);

	UWorld* World = GEditor != nullptr ? GEditor->GetEditorWorldContext().World() : nullptr;
	const FString Command = FString::Printf(TEXT("Automation RunTests %s"), *Clean);
	UE_LOG(LogUEToVSLink, Log, TEXT("UEToVSLink: %s"), *Command);
	GEngine->Exec(World, *Command);

	TSharedRef<FJsonObject> Obj = MakeShared<FJsonObject>();
	Obj->SetBoolField(TEXT("ok"), true);
	Obj->SetStringField(TEXT("id"), RunId);
	return UEToVSLink::ToJson(Obj);
}

void FUEToVSLinkModule::OnTestStart(FAutomationTestBase* Test)
{
	QueueTestEvent(Test, true);
}

void FUEToVSLinkModule::OnTestEnd(FAutomationTestBase* Test)
{
	QueueTestEvent(Test, false);
}

void FUEToVSLinkModule::QueueTestEvent(FAutomationTestBase* Test, bool bStart)
{
	if (Test == nullptr)
	{
		return;
	}
	FTestEvent Event;
	Event.bStart = bStart;
	Event.Name = UEToVSLink::TestName(Test);
	Event.Seq = Capture.IsValid() ? Capture->NextSeq() : 0;
	Event.Time = FPlatformTime::Seconds();
	if (!bStart)
	{
		FAutomationTestExecutionInfo Info;
		Test->GetExecutionInfo(Info);
		for (const FAutomationExecutionEntry& Entry : Info.GetEntries())
		{
			if (Entry.Event.Type == EAutomationEventType::Error)
			{
				Event.Errors.Add(UEToVSLink::Clip(Entry.Event.Message, UEToVSLink::MaxTestMessageChars));
			}
			else if (Entry.Event.Type == EAutomationEventType::Warning)
			{
				Event.Warnings.Add(UEToVSLink::Clip(Entry.Event.Message, UEToVSLink::MaxTestMessageChars));
			}
		}
	}
	TestEvents.Enqueue(MoveTemp(Event));
}

void FUEToVSLinkModule::TickTests(const TArray<FUEToVSLinkLogEntry>& Entries)
{
	if (bTestRunActive)
	{
		for (const FUEToVSLinkLogEntry& Entry : Entries)
		{
			RunLogs.Add(Entry);
		}
		if (RunLogs.Num() > UEToVSLink::MaxRunLogs)
		{
			RunLogs.RemoveAt(0, RunLogs.Num() - UEToVSLink::MaxRunLogs);
		}
	}

	FTestEvent Event;
	while (TestEvents.Dequeue(Event))
	{
		TestLastActivity = FPlatformTime::Seconds();
		if (Event.bStart)
		{
			bTestStarted = true;
			bTestInProgress = true;
			CurrentTestName = Event.Name;
			CurrentTestStartSeq = Event.Seq;
			CurrentTestStartTime = Event.Time;
			FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"testStarted\",\"id\":");
			UEToVSLink::AppendJsonString(Json, TestRunId);
			Json.Append(TEXT(",\"name\":"));
			UEToVSLink::AppendJsonString(Json, Event.Name);
			Json.AppendChar(TEXT('}'));
			Broadcast(Json, true, false);
			continue;
		}

		const bool bPassed = Event.Errors.Num() == 0;
		bTestInProgress = false;
		if (bPassed)
		{
			++TestsPassed;
		}
		else
		{
			++TestsFailed;
		}
		if (FinishedTests.Num() < UEToVSLink::MaxFinishedTests)
		{
			FinishedTests.Add({ Event.Name, bPassed });
		}

		TArray<FString> Logs;
		const uint64 StartSeq = Event.Name == CurrentTestName ? CurrentTestStartSeq : Event.Seq;
		for (const FUEToVSLinkLogEntry& Entry : RunLogs)
		{
			if (Entry.Seq >= StartSeq && Entry.Seq < Event.Seq && Logs.Num() < UEToVSLink::MaxTestLogs)
			{
				Logs.Add(FString::Printf(TEXT("%s: %s"), *Entry.Category.ToString(), *UEToVSLink::Clip(Entry.Message, UEToVSLink::MaxTestMessageChars)));
			}
		}
		RunLogs.RemoveAll([&Event](const FUEToVSLinkLogEntry& Entry) { return Entry.Seq < Event.Seq; });

		const double DurationMs = Event.Name == CurrentTestName ? (Event.Time - CurrentTestStartTime) * 1000.0 : 0.0;
		FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"testFinished\",\"id\":");
		UEToVSLink::AppendJsonString(Json, TestRunId);
		Json.Append(TEXT(",\"name\":"));
		UEToVSLink::AppendJsonString(Json, Event.Name);
		Json.Append(bPassed ? TEXT(",\"result\":\"passed\"") : TEXT(",\"result\":\"failed\""));
		Json.Appendf(TEXT(",\"durationMs\":%.1f,\"errors\":"), DurationMs);
		UEToVSLink::AppendJsonStringArray(Json, Event.Errors);
		Json.Append(TEXT(",\"warnings\":"));
		UEToVSLink::AppendJsonStringArray(Json, Event.Warnings);
		Json.Append(TEXT(",\"logs\":"));
		UEToVSLink::AppendJsonStringArray(Json, Logs);
		Json.AppendChar(TEXT('}'));
		Broadcast(Json, true, false);
	}

	if (!bTestRunActive)
	{
		return;
	}

	// End of run: the command line runner reports completion in its log; an idle timeout covers engine versions that
	// word it differently. A failed result reported by the controller overrides a "passed" without error entries.
	for (const FUEToVSLinkLogEntry& Entry : Entries)
	{
		const FString Category = Entry.Category.ToString();
		if (Category == TEXT("LogAutomationController") && Entry.Message.StartsWith(TEXT("Test Completed.")))
		{
			FString Result;
			FString Path;
			const int32 ResultAt = Entry.Message.Find(TEXT("Result={"));
			const int32 PathAt = Entry.Message.Find(TEXT("Path={"));
			if (ResultAt != INDEX_NONE && PathAt != INDEX_NONE)
			{
				Result = Entry.Message.Mid(ResultAt + 8);
				Result = Result.Left(Result.Find(TEXT("}")));
				Path = Entry.Message.Mid(PathAt + 6);
				Path = Path.Left(Path.Find(TEXT("}")));
			}
			if (Result.Contains(TEXT("fail"), ESearchCase::IgnoreCase))
			{
				for (FTestRecord& Record : FinishedTests)
				{
					if (Record.bPassed && (Path == Record.Name || Path.StartsWith(Record.Name + TEXT("."))))
					{
						Record.bPassed = false;
						--TestsPassed;
						++TestsFailed;
						FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"testFinished\",\"id\":");
						UEToVSLink::AppendJsonString(Json, TestRunId);
						Json.Append(TEXT(",\"name\":"));
						UEToVSLink::AppendJsonString(Json, Record.Name);
						Json.Append(TEXT(",\"result\":\"failed\",\"errors\":"));
						UEToVSLink::AppendJsonStringArray(Json, { Entry.Message });
						Json.AppendChar(TEXT('}'));
						Broadcast(Json, true, false);
						break;
					}
				}
			}
			continue;
		}
		if (Category != TEXT("LogAutomationCommandLine"))
		{
			continue;
		}
		if (Entry.Message.Contains(TEXT("No automation tests matched"), ESearchCase::IgnoreCase))
		{
			FinishTestRun(TEXT("no tests matched"));
			return;
		}
		if (Entry.Message.Contains(TEXT("TEST COMPLETE"), ESearchCase::CaseSensitive))
		{
			FinishTestRun(FString());
			return;
		}
	}

	const double Idle = FPlatformTime::Seconds() - TestLastActivity;
	if (!bTestInProgress && Idle > (bTestStarted ? UEToVSLink::TestIdleSeconds : UEToVSLink::TestIdleBeforeFirstSeconds))
	{
		FinishTestRun(bTestStarted ? FString() : TEXT("no tests started"));
	}
}

void FUEToVSLinkModule::FinishTestRun(const FString& Message)
{
	FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"runFinished\",\"id\":");
	UEToVSLink::AppendJsonString(Json, TestRunId);
	Json.Appendf(TEXT(",\"passed\":%d,\"failed\":%d,\"durationMs\":%.0f"), TestsPassed, TestsFailed, (FPlatformTime::Seconds() - TestRunStart) * 1000.0);
	if (!Message.IsEmpty())
	{
		Json.Append(TEXT(",\"message\":"));
		UEToVSLink::AppendJsonString(Json, Message);
	}
	Json.AppendChar(TEXT('}'));
	Broadcast(Json, true, false);

	UE_LOG(LogUEToVSLink, Log, TEXT("UEToVSLink: test run %s finished (%d passed, %d failed)"), *TestRunId, TestsPassed, TestsFailed);
	bTestRunActive = false;
	bTestInProgress = false;
	TestRunId.Reset();
	RunLogs.Empty();
	FinishedTests.Empty();
	UpdateCapture();
}

// ---------------------------------------------------------------------------------------------------------------------
// Live Coding

void FUEToVSLinkModule::BindLiveCoding()
{
#if UETOVS_WITH_LIVE_CODING
	if (bLiveCodingBound)
	{
		return;
	}
	if (ILiveCodingModule* LiveCoding = FModuleManager::GetModulePtr<ILiveCodingModule>(LIVE_CODING_MODULE_NAME))
	{
		PatchCompleteHandle = LiveCoding->GetOnPatchCompleteDelegate().AddRaw(this, &FUEToVSLinkModule::OnLiveCodingPatchComplete);
		bLiveCodingBound = true;
	}
#endif
}

void FUEToVSLinkModule::UnbindLiveCoding()
{
#if UETOVS_WITH_LIVE_CODING
	if (bLiveCodingBound)
	{
		if (ILiveCodingModule* LiveCoding = FModuleManager::GetModulePtr<ILiveCodingModule>(LIVE_CODING_MODULE_NAME))
		{
			LiveCoding->GetOnPatchCompleteDelegate().Remove(PatchCompleteHandle);
		}
		PatchCompleteHandle.Reset();
		bLiveCodingBound = false;
	}
#endif
}

void FUEToVSLinkModule::OnLiveCodingPatchComplete()
{
	bLiveCodingPatched = true;
}

FString FUEToVSLinkModule::FindLiveCodingConsoleLog() const
{
	// LiveCodingConsole is a separate program; where its Saved folder lives depends on source vs installed engines.
	const TCHAR* Relative = TEXT("Programs/LiveCodingConsole/Saved/Logs/LiveCodingConsole.log");
	const FString Candidates[] = {
		FPaths::Combine(FPaths::EngineDir(), Relative),
		FPaths::Combine(FPaths::EngineUserDir(), Relative),
		FPaths::Combine(FPaths::EngineVersionAgnosticUserDir(), Relative),
	};
	FString Best;
	FDateTime BestTime = FDateTime::MinValue();
	for (const FString& Candidate : Candidates)
	{
		const FDateTime Stamp = IFileManager::Get().GetTimeStamp(*Candidate);
		if (Stamp > BestTime)
		{
			BestTime = Stamp;
			Best = Candidate;
		}
	}
	return Best;
}

void FUEToVSLinkModule::ReadLiveCodingConsoleTail(TArray<FString>& OutLines) const
{
	if (LiveCodingConsolePath.IsEmpty())
	{
		return;
	}
	TUniquePtr<FArchive> Reader(IFileManager::Get().CreateFileReader(*LiveCodingConsolePath, FILEREAD_AllowWrite));
	if (!Reader.IsValid())
	{
		return;
	}
	const int64 Size = Reader->TotalSize();
	if (Size <= 0)
	{
		return;
	}
	uint8 Bom[2] = { 0, 0 };
	if (Size >= 2)
	{
		Reader->Serialize(Bom, 2);
	}
	const bool bUtf16 = Bom[0] == 0xFF && Bom[1] == 0xFE;

	int64 Start = LiveCodingConsoleStartBytes <= Size ? LiveCodingConsoleStartBytes : 0; // rotated / truncated log
	Start = FMath::Max(Start, Size - UEToVSLink::MaxConsoleTailBytes);
	if (bUtf16)
	{
		Start = FMath::Max<int64>(2, Start & ~static_cast<int64>(1));
	}
	const int64 Count = Size - Start;
	if (Count <= 0)
	{
		return;
	}
	TArray<uint8> Bytes;
	Bytes.SetNumUninitialized(static_cast<int32>(Count));
	Reader->Seek(Start);
	Reader->Serialize(Bytes.GetData(), Count);
	if (Reader->IsError())
	{
		return;
	}

	FString Text;
	if (bUtf16)
	{
		const int32 Chars = static_cast<int32>(Count / 2);
		Text.Reserve(Chars);
		const uint8* Data = Bytes.GetData();
		for (int32 i = 0; i < Chars; ++i)
		{
			Text.AppendChar(static_cast<TCHAR>(Data[i * 2] | (Data[i * 2 + 1] << 8)));
		}
	}
	else
	{
		FUTF8ToTCHAR Converted(reinterpret_cast<const ANSICHAR*>(Bytes.GetData()), Bytes.Num());
		Text = FString(Converted.Length(), Converted.Get());
	}

	TArray<FString> Lines;
	Text.ParseIntoArrayLines(Lines);
	const int32 First = FMath::Max(0, Lines.Num() - UEToVSLink::MaxConsoleTailLines);
	for (int32 i = First; i < Lines.Num(); ++i)
	{
		OutLines.Add(UEToVSLink::Clip(Lines[i], FUEToVSLinkLogCapture::MaxMessageChars));
	}
}

void FUEToVSLinkModule::TickLiveCoding(const TArray<FUEToVSLinkLogEntry>& Entries)
{
#if UETOVS_WITH_LIVE_CODING
	bool bWanted = false;
	for (const FSubscriber& Sub : Subscribers)
	{
		bWanted |= Sub.bLiveCoding && !Sub.bDead;
	}
	if (!bWanted && !bLiveCodingCompiling)
	{
		return; // nobody listens: no polling at all
	}

	ILiveCodingModule* LiveCoding = FModuleManager::GetModulePtr<ILiveCodingModule>(LIVE_CODING_MODULE_NAME);
	if (LiveCoding == nullptr)
	{
		return;
	}
	BindLiveCoding();
	const bool bCompiling = LiveCoding->IsCompiling();

	if (bCompiling && !bLiveCodingCompiling)
	{
		bLiveCodingCompiling = true;
		bLiveCodingPatched = false;
		bLiveCodingSawError = false;
		LiveCodingHints.Reset();
		LiveCodingStart = FPlatformTime::Seconds();
		LiveCodingConsolePath = FindLiveCodingConsoleLog();
		LiveCodingConsoleStartBytes = LiveCodingConsolePath.IsEmpty() ? 0 : FMath::Max<int64>(0, IFileManager::Get().FileSize(*LiveCodingConsolePath));
		UpdateCapture();
		Broadcast(TEXT("{\"event\":\"liveCoding\",\"phase\":\"started\"}"), false, true);
	}
	if (!bLiveCodingCompiling)
	{
		return;
	}

	TArray<FString> Output;
	for (const FUEToVSLinkLogEntry& Entry : Entries)
	{
		if (Entry.Category != FName(TEXT("LogLiveCoding")))
		{
			continue;
		}
		Output.Add(Entry.Message);
		LiveCodingHints.Add(Entry.Message);
		bLiveCodingSawError |= Entry.Verbosity <= ELogVerbosity::Error;
	}
	if (Output.Num() > 0)
	{
		FString Json = TEXT("{\"event\":\"liveCoding\",\"phase\":\"output\",\"lines\":");
		UEToVSLink::AppendJsonStringArray(Json, Output);
		Json.AppendChar(TEXT('}'));
		Broadcast(Json, false, true);
	}

	if (bCompiling)
	{
		return;
	}

	// Finished: the compiler output lives in the Live Coding console's log, not in the editor log.
	TArray<FString> Tail;
	ReadLiveCodingConsoleTail(Tail);
	bool bTailError = false;
	for (const FString& Line : Tail)
	{
		bTailError |= Line.Contains(TEXT(": error ")) || Line.Contains(TEXT(": fatal error "));
	}
	if (Tail.Num() > 0)
	{
		FString Json = TEXT("{\"event\":\"liveCoding\",\"phase\":\"output\",\"source\":\"console\",\"lines\":");
		UEToVSLink::AppendJsonStringArray(Json, Tail);
		Json.AppendChar(TEXT('}'));
		Broadcast(Json, false, true);
	}

	FString Result = TEXT("unknown");
	FString Message;
	for (const FString& Hint : LiveCodingHints)
	{
		if (Hint.Contains(TEXT("No changes"), ESearchCase::IgnoreCase) || Hint.Contains(TEXT("No code changes"), ESearchCase::IgnoreCase))
		{
			Result = TEXT("noChanges");
			Message = Hint;
		}
		else if (Hint.Contains(TEXT("cancel"), ESearchCase::IgnoreCase))
		{
			Result = TEXT("cancelled");
			Message = Hint;
		}
		else if (Hint.Contains(TEXT("fail"), ESearchCase::IgnoreCase))
		{
			Result = TEXT("failed");
			Message = Hint;
		}
		else if (Hint.Contains(TEXT("succeeded"), ESearchCase::IgnoreCase) && Result == TEXT("unknown"))
		{
			Result = TEXT("success");
			Message = Hint;
		}
	}
	if (bLiveCodingPatched && Result != TEXT("failed"))
	{
		Result = TEXT("success");
	}
	if (bTailError || (bLiveCodingSawError && Result == TEXT("unknown")))
	{
		Result = TEXT("failed");
	}

	FString Json = TEXT("{\"event\":\"liveCoding\",\"phase\":\"finished\",\"result\":");
	UEToVSLink::AppendJsonString(Json, Result);
	Json.Appendf(TEXT(",\"durationMs\":%.0f"), (FPlatformTime::Seconds() - LiveCodingStart) * 1000.0);
	if (!Message.IsEmpty())
	{
		Json.Append(TEXT(",\"message\":"));
		UEToVSLink::AppendJsonString(Json, Message);
	}
	Json.AppendChar(TEXT('}'));
	Broadcast(Json, false, true);

	bLiveCodingCompiling = false;
	LiveCodingHints.Empty();
	UpdateCapture();
#endif
}

IMPLEMENT_MODULE(FUEToVSLinkModule, UEToVSLink)
