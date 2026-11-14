// 蓝图写入：bp.node_search（能放哪些节点，和编辑器右键菜单同一个来源）、bp.edit（操作列表，整批在一个事务里）、bp.compile。
// 不提供整张图替换：整图替换会丢掉没读到的部分，一处写错全图报废（设计稿「蓝图读写 · 写」）。
// 任何一步失败整批取消；dryRun 照常执行和编译，拿到结果后取消，不留改动。
#include "AgentPanelCompat.h"
#include "AgentPanelEditorShared.h"

#include "BlueprintActionDatabase.h"
#include "BlueprintFunctionNodeSpawner.h"
#include "BlueprintNodeBinder.h"
#include "BlueprintNodeSpawner.h"
#include "Components/ActorComponent.h"
#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphNode.h"
#include "EdGraph/EdGraphPin.h"
#include "EdGraphNode_Comment.h"
#include "EdGraphSchema_K2.h"
#include "Editor.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/SCS_Node.h"
#include "Engine/SimpleConstructionScript.h"
#include "K2Node_CallFunction.h"
#include "K2Node_CustomEvent.h"
#include "K2Node_DynamicCast.h"
#include "K2Node_Event.h"
#include "K2Node_ExecutionSequence.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "K2Node_IfThenElse.h"
#include "K2Node_MacroInstance.h"
#include "K2Node_Self.h"
#include "K2Node_VariableGet.h"
#include "K2Node_VariableSet.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "UObject/Interface.h"
#include "UObject/Package.h"

using namespace AgentPanel;
using namespace AgentPanelEd;

namespace
{
	// ---------- 类型：Agent 写的类型文字 → 引脚类型 ----------
	bool ParseTerminal(const FString& Raw, FEdGraphPinType& T, FString& Error)
	{
		const FString S = Raw.TrimStartAndEnd();
		const FString L = S.ToLower();
		if (L == TEXT("bool") || L == TEXT("boolean"))
		{
			T.PinCategory = UEdGraphSchema_K2::PC_Boolean;
		}
		else if (L == TEXT("int") || L == TEXT("int32") || L == TEXT("integer"))
		{
			T.PinCategory = UEdGraphSchema_K2::PC_Int;
		}
		else if (L == TEXT("int64"))
		{
			T.PinCategory = UEdGraphSchema_K2::PC_Int64;
		}
		else if (L == TEXT("float") || L == TEXT("double") || L == TEXT("real"))
		{
			// 蓝图里的浮点数统一是 real，子类别区分单精度 / 双精度；蓝图变量用双精度
			T.PinCategory = UEdGraphSchema_K2::PC_Real;
			T.PinSubCategory = UEdGraphSchema_K2::PC_Double;
		}
		else if (L == TEXT("byte") || L == TEXT("uint8"))
		{
			T.PinCategory = UEdGraphSchema_K2::PC_Byte;
		}
		else if (L == TEXT("name") || L == TEXT("fname"))
		{
			T.PinCategory = UEdGraphSchema_K2::PC_Name;
		}
		else if (L == TEXT("string") || L == TEXT("fstring"))
		{
			T.PinCategory = UEdGraphSchema_K2::PC_String;
		}
		else if (L == TEXT("text") || L == TEXT("ftext"))
		{
			T.PinCategory = UEdGraphSchema_K2::PC_Text;
		}
		else
		{
			// class<X> / soft<X> / softclass<X>
			int32 Lt = INDEX_NONE;
			if (S.FindChar(TEXT('<'), Lt) && S.EndsWith(TEXT(">")))
			{
				const FString Wrap = S.Left(Lt).ToLower();
				const FString Inner = S.Mid(Lt + 1, S.Len() - Lt - 2).TrimStartAndEnd();
				UClass* C = FindClassByName(Inner);
				if (!C)
				{
					Error = FString::Printf(TEXT("类型 %s 里找不到类 %s"), *S, *Inner);
					return false;
				}
				if (Wrap == TEXT("class") || Wrap == TEXT("tsubclassof"))
				{
					T.PinCategory = UEdGraphSchema_K2::PC_Class;
				}
				else if (Wrap == TEXT("soft") || Wrap == TEXT("tsoftobjectptr"))
				{
					T.PinCategory = UEdGraphSchema_K2::PC_SoftObject;
				}
				else if (Wrap == TEXT("softclass") || Wrap == TEXT("tsoftclassptr"))
				{
					T.PinCategory = UEdGraphSchema_K2::PC_SoftClass;
				}
				else
				{
					Error = FString::Printf(TEXT("不认识的类型写法 %s"), *S);
					return false;
				}
				T.PinSubCategoryObject = C;
				return true;
			}
			if (UScriptStruct* Struct = FindStructByName(S))
			{
				T.PinCategory = UEdGraphSchema_K2::PC_Struct;
				T.PinSubCategoryObject = Struct;
			}
			else if (UEnum* Enum = FindEnumByName(S))
			{
				T.PinCategory = UEdGraphSchema_K2::PC_Byte;
				T.PinSubCategoryObject = Enum;
			}
			else if (UClass* C = FindClassByName(S))
			{
				if (C->IsChildOf(UInterface::StaticClass()))
				{
					T.PinCategory = UEdGraphSchema_K2::PC_Interface;
				}
				else
				{
					T.PinCategory = UEdGraphSchema_K2::PC_Object;
				}
				T.PinSubCategoryObject = C;
			}
			else
			{
				Error = FString::Printf(TEXT("不认识的类型 %s。能写：bool、int、int64、float、byte、name、string、text、结构体名（Vector、Transform、自定义结构）、枚举名、类名（对象引用）、class<类>、soft<类>，外面可以套 array<>、set<>、map<键,值>"), *S);
				return false;
			}
		}
		return true;
	}

	bool ParseType(const FString& Raw, FEdGraphPinType& T, FString& Error)
	{
		const FString S = Raw.TrimStartAndEnd();
		const FString L = S.ToLower();
		const auto Inner = [&S](int32 PrefixLen)
		{
			return S.Mid(PrefixLen, S.Len() - PrefixLen - 1);
		};
		if ((L.StartsWith(TEXT("array<")) || L.StartsWith(TEXT("tarray<"))) && S.EndsWith(TEXT(">")))
		{
			T.ContainerType = EPinContainerType::Array;
			return ParseTerminal(Inner(L.StartsWith(TEXT("t")) ? 7 : 6), T, Error);
		}
		if ((L.StartsWith(TEXT("set<")) || L.StartsWith(TEXT("tset<"))) && S.EndsWith(TEXT(">")))
		{
			T.ContainerType = EPinContainerType::Set;
			return ParseTerminal(Inner(L.StartsWith(TEXT("t")) ? 5 : 4), T, Error);
		}
		if ((L.StartsWith(TEXT("map<")) || L.StartsWith(TEXT("tmap<"))) && S.EndsWith(TEXT(">")))
		{
			const FString Both = Inner(L.StartsWith(TEXT("t")) ? 5 : 4);
			FString Key;
			FString Value;
			if (!Both.Split(TEXT(","), &Key, &Value))
			{
				Error = FString::Printf(TEXT("map 要写成 map<键类型,值类型>：%s"), *S);
				return false;
			}
			FEdGraphPinType ValueType;
			if (!ParseTerminal(Key, T, Error) || !ParseTerminal(Value, ValueType, Error))
			{
				return false;
			}
			T.ContainerType = EPinContainerType::Map;
			T.PinValueType = FEdGraphTerminalType::FromPinType(ValueType);
			return true;
		}
		return ParseTerminal(S, T, Error);
	}

