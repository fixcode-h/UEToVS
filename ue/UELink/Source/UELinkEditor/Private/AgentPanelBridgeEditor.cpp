// 编辑器模块的操作：DataTable 行编辑、资源保存 / 放弃改动 / 列出改过的、PIE、Live Coding、在编辑器里打开资源、编辑器视口截图。
// 推送通道（UEToVS 用的日志 / 测试 / Live Coding 推送）在 UELinkStream*.cpp，它的 HTTP 操作在 UELinkStreamOps.cpp。
// 资源通用层和蓝图读写在同目录的 AgentPanelAssetOps.cpp、AgentPanelBlueprint*.cpp（共用的东西在 AgentPanelEditorShared.h）。
// 后台编辑器（-run=AgentPanel，见 AgentPanelCommandlet）也加载这个模块，所以资源写入在编辑器没开时同样可用。
#include "AgentPanelBridge.h"
#include "AgentPanelBridgeUtil.h"
#include "AgentPanelEditorShared.h"
#include "UELinkStream.h"

#include "DataTableEditorUtils.h"
#include "Editor.h"
#include "Editor/EditorEngine.h"
#include "Engine/DataTable.h"
#include "HAL/FileManager.h"
#include "Misc/PackageName.h"
#include "Modules/ModuleManager.h"
#include "Misc/CoreDelegates.h"
#include "Runtime/Launch/Resources/Version.h"
#include "PackageTools.h"
#include "Settings/LevelEditorPlaySettings.h"
#include "Subsystems/AssetEditorSubsystem.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UnrealClient.h"
#if AGENT_PANEL_LIVE_CODING
#include "ILiveCodingModule.h"
#endif

// 5.8 起 FCoreDelegates::OnPostEngineInit 弃用（同 UELinkCore 的 AgentPanelBridge.cpp）
#if ENGINE_MAJOR_VERSION > 5 || (ENGINE_MAJOR_VERSION == 5 && ENGINE_MINOR_VERSION >= 8)
#define UELINK_EDITOR_POST_ENGINE_INIT FCoreDelegates::GetOnPostEngineInit()
#else
#define UELINK_EDITOR_POST_ENGINE_INIT FCoreDelegates::OnPostEngineInit
#endif

using namespace AgentPanel;
using namespace AgentPanelEd;

namespace
{
	UDataTable* LoadTable(const TSharedPtr<FJsonObject>& A, FString& Error)
	{
		UDataTable* DT = Cast<UDataTable>(FindObjectByPath(Str(A, TEXT("path")), true, Error));
		if (!DT && Error.IsEmpty())
		{
			Error = TEXT("这不是 DataTable");
		}
		return DT;
	}

	UPackage* PackageOf(const FString& Name)
	{
		FString Pkg = Name;
		int32 Dot = INDEX_NONE;
		if (Pkg.FindChar(TEXT('.'), Dot))
		{
			Pkg = Pkg.Left(Dot);
		}
		return FindPackage(nullptr, *Pkg);
	}

	TArray<FString> PackagesArg(const TSharedPtr<FJsonObject>& A)
	{
		TArray<FString> Out;
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		if (A->TryGetArrayField(TEXT("packages"), Arr))
		{
			for (const TSharedPtr<FJsonValue>& V : *Arr)
			{
				Out.Add(V->AsString());
			}
		}
		else
		{
			Out = DirtyPackages().Array();
		}
		return Out;
	}
}

