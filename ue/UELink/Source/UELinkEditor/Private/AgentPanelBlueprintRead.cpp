// 蓝图的只读操作，由粗到细三级：bp.outline（大纲）→ bp.graph（一张图：伪代码或节点 JSON）→ bp.graph 带 node（一个节点的全部引脚）；
// 另有 bp.find 跨蓝图搜节点。蓝图资源本身的路径在这里不换成类默认对象。
#include "AgentPanelEditorShared.h"

#include "AssetRegistry/AssetData.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetRegistry/IAssetRegistry.h"
#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphNode.h"
#include "EdGraph/EdGraphPin.h"
#include "EdGraphNode_Comment.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/SCS_Node.h"
#include "Engine/SimpleConstructionScript.h"
#include "Engine/TimelineTemplate.h"
#include "K2Node_CallFunction.h"
#include "K2Node_CustomEvent.h"
#include "K2Node_DynamicCast.h"
#include "K2Node_Event.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "K2Node_IfThenElse.h"
#include "K2Node_MacroInstance.h"
#include "K2Node_Variable.h"
#include "K2Node_VariableGet.h"
#include "K2Node_VariableSet.h"

using namespace AgentPanel;
using namespace AgentPanelEd;

namespace
{
	/** 节点的种类（给 Agent 分辨用，和 add_node 的 kind 对应） */
	FString KindOf(const UEdGraphNode* N)
	{
		if (N->IsA<UEdGraphNode_Comment>())
		{
			return TEXT("comment");
		}
		if (N->IsA<UK2Node_CustomEvent>())
		{
			return TEXT("custom_event");
		}
		if (N->IsA<UK2Node_Event>())
		{
			return TEXT("event");
		}
		if (N->IsA<UK2Node_FunctionEntry>())
		{
			return TEXT("entry");
		}
		if (N->IsA<UK2Node_FunctionResult>())
		{
			return TEXT("return");
		}
		if (N->IsA<UK2Node_CallFunction>())
		{
			return TEXT("call");
		}
		if (N->IsA<UK2Node_VariableGet>())
		{
			return TEXT("get");
		}
		if (N->IsA<UK2Node_VariableSet>())
		{
			return TEXT("set");
		}
		if (N->IsA<UK2Node_IfThenElse>())
		{
			return TEXT("branch");
		}
		if (N->IsA<UK2Node_DynamicCast>())
		{
			return TEXT("cast");
		}
		if (N->IsA<UK2Node_MacroInstance>())
		{
			return TEXT("macro");
		}
		return TEXT("other");
	}

	/** 节点引用的成员：调用的函数、读写的变量（搜节点时按它比对，比标题稳定） */
	FString MemberOf(const UEdGraphNode* N)
	{
		if (const UK2Node_CallFunction* Call = Cast<UK2Node_CallFunction>(N))
		{
			const UClass* Owner = Call->FunctionReference.GetMemberParentClass(Call->GetBlueprintClassFromNode());
			return (Owner ? Owner->GetName() + TEXT(".") : FString()) + Call->GetFunctionName().ToString();
		}
		if (const UK2Node_Variable* Var = Cast<UK2Node_Variable>(N))
		{
			return Var->GetVarName().ToString();
		}
		if (const UK2Node_Event* Event = Cast<UK2Node_Event>(N))
		{
			return Event->GetFunctionName().ToString();
		}
		if (const UK2Node_DynamicCast* CastNode = Cast<UK2Node_DynamicCast>(N))
		{
			return CastNode->TargetType ? CastNode->TargetType->GetName() : FString();
		}
		if (const UK2Node_MacroInstance* Macro = Cast<UK2Node_MacroInstance>(N))
		{
			return Macro->GetMacroGraph() ? Macro->GetMacroGraph()->GetName() : FString();
		}
		return FString();
	}