	FString ValueText(const TSharedPtr<FJsonValue>& V)
	{
		if (!V.IsValid() || V->IsNull())
		{
			return FString();
		}
		FString S;
		if (V->TryGetString(S))
		{
			return S;
		}
		bool bB = false;
		if (V->TryGetBool(bB))
		{
			return bB ? TEXT("true") : TEXT("false");
		}
		double D = 0;
		if (V->TryGetNumber(D))
		{
			return FMath::IsNearlyEqual(D, FMath::RoundToDouble(D)) ? FString::Printf(TEXT("%lld"), static_cast<int64>(D)) : FString::SanitizeFloat(D);
		}
		return FString();
	}

	// ---------- 节点候选：和编辑器右键菜单同一个动作库 ----------
	struct FCandidate
	{
		UBlueprintNodeSpawner* Spawner = nullptr;
		FString Name;
		FString Category;
		FString Owner;
		int32 Score = 0;
	};

	FString SpawnerKey(const UBlueprintNodeSpawner* Sp)
	{
		return Sp->GetSpawnerSignature().AsGuid().ToString(EGuidFormats::Digits).ToLower();
	}

	void SearchSpawners(UBlueprint* BP, UEdGraph* Graph, const FString& Query, TArray<FCandidate>& Out)
	{
		const FString Q = Query.ToLower();
		const FBlueprintActionDatabase::FActionRegistry& Registry = FBlueprintActionDatabase::Get().GetAllActions();
		for (const auto& Entry : Registry)
		{
			for (UBlueprintNodeSpawner* Sp : Entry.Value)
			{
				if (!Sp || !Sp->NodeClass)
				{
					continue;
				}
				const FBlueprintActionUiSpec& Ui = Sp->PrimeDefaultUiSpec(Graph);
				const FString Name = Ui.MenuName.ToString();
				const FString Low = Name.ToLower();
				FString Owner;
				FString FuncName;
				if (const UBlueprintFunctionNodeSpawner* Fn = Cast<UBlueprintFunctionNodeSpawner>(Sp))
				{
					if (const UFunction* F = Fn->GetFunction())
					{
						FuncName = F->GetName().ToLower();
						Owner = F->GetOwnerClass() ? F->GetOwnerClass()->GetName() : FString();
					}
				}
				int32 Score = 0;
				if (Low == Q || FuncName == Q)
				{
					Score = 100;
				}
				else if (Low.StartsWith(Q) || FuncName.StartsWith(Q))
				{
					Score = 60;
				}
				else if (Low.Contains(Q) || FuncName.Contains(Q))
				{
					Score = 30;
				}
				else if (Ui.Keywords.ToString().ToLower().Contains(Q))
				{
					Score = 10;
				}
				if (!Score)
				{
					continue;
				}
				// 这个蓝图自己和父类上的成员排前面
				if (!Owner.IsEmpty() && BP->SkeletonGeneratedClass)
				{
					for (const UClass* C = BP->SkeletonGeneratedClass; C; C = C->GetSuperClass())
					{
						if (C->GetName() == Owner || C->GetName() == TEXT("SKEL_") + Owner)
						{
							Score += 20;
							break;
						}
					}
				}
				FCandidate C;
				C.Spawner = Sp;
				C.Name = Name;
				C.Category = Ui.Category.ToString();
				C.Owner = Owner;
				C.Score = Score;
				Out.Add(C);
			}
		}
		Out.Sort([](const FCandidate& L, const FCandidate& R)
		{
			return L.Score != R.Score ? L.Score > R.Score : L.Name.Len() < R.Name.Len();
		});
	}

	UBlueprintNodeSpawner* FindSpawner(const FString& Key)
	{
		const FBlueprintActionDatabase::FActionRegistry& Registry = FBlueprintActionDatabase::Get().GetAllActions();
		for (const auto& Entry : Registry)
		{
			for (UBlueprintNodeSpawner* Sp : Entry.Value)
			{
				if (Sp && SpawnerKey(Sp) == Key)
				{
					return Sp;
				}
			}
		}
		return nullptr;
	}

	/** 常用宏（ForEachLoop、ForLoop、DoOnce、Gate、IsValid…）在引擎的标准宏库里 */
	UEdGraph* FindStandardMacro(const FString& Name)
	{
		UBlueprint* Lib = LoadObject<UBlueprint>(nullptr, TEXT("/Engine/EditorBlueprintResources/StandardMacros.StandardMacros"));
		if (!Lib)
		{
			return nullptr;
		}
		for (UEdGraph* G : Lib->MacroGraphs)
		{
			if (G && G->GetName().Equals(Name, ESearchCase::IgnoreCase))
			{
				return G;
			}
		}
		return nullptr;
	}

	/** 「类名.函数名」或只写函数名（先在这个蓝图和父类上找，再到常用函数库里找） */
	UFunction* FindFunction(UBlueprint* BP, const FString& Spec, FString& Error)
	{
		FString ClassName;
		FString FuncName = Spec;
		Spec.Split(TEXT("."), &ClassName, &FuncName, ESearchCase::CaseSensitive, ESearchDir::FromEnd);
		if (!ClassName.IsEmpty())
		{
			UClass* C = FindClassByName(ClassName);
			if (!C)
			{
				Error = FString::Printf(TEXT("找不到类 %s"), *ClassName);
				return nullptr;
			}
			UFunction* F = C->FindFunctionByName(FName(*FuncName));
			if (!F)
			{
				Error = FString::Printf(TEXT("%s 上没有函数 %s（用 node_search 查准确的名字）"), *C->GetName(), *FuncName);
			}
			return F;
		}
		if (BP->SkeletonGeneratedClass)
		{
			if (UFunction* F = BP->SkeletonGeneratedClass->FindFunctionByName(FName(*FuncName)))
			{
				return F;
			}
		}
		static const TCHAR* Libs[] = { TEXT("KismetSystemLibrary"), TEXT("KismetMathLibrary"), TEXT("GameplayStatics"), TEXT("KismetStringLibrary"), TEXT("KismetArrayLibrary"), TEXT("KismetTextLibrary") };
		for (const TCHAR* LibName : Libs)
		{
			UClass* C = FindClassByName(LibName);
			if (UFunction* F = C ? C->FindFunctionByName(FName(*FuncName)) : nullptr)
			{
				return F;
			}
		}
		Error = FString::Printf(TEXT("找不到函数 %s：写成「类名.函数名」，或用 node_search 查到 spawner 再放"), *Spec);
		return nullptr;
	}

