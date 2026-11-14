// 把一版 .uasset 文件转成可读文本（asset.textify）：面板评审看二进制资产改了什么时用（面板 ServerPlugin.textify）。
// 旧版本只在版本控制里，不在 Content 下，所以面板把那一版的字节写成临时文件交过来：这里给临时目录挂一个独立的
// 挂载点，按包加载、导出文本、卸载、撤掉挂载点，不碰项目里同名的资源，也不进资源注册表。
// 文本按行比较，所以要求「同一内容给同样的文本」：子对象按路径排序，临时包名换回原来的包名，不带加载状态和时间。
#include "AgentPanelCompat.h"
#include "AgentPanelEditorShared.h"

#include "EdGraph/EdGraph.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/CurveTable.h"
#include "Engine/DataTable.h"
#include "Engine/SCS_Node.h"
#include "Engine/SimpleConstructionScript.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "PackageTools.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/Package.h"
#include "UObject/UObjectHash.h"

using namespace AgentPanel;
using namespace AgentPanelEd;

namespace
{
	/** 一个对象保存下来的属性：{属性名: 值}，多行 JSON（数组、结构体逐项一行，按行比较时只标出真正改了的那几行） */
	FString PropsText(UObject* Obj)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		for (TFieldIterator<FProperty> It(Obj->GetClass()); It; ++It)
		{
			FProperty* Prop = *It;
			// Transient 不存盘：两版内容一样时它也可能不一样，留着只会造出假差异
			if (Prop->HasAnyPropertyFlags(CPF_Transient | CPF_DuplicateTransient))
			{
				continue;
			}
			O->SetField(Prop->GetAuthoredName(), ToJson(Prop, Prop->ContainerPtrToValuePtr<void>(Obj), 0));
		}
		FString Text;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Text);
		FJsonSerializer::Serialize(O.ToSharedRef(), Writer);
		return Text;
	}

	void AddObject(FString& Out, UObject* Obj)
	{
		Out += FString::Printf(TEXT("--- %s（%s）\n"), *Obj->GetPathName(), *Obj->GetClass()->GetName());
		Out += PropsText(Obj);
		Out += TEXT("\n");
	}

	void AddComponents(FString& Out, const USCS_Node* Node, int32 Depth)
	{
		Out += FString::ChrN(Depth * 2, TEXT(' '));
		Out += FString::Printf(TEXT("%s（%s）\n"), *Node->GetVariableName().ToString(), Node->ComponentClass ? *Node->ComponentClass->GetName() : TEXT("?"));
		for (const USCS_Node* Child : Node->GetChildNodes())
		{
			if (Child)
			{
				AddComponents(Out, Child, Depth + 1);
			}
		}
	}

	void SortByPath(TArray<UObject*>& Objs)
	{
		Objs.Sort([](const UObject& A, const UObject& B) { return A.GetPathName() < B.GetPathName(); });
	}

	/** 蓝图：大纲（父类、接口、变量、组件树）、类默认值和组件模板、每张图的伪代码（和 ue_blueprint 读到的一样） */
	void BlueprintText(FString& Out, UBlueprint* BP)
	{
		Out += FString::Printf(TEXT("父类：%s\n"), BP->ParentClass ? *BP->ParentClass->GetPathName() : TEXT(""));
		for (const FBPInterfaceDescription& I : BP->ImplementedInterfaces)
		{
			if (I.Interface)
			{
				Out += FString::Printf(TEXT("接口：%s\n"), *I.Interface->GetPathName());
			}
		}

		Out += TEXT("\n== 变量\n");
		for (const FBPVariableDescription& V : BP->NewVariables)
		{
			Out += FString::Printf(TEXT("%s: %s"), *V.VarName.ToString(), *PinTypeText(V.VarType));
			if (!V.Category.IsEmpty())
			{
				Out += FString::Printf(TEXT("  分类=%s"), *V.Category.ToString());
			}
			if (V.PropertyFlags & CPF_Net)
			{
				Out += (V.PropertyFlags & CPF_RepNotify) ? TEXT("  repNotify") : TEXT("  replicated");
			}
			if (!(V.PropertyFlags & CPF_DisableEditOnInstance))
			{
				Out += TEXT("  instanceEditable");
			}
			if (V.PropertyFlags & CPF_ExposeOnSpawn)
			{
				Out += TEXT("  exposeOnSpawn");
			}
			Out += TEXT("\n");
		}

		Out += TEXT("\n== 组件\n");
		if (BP->SimpleConstructionScript)
		{
			for (const USCS_Node* Root : BP->SimpleConstructionScript->GetRootNodes())
			{
				if (Root)
				{
					AddComponents(Out, Root, 0);
				}
			}
		}

		// 类默认值和组件模板：变量的默认值在这里（定义里的默认值字符串常常是空的）
		if (BP->GeneratedClass)
		{
			Out += TEXT("\n== 类默认值和组件模板\n");
			UObject* CDO = BP->GeneratedClass->GetDefaultObject();
			AddObject(Out, CDO);
			TArray<UObject*> Subs;
			CDO->GetDefaultSubobjects(Subs);
			if (BP->SimpleConstructionScript)
			{
				for (USCS_Node* N : BP->SimpleConstructionScript->GetAllNodes())
				{
					if (N && N->ComponentTemplate)
					{
						Subs.AddUnique(N->ComponentTemplate);
					}
				}
			}
			SortByPath(Subs);
			for (UObject* S : Subs)
			{
				AddObject(Out, S);
			}
		}

		for (UEdGraph* G : AllGraphs(BP))
		{
			if (!G)
			{
				continue;
			}
			Out += FString::Printf(TEXT("\n== 图 %s（%s）\n"), *G->GetName(), *GraphKind(BP, G));
			bool bMore = false;
			Out += GraphPseudo(BP, G, 100000, bMore);
			Out += TEXT("\n");
		}
	}

	/** 其他资源：资源自己和内嵌子对象的属性；数据表、曲线表的行不是属性，另外按行导出 */
	void AssetText(FString& Out, UObject* Asset)
	{
		if (UDataTable* DT = Cast<UDataTable>(Asset))
		{
			Out += FString::Printf(TEXT("行结构：%s\n\n== 行\n"), DT->GetRowStruct() ? *DT->GetRowStruct()->GetPathName() : TEXT(""));
			Out += DT->GetTableAsJSON();
			Out += TEXT("\n");
		}
		else if (UCurveTable* CT = Cast<UCurveTable>(Asset))
		{
			Out += TEXT("== 行\n");
			Out += CT->GetTableAsJSON();
			Out += TEXT("\n");
		}
		Out += TEXT("\n== 属性\n");
		AddObject(Out, Asset);
		TArray<UObject*> Subs;
		GetObjectsWithOuter(Asset, Subs, AGENT_PANEL_OUTER_NESTED(true));
		Subs.RemoveAll([](const UObject* S) { return !S || S->HasAnyFlags(RF_Transient); });
		SortByPath(Subs);
		for (UObject* S : Subs)
		{
			AddObject(Out, S);
		}
	}

	/** 包里的主资源：先按文件名（资源名和文件名一致），再找第一个资源对象 */
	UObject* MainAsset(UPackage* Pkg, const FString& Name)
	{
		if (UObject* Obj = StaticFindObjectFast(UObject::StaticClass(), Pkg, FName(*Name)))
		{
			return Obj;
		}
		TArray<UObject*> Objs;
		GetObjectsWithOuter(Pkg, Objs, AGENT_PANEL_OUTER_NESTED(false));
		for (UObject* Obj : Objs)
		{
			if (Obj && Obj->IsAsset())
			{
				return Obj;
			}
		}
		return nullptr;
	}
}

