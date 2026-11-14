// 编辑器模块共用的实现：资源寻址、事务、图表节点查找、编译回读。说明见 AgentPanelEditorShared.h。
#include "AgentPanelEditorShared.h"

#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphNode.h"
#include "EdGraph/EdGraphPin.h"
#include "EdGraphSchema_K2.h"
#include "Editor.h"
#include "Editor/Transactor.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "FileHelpers.h"
#include "HAL/FileManager.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/CompilerResultsLog.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Logging/TokenizedMessage.h"
#include "Misc/PackageName.h"
#include "UObject/Package.h"
#include "UObject/UObjectHash.h"

using namespace AgentPanel;

namespace AgentPanelEd
{
	void NoteDirty(UObject* Obj)
	{
		Obj->MarkPackageDirty();
		DirtyPackages().Add(Obj->GetOutermost()->GetName());
	}

	FString PackageFile(UPackage* Pkg)
	{
		const FString Ext = Pkg->ContainsMap() ? FPackageName::GetMapPackageExtension() : FPackageName::GetAssetPackageExtension();
		return FPaths::ConvertRelativePathToFull(FPackageName::LongPackageNameToFilename(Pkg->GetName(), Ext));
	}

	TSet<UPackage*> DirtyNow()
	{
		TArray<UPackage*> List;
		FEditorFileUtils::GetDirtyContentPackages(List);
		return TSet<UPackage*>(List);
	}

	void TrackNewDirty(const TSet<UPackage*>& Before, TSharedPtr<FJsonObject>& Out)
	{
		TArray<TSharedPtr<FJsonValue>> Names;
		for (UPackage* Pkg : DirtyNow())
		{
			if (Pkg && !Before.Contains(Pkg))
			{
				DirtyPackages().Add(Pkg->GetName());
				Names.Add(JStr(Pkg->GetName()));
			}
		}
		if (Names.Num())
		{
			Out->SetArrayField(TEXT("dirtied"), Names);
		}
	}

	UClass* FindClassByName(const FString& Name)
	{
		if (Name.IsEmpty())
		{
			return nullptr;
		}
		if (Name.StartsWith(TEXT("/")))
		{
			if (UClass* C = FindObject<UClass>(nullptr, *Name))
			{
				return C;
			}
			FString Ignored;
			UObject* Obj = FindObjectByPath(Name, true, Ignored, true);
			if (UBlueprint* BP = Cast<UBlueprint>(Obj))
			{
				return BP->GeneratedClass;
			}
			return Cast<UClass>(Obj);
		}
		UClass* C = FindFirstObject<UClass>(*Name, EFindFirstObjectOptions::NativeFirst);
		if (!C && !Name.EndsWith(TEXT("_C")))
		{
			C = FindFirstObject<UClass>(*(Name + TEXT("_C")), EFindFirstObjectOptions::None);
		}
		// 反射里的类名不带 U / A 前缀，Agent 常照 C++ 的写法带上
		if (!C && Name.Len() > 1 && (Name[0] == TEXT('U') || Name[0] == TEXT('A')) && FChar::IsUpper(Name[1]))
		{
			C = FindFirstObject<UClass>(*Name.Mid(1), EFindFirstObjectOptions::NativeFirst);
		}
		return C;
	}

	UScriptStruct* FindStructByName(const FString& Name)
	{
		if (Name.StartsWith(TEXT("/")))
		{
			FString Ignored;
			return Cast<UScriptStruct>(FindObjectByPath(Name, true, Ignored, true));
		}
		UScriptStruct* S = FindFirstObject<UScriptStruct>(*Name, EFindFirstObjectOptions::NativeFirst);
		if (!S && Name.Len() > 1 && Name[0] == TEXT('F') && FChar::IsUpper(Name[1]))
		{
			S = FindFirstObject<UScriptStruct>(*Name.Mid(1), EFindFirstObjectOptions::NativeFirst);
		}
		return S;
	}

	UEnum* FindEnumByName(const FString& Name)
	{
		if (Name.StartsWith(TEXT("/")))
		{
			FString Ignored;
			return Cast<UEnum>(FindObjectByPath(Name, true, Ignored, true));
		}
		return FindFirstObject<UEnum>(*Name, EFindFirstObjectOptions::NativeFirst);
	}

	UObject* LoadAsset(const FString& Path, FString& Error)
	{
		return FindObjectByPath(Path, true, Error, true);
	}