	// ---------- 一批操作的执行环境 ----------
	struct FEdit
	{
		UBlueprint* BP = nullptr;
		/** 这一批里新建的节点：临时名 → 节点，后面的操作用临时名引用 */
		TMap<FString, UEdGraphNode*> Named;
		/** 新建且没指定位置的节点：连线时按连到谁自动摆 */
		TSet<UEdGraphNode*> AutoPlaced;
		/** 改了变量、函数、组件这些结构：读类默认值前要先编译，最后也要按结构变化处理 */
		bool bStructural = false;
		bool bCompiledMidway = false;
		TMap<UEdGraph*, int32> NextSlot;

		UEdGraph* Graph(const TSharedPtr<FJsonObject>& Op, FString& Error)
		{
			return FindGraph(BP, Str(Op, TEXT("graph")), Error);
		}

		UEdGraphNode* Node(const FString& Ref, FString& Error)
		{
			if (UEdGraphNode** N = Named.Find(Ref))
			{
				return *N;
			}
			return FindNode(BP, nullptr, Ref, Error);
		}

		/** 「节点.引脚」：节点是 id 或这一批里起的临时名；引脚名里可以有点，所以从第一个点拆 */
		UEdGraphPin* Pin(const FString& Ref, FString& Error)
		{
			FString NodeRef;
			FString PinName;
			if (!Ref.Split(TEXT("."), &NodeRef, &PinName))
			{
				Error = FString::Printf(TEXT("引脚要写成「节点.引脚名」：%s"), *Ref);
				return nullptr;
			}
			UEdGraphNode* N = Node(NodeRef, Error);
			return N ? FindPin(N, PinName, Error) : nullptr;
		}

		/** 没指定位置的新节点先排在图的下方一行，等连线时再挪到连接对象旁边 */
		void DefaultPlace(UEdGraph* G, UEdGraphNode* N)
		{
			int32 MinX = 0;
			int32 MaxY = 0;
			bool bAny = false;
			for (const UEdGraphNode* Other : G->Nodes)
			{
				if (Other && Other != N && !AutoPlaced.Contains(Other))
				{
					MinX = bAny ? FMath::Min(MinX, Other->NodePosX) : Other->NodePosX;
					MaxY = bAny ? FMath::Max(MaxY, Other->NodePosY) : Other->NodePosY;
					bAny = true;
				}
			}
			int32& Slot = NextSlot.FindOrAdd(G);
			N->NodePosX = MinX + Slot * 320;
			N->NodePosY = MaxY + 260;
			++Slot;
			AutoPlaced.Add(N);
		}

		/** 连线后摆位置：执行线下游的放在上游右边，数据提供者放在使用者左下方 */
		void PlaceByLink(UEdGraphPin* From, UEdGraphPin* To)
		{
			UEdGraphNode* A = From->GetOwningNode();
			UEdGraphNode* B = To->GetOwningNode();
			UEdGraphPin* OutPin = From->Direction == EGPD_Output ? From : To;
			UEdGraphNode* Src = OutPin->GetOwningNode();
			UEdGraphNode* Dst = Src == A ? B : A;
			if (IsExec(OutPin))
			{
				if (AutoPlaced.Contains(Dst))
				{
					Dst->NodePosX = Src->NodePosX + 320;
					Dst->NodePosY = Src->NodePosY;
					AutoPlaced.Remove(Dst);
				}
			}
			else if (AutoPlaced.Contains(Src))
			{
				Src->NodePosX = Dst->NodePosX - 260;
				Src->NodePosY = Dst->NodePosY + 140;
				AutoPlaced.Remove(Src);
			}
		}

		FBPVariableDescription* Var(const FName Name)
		{
			for (FBPVariableDescription& V : BP->NewVariables)
			{
				if (V.VarName == Name)
				{
					return &V;
				}
			}
			return nullptr;
		}

		bool ApplyVarFlags(const TSharedPtr<FJsonObject>& Op, const FName Name, FString& Error)
		{
			FString S;
			if (Op->TryGetStringField(TEXT("category"), S))
			{
				FBlueprintEditorUtils::SetBlueprintVariableCategory(BP, Name, nullptr, FText::FromString(S));
			}
			bool bFlag = false;
			if (Op->TryGetBoolField(TEXT("instanceEditable"), bFlag))
			{
				FBlueprintEditorUtils::SetBlueprintOnlyEditableFlag(BP, Name, !bFlag);
			}
			if (Op->TryGetBoolField(TEXT("replicated"), bFlag))
			{
				if (uint64* Flags = FBlueprintEditorUtils::GetBlueprintVariablePropertyFlags(BP, Name))
				{
					*Flags = bFlag ? (*Flags | CPF_Net) : (*Flags & ~(CPF_Net | CPF_RepNotify));
				}
			}
			if (Op->TryGetBoolField(TEXT("exposeOnSpawn"), bFlag))
			{
				if (uint64* Flags = FBlueprintEditorUtils::GetBlueprintVariablePropertyFlags(BP, Name))
				{
					*Flags = bFlag ? (*Flags | CPF_ExposeOnSpawn) : (*Flags & ~CPF_ExposeOnSpawn);
				}
				if (bFlag)
				{
					FBlueprintEditorUtils::SetBlueprintVariableMetaData(BP, Name, nullptr, FBlueprintMetadata::MD_ExposeOnSpawn, TEXT("true"));
				}
				else
				{
					FBlueprintEditorUtils::RemoveBlueprintVariableMetaData(BP, Name, nullptr, FBlueprintMetadata::MD_ExposeOnSpawn);
				}
			}
			if (Op->HasField(TEXT("default")))
			{
				FBPVariableDescription* V = Var(Name);
				if (!V)
				{
					Error = FString::Printf(TEXT("没有变量 %s"), *Name.ToString());
					return false;
				}
				// 定义里的默认值是 UE 的文本格式；编译后进类默认对象
				V->DefaultValue = ValueText(Op->TryGetField(TEXT("default")));
			}
			return true;
		}