	TSharedPtr<FJsonObject> PinJson(const UEdGraphPin* P)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetStringField(TEXT("name"), P->PinName.ToString());
		O->SetStringField(TEXT("dir"), P->Direction == EGPD_Input ? TEXT("in") : TEXT("out"));
		O->SetStringField(TEXT("type"), IsExec(P) ? TEXT("exec") : PinTypeText(P->PinType));
		if (P->Direction == EGPD_Input && !IsExec(P) && P->LinkedTo.Num() == 0)
		{
			const FString Default = P->DefaultObject ? P->DefaultObject->GetPathName() : !P->DefaultTextValue.IsEmpty() ? P->DefaultTextValue.ToString() : P->GetDefaultAsString();
			if (!Default.IsEmpty())
			{
				O->SetStringField(TEXT("default"), Default);
			}
		}
		if (P->LinkedTo.Num())
		{
			TArray<TSharedPtr<FJsonValue>> Links;
			for (const UEdGraphPin* L : P->LinkedTo)
			{
				if (L && L->GetOwningNodeUnchecked())
				{
					Links.Add(JStr(NodeId(L->GetOwningNodeUnchecked()) + TEXT(".") + L->PinName.ToString()));
				}
			}
			O->SetArrayField(TEXT("to"), Links);
		}
		return O;
	}

	/** bFull：单个节点的详情，带完整 guid、位置、隐藏引脚；列表里只给看得见的引脚 */
	TSharedPtr<FJsonObject> NodeJson(const UEdGraphNode* N, bool bFull)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetStringField(TEXT("id"), NodeId(N));
		O->SetStringField(TEXT("kind"), KindOf(N));
		O->SetStringField(TEXT("title"), NodeTitle(N));
		const FString Member = MemberOf(N);
		if (!Member.IsEmpty())
		{
			O->SetStringField(TEXT("member"), Member);
		}
		if (!N->NodeComment.IsEmpty())
		{
			O->SetStringField(TEXT("comment"), N->NodeComment);
		}
		if (N->bHasCompilerMessage)
		{
			O->SetStringField(TEXT("error"), N->ErrorMsg);
		}
		if (bFull)
		{
			O->SetStringField(TEXT("guid"), N->NodeGuid.ToString(EGuidFormats::Digits).ToLower());
			O->SetStringField(TEXT("class"), N->GetClass()->GetName());
			O->SetNumberField(TEXT("x"), N->NodePosX);
			O->SetNumberField(TEXT("y"), N->NodePosY);
			O->SetStringField(TEXT("tooltip"), N->GetTooltipText().ToString().Left(400));
		}
		TArray<TSharedPtr<FJsonValue>> Pins;
		for (const UEdGraphPin* P : N->Pins)
		{
			if (P && (bFull || !P->bHidden || P->LinkedTo.Num()))
			{
				Pins.Add(JObj(PinJson(P)));
			}
		}
		O->SetArrayField(TEXT("pins"), Pins);
		return O;
	}

	/** 入口 / 返回节点上的参数表 */
	TArray<TSharedPtr<FJsonValue>> PinParams(const UEdGraphNode* N, EEdGraphPinDirection Dir)
	{
		TArray<TSharedPtr<FJsonValue>> Out;
		for (const UEdGraphPin* P : N->Pins)
		{
			if (P && P->Direction == Dir && !IsExec(P) && !P->bHidden && P->PinName != UEdGraphSchema_K2::PN_Self)
			{
				TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
				O->SetStringField(TEXT("name"), P->PinName.ToString());
				O->SetStringField(TEXT("type"), PinTypeText(P->PinType));
				Out.Add(JObj(O));
			}
		}
		return Out;
	}

	TSharedPtr<FJsonObject> GraphBrief(UBlueprint* BP, UEdGraph* G)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetStringField(TEXT("name"), G->GetName());
		O->SetStringField(TEXT("kind"), GraphKind(BP, G));
		O->SetNumberField(TEXT("nodes"), G->Nodes.Num());
		TArray<TSharedPtr<FJsonValue>> Events;
		for (const UEdGraphNode* N : G->Nodes)
		{
			if (!N)
			{
				continue;
			}
			if (N->IsA<UK2Node_FunctionEntry>())
			{
				O->SetArrayField(TEXT("inputs"), PinParams(N, EGPD_Output));
				if (const UK2Node_FunctionEntry* Entry = Cast<UK2Node_FunctionEntry>(N))
				{
					if (Entry->GetExtraFlags() & FUNC_BlueprintPure)
					{
						O->SetBoolField(TEXT("pure"), true);
					}
				}
			}
			else if (N->IsA<UK2Node_FunctionResult>())
			{
				O->SetArrayField(TEXT("outputs"), PinParams(N, EGPD_Input));
			}
			else if (const UK2Node_Event* Event = Cast<UK2Node_Event>(N))
			{
				TSharedPtr<FJsonObject> E = MakeShared<FJsonObject>();
				E->SetStringField(TEXT("name"), Event->GetFunctionName().ToString());
				E->SetStringField(TEXT("node"), NodeId(N));
				E->SetBoolField(TEXT("custom"), N->IsA<UK2Node_CustomEvent>());
				Events.Add(JObj(E));
			}
		}
		if (Events.Num())
		{
			O->SetArrayField(TEXT("events"), Events);
		}
		return O;
	}

	TSharedPtr<FJsonObject> ComponentJson(const USCS_Node* Node)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetStringField(TEXT("name"), Node->GetVariableName().ToString());
		O->SetStringField(TEXT("class"), Node->ComponentClass ? Node->ComponentClass->GetName() : FString());
		if (Node->ComponentTemplate)
		{
			// 组件模板的路径：用 ue_get / ue_set 读改它的属性
			O->SetStringField(TEXT("template"), Node->ComponentTemplate->GetPathName());
		}
		TArray<TSharedPtr<FJsonValue>> Children;
		for (const USCS_Node* Child : Node->GetChildNodes())
		{
			if (Child)
			{
				Children.Add(JObj(ComponentJson(Child)));
			}
		}
		if (Children.Num())
		{
			O->SetArrayField(TEXT("children"), Children);
		}
		return O;
	}
}