	UBlueprint* LoadBlueprint(const FString& Path, FString& Error)
	{
		UObject* Obj = LoadAsset(Path, Error);
		if (!Obj)
		{
			return nullptr;
		}
		if (UBlueprint* BP = Cast<UBlueprint>(Obj))
		{
			return BP;
		}
		// 给的是生成的类（_C）或类默认对象时，找回蓝图资源
		UClass* Class = Cast<UClass>(Obj);
		if (!Class)
		{
			Class = Obj->GetClass();
		}
		if (UBlueprint* BP = UBlueprint::GetBlueprintFromClass(Class))
		{
			return BP;
		}
		Error = FString::Printf(TEXT("%s 不是蓝图（是 %s）"), *Path, *Obj->GetClass()->GetName());
		return nullptr;
	}

	// ---------- 事务 ----------
	FTx::FTx(const FString& Summary)
	{
		Id = FGuid::NewGuid().ToString(EGuidFormats::Digits).Left(8).ToLower();
		if (GEditor && GEditor->Trans)
		{
			Index = GEditor->BeginTransaction(FText::FromString(FString::Printf(TEXT("AgentPanel %s: %s"), *Id, *Summary.Left(120))));
			bOpen = Index != INDEX_NONE;
		}
	}

	FTx::~FTx()
	{
		if (bOpen)
		{
			GEditor->EndTransaction();
		}
	}

	/** 栈顶（下一次 Ctrl+Z 会撤的那一步）是不是 TxId 这次事务 */
	static bool TopIsTx(const FString& TxId)
	{
		UTransactor* T = GEditor ? ToRawPtr(GEditor->Trans) : nullptr;
		if (!T)
		{
			return false;
		}
		const int32 Top = T->GetQueueLength() - T->GetUndoCount() - 1;
		const FTransaction* Tr = Top >= 0 ? T->GetTransaction(Top) : nullptr;
		return Tr && Tr->GetTitle().ToString().Contains(TxId);
	}

	bool FTx::Finish(bool bKeep, FString& Error)
	{
		if (!bOpen)
		{
			if (!bKeep)
			{
				Error = TEXT("这个进程里没有编辑器事务，没法回滚：改动已经留在内存里，用「放弃改动」丢掉");
				return false;
			}
			return true;
		}
		GEditor->EndTransaction();
		bOpen = false;
		if (bKeep)
		{
			return true;
		}
		// 事务里什么都没记时引擎不会留下它，这时栈顶是用户自己的上一步，不能撤
		if (!TopIsTx(Id))
		{
			return true;
		}
		if (!GEditor->UndoTransaction(false))
		{
			Error = TEXT("回滚没成功：改动可能还留在内存里，用「放弃改动」丢掉");
			return false;
		}
		return true;
	}

	bool UndoTx(const FString& TxId, FString& Error)
	{
		if (TxId.IsEmpty() || !GEditor || !GEditor->Trans)
		{
			Error = TEXT("这个进程里没有可撤回的事务");
			return false;
		}
		if (!TopIsTx(TxId))
		{
			Error = TEXT("这一步不在撤销栈顶：之后编辑器里又有别的修改（或已经撤过、保存后重开过）。先撤回后面的修改，或用「放弃改动」整个丢掉");
			return false;
		}
		if (!GEditor->UndoTransaction(true))
		{
			Error = TEXT("编辑器拒绝了撤销");
			return false;
		}
		return true;
	}

	// ---------- 图表 ----------
	FString NodeId(const UEdGraphNode* Node)
	{
		return Node ? Node->NodeGuid.ToString(EGuidFormats::Digits).ToLower().Left(8) : FString();
	}

	FString NodeTitle(const UEdGraphNode* Node)
	{
		FString S = Node->GetNodeTitle(ENodeTitleType::ListView).ToString();
		S.ReplaceInline(TEXT("\r"), TEXT(""));
		S.ReplaceInline(TEXT("\n"), TEXT(" "));
		return S;
	}

	FString PinTypeText(const FEdGraphPinType& Type)
	{
		return UEdGraphSchema_K2::TypeToText(Type).ToString();
	}

	TArray<UEdGraph*> AllGraphs(UBlueprint* BP)
	{
		TArray<UEdGraph*> Out;
		BP->GetAllGraphs(Out);
		return Out;
	}

	FString GraphKind(UBlueprint* BP, const UEdGraph* Graph)
	{
		if (BP->UbergraphPages.Contains(Graph))
		{
			return TEXT("event");
		}
		if (BP->FunctionGraphs.Contains(Graph))
		{
			return TEXT("function");
		}
		if (BP->MacroGraphs.Contains(Graph))
		{
			return TEXT("macro");
		}
		if (BP->DelegateSignatureGraphs.Contains(Graph))
		{
			return TEXT("delegate");
		}
		for (const FBPInterfaceDescription& I : BP->ImplementedInterfaces)
		{
			if (I.Graphs.Contains(Graph))
			{
				return TEXT("interface");
			}
		}
		return TEXT("sub");
	}