static void RegisterEditorOps()
{
	FAgentPanelBridge& B = FAgentPanelBridge::Get();

	// ---------- DataTable 行编辑（改动先标脏，保存另走 asset.save） ----------
	B.AddOp(TEXT("datatable.set"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UDataTable* DT = LoadTable(A, Error);
		if (!DT)
		{
			return false;
		}
		const FName RowName(*Str(A, TEXT("row")));
		uint8* Row = DT->FindRowUnchecked(RowName);
		if (!Row)
		{
			Error = FString::Printf(TEXT("%s 里没有行 %s"), *DT->GetName(), *RowName.ToString());
			return false;
		}
		FResolved R;
		if (!ResolvePath(const_cast<UScriptStruct*>(DT->GetRowStruct()), Row, DT, Str(A, TEXT("column")), R, Error))
		{
			return false;
		}
		Out->SetField(TEXT("prev"), ToJson(R.Prop, R.Value, 0));
		// 包进编辑器事务：编辑器里 Ctrl+Z 能撤，ue_undo 按 tx 撤同一步
		FTx Tx(FString::Printf(TEXT("%s %s.%s"), *DT->GetName(), *RowName.ToString(), *Str(A, TEXT("column"))));
		DT->Modify();
		FDataTableEditorUtils::BroadcastPreChange(DT, FDataTableEditorUtils::EDataTableChangeInfo::RowData);
		const bool bOk = FromJson(R.Prop, R.Value, A->TryGetField(TEXT("value")), Error);
		DT->HandleDataTableChanged(RowName);
		FDataTableEditorUtils::BroadcastPostChange(DT, FDataTableEditorUtils::EDataTableChangeInfo::RowData);
		NoteDirty(DT);
		Out->SetField(TEXT("value"), ToJson(R.Prop, R.Value, 0));
		FString TxError;
		if (Tx.IsValid() && Tx.Finish(true, TxError))
		{
			Out->SetStringField(TEXT("tx"), Tx.Id);
		}
		return bOk;
	});

	B.AddOp(TEXT("datatable.rows"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UDataTable* DT = LoadTable(A, Error);
		if (!DT)
		{
			return false;
		}
		const FString Action = Str(A, TEXT("action"));
		const FName Row(*Str(A, TEXT("row")));
		const FName To(*Str(A, TEXT("to")));
		bool bOk = false;
		if (Action == TEXT("add"))
		{
			bOk = FDataTableEditorUtils::AddRow(DT, Row) != nullptr;
		}
		else if (Action == TEXT("remove"))
		{
			bOk = FDataTableEditorUtils::RemoveRow(DT, Row);
		}
		else if (Action == TEXT("duplicate"))
		{
			bOk = FDataTableEditorUtils::DuplicateRow(DT, Row, To) != nullptr;
		}
		else if (Action == TEXT("rename"))
		{
			bOk = FDataTableEditorUtils::RenameRow(DT, Row, To);
		}
		else
		{
			Error = TEXT("action 只能是 add / remove / duplicate / rename");
			return false;
		}
		if (!bOk)
		{
			Error = FString::Printf(TEXT("%s 行 %s 没成功（行名可能已存在或不存在）"), *Action, *Row.ToString());
			return false;
		}
		NoteDirty(DT);
		return true;
	});

	// ---------- 资源保存 ----------
	B.AddOp(TEXT("asset.dirty"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		TArray<TSharedPtr<FJsonValue>> Items;
		for (const FString& Name : DirtyPackages().Array())
		{
			UPackage* Pkg = PackageOf(Name);
			if (!Pkg || !Pkg->IsDirty())
			{
				DirtyPackages().Remove(Name);
				continue;
			}
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			const FString File = PackageFile(Pkg);
			O->SetStringField(TEXT("package"), Name);
			O->SetStringField(TEXT("file"), File);
			O->SetBoolField(TEXT("readOnly"), IFileManager::Get().IsReadOnly(*File));
			// 还没存过盘的新资源：P4 项目保存后要 p4 add
			O->SetBoolField(TEXT("isNew"), !IFileManager::Get().FileExists(*File));
			Items.Add(MakeShared<FJsonValueObject>(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		return true;
	});

	B.AddOp(TEXT("asset.save"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		TArray<TSharedPtr<FJsonValue>> Saved;
		TArray<FString> Failed;
		for (const FString& Name : PackagesArg(A))
		{
			UPackage* Pkg = PackageOf(Name);
			if (!Pkg)
			{
				Failed.Add(Name + TEXT("（没加载）"));
				continue;
			}
			const FString File = PackageFile(Pkg);
			if (IFileManager::Get().IsReadOnly(*File))
			{
				// 面板保存前会先 p4 edit；还是只读说明没签出成功，不去改文件属性
				Failed.Add(File + TEXT("（只读，没签出）"));
				continue;
			}
			FSavePackageArgs Args;
			Args.TopLevelFlags = RF_Public | RF_Standalone;
			Args.SaveFlags = SAVE_NoError;
			if (UPackage::SavePackage(Pkg, nullptr, *File, Args))
			{
				DirtyPackages().Remove(Pkg->GetName());
				Saved.Add(MakeShared<FJsonValueString>(File));
			}
			else
			{
				Failed.Add(File);
			}
		}
		Out->SetArrayField(TEXT("saved"), Saved);
		if (Failed.Num())
		{
			Error = TEXT("没保存成功：") + FString::Join(Failed, TEXT("；"));
			return false;
		}
		return true;
	});

	B.AddOp(TEXT("asset.revert"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		TArray<UPackage*> Pkgs;
		for (const FString& Name : PackagesArg(A))
		{
			if (UPackage* Pkg = PackageOf(Name))
			{
				Pkgs.Add(Pkg);
				DirtyPackages().Remove(Pkg->GetName());
			}
		}
		if (Pkgs.Num() == 0)
		{
			return true;
		}
		// 从磁盘重新加载：内存里没保存的改动丢掉
		for (UPackage* Pkg : Pkgs)
		{
			Pkg->SetDirtyFlag(false);
		}
		FText ErrorText;
		if (!UPackageTools::ReloadPackages(Pkgs, ErrorText, EReloadPackagesInteractionMode::AssumePositive))
		{
			Error = ErrorText.ToString();
			return false;
		}
		Out->SetNumberField(TEXT("reverted"), Pkgs.Num());
		return true;
	});

	B.AddOp(TEXT("asset.open"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		if (!GEditor || IsRunningCommandlet())
		{
			Error = TEXT("后台编辑器里不能打开资源编辑器");
			return false;
		}
		const FString Path = Str(A, TEXT("path"));
		UObject* Obj = StaticFindObject(UObject::StaticClass(), nullptr, *Path);
		if (!Obj)
		{
			Obj = LoadObject<UObject>(nullptr, *Path);
		}
		if (!Obj)
		{
			Error = FString::Printf(TEXT("找不到资源 %s"), *Path);
			return false;
		}
		GEditor->GetEditorSubsystem<UAssetEditorSubsystem>()->OpenEditorForAsset(Obj);
		return true;
	});

	// ---------- PIE ----------
	B.AddOp(TEXT("pie"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		if (!GEditor || IsRunningCommandlet())
		{
			Error = TEXT("只有编辑器能开 PIE");
			return false;
		}
		const FString Action = Str(A, TEXT("action"), TEXT("status"));
		if (Action == TEXT("start"))
		{
			if (GEditor->PlayWorld)
			{
				Error = TEXT("PIE 已经在运行");
				return false;
			}
			FRequestPlaySessionParams Params;
			ULevelEditorPlaySettings* Settings = DuplicateObject(GetDefault<ULevelEditorPlaySettings>(), GetTransientPackage());
			const int32 Players = FMath::Clamp(Int(A, TEXT("players"), 1), 1, 8);
			Settings->SetPlayNumberOfClients(Players);
			const FString Net = Str(A, TEXT("netMode"), TEXT("standalone"));
			Settings->SetPlayNetMode(Net == TEXT("listen") ? EPlayNetMode::PIE_ListenServer : Net == TEXT("client") ? EPlayNetMode::PIE_Client : EPlayNetMode::PIE_Standalone);
			Params.EditorPlaySettings = Settings;
			Params.WorldType = EPlaySessionWorldType::PlayInEditor;
			GEditor->RequestPlaySession(Params);
		}
		else if (Action == TEXT("stop"))
		{
			GEditor->RequestEndPlayMap();
		}
		Out->SetBoolField(TEXT("running"), GEditor->PlayWorld != nullptr || GEditor->IsPlaySessionRequestQueued());
		return true;
	});

	// ---------- Live Coding ----------
	B.AddOp(TEXT("livecoding"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		// 和 UEToVS 共用一份触发逻辑；编译结果用 livecoding.status 查，或订阅推送通道的 liveCoding 事件
		if (IsRunningCommandlet())
		{
			Error = TEXT("Live Coding 不可用（后台编辑器里没有）");
			return false;
		}
		FString Message;
		if (!FUELinkStream::Get().TriggerLiveCoding(Message, Error))
		{
			return false;
		}
		Out->SetBoolField(TEXT("started"), Message.IsEmpty());
		if (!Message.IsEmpty())
		{
			Out->SetStringField(TEXT("message"), Message);
		}
		return true;
	});

	// ---------- 编辑器视口截图：编辑器世界用关卡编辑器当前的视口，其他世界交给运行时的做法 ----------
	B.AddOp(TEXT("screenshot"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UWorld* World = FAgentPanelBridge::ResolveWorld(Str(A, TEXT("world")), Error);
		if (!World)
		{
			return false;
		}
		FViewport* VP = nullptr;
		if (World->WorldType == EWorldType::Editor)
		{
			VP = GEditor ? GEditor->GetActiveViewport() : nullptr;
		}
		else if (UGameViewportClient* VC = World->GetGameViewport())
		{
			VP = VC->Viewport;
		}
		Out->SetStringField(TEXT("world"), FAgentPanelBridge::WorldId(World));
		return AgentPanelCaptureViewport(VP, Int(A, TEXT("maxWidth"), 1280), Out, Error);
	});
}

class FAgentPanelBridgeEditorModule : public IModuleInterface
{
public:
	virtual void StartupModule() override
	{
		// 运行时模块的服务在引擎初始化后起；编辑器的操作先登记好，起服务时一起可用
		RegisterEditorOps();
		RegisterAssetOps();
		RegisterBlueprintReadOps();
		RegisterBlueprintEditOps();
		RegisterTraceOps();
		RegisterTextifyOps();
		RegisterStreamOps();
		// 推送通道等引擎初始化完再起（和运行时模块的 HTTP 服务同一时机）；commandlet、游戏进程里 Start 自己会跳过
		PostInit = UELINK_EDITOR_POST_ENGINE_INIT.AddLambda([]() { FUELinkStream::Get().Start(); });
	}

	virtual void ShutdownModule() override
	{
		UELINK_EDITOR_POST_ENGINE_INIT.Remove(PostInit);
		FUELinkStream::Get().Stop();
	}

private:
	FDelegateHandle PostInit;
};

IMPLEMENT_MODULE(FAgentPanelBridgeEditorModule, UELinkEditor)