		bool AddPins(UK2Node_EditablePinBase* Node, const TSharedPtr<FJsonObject>& Op, const TCHAR* Field, EEdGraphPinDirection Dir, FString& Error)
		{
			const TArray<TSharedPtr<FJsonValue>>* List = nullptr;
			if (!Op->TryGetArrayField(Field, List))
			{
				return true;
			}
			for (const TSharedPtr<FJsonValue>& V : *List)
			{
				const TSharedPtr<FJsonObject>* P = nullptr;
				if (!V.IsValid() || !V->TryGetObject(P))
				{
					Error = FString::Printf(TEXT("%s 的每一项要写成 {name, type}"), Field);
					return false;
				}
				FEdGraphPinType Type;
				if (!ParseType(Str(*P, TEXT("type")), Type, Error))
				{
					return false;
				}
				if (!Node->CreateUserDefinedPin(FName(*Str(*P, TEXT("name"))), Type, Dir))
				{
					Error = FString::Printf(TEXT("参数 %s 没加上"), *Str(*P, TEXT("name")));
					return false;
				}
			}
			return true;
		}

		USCS_Node* Component(const FString& Name, FString& Error)
		{
			if (BP->SimpleConstructionScript)
			{
				if (USCS_Node* N = BP->SimpleConstructionScript->FindSCSNode(FName(*Name)))
				{
					return N;
				}
			}
			Error = FString::Printf(TEXT("%s 里没有组件 %s"), *BP->GetName(), *Name);
			return nullptr;
		}

		/** 读写类默认值、组件模板前：这一批里改过结构的话先编译一次，新加的变量才在生成的类上 */
		void EnsureCompiled()
		{
			if (bStructural && !bCompiledMidway)
			{
				FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(BP);
				FKismetEditorUtilities::CompileBlueprint(BP, EBlueprintCompileOptions::SkipGarbageCollection);
				bCompiledMidway = true;
			}
		}

		bool SetProp(UObject* Target, const TSharedPtr<FJsonObject>& Op, FString& Error)
		{
			FResolved R;
			if (!ResolvePath(Target->GetClass(), Target, Target, Str(Op, TEXT("prop")), R, Error))
			{
				return false;
			}
			if (!R.bEditable)
			{
				Error = FString::Printf(TEXT("%s 不能改：%s"), *Str(Op, TEXT("prop")), *R.ReadOnlyWhy);
				return false;
			}
			Target->Modify();
			return FromJson(R.Prop, R.Value, Op->TryGetField(TEXT("value")), Error);
		}

		UEdGraphNode* AddNode(const TSharedPtr<FJsonObject>& Op, FString& Error);
		bool Run(const TSharedPtr<FJsonObject>& Op, TSharedPtr<FJsonObject>& Res, FString& Error);
	};

	template <typename T>
	T* NewNode(UEdGraph* G, TFunctionRef<void(T*)> Setup)
	{
		FGraphNodeCreator<T> Creator(*G);
		T* N = Creator.CreateNode(false);
		Setup(N);
		Creator.Finalize();
		return N;
	}