	UEdGraph* FindGraph(UBlueprint* BP, const FString& Name, FString& Error)
	{
		const TArray<UEdGraph*> Graphs = AllGraphs(BP);
		if (Name.IsEmpty())
		{
			if (BP->UbergraphPages.Num())
			{
				return BP->UbergraphPages[0];
			}
			if (Graphs.Num())
			{
				return Graphs[0];
			}
		}
		TArray<FString> Names;
		for (UEdGraph* G : Graphs)
		{
			if (G && G->GetName().Equals(Name, ESearchCase::IgnoreCase))
			{
				return G;
			}
			if (G)
			{
				Names.Add(G->GetName());
			}
		}
		Error = FString::Printf(TEXT("%s 里没有图 %s，有：%s"), *BP->GetName(), *Name, *FString::Join(Names, TEXT("、")));
		return nullptr;
	}

	UEdGraphNode* FindNode(UBlueprint* BP, UEdGraph* Graph, const FString& Id, FString& Error)
	{
		const FString Want = Id.TrimStartAndEnd().Replace(TEXT("#"), TEXT("")).Replace(TEXT("-"), TEXT("")).ToLower();
		if (Want.IsEmpty())
		{
			Error = TEXT("没有给节点 id");
			return nullptr;
		}
		UEdGraphNode* Found = nullptr;
		const TArray<UEdGraph*> Graphs = Graph ? TArray<UEdGraph*>({ Graph }) : AllGraphs(BP);
		for (UEdGraph* G : Graphs)
		{
			if (!G)
			{
				continue;
			}
			for (UEdGraphNode* N : G->Nodes)
			{
				if (N && N->NodeGuid.ToString(EGuidFormats::Digits).ToLower().StartsWith(Want))
				{
					if (Found && Found != N)
					{
						Error = FString::Printf(TEXT("节点 id %s 对上了不止一个节点，写长一点（节点详情里有完整的 guid）"), *Id);
						return nullptr;
					}
					Found = N;
				}
			}
		}
		if (!Found)
		{
			Error = FString::Printf(TEXT("%s%s 里没有节点 %s"), *BP->GetName(), Graph ? *(TEXT(" 的图 ") + Graph->GetName()) : TEXT(""), *Id);
		}
		return Found;
	}

	bool IsExec(const UEdGraphPin* Pin)
	{
		return Pin && Pin->PinType.PinCategory == UEdGraphSchema_K2::PC_Exec;
	}

	UEdGraphPin* FindPin(UEdGraphNode* Node, const FString& Name, FString& Error)
	{
		const FString Want = Name.TrimStartAndEnd();
		if (UEdGraphPin* P = Node->FindPin(FName(*Want)))
		{
			return P;
		}
		for (UEdGraphPin* P : Node->Pins)
		{
			if (P && (P->PinName.ToString().Equals(Want, ESearchCase::IgnoreCase) || P->GetDisplayName().ToString().Equals(Want, ESearchCase::IgnoreCase)))
			{
				return P;
			}
		}
		// 常用别名：exec / then / self / return
		const FString Low = Want.ToLower();
		FName Alias;
		if (Low == TEXT("exec") || Low == TEXT("execute") || Low == TEXT("in"))
		{
			Alias = UEdGraphSchema_K2::PN_Execute;
		}
		else if (Low == TEXT("then") || Low == TEXT("out"))
		{
			Alias = UEdGraphSchema_K2::PN_Then;
		}
		else if (Low == TEXT("self") || Low == TEXT("target"))
		{
			Alias = UEdGraphSchema_K2::PN_Self;
		}
		else if (Low == TEXT("return") || Low == TEXT("returnvalue"))
		{
			Alias = UEdGraphSchema_K2::PN_ReturnValue;
		}
		if (!Alias.IsNone())
		{
			if (UEdGraphPin* P = Node->FindPin(Alias))
			{
				return P;
			}
		}
		// 只有一个执行输入 / 输出时，exec / then 指的就是它（事件节点的输出、宏的入口名字各不相同）
		if (Alias == UEdGraphSchema_K2::PN_Execute || Alias == UEdGraphSchema_K2::PN_Then)
		{
			const EEdGraphPinDirection Dir = Alias == UEdGraphSchema_K2::PN_Execute ? EGPD_Input : EGPD_Output;
			UEdGraphPin* Only = nullptr;
			int32 Count = 0;
			for (UEdGraphPin* P : Node->Pins)
			{
				if (P && !P->bHidden && P->Direction == Dir && IsExec(P))
				{
					Only = P;
					++Count;
				}
			}
			if (Count == 1)
			{
				return Only;
			}
		}
		TArray<FString> Names;
		for (UEdGraphPin* P : Node->Pins)
		{
			if (P && !P->bHidden)
			{
				Names.Add(FString::Printf(TEXT("%s%s"), *P->PinName.ToString(), P->Direction == EGPD_Input ? TEXT("(入)") : TEXT("(出)")));
			}
		}
		Error = FString::Printf(TEXT("节点 %s（%s）上没有引脚 %s，有：%s"), *NodeId(Node), *NodeTitle(Node), *Name, *FString::Join(Names, TEXT("、")));
		return nullptr;
	}

