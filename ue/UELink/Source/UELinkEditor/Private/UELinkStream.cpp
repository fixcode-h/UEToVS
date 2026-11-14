// 推送通道的服务、命令和订阅（协议说明见 UELinkStream.h）。测试和 Live Coding 在同目录的 UELinkStreamTests.cpp、UELinkStreamLiveCoding.cpp。
#include "UELinkStream.h"
#include "UELinkStreamJson.h"
#include "UELinkVersion.h"
#include "AgentPanelBridge.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Common/TcpListener.h"
#include "ContentBrowserModule.h"
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
#include "Misc/EngineVersion.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonReader.h"
#include "SocketSubsystem.h"
#include "Sockets.h"
#include "Subsystems/AssetEditorSubsystem.h"
#include "Widgets/SWindow.h"

#if AGENT_PANEL_LIVE_CODING
#include "ILiveCodingModule.h"
#endif

DEFINE_LOG_CATEGORY_STATIC(LogUELinkStream, Log, All);

// 共用工具一律写全 UELinkStream::：单体构建（unity）会把同模块的 .cpp 拼在一起，别的文件的 using namespace 会漏进来

namespace
{
	void BringEditorToFront()
	{
		TSharedPtr<SWindow> Root = FGlobalTabmanager::Get()->GetRootWindow();
		if (Root.IsValid())
		{
			Root->HACK_ForceToFront();
		}
	}

	FAssetData FindAsset(const FString& ObjectPath)
	{
		IAssetRegistry& Registry = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
		return Registry.GetAssetByObjectPath(FSoftObjectPath(ObjectPath));
	}

	TArray<FString> ReadTopics(const TSharedPtr<FJsonObject>& Request)
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

	FString FirstLine(TArray<uint8>& Buffer, bool& bOutFound)
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

	/** 请求带了令牌就必须对；不带的是旧 UEToVS（兼容层），先放行 */
	bool TokenOk(const TSharedPtr<FJsonObject>& Request)
	{
		FString Token;
		if (!Request->TryGetStringField(TEXT("token"), Token))
		{
			return true;
		}
		return Token == FAgentPanelBridge::Get().GetToken();
	}
}

FUELinkStream& FUELinkStream::Get()
{
	static FUELinkStream Instance;
	return Instance;
}

TArray<FString> FUELinkStream::Features()
{
	TArray<FString> Out = {TEXT("log"), TEXT("tests")};
#if AGENT_PANEL_LIVE_CODING
	Out.Add(TEXT("liveCoding"));
#endif
	return Out;
}

// ---------------------------------------------------------------------------------------------------------------------
// 启停

void FUELinkStream::Start()
{
	if (Listener.IsValid() || IsRunningCommandlet() || !GIsEditor)
	{
		return;
	}
	// 端口 0：让系统挑空闲端口，挑到的写进名片和 port.txt
	FIPv4Endpoint Endpoint(FIPv4Address(127, 0, 0, 1), 0);
	Listener = MakeUnique<FTcpListener>(Endpoint, FTimespan::FromMilliseconds(100), false);
	Listener->OnConnectionAccepted().BindRaw(this, &FUELinkStream::OnConnection);
	Port = 0;
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
		UE_LOG(LogUELinkStream, Warning, TEXT("UELink：推送通道没有拿到本机端口"));
		Listener.Reset();
		return;
	}

	// 日志统一从 UELinkCore 的缓冲取；带 -NoAgentPanel 启动时 HTTP 服务不起、缓冲也没挂上，这里补挂（重复挂不会挂两次）
	FAgentPanelLog::Get().Attach();
	LogCursor = FAgentPanelLog::Get().LastSeq();
	StartSeconds = FPlatformTime::Seconds();

	FAutomationTestFramework& Framework = FAutomationTestFramework::Get();
	TestStartHandle = Framework.OnTestStartEvent.AddRaw(this, &FUELinkStream::OnTestStart);
	TestEndHandle = Framework.OnTestEndEvent.AddRaw(this, &FUELinkStream::OnTestEnd);

	PublishPort();
	TickHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateRaw(this, &FUELinkStream::Tick), 0.05f);
	UE_LOG(LogUELinkStream, Log, TEXT("UELink %s 推送通道：127.0.0.1:%d（协议 %d）"), UELINK_VERSION, Port, UELINK_STREAM_PROTOCOL);
}