	UEdGraphNode* FEdit::AddNode(const TSharedPtr<FJsonObject>& Op, FString& Error)
	{
		UEdGraph* G = Graph(Op, Error);
		if (!G)
		{
			return nullptr;
		}
		G->Modify();
		const FString Kind = Str(Op, TEXT("kind")).ToLower();
		const FString Key = Str(Op, TEXT("spawner"));
		UEdGraphNode* N = nullptr;
		if (!Key.IsEmpty())
		{
			UBlueprintNodeSpawner* Sp = FindSpawner(Key.ToLower());
			if (!Sp)
			{
				Error = FString::Printf(TEXT("没有 spawner %s（键来自 node_search 的结果；编辑器重启或蓝图结构变了以后要重新查）"), *Key);
				return nullptr;
			}
			// Invoke 到 5.8 一直收 FVector2D（没换成 FVector2f），所以这里不走 AgentPanelCompat.h 的坐标宏
			N = Sp->Invoke(G, IBlueprintNodeBinder::FBindingSet(), FVector2D(0, 0));
			if (!N)
			{
				Error = TEXT("这个节点放不到这张图里");
				return nullptr;
			}
		}
		else if (Kind == TEXT("call"))
		{
			UFunction* F = FindFunction(BP, Str(Op, TEXT("function")), Error);
			if (!F)
			{
				return nullptr;
			}
			N = NewNode<UK2Node_CallFunction>(G, [F](UK2Node_CallFunction* Node) { Node->SetFromFunction(F); });
		}
		else if (Kind == TEXT("get") || Kind == TEXT("set"))
		{
			const FName VarName(*Str(Op, TEXT("variable")));
			UClass* Scope = BP->SkeletonGeneratedClass ? BP->SkeletonGeneratedClass.Get() : BP->GeneratedClass.Get();
			if (!Scope || !FindFProperty<FProperty>(Scope, VarName))
			{
				// 这一批里刚加的变量要等骨架类重新生成才认得
				if (bStructural)
				{
					FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(BP);
					Scope = BP->SkeletonGeneratedClass ? BP->SkeletonGeneratedClass.Get() : BP->GeneratedClass.Get();
				}
				if (!Scope || !FindFProperty<FProperty>(Scope, VarName))
				{
					Error = FString::Printf(TEXT("%s 上没有变量 %s（别的对象上的变量用 node_search 查到 spawner 再放）"), *BP->GetName(), *VarName.ToString());
					return nullptr;
				}
			}
			if (Kind == TEXT("get"))
			{
				N = NewNode<UK2Node_VariableGet>(G, [VarName](UK2Node_VariableGet* Node) { Node->VariableReference.SetSelfMember(VarName); });
			}
			else
			{
				N = NewNode<UK2Node_VariableSet>(G, [VarName](UK2Node_VariableSet* Node) { Node->VariableReference.SetSelfMember(VarName); });
			}
		}
		else if (Kind == TEXT("branch"))
		{
			N = NewNode<UK2Node_IfThenElse>(G, [](UK2Node_IfThenElse*) {});
		}
		else if (Kind == TEXT("sequence"))
		{
			N = NewNode<UK2Node_ExecutionSequence>(G, [](UK2Node_ExecutionSequence*) {});
		}
		else if (Kind == TEXT("self"))
		{
			N = NewNode<UK2Node_Self>(G, [](UK2Node_Self*) {});
		}
		else if (Kind == TEXT("cast"))
		{
			UClass* C = FindClassByName(Str(Op, TEXT("class")));
			if (!C)
			{
				Error = FString::Printf(TEXT("找不到类 %s"), *Str(Op, TEXT("class")));
				return nullptr;
			}
			N = NewNode<UK2Node_DynamicCast>(G, [C](UK2Node_DynamicCast* Node) { Node->TargetType = C; });
		}
		else if (Kind == TEXT("custom_event"))
		{
			const FName EventName = FBlueprintEditorUtils::FindUniqueKismetName(BP, Str(Op, TEXT("name"), TEXT("CustomEvent")));
			UK2Node_CustomEvent* Event = NewNode<UK2Node_CustomEvent>(G, [EventName](UK2Node_CustomEvent* Node) { Node->CustomFunctionName = EventName; });
			if (!AddPins(Event, Op, TEXT("inputs"), EGPD_Output, Error))
			{
				return nullptr;
			}
			N = Event;
		}
		else if (Kind == TEXT("event"))
		{
			// 重写父类的事件（ReceiveBeginPlay、ReceiveTick…）；已经有了就返回原来那个
			const FName EventName(*Str(Op, TEXT("name")));
			UClass* Parent = BP->ParentClass;
			UFunction* F = Parent ? Parent->FindFunctionByName(EventName) : nullptr;
			if (!F && Parent && !EventName.ToString().StartsWith(TEXT("Receive")))
			{
				// BeginPlay、Tick 在蓝图里的函数名带 Receive 前缀
				F = Parent->FindFunctionByName(FName(*(TEXT("Receive") + EventName.ToString())));
			}
			if (!F)
			{
				Error = FString::Printf(TEXT("父类 %s 上没有可重写的事件 %s"), Parent ? *Parent->GetName() : TEXT("?"), *EventName.ToString());
				return nullptr;
			}
			if (UK2Node_Event* Existing = FBlueprintEditorUtils::FindOverrideForFunction(BP, F->GetOwnerClass(), F->GetFName()))
			{
				return Existing;
			}
			UClass* Owner = F->GetOwnerClass();
			const FName FuncName = F->GetFName();
			N = NewNode<UK2Node_Event>(G, [Owner, FuncName](UK2Node_Event* Node)
			{
				Node->EventReference.SetExternalMember(FuncName, Owner);
				Node->bOverrideFunction = true;
			});
		}
		else if (Kind == TEXT("macro"))
		{
			UEdGraph* Macro = FindStandardMacro(Str(Op, TEXT("macro")));
			if (!Macro)
			{
				for (UEdGraph* Own : BP->MacroGraphs)
				{
					if (Own && Own->GetName().Equals(Str(Op, TEXT("macro")), ESearchCase::IgnoreCase))
					{
						Macro = Own;
					}
				}
			}
			if (!Macro)
			{
				Error = FString::Printf(TEXT("找不到宏 %s（标准宏：ForEachLoop、ForLoop、WhileLoop、DoOnce、Gate、FlipFlop、IsValid…）"), *Str(Op, TEXT("macro")));
				return nullptr;
			}
			N = NewNode<UK2Node_MacroInstance>(G, [Macro](UK2Node_MacroInstance* Node) { Node->SetMacroGraph(Macro); });
		}
		else
		{
			Error = FString::Printf(TEXT("add_node 要给 spawner（node_search 查到的键）或 kind：call / get / set / branch / sequence / cast / self / event / custom_event / macro；收到的 kind 是「%s」"), *Kind);
			return nullptr;
		}

		if (Op->HasField(TEXT("x")) || Op->HasField(TEXT("y")))
		{
			N->NodePosX = Int(Op, TEXT("x"), 0);
			N->NodePosY = Int(Op, TEXT("y"), 0);
		}
		else
		{
			DefaultPlace(G, N);
		}
		if (!N->NodeGuid.IsValid())
		{
			N->CreateNewGuid();
		}
		const FString Comment = Str(Op, TEXT("comment"));
		if (!Comment.IsEmpty())
		{
			N->NodeComment = Comment;
			N->bCommentBubbleVisible = true;
		}
		// 顺手设引脚默认值：pins: { 引脚名: 值 }
		const TSharedPtr<FJsonObject>* Pins = nullptr;
		if (Op->TryGetObjectField(TEXT("pins"), Pins))
		{
			const UEdGraphSchema* Schema = G->GetSchema();
			for (const auto& Pair : (*Pins)->Values)
			{
				UEdGraphPin* P = FindPin(N, AGENT_PANEL_JSON_KEY(Pair.Key), Error);
				if (!P)
				{
					return nullptr;
				}
				Schema->TrySetDefaultValue(*P, ValueText(Pair.Value));
			}
		}
		return N;
	}