void AgentPanelEd::RegisterTextifyOps()
{
	FAgentPanelBridge& B = FAgentPanelBridge::Get();

	// args：file 临时文件的绝对路径（文件名就是资源名），package 这个资源在项目里的包名（/Game/Dir/Name，文本里换回它）
	B.AddOp(TEXT("asset.textify"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const FString File = FPaths::ConvertRelativePathToFull(Str(A, TEXT("file")));
		if (!FPaths::FileExists(File))
		{
			Error = FString::Printf(TEXT("没有这个文件：%s"), *File);
			return false;
		}
		const FString Name = FPaths::GetBaseFilename(File);
		const FString Original = Str(A, TEXT("package"), TEXT("/Game/") + Name);
		const FString Dir = FPaths::GetPath(File) + TEXT("/");
		// 每次一个新挂载点：前后两版同名，挂在同一个根下会撞包名
		const FString Root = FString::Printf(TEXT("/APTextify%s/"), *FGuid::NewGuid().ToString(EGuidFormats::Digits).Left(12));
		const FString TempPkg = Root + Name;
		FPackageName::RegisterMountPoint(Root, Dir);

		UPackage* Pkg = LoadPackage(nullptr, *TempPkg, LOAD_NoWarn | LOAD_Quiet);
		UObject* Asset = Pkg ? MainAsset(Pkg, Name) : nullptr;
		FString Text;
		if (Asset)
		{
			Text += FString::Printf(TEXT("资源 %s（%s）\n"), *Original, *Asset->GetClass()->GetName());
			if (UBlueprint* BP = Cast<UBlueprint>(Asset))
			{
				BlueprintText(Text, BP);
			}
			else
			{
				AssetText(Text, Asset);
			}
			// 文本里的临时包名换回项目里的包名：同一内容两次转出来的文本才一样
			Text.ReplaceInline(*TempPkg, *Original, ESearchCase::CaseSensitive);
			Text.ReplaceInline(*Root, *(FPackageName::GetLongPackagePath(Original) + TEXT("/")), ESearchCase::CaseSensitive);
		}

		// 卸载：加载蓝图时会顺手编译、可能标脏，这里不保存，直接连脏的一起卸掉
		if (Pkg)
		{
			Pkg->SetDirtyFlag(false);
			TArray<UPackage*> Pkgs;
			Pkgs.Add(Pkg);
			FText UnloadError;
			UPackageTools::UnloadPackages(Pkgs, UnloadError, true);
		}
		FPackageName::UnRegisterMountPoint(Root, Dir);

		if (!Pkg)
		{
			Error = FString::Printf(TEXT("加载不了这一版的 %s：文件可能不完整，或者是更新的引擎版本存的"), *Original);
			return false;
		}
		if (!Asset)
		{
			Error = FString::Printf(TEXT("这一版的 %s 里没有找到资源对象"), *Original);
			return false;
		}
		Out->SetStringField(TEXT("text"), Text);
		return true;
	});
}