	// ---------- 编译回读 ----------
	FString StatusText(const UBlueprint* BP)
	{
		switch (BP->Status)
		{
		case BS_UpToDate:
			return TEXT("ok");
		case BS_UpToDateWithWarnings:
			return TEXT("warnings");
		case BS_Error:
			return TEXT("error");
		case BS_Dirty:
			return TEXT("dirty");
		case BS_BeingCreated:
			return TEXT("creating");
		default:
			return TEXT("unknown");
		}
	}

	TSharedPtr<FJsonObject> CompileReport(UBlueprint* BP, bool bCompile)
	{
		TSharedPtr<FJsonObject> R = MakeShared<FJsonObject>();
		if (bCompile)
		{
			FCompilerResultsLog Log;
			Log.bSilentMode = true;
			Log.SetSourcePath(BP->GetPathName());
			FKismetEditorUtilities::CompileBlueprint(BP, EBlueprintCompileOptions::SkipGarbageCollection, &Log);
			R->SetNumberField(TEXT("errors"), Log.NumErrors);
			R->SetNumberField(TEXT("warnings"), Log.NumWarnings);
			// 编译器的原话：有些错误不挂在节点上（变量类型失效、父类问题），只在这里
			TArray<TSharedPtr<FJsonValue>> Messages;
			for (const TSharedRef<FTokenizedMessage>& M : Log.Messages)
			{
				const EMessageSeverity::Type Sev = M->GetSeverity();
				if (Sev == EMessageSeverity::Info || Messages.Num() >= 60)
				{
					continue;
				}
				TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
				O->SetStringField(TEXT("severity"), Sev == EMessageSeverity::Error ? TEXT("error") : TEXT("warning"));
				O->SetStringField(TEXT("text"), M->ToText().ToString());
				Messages.Add(JObj(O));
			}
			R->SetArrayField(TEXT("messages"), Messages);
		}
		R->SetStringField(TEXT("status"), StatusText(BP));

		// 挂在节点上的错误和警告：带图名和节点 id，Agent 按 id 接着修
		TArray<TSharedPtr<FJsonValue>> Nodes;
		for (UEdGraph* G : AllGraphs(BP))
		{
			if (!G)
			{
				continue;
			}
			for (UEdGraphNode* N : G->Nodes)
			{
				if (!N || !N->bHasCompilerMessage || Nodes.Num() >= 100)
				{
					continue;
				}
				TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
				O->SetStringField(TEXT("graph"), G->GetName());
				O->SetStringField(TEXT("node"), NodeId(N));
				O->SetStringField(TEXT("title"), NodeTitle(N));
				O->SetStringField(TEXT("severity"), N->ErrorType <= static_cast<int32>(EMessageSeverity::Error) ? TEXT("error") : TEXT("warning"));
				O->SetStringField(TEXT("text"), N->ErrorMsg);
				Nodes.Add(JObj(O));
			}
		}
		R->SetArrayField(TEXT("nodes"), Nodes);

		if (bCompile)
		{
			// 受影响的蓝图：子类和用到它的蓝图，编译管理器已经连带重编了，这里只报它们现在的状态
			TSet<UBlueprint*> Seen;
			TArray<TSharedPtr<FJsonValue>> Affected;
			const auto Add = [&](UBlueprint* Other, const TCHAR* Why)
			{
				if (!Other || Other == BP || Seen.Contains(Other) || Affected.Num() >= 40)
				{
					return;
				}
				Seen.Add(Other);
				TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
				O->SetStringField(TEXT("path"), Other->GetPathName());
				O->SetStringField(TEXT("why"), Why);
				O->SetStringField(TEXT("status"), StatusText(Other));
				Affected.Add(JObj(O));
			};
			if (BP->GeneratedClass)
			{
				TArray<UClass*> Derived;
				GetDerivedClasses(BP->GeneratedClass, Derived, true);
				for (UClass* C : Derived)
				{
					Add(UBlueprint::GetBlueprintFromClass(C), TEXT("child"));
				}
			}
			TArray<UBlueprint*> Dependents;
			FBlueprintEditorUtils::GetDependentBlueprints(BP, Dependents);
			for (UBlueprint* D : Dependents)
			{
				Add(D, TEXT("user"));
			}
			R->SetArrayField(TEXT("affected"), Affected);
		}
		return R;
	}
}