	bool FEdit::Run(const TSharedPtr<FJsonObject>& Op, TSharedPtr<FJsonObject>& Res, FString& Error)
	{
		const FString What = Str(Op, TEXT("op"));
		const FString Name = Str(Op, TEXT("name"));

		// ----- 成员 -----
		if (What == TEXT("add_variable"))
		{
			FEdGraphPinType Type;
			if (!ParseType(Str(Op, TEXT("type")), Type, Error))
			{
				return false;
			}
			if (Var(FName(*Name)))
			{
				Error = FString::Printf(TEXT("变量 %s 已经存在"), *Name);
				return false;
			}
			if (!FBlueprintEditorUtils::AddMemberVariable(BP, FName(*Name), Type))
			{
				Error = FString::Printf(TEXT("变量 %s 没加上（名字可能和已有的成员冲突）"), *Name);
				return false;
			}
			bStructural = true;
			bCompiledMidway = false;
			return ApplyVarFlags(Op, FName(*Name), Error);
		}
		if (What == TEXT("set_variable"))
		{
			FName VarName(*Name);
			if (!Var(VarName))
			{
				Error = FString::Printf(TEXT("没有变量 %s（只能改这个蓝图自己定义的变量）"), *Name);
				return false;
			}
			FString TypeText;
			if (Op->TryGetStringField(TEXT("type"), TypeText))
			{
				FEdGraphPinType Type;
				if (!ParseType(TypeText, Type, Error))
				{
					return false;
				}
				FBlueprintEditorUtils::ChangeMemberVariableType(BP, VarName, Type);
			}
			FString NewName;
			if (Op->TryGetStringField(TEXT("rename"), NewName) && !NewName.IsEmpty())
			{
				FBlueprintEditorUtils::RenameMemberVariable(BP, VarName, FName(*NewName));
				VarName = FName(*NewName);
			}
			bStructural = true;
			bCompiledMidway = false;
			return ApplyVarFlags(Op, VarName, Error);
		}
		if (What == TEXT("remove_variable"))
		{
			if (!Var(FName(*Name)))
			{
				Error = FString::Printf(TEXT("没有变量 %s"), *Name);
				return false;
			}
			FBlueprintEditorUtils::RemoveMemberVariable(BP, FName(*Name));
			bStructural = true;
			bCompiledMidway = false;
			return true;
		}
		if (What == TEXT("add_function"))
		{
			for (const UEdGraph* G : AllGraphs(BP))
			{
				if (G && G->GetName().Equals(Name, ESearchCase::IgnoreCase))
				{
					Error = FString::Printf(TEXT("已经有叫 %s 的图"), *Name);
					return false;
				}
			}
			UEdGraph* G = FBlueprintEditorUtils::CreateNewGraph(BP, FName(*Name), UEdGraph::StaticClass(), UEdGraphSchema_K2::StaticClass());
			FBlueprintEditorUtils::AddFunctionGraph<UClass>(BP, G, true, nullptr);
			UK2Node_FunctionEntry* Entry = nullptr;
			for (UEdGraphNode* N : G->Nodes)
			{
				if (UK2Node_FunctionEntry* E = Cast<UK2Node_FunctionEntry>(N))
				{
					Entry = E;
				}
			}
			if (!Entry)
			{
				Error = TEXT("新函数里没有入口节点");
				return false;
			}
			if (!AddPins(Entry, Op, TEXT("inputs"), EGPD_Output, Error))
			{
				return false;
			}
			if (Op->HasField(TEXT("outputs")))
			{
				UK2Node_FunctionResult* Result = FBlueprintEditorUtils::FindOrCreateFunctionResultNode(Entry);
				if (!Result || !AddPins(Result, Op, TEXT("outputs"), EGPD_Input, Error))
				{
					return false;
				}
				Res->SetStringField(TEXT("result"), NodeId(Result));
			}
			if (Bool(Op, TEXT("pure"), false))
			{
				Entry->AddExtraFlags(FUNC_BlueprintPure);
			}
			Res->SetStringField(TEXT("graph"), G->GetName());
			Res->SetStringField(TEXT("entry"), NodeId(Entry));
			bStructural = true;
			bCompiledMidway = false;
			return true;
		}
		if (What == TEXT("remove_function"))
		{
			UEdGraph* G = FindGraph(BP, Name, Error);
			if (!G)
			{
				return false;
			}
			if (!BP->FunctionGraphs.Contains(G) && !BP->MacroGraphs.Contains(G))
			{
				Error = FString::Printf(TEXT("%s 不是这个蓝图自己的函数或宏，不能删"), *Name);
				return false;
			}
			FBlueprintEditorUtils::RemoveGraph(BP, G, EGraphRemoveFlags::None);
			bStructural = true;
			bCompiledMidway = false;
			return true;
		}

		// ----- 类设置 -----
		if (What == TEXT("add_interface") || What == TEXT("remove_interface"))
		{
			UClass* I = FindClassByName(Str(Op, TEXT("interface")));
			if (!I || !I->IsChildOf(UInterface::StaticClass()))
			{
				Error = FString::Printf(TEXT("%s 不是接口"), *Str(Op, TEXT("interface")));
				return false;
			}
			if (What == TEXT("add_interface"))
			{
				if (!FBlueprintEditorUtils::ImplementNewInterface(BP, I->GetClassPathName()))
				{
					Error = FString::Printf(TEXT("接口 %s 没加上（可能已经实现了）"), *I->GetName());
					return false;
				}
			}
			else
			{
				FBlueprintEditorUtils::RemoveInterface(BP, I->GetClassPathName(), Bool(Op, TEXT("keepFunctions"), false));
			}
			bStructural = true;
			bCompiledMidway = false;
			return true;
		}
		if (What == TEXT("reparent"))
		{
			UClass* Parent = FindClassByName(Str(Op, TEXT("parent")));
			if (!Parent)
			{
				Error = FString::Printf(TEXT("找不到父类 %s"), *Str(Op, TEXT("parent")));
				return false;
			}
			if (!FKismetEditorUtilities::CanCreateBlueprintOfClass(Parent) || (BP->GeneratedClass && Parent->IsChildOf(BP->GeneratedClass)))
			{
				Error = FString::Printf(TEXT("%s 不能当这个蓝图的父类"), *Parent->GetName());
				return false;
			}
			BP->Modify();
			BP->ParentClass = Parent;
			FBlueprintEditorUtils::RefreshAllNodes(BP);
			bStructural = true;
			bCompiledMidway = false;
			return true;
		}
		if (What == TEXT("set_default"))
		{
			EnsureCompiled();
			UObject* CDO = BP->GeneratedClass ? BP->GeneratedClass->GetDefaultObject() : nullptr;
			if (!CDO)
			{
				Error = TEXT("这个蓝图还没有生成的类");
				return false;
			}
			return SetProp(CDO, Op, Error);
		}

		// ----- 组件 -----
		if (What == TEXT("add_component"))
		{
			USimpleConstructionScript* SCS = BP->SimpleConstructionScript;
			if (!SCS)
			{
				Error = TEXT("这个蓝图不能加组件（父类不是 Actor）");
				return false;
			}
			UClass* C = FindClassByName(Str(Op, TEXT("class")));
			if (!C || !C->IsChildOf(UActorComponent::StaticClass()) || C->HasAnyClassFlags(CLASS_Abstract))
			{
				Error = FString::Printf(TEXT("%s 不是能加的组件类"), *Str(Op, TEXT("class")));
				return false;
			}
			if (!Name.IsEmpty() && SCS->FindSCSNode(FName(*Name)))
			{
				Error = FString::Printf(TEXT("组件 %s 已经存在"), *Name);
				return false;
			}
			USCS_Node* Parent = nullptr;
			const FString ParentName = Str(Op, TEXT("parent"));
			if (!ParentName.IsEmpty())
			{
				Parent = Component(ParentName, Error);
				if (!Parent)
				{
					return false;
				}
			}
			SCS->Modify();
			USCS_Node* N = SCS->CreateNode(C, Name.IsEmpty() ? NAME_None : FName(*Name));
			if (Parent)
			{
				Parent->Modify();
				Parent->AddChildNode(N);
			}
			else
			{
				SCS->AddNode(N);
			}
			Res->SetStringField(TEXT("name"), N->GetVariableName().ToString());
			if (N->ComponentTemplate)
			{
				Res->SetStringField(TEXT("template"), N->ComponentTemplate->GetPathName());
			}
			bStructural = true;
			bCompiledMidway = false;
			return true;
		}
		if (What == TEXT("remove_component"))
		{
			USCS_Node* N = Component(Name, Error);
			if (!N)
			{
				return false;
			}
			BP->SimpleConstructionScript->Modify();
			BP->SimpleConstructionScript->RemoveNodeAndPromoteChildren(N);
			bStructural = true;
			bCompiledMidway = false;
			return true;
		}
		if (What == TEXT("set_component"))
		{
			USCS_Node* N = Component(Name, Error);
			if (!N)
			{
				return false;
			}
			if (!N->ComponentTemplate)
			{
				Error = FString::Printf(TEXT("组件 %s 没有模板对象"), *Name);
				return false;
			}
			return SetProp(N->ComponentTemplate, Op, Error);
		}

		// ----- 节点 -----
		if (What == TEXT("add_node"))
		{
			UEdGraphNode* N = AddNode(Op, Error);
			if (!N)
			{
				return false;
			}
			const FString As = Str(Op, TEXT("as"));
			if (!As.IsEmpty())
			{
				Named.Add(As, N);
				Res->SetStringField(TEXT("as"), As);
			}
			Res->SetStringField(TEXT("node"), NodeId(N));
			Res->SetStringField(TEXT("title"), NodeTitle(N));
			TArray<TSharedPtr<FJsonValue>> Pins;
			for (const UEdGraphPin* P : N->Pins)
			{
				if (P && !P->bHidden)
				{
					Pins.Add(JStr(FString::Printf(TEXT("%s %s: %s"), P->Direction == EGPD_Input ? TEXT("in") : TEXT("out"), *P->PinName.ToString(), IsExec(P) ? TEXT("exec") : *PinTypeText(P->PinType))));
				}
			}
			Res->SetArrayField(TEXT("pins"), Pins);
			return true;
		}
		if (What == TEXT("remove_node"))
		{
			UEdGraphNode* N = Node(Str(Op, TEXT("node")), Error);
			if (!N)
			{
				return false;
			}
			if (!N->CanUserDeleteNode())
			{
				Error = FString::Printf(TEXT("节点 %s（%s）不能删（函数入口这类节点跟着图走）"), *NodeId(N), *NodeTitle(N));
				return false;
			}
			FBlueprintEditorUtils::RemoveNode(BP, N, true);
			return true;
		}
		if (What == TEXT("set_pin"))
		{
			UEdGraphPin* P = Pin(Str(Op, TEXT("pin")), Error);
			if (!P)
			{
				return false;
			}
			if (P->Direction != EGPD_Input || IsExec(P))
			{
				Error = FString::Printf(TEXT("%s 不是数据输入引脚，没有默认值可设"), *Str(Op, TEXT("pin")));
				return false;
			}
			UEdGraphNode* N = P->GetOwningNode();
			N->Modify();
			const FString Value = ValueText(Op->TryGetField(TEXT("value")));
			N->GetSchema()->TrySetDefaultValue(*P, Value);
			Res->SetStringField(TEXT("value"), P->DefaultObject ? P->DefaultObject->GetPathName() : P->GetDefaultAsString());
			return true;
		}
		if (What == TEXT("connect") || What == TEXT("disconnect"))
		{
			UEdGraphPin* From = Pin(Str(Op, TEXT("from")), Error);
			if (!From)
			{
				return false;
			}
			const FString ToRef = Str(Op, TEXT("to"));
			const UEdGraphSchema* Schema = From->GetOwningNode()->GetSchema();
			if (What == TEXT("disconnect") && ToRef.IsEmpty())
			{
				Schema->BreakPinLinks(*From, true);
				return true;
			}
			UEdGraphPin* To = Pin(ToRef, Error);
			if (!To)
			{
				return false;
			}
			if (What == TEXT("disconnect"))
			{
				if (!From->LinkedTo.Contains(To))
				{
					Error = FString::Printf(TEXT("%s 和 %s 之间本来就没有连线"), *Str(Op, TEXT("from")), *ToRef);
					return false;
				}
				Schema->BreakSinglePinLink(From, To);
				return true;
			}
			if (From->GetOwningNode()->GetGraph() != To->GetOwningNode()->GetGraph())
			{
				Error = TEXT("两个引脚不在同一张图里，不能连");
				return false;
			}
			// 和手动拖线同一个判断：不让连时把编辑器给的原因原样返回
			const FPinConnectionResponse Response = Schema->CanCreateConnection(From, To);
			if (Response.Response == CONNECT_RESPONSE_DISALLOW)
			{
				Error = FString::Printf(TEXT("%s → %s 连不上：%s"), *Str(Op, TEXT("from")), *ToRef, *Response.Message.ToString());
				return false;
			}
			if (!Schema->TryCreateConnection(From, To))
			{
				Error = FString::Printf(TEXT("%s → %s 没连上：%s"), *Str(Op, TEXT("from")), *ToRef, *Response.Message.ToString());
				return false;
			}
			if (Response.Response == CONNECT_RESPONSE_MAKE_WITH_CONVERSION_NODE || Response.Response == CONNECT_RESPONSE_MAKE_WITH_PROMOTION)
			{
				// 类型不完全一样时编辑器会自动插转换节点，告诉 Agent 图里多了东西
				Res->SetStringField(TEXT("note"), Response.Message.ToString());
			}
			PlaceByLink(From, To);
			return true;
		}
		if (What == TEXT("move"))
		{
			UEdGraphNode* N = Node(Str(Op, TEXT("node")), Error);
			if (!N)
			{
				return false;
			}
			N->Modify();
			N->NodePosX = Int(Op, TEXT("x"), N->NodePosX);
			N->NodePosY = Int(Op, TEXT("y"), N->NodePosY);
			AutoPlaced.Remove(N);
			return true;
		}
		if (What == TEXT("comment"))
		{
			UEdGraph* G = Graph(Op, Error);
			if (!G)
			{
				return false;
			}
			// 给了 nodes 就把注释框套在这些节点外面，否则按 x / y / w / h
			int32 X = Int(Op, TEXT("x"), 0);
			int32 Y = Int(Op, TEXT("y"), 0);
			int32 W = Int(Op, TEXT("w"), 400);
			int32 H = Int(Op, TEXT("h"), 200);
			const TArray<TSharedPtr<FJsonValue>>* Ids = nullptr;
			if (Op->TryGetArrayField(TEXT("nodes"), Ids) && Ids->Num())
			{
				int32 MinX = MAX_int32;
				int32 MinY = MAX_int32;
				int32 MaxX = MIN_int32;
				int32 MaxY = MIN_int32;
				for (const TSharedPtr<FJsonValue>& V : *Ids)
				{
					UEdGraphNode* N = Node(V->AsString(), Error);
					if (!N)
					{
						return false;
					}
					MinX = FMath::Min(MinX, N->NodePosX);
					MinY = FMath::Min(MinY, N->NodePosY);
					MaxX = FMath::Max(MaxX, N->NodePosX + 280);
					MaxY = FMath::Max(MaxY, N->NodePosY + 140);
				}
				X = MinX - 40;
				Y = MinY - 70;
				W = MaxX - MinX + 80;
				H = MaxY - MinY + 110;
			}
			G->Modify();
			const FString Text = Str(Op, TEXT("text"));
			UEdGraphNode_Comment* C = NewNode<UEdGraphNode_Comment>(G, [&](UEdGraphNode_Comment* Node)
			{
				Node->NodePosX = X;
				Node->NodePosY = Y;
				Node->NodeWidth = W;
				Node->NodeHeight = H;
				Node->NodeComment = Text;
			});
			Res->SetStringField(TEXT("node"), NodeId(C));
			return true;
		}

		Error = FString::Printf(TEXT("不认识的操作 %s。能用的：add_variable、set_variable、remove_variable、add_function、remove_function、add_interface、remove_interface、reparent、set_default、add_component、remove_component、set_component、add_node、remove_node、set_pin、connect、disconnect、move、comment"), *What);
		return false;
	}
}