void FUELinkStream::PublishPort()
{
	// 旧 UEToVS 读 port.txt（兼容层）；新客户端读名片里的 stream
	FFileHelper::SaveStringToFile(FString::FromInt(Port), *PortFilePath());
	FAgentPanelBridge& Bridge = FAgentPanelBridge::Get();
	Bridge.SetCardField(TEXT("stream"), MakeShared<FJsonValueNumber>(Port));
	Bridge.SetCardField(TEXT("protocol"), MakeShared<FJsonValueNumber>(UELINK_STREAM_PROTOCOL));
	Bridge.SetCardField(TEXT("features"), MakeShared<FJsonValueArray>(UELinkStream::StringValues(Features())));
}

void FUELinkStream::Stop()
{
	if (TickHandle.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(TickHandle);
		TickHandle.Reset();
	}
	if (!Listener.IsValid())
	{
		return;
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
	Port = 0;
}

FString FUELinkStream::PortFilePath() const
{
	return FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("UEToVSLink"), TEXT("port.txt"));
}

bool FUELinkStream::OnConnection(FSocket* Socket, const FIPv4Endpoint& Endpoint)
{
	// 监听线程：把套接字交给游戏线程
	Pending.Enqueue(Socket);
	return true;
}

// ---------------------------------------------------------------------------------------------------------------------
// Tick（游戏线程）

bool FUELinkStream::Tick(float DeltaTime)
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
			const FString Line = FirstLine(Client.Buffer, bFound);
			bool bSubscribe = false;
			TArray<FString> Topics;
			const FString Response = Handle(Line, bSubscribe, Topics);
			if (bSubscribe)
			{
				// 订阅：连接留着，套接字转给订阅者列表
				FSubscriber& Sub = Subscribers.AddDefaulted_GetRef();
				Sub.Socket = Client.Socket;
				Sub.In = MoveTemp(Client.Buffer);
				ApplyTopics(Sub, Topics);
				Enqueue(Sub, Response);
				Clients.RemoveAt(i);
				UE_LOG(LogUELinkStream, Verbose, TEXT("UELink：订阅者连上（%d 个）"), Subscribers.Num());
				continue;
			}
			Reply(Client.Socket, Response);
		}
		Client.Socket->Close();
		Sockets->DestroySocket(Client.Socket);
		Clients.RemoveAt(i);
	}

	// 只在有人要（日志订阅、测试运行中、Live Coding 编译中）时才从缓冲拷行
	TArray<FAgentPanelLog::FEntry> Entries;
	int64 Dropped = 0;
	if (WantsLogs() || bTestRunActive || bLiveCodingCompiling)
	{
		LogCursor = FAgentPanelLog::Get().EntriesSince(LogCursor, 20000, Entries, Dropped);
	}
	else
	{
		LogCursor = FAgentPanelLog::Get().LastSeq();
	}

	TickTests(Entries);
	ProcessLogs(Entries, Dropped);
	TickLiveCoding(Entries);
	TickSubscribers();
	return true;
}

void FUELinkStream::Reply(FSocket* Socket, const FString& Json)
{
	FTCHARToUTF8 Utf8(*(Json + TEXT("\n")));
	int32 Sent = 0;
	Socket->SetNonBlocking(false);
	Socket->Send(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length(), Sent);
}

