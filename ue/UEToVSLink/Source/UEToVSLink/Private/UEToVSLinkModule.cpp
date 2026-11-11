#include "UEToVSLinkModule.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Common/TcpListener.h"
#include "ContentBrowserModule.h"
#include "Dom/JsonObject.h"
#include "Editor.h"
#include "Framework/Docking/TabManager.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformTime.h"
#include "IContentBrowserSingleton.h"
#include "Interfaces/IPv4/IPv4Address.h"
#include "Interfaces/IPv4/IPv4Endpoint.h"
#include "Misc/App.h"
#include "Misc/EngineVersion.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Policies/CondensedJsonPrintPolicy.h"
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
}

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

	FFileHelper::SaveStringToFile(FString::FromInt(Port), *PortFilePath());
	TickHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateRaw(this, &FUEToVSLinkModule::Tick), 0.05f);
	UE_LOG(LogUEToVSLink, Log, TEXT("UEToVSLink listening on 127.0.0.1:%d"), Port);
}

void FUEToVSLinkModule::ShutdownModule()
{
	if (TickHandle.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(TickHandle);
		TickHandle.Reset();
	}
	Listener.Reset();

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

		int32 Newline = Client.Buffer.Find(static_cast<uint8>('\n'));
		const bool bTimedOut = FPlatformTime::Seconds() - Client.StartTime > 5.0;
		if (Newline == INDEX_NONE && !bTimedOut && Client.Buffer.Num() < 64 * 1024)
		{
			continue;
		}

		if (Newline != INDEX_NONE)
		{
			FUTF8ToTCHAR Converted(reinterpret_cast<const ANSICHAR*>(Client.Buffer.GetData()), Newline);
			FString Line(Converted.Length(), Converted.Get());
			Reply(Client.Socket, Handle(Line.TrimStartAndEnd()));
		}
		Client.Socket->Close();
		Sockets->DestroySocket(Client.Socket);
		Clients.RemoveAt(i);
	}
	return true;
}

void FUEToVSLinkModule::Reply(FSocket* Socket, const FString& Json)
{
	FTCHARToUTF8 Utf8(*(Json + TEXT("\n")));
	int32 Sent = 0;
	Socket->SetNonBlocking(false);
	Socket->Send(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length(), Sent);
}

FString FUEToVSLinkModule::Handle(const FString& Line)
{
	TSharedPtr<FJsonObject> Request;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Line);
	if (!FJsonSerializer::Deserialize(Reader, Request) || !Request.IsValid())
	{
		return UEToVSLink::Result(false, TEXT("bad request"));
	}

	const FString Cmd = Request->GetStringField(TEXT("cmd"));
	if (Cmd == TEXT("ping"))
	{
		TSharedRef<FJsonObject> Obj = MakeShared<FJsonObject>();
		Obj->SetBoolField(TEXT("ok"), true);
		Obj->SetStringField(TEXT("project"), FApp::GetProjectName());
		Obj->SetStringField(TEXT("engine"), FEngineVersion::Current().ToString());
		return UEToVSLink::ToJson(Obj);
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

IMPLEMENT_MODULE(FUEToVSLinkModule, UEToVSLink)