void AgentPanelEd::RegisterBlueprintEditOps()
{
	FAgentPanelBridge& B = FAgentPanelBridge::Get();

	// ---------- 能放哪些节点：按关键字查，候选项和编辑器右键菜单同一个来源 ----------
	B.AddOp(TEXT("bp.node_search"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UBlueprint* BP = LoadBlueprint(Str(A, TEXT("path")), Error);
		if (!BP)
		{
			return false;
		}
		UEdGraph* G = FindGraph(BP, Str(A, TEXT("graph")), Error);
		if (!G)
		{
			return false;
		}
		const FString Query = Str(A, TEXT("query")).TrimStartAndEnd();
		if (Query.Len() < 2)
		{
			Error = TEXT("query 至少写两个字符（函数名、变量名、节点名的一部分）");
			return false;
		}
		TArray<FCandidate> Found;
		SearchSpawners(BP, G, Query, Found);
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 30), 1, 200);
		TArray<TSharedPtr<FJsonValue>> Items;
		for (int32 i = 0; i < Found.Num() && i < Limit; ++i)
		{
			const FCandidate& C = Found[i];
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("spawner"), SpawnerKey(C.Spawner));
			O->SetStringField(TEXT("name"), C.Name);
			O->SetStringField(TEXT("category"), C.Category);
			if (!C.Owner.IsEmpty())
			{
				O->SetStringField(TEXT("owner"), C.Owner);
			}
			O->SetStringField(TEXT("nodeClass"), C.Spawner->NodeClass->GetName());
			Items.Add(JObj(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetNumberField(TEXT("total"), Found.Num());
		return true;
	});

	// ---------- 操作列表 ----------
	B.AddOp(TEXT("bp.edit"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UBlueprint* BP = LoadBlueprint(Str(A, TEXT("path")), Error);
		if (!BP)
		{
			return false;
		}
		const TArray<TSharedPtr<FJsonValue>>* Ops = nullptr;
		if (!A->TryGetArrayField(TEXT("ops"), Ops) || !Ops->Num())
		{
			Error = TEXT("要给 ops（操作数组）");
			return false;
		}
		const bool bDryRun = Bool(A, TEXT("dryRun"), false);
		const bool bCompile = Bool(A, TEXT("compile"), true);
		UPackage* Pkg = BP->GetOutermost();
		const bool bWasDirty = Pkg->IsDirty();
		const TSet<UPackage*> DirtyBefore = DirtyNow();

		FTx Tx(FString::Printf(TEXT("%s %d 个操作"), *BP->GetName(), Ops->Num()));
		if (bDryRun && !Tx.IsValid())
		{
			Error = TEXT("这个进程里没有编辑器事务，没法演练（演练要靠撤销把改动取消）");
			return false;
		}
		BP->Modify();

		FEdit Edit;
		Edit.BP = BP;
		TArray<TSharedPtr<FJsonValue>> Results;
		FString Failed;
		for (int32 i = 0; i < Ops->Num(); ++i)
		{
			const TSharedPtr<FJsonObject>* Op = nullptr;
			TSharedPtr<FJsonObject> Res = MakeShared<FJsonObject>();
			FString OpError;
			if (!(*Ops)[i].IsValid() || !(*Ops)[i]->TryGetObject(Op))
			{
				OpError = TEXT("每个操作要是一个对象 {op: ...}");
			}
			else
			{
				Res->SetStringField(TEXT("op"), Str(*Op, TEXT("op")));
				if (!Edit.Run(*Op, Res, OpError) && OpError.IsEmpty())
				{
					OpError = TEXT("没有成功（引擎没给原因）");
				}
			}
			if (!OpError.IsEmpty())
			{
				Failed = FString::Printf(TEXT("第 %d 个操作（%s）失败：%s"), i + 1, Op ? *Str(*Op, TEXT("op")) : TEXT("?"), *OpError);
				break;
			}
			Results.Add(JObj(Res));
		}

		TSharedPtr<FJsonObject> Compile;
		if (Failed.IsEmpty())
		{
			if (Edit.bStructural)
			{
				FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(BP);
			}
			else
			{
				FBlueprintEditorUtils::MarkBlueprintAsModified(BP);
			}
			if (bCompile || bDryRun)
			{
				Compile = CompileReport(BP, true);
			}
		}

		const bool bKeep = Failed.IsEmpty() && !bDryRun;
		FString TxError;
		const bool bFinished = Tx.Finish(bKeep, TxError);
		if (!bKeep)
		{
			// 撤掉之后重新编译，让生成的类回到改动前的样子；脏标记也还原
			if (bFinished && Tx.Id.Len())
			{
				FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(BP);
				FKismetEditorUtilities::CompileBlueprint(BP, EBlueprintCompileOptions::SkipGarbageCollection);
				if (!bWasDirty)
				{
					Pkg->SetDirtyFlag(false);
				}
			}
			if (!Failed.IsEmpty())
			{
				Error = Failed + (bFinished ? FString(TEXT("。整批已取消，蓝图没有变化")) : TEXT("。") + TxError);
				return false;
			}
			if (!bFinished)
			{
				Error = TxError;
				return false;
			}
		}
		else
		{
			NoteDirty(BP);
			TrackNewDirty(DirtyBefore, Out);
			if (Tx.Id.Len() && bFinished)
			{
				Out->SetStringField(TEXT("tx"), Tx.Id);
			}
		}
		Out->SetStringField(TEXT("path"), BP->GetPathName());
		Out->SetBoolField(TEXT("dryRun"), bDryRun);
		Out->SetArrayField(TEXT("results"), Results);
		if (Compile.IsValid())
		{
			Out->SetObjectField(TEXT("compile"), Compile);
		}
		return true;
	});

	// ---------- 编译 ----------
	B.AddOp(TEXT("bp.compile"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UBlueprint* BP = LoadBlueprint(Str(A, TEXT("path")), Error);
		if (!BP)
		{
			return false;
		}
		const TSet<UPackage*> DirtyBefore = DirtyNow();
		Out->SetStringField(TEXT("path"), BP->GetPathName());
		Out->SetObjectField(TEXT("compile"), CompileReport(BP, true));
		TrackNewDirty(DirtyBefore, Out);
		return true;
	});
}