FString FUELinkStream::Handle(const FString& Line, bool& bOutSubscribe, TArray<FString>& OutTopics)
{
	bOutSubscribe = false;
	TSharedPtr<FJsonObject> Request;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Line);
	if (!FJsonSerializer::Deserialize(Reader, Request) || !Request.IsValid())
	{
		return UELinkStream::Result(false, TEXT("bad request"));
	}
	if (!TokenOk(Request))
	{
		return UELinkStream::Result(false, TEXT("bad token"));
	}

	FString Cmd;
	Request->TryGetStringField(TEXT("cmd"), Cmd);
	if (Cmd == TEXT("ping"))
	{
		TSharedRef<FJsonObject> Obj = MakeShared<FJsonObject>();
		Obj->SetBoolField(TEXT("ok"), true);
		Obj->SetStringField(TEXT("project"), FApp::GetProjectName());
		Obj->SetStringField(TEXT("engine"), FEngineVersion::Current().ToString());
		Obj->SetNumberField(TEXT("protocol"), UELINK_STREAM_PROTOCOL);
		Obj->SetStringField(TEXT("plugin"), UELINK_VERSION);
		Obj->SetStringField(TEXT("name"), TEXT("UELink"));
		Obj->SetArrayField(TEXT("features"), UELinkStream::StringValues(Features()));
		return UELinkStream::ToJson(Obj);
	}

	if (Cmd == TEXT("subscribe"))
	{
		if (Subscribers.Num() >= UELinkStream::MaxSubscribers)
		{
			return UELinkStream::Result(false, TEXT("too many subscribers"));
		}
		bOutSubscribe = true;
		OutTopics = ReadTopics(Request);
		TSharedRef<FJsonObject> Obj = MakeShared<FJsonObject>();
		Obj->SetBoolField(TEXT("ok"), true);
		Obj->SetNumberField(TEXT("protocol"), UELINK_STREAM_PROTOCOL);
		Obj->SetStringField(TEXT("plugin"), UELINK_VERSION);
		return UELinkStream::ToJson(Obj);
	}

	if (Cmd == TEXT("runTests"))
	{
		FString RunId;
		FString Filter;
		Request->TryGetStringField(TEXT("id"), RunId);
		Request->TryGetStringField(TEXT("filter"), Filter);
		FString Error;
		if (!StartTests(RunId, Filter, Error))
		{
			return UELinkStream::Result(false, Error);
		}
		TSharedRef<FJsonObject> Obj = MakeShared<FJsonObject>();
		Obj->SetBoolField(TEXT("ok"), true);
		Obj->SetStringField(TEXT("id"), RunId);
		return UELinkStream::ToJson(Obj);
	}

	if (Cmd == TEXT("openAsset") || Cmd == TEXT("browseAsset"))
	{
		FString Path;
		Request->TryGetStringField(TEXT("path"), Path);
		FString Message;
		FString Error;
		if (!OpenAsset(Path, Cmd == TEXT("browseAsset"), Message, Error))
		{
			return UELinkStream::Result(false, Error);
		}
		return UELinkStream::Result(true, Message);
	}

	if (Cmd == TEXT("liveCoding"))
	{
		FString Message;
		FString Error;
		if (!TriggerLiveCoding(Message, Error))
		{
			return UELinkStream::Result(false, Error);
		}
		return UELinkStream::Result(true, Message);
	}

	// 旧 UEToVS 靠 "unknown command" 开头判断插件太旧，这句不能改
	return UELinkStream::Result(false, FString::Printf(TEXT("unknown command: %s"), *Cmd));
}

// ---------------------------------------------------------------------------------------------------------------------
// 一次性命令的实现（HTTP 操作也调这几个）

bool FUELinkStream::OpenAsset(const FString& Path, bool bBrowse, FString& Message, FString& Error)
{
	// 这几句报错是英文：旧 UEToVS 原样显示 UEToVSLink 的英文报错，保持一致
	if (Path.IsEmpty())
	{
		Error = TEXT("missing path");
		return false;
	}
	FAssetData Asset = FindAsset(Path);
	if (!Asset.IsValid())
	{
		Error = FString::Printf(TEXT("asset not found: %s"), *Path);
		return false;
	}
	BringEditorToFront();
	FContentBrowserModule& ContentBrowser = FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
	if (bBrowse)
	{
		ContentBrowser.Get().SyncBrowserToAssets(TArray<FAssetData>{Asset});
		return true;
	}
	UObject* Object = Asset.GetAsset();
	if (Object == nullptr || GEditor == nullptr)
	{
		Error = TEXT("could not load asset");
		return false;
	}
	UAssetEditorSubsystem* Editors = GEditor->GetEditorSubsystem<UAssetEditorSubsystem>();
	const bool bOpened = Editors != nullptr && Editors->OpenEditorForAsset(Object);
	if (!bOpened)
	{
		// 关卡、没有编辑器的资源：至少在内容浏览器里定位出来
		ContentBrowser.Get().SyncBrowserToAssets(TArray<FAssetData>{Asset});
		Message = TEXT("shown in Content Browser");
	}
	return true;
}

bool FUELinkStream::TriggerLiveCoding(FString& Message, FString& Error)
{
#if AGENT_PANEL_LIVE_CODING
	ILiveCodingModule* LiveCoding = FModuleManager::GetModulePtr<ILiveCodingModule>(LIVE_CODING_MODULE_NAME);
	if (LiveCoding == nullptr)
	{
		Error = TEXT("Live Coding module is not loaded");
		return false;
	}
	if (!LiveCoding->IsEnabledByDefault() && !LiveCoding->IsEnabledForSession())
	{
		Error = TEXT("Live Coding is disabled (Editor Preferences > Live Coding)");
		return false;
	}
	if (!LiveCoding->IsEnabledForSession())
	{
		LiveCoding->EnableForSession(true);
	}
	if (!LiveCoding->IsEnabledForSession())
	{
		Error = LiveCoding->GetEnableErrorText().ToString();
		return false;
	}
	if (LiveCoding->IsCompiling())
	{
		Message = TEXT("already compiling");
		return true;
	}
	LiveCoding->Compile();
	return true;
#else
	Error = TEXT("Live Coding is only available on Windows");
	return false;
#endif
}

// ---------------------------------------------------------------------------------------------------------------------
// 订阅