void AgentPanelEd::RegisterBlueprintReadOps()
{
	FAgentPanelBridge& B = FAgentPanelBridge::Get();

	// ---------- 大纲 ----------
	B.AddOp(TEXT("bp.outline"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UBlueprint* BP = LoadBlueprint(Str(A, TEXT("path")), Error);
		if (!BP)
		{
			return false;
		}
		Out->SetStringField(TEXT("path"), BP->GetPathName());
		Out->SetStringField(TEXT("name"), BP->GetName());
		Out->SetStringField(TEXT("blueprintClass"), BP->GetClass()->GetName());
		Out->SetStringField(TEXT("parent"), BP->ParentClass ? BP->ParentClass->GetPathName() : FString());
		Out->SetStringField(TEXT("generatedClass"), BP->GeneratedClass ? BP->GeneratedClass->GetPathName() : FString());
		Out->SetStringField(TEXT("status"), StatusText(BP));
		Out->SetBoolField(TEXT("dirty"), BP->GetOutermost()->IsDirty());

		TArray<TSharedPtr<FJsonValue>> Interfaces;
		for (const FBPInterfaceDescription& I : BP->ImplementedInterfaces)
		{
			if (I.Interface)
			{
				Interfaces.Add(JStr(I.Interface->GetPathName()));
			}
		}
		Out->SetArrayField(TEXT("interfaces"), Interfaces);

		// 变量：定义在 NewVariables 里；默认值从类默认对象上读（定义里的字符串常常是空的）
		UObject* CDO = BP->GeneratedClass ? BP->GeneratedClass->GetDefaultObject(false) : nullptr;
		TArray<TSharedPtr<FJsonValue>> Vars;
		TArray<TSharedPtr<FJsonValue>> Dispatchers;
		for (const FBPVariableDescription& V : BP->NewVariables)
		{
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("name"), V.VarName.ToString());
			O->SetStringField(TEXT("type"), PinTypeText(V.VarType));
			if (V.VarType.PinCategory == UEdGraphSchema_K2::PC_MCDelegate)
			{
				Dispatchers.Add(JObj(O));
				continue;
			}
			if (!V.Category.IsEmpty())
			{
				O->SetStringField(TEXT("category"), V.Category.ToString());
			}
			if (V.PropertyFlags & CPF_Net)
			{
				O->SetStringField(TEXT("replication"), (V.PropertyFlags & CPF_RepNotify) ? TEXT("repNotify") : TEXT("replicated"));
			}
			O->SetBoolField(TEXT("instanceEditable"), !(V.PropertyFlags & CPF_DisableEditOnInstance));
			if (V.PropertyFlags & CPF_ExposeOnSpawn)
			{
				O->SetBoolField(TEXT("exposeOnSpawn"), true);
			}
			FProperty* Prop = CDO ? FindFProperty<FProperty>(CDO->GetClass(), V.VarName) : nullptr;
			if (Prop)
			{
				O->SetField(TEXT("default"), ToJson(Prop, Prop->ContainerPtrToValuePtr<void>(CDO), 300));
			}
			else if (!V.DefaultValue.IsEmpty())
			{
				O->SetStringField(TEXT("default"), V.DefaultValue);
			}
			Vars.Add(JObj(O));
		}
		Out->SetArrayField(TEXT("variables"), Vars);
		Out->SetArrayField(TEXT("dispatchers"), Dispatchers);

		TArray<TSharedPtr<FJsonValue>> Components;
		if (BP->SimpleConstructionScript)
		{
			for (const USCS_Node* Root : BP->SimpleConstructionScript->GetRootNodes())
			{
				if (Root)
				{
					Components.Add(JObj(ComponentJson(Root)));
				}
			}
		}
		Out->SetArrayField(TEXT("components"), Components);

		TArray<TSharedPtr<FJsonValue>> Graphs;
		for (UEdGraph* G : AllGraphs(BP))
		{
			if (G)
			{
				Graphs.Add(JObj(GraphBrief(BP, G)));
			}
		}
		Out->SetArrayField(TEXT("graphs"), Graphs);

		TArray<TSharedPtr<FJsonValue>> Timelines;
		for (const UTimelineTemplate* T : BP->Timelines)
		{
			if (T)
			{
				Timelines.Add(JStr(T->GetVariableName().ToString()));
			}
		}
		Out->SetArrayField(TEXT("timelines"), Timelines);

		// 上次编译留在节点上的错误和警告
		Out->SetObjectField(TEXT("compile"), CompileReport(BP, false));
		return true;
	});

	// ---------- 一张图 / 一个节点 ----------
	B.AddOp(TEXT("bp.graph"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UBlueprint* BP = LoadBlueprint(Str(A, TEXT("path")), Error);
		if (!BP)
		{
			return false;
		}
		const FString GraphName = Str(A, TEXT("graph"));
		const FString NodeArg = Str(A, TEXT("node"));
		if (!NodeArg.IsEmpty())
		{
			UEdGraph* Scope = nullptr;
			if (!GraphName.IsEmpty())
			{
				Scope = FindGraph(BP, GraphName, Error);
				if (!Scope)
				{
					return false;
				}
			}
			UEdGraphNode* N = FindNode(BP, Scope, NodeArg, Error);
			if (!N)
			{
				return false;
			}
			Out->SetStringField(TEXT("graph"), N->GetGraph() ? N->GetGraph()->GetName() : FString());
			Out->SetObjectField(TEXT("node"), NodeJson(N, true));
			return true;
		}

		UEdGraph* G = FindGraph(BP, GraphName, Error);
		if (!G)
		{
			return false;
		}
		Out->SetStringField(TEXT("graph"), G->GetName());
		Out->SetStringField(TEXT("kind"), GraphKind(BP, G));
		Out->SetNumberField(TEXT("total"), G->Nodes.Num());
		if (Str(A, TEXT("format"), TEXT("pseudo")) != TEXT("json"))
		{
			bool bMore = false;
			Out->SetStringField(TEXT("pseudo"), GraphPseudo(BP, G, FMath::Clamp(Int(A, TEXT("maxLines"), 400), 20, 4000), bMore));
			Out->SetBoolField(TEXT("more"), bMore);
			return true;
		}

		// JSON：整张图分页，或只取某个节点周围几跳（around + hops）
		TArray<const UEdGraphNode*> Pick;
		const FString Around = Str(A, TEXT("around"));
		if (!Around.IsEmpty())
		{
			UEdGraphNode* Center = FindNode(BP, G, Around, Error);
			if (!Center)
			{
				return false;
			}
			const int32 Hops = FMath::Clamp(Int(A, TEXT("hops"), 2), 0, 6);
			TSet<const UEdGraphNode*> Seen;
			TArray<const UEdGraphNode*> Layer;
			Layer.Add(Center);
			Seen.Add(Center);
			Pick.Add(Center);
			for (int32 i = 0; i < Hops; ++i)
			{
				TArray<const UEdGraphNode*> Next;
				for (const UEdGraphNode* N : Layer)
				{
					for (const UEdGraphPin* P : N->Pins)
					{
						if (!P)
						{
							continue;
						}
						for (const UEdGraphPin* L : P->LinkedTo)
						{
							const UEdGraphNode* Other = L ? L->GetOwningNodeUnchecked() : nullptr;
							if (Other && !Seen.Contains(Other))
							{
								Seen.Add(Other);
								Next.Add(Other);
								Pick.Add(Other);
							}
						}
					}
				}
				Layer = MoveTemp(Next);
			}
		}
		else
		{
			for (const UEdGraphNode* N : G->Nodes)
			{
				if (N)
				{
					Pick.Add(N);
				}
			}
		}
		const int32 Offset = FMath::Max(Int(A, TEXT("offset"), 0), 0);
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 40), 1, 300);
		TArray<TSharedPtr<FJsonValue>> Nodes;
		for (int32 i = Offset; i < Pick.Num() && Nodes.Num() < Limit; ++i)
		{
			Nodes.Add(JObj(NodeJson(Pick[i], false)));
		}
		Out->SetArrayField(TEXT("nodes"), Nodes);
		Out->SetNumberField(TEXT("matched"), Pick.Num());
		if (Offset + Nodes.Num() < Pick.Num())
		{
			Out->SetNumberField(TEXT("next"), Offset + Nodes.Num());
		}
		return true;
	});

	// ---------- 跨蓝图搜节点：标题里有这段文字，或调用的函数 / 读写的变量 / 转换的类对得上 ----------
	// 不走编辑器的 Find in Blueprints 索引（它是异步的，后台编辑器里不一定建好）：逐个加载目录下的蓝图来扫，限数量和时间
	B.AddOp(TEXT("bp.find"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const FString Query = Str(A, TEXT("query")).TrimStartAndEnd();
		if (Query.IsEmpty())
		{
			Error = TEXT("要给 query（函数名、变量名、类名，或节点标题里的文字）");
			return false;
		}
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 50), 1, 500);
		const int32 MaxLoad = FMath::Clamp(Int(A, TEXT("maxLoad"), 200), 1, 2000);
		const double Deadline = FPlatformTime::Seconds() + FMath::Clamp(Int(A, TEXT("seconds"), 20), 1, 120);

		TArray<FSoftObjectPath> Targets;
		const FString One = Str(A, TEXT("blueprint"));
		if (!One.IsEmpty())
		{
			UBlueprint* BP = LoadBlueprint(One, Error);
			if (!BP)
			{
				return false;
			}
			Targets.Add(FSoftObjectPath(BP));
		}
		else
		{
			IAssetRegistry& AR = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
			FARFilter Filter;
			Filter.bRecursivePaths = true;
			Filter.bRecursiveClasses = true;
			Filter.PackagePaths.Add(FName(*Str(A, TEXT("dir"), TEXT("/Game"))));
			Filter.ClassPaths.Add(UBlueprint::StaticClass()->GetClassPathName());
			TArray<FAssetData> Found;
			AR.GetAssets(Filter, Found);
			// 已经在内存里的排前面：不用加载，先扫完
			Found.Sort([](const FAssetData& L, const FAssetData& R)
			{
				return L.IsAssetLoaded() && !R.IsAssetLoaded();
			});
			for (const FAssetData& D : Found)
			{
				Targets.Add(D.GetSoftObjectPath());
			}
		}

		TArray<TSharedPtr<FJsonValue>> Items;
		int32 Scanned = 0;
		int32 Matched = 0;
		bool bStopped = false;
		for (const FSoftObjectPath& Path : Targets)
		{
			if (Scanned >= MaxLoad || FPlatformTime::Seconds() > Deadline)
			{
				bStopped = true;
				break;
			}
			UBlueprint* BP = Cast<UBlueprint>(Path.TryLoad());
			++Scanned;
			if (!BP)
			{
				continue;
			}
			for (UEdGraph* G : AllGraphs(BP))
			{
				if (!G)
				{
					continue;
				}
				for (const UEdGraphNode* N : G->Nodes)
				{
					if (!N)
					{
						continue;
					}
					const FString Member = MemberOf(N);
					const bool bHit = Member.Equals(Query, ESearchCase::IgnoreCase) || Member.EndsWith(TEXT(".") + Query, ESearchCase::IgnoreCase) || NodeTitle(N).Contains(Query, ESearchCase::IgnoreCase);
					if (!bHit)
					{
						continue;
					}
					++Matched;
					if (Items.Num() >= Limit)
					{
						continue;
					}
					TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
					O->SetStringField(TEXT("blueprint"), BP->GetPathName());
					O->SetStringField(TEXT("graph"), G->GetName());
					O->SetStringField(TEXT("node"), NodeId(N));
					O->SetStringField(TEXT("kind"), KindOf(N));
					O->SetStringField(TEXT("title"), NodeTitle(N));
					if (!Member.IsEmpty())
					{
						O->SetStringField(TEXT("member"), Member);
					}
					Items.Add(JObj(O));
				}
			}
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetNumberField(TEXT("matched"), Matched);
		Out->SetNumberField(TEXT("scanned"), Scanned);
		Out->SetNumberField(TEXT("blueprints"), Targets.Num());
		// 没扫完时说清楚：结果不全，缩小 dir 或加大 maxLoad / seconds 再搜
		Out->SetBoolField(TEXT("partial"), bStopped);
		return true;
	});
}