void FUELinkStream::ApplyTopics(FSubscriber& Sub, const TArray<FString>& Topics)
{
	Sub.bLog = Topics.Contains(TEXT("log"));
	Sub.bTests = Topics.Contains(TEXT("tests"));
	Sub.bLiveCoding = Topics.Contains(TEXT("liveCoding"));
}

void FUELinkStream::HandleSubscriberLine(FSubscriber& Sub, const FString& Line)
{
	TSharedPtr<FJsonObject> Request;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Line);
	if (!FJsonSerializer::Deserialize(Reader, Request) || !Request.IsValid() || !TokenOk(Request))
	{
		return;
	}
	FString Cmd;
	Request->TryGetStringField(TEXT("cmd"), Cmd);
	if (Cmd != TEXT("subscribe"))
	{
		return; // 一次性命令走自己的连接
	}
	const TArray<FString> Topics = ReadTopics(Request);
	ApplyTopics(Sub, Topics);
	FString Json = TEXT("{\"event\":\"subscribed\",\"topics\":");
	UELinkStream::AppendJsonStringArray(Json, Topics);
	Json.AppendChar(TEXT('}'));
	Enqueue(Sub, Json);
}

void FUELinkStream::Enqueue(FSubscriber& Sub, const FString& JsonLine)
{
	if (Sub.bDead)
	{
		return;
	}
	FTCHARToUTF8 Utf8(*JsonLine);
	Sub.Out.Append(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
	Sub.Out.Add(static_cast<uint8>('\n'));
	if (Sub.Out.Num() - Sub.OutOffset > UELinkStream::HardOutCap)
	{
		// 客户端不读了：断开它，不让缓冲无限长
		Sub.bDead = true;
	}
}

void FUELinkStream::Broadcast(const FString& JsonLine, bool bTestsTopic, bool bLiveCodingTopic)
{
	for (FSubscriber& Sub : Subscribers)
	{
		if ((bTestsTopic && Sub.bTests) || (bLiveCodingTopic && Sub.bLiveCoding))
		{
			Enqueue(Sub, JsonLine);
		}
	}
}

void FUELinkStream::BroadcastLogs(const FString& LinesJson, int32 LineCount, int64 Dropped)
{
	for (FSubscriber& Sub : Subscribers)
	{
		if (!Sub.bLog || Sub.bDead)
		{
			continue;
		}
		if (Sub.Out.Num() - Sub.OutOffset > UELinkStream::SoftOutCap)
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

void FUELinkStream::Flush(FSubscriber& Sub)
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

void FUELinkStream::TickSubscribers()
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
					Sub.bDead = true; // 客户端关了（读到 0 字节）或套接字出错
					break;
				}
				if (Read <= 0)
				{
					break; // 暂时没数据
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
			const FString Line = FirstLine(Sub.In, bFound);
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
			UE_LOG(LogUELinkStream, Verbose, TEXT("UELink：订阅者断开（还剩 %d 个）"), Subscribers.Num());
		}
	}
}

bool FUELinkStream::WantsLogs() const
{
	for (const FSubscriber& Sub : Subscribers)
	{
		if (Sub.bLog && !Sub.bDead)
		{
			return true;
		}
	}
	return false;
}

void FUELinkStream::ProcessLogs(const TArray<FAgentPanelLog::FEntry>& Entries, int64 Dropped)
{
	if (!WantsLogs() || (Entries.Num() == 0 && Dropped == 0))
	{
		return;
	}
	// 一次涌进来太多时只推最新的一批
	const int32 First = FMath::Max(0, Entries.Num() - UELinkStream::MaxLogLinesPerTick);
	const int64 TotalDropped = Dropped + First;
	FString Lines;
	Lines.Reserve((Entries.Num() - First) * 96);
	Lines.AppendChar(TEXT('['));
	for (int32 i = First; i < Entries.Num(); ++i)
	{
		const FAgentPanelLog::FEntry& Entry = Entries[i];
		if (i > First)
		{
			Lines.AppendChar(TEXT(','));
		}
		// 字段名和 UEToVSLink 一样：t 相对秒数，c 分类，v 级别（ELogVerbosity 的数值），m 文字
		Lines.Appendf(TEXT("{\"t\":%.3f,\"c\":"), Entry.Time - StartSeconds);
		UELinkStream::AppendJsonString(Lines, Entry.Category.ToString());
		Lines.Appendf(TEXT(",\"v\":%d,\"m\":"), static_cast<int32>(Entry.Verbosity));
		UELinkStream::AppendJsonString(Lines, Entry.Text);
		Lines.AppendChar(TEXT('}'));
	}
	Lines.AppendChar(TEXT(']'));
	BroadcastLogs(Lines, Entries.Num() - First, TotalDropped);
}
