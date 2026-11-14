// 资源通用层（和具体资源类型无关）：摘要、整份导出、引用关系、数据校验、新建、复制、改名 / 移动、删除、撤回一步。
// 写入都只改内存并标脏，保存另走 asset.save；面板服务端负责权限、确认、操作记录和 P4。
#include "AgentPanelCompat.h"
#include "AgentPanelEditorShared.h"

#include "AssetRegistry/AssetData.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetRegistry/IAssetRegistry.h"
#include "AssetToolsModule.h"
#include "Curves/CurveBase.h"
#include "Editor.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/CurveTable.h"
#include "Engine/DataAsset.h"
#include "Engine/DataTable.h"
#include "Engine/SCS_Node.h"
#include "Engine/SimpleConstructionScript.h"
#include "HAL/FileManager.h"
#include "IAssetTools.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/DataValidation.h"
#include "Misc/EngineVersion.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "ObjectTools.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/Interface.h"
#include "UObject/Package.h"
#include "UObject/UObjectHash.h"

using namespace AgentPanel;
using namespace AgentPanelEd;

namespace
{
	IAssetRegistry& Registry()
	{
		return FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
	}

	/** /Game/Dir/Name 或 /Game/Dir/Name.Name → 包名和资源名 */
	bool SplitAssetPath(const FString& Path, FString& Package, FString& Name, FString& Error)
	{
		Package = Path;
		int32 Dot = INDEX_NONE;
		if (Package.FindChar(TEXT('.'), Dot))
		{
			Package = Package.Left(Dot);
		}
		FText Why;
		if (!FPackageName::IsValidLongPackageName(Package, false, &Why))
		{
			Error = FString::Printf(TEXT("资源路径不对：%s（%s）。要写成 /Game/目录/名字"), *Path, *Why.ToString());
			return false;
		}
		Name = FPackageName::GetShortName(Package);
		return true;
	}

	bool PackageExists(const FString& Package)
	{
		return FindPackage(nullptr, *Package) != nullptr || FPackageName::DoesPackageExist(Package);
	}

	/** 引用关系：bReferencers 为真是「谁用了它」，否则是「它用了谁」；只算包之间的引用 */
	void Related(const FName Package, bool bReferencers, bool bHardOnly, TArray<FName>& Out)
	{
		UE::AssetRegistry::FDependencyQuery Query;
		if (bHardOnly)
		{
			Query.Required = UE::AssetRegistry::EDependencyProperty::Hard;
		}
		if (bReferencers)
		{
			Registry().GetReferencers(Package, Out, UE::AssetRegistry::EDependencyCategory::Package, Query);
		}
		else
		{
			Registry().GetDependencies(Package, Out, UE::AssetRegistry::EDependencyCategory::Package, Query);
		}
		Out.Remove(Package);
	}

	/** 逐层往外查，最多 Depth 层、Limit 个；每项带是第几层 */
	TArray<TSharedPtr<FJsonValue>> RelatedTree(const FName Root, bool bReferencers, bool bHardOnly, int32 Depth, int32 Limit, int32& Total)
	{
		TArray<TSharedPtr<FJsonValue>> Items;
		TSet<FName> Seen;
		Seen.Add(Root);
		TArray<FName> Layer;
		Layer.Add(Root);
		Total = 0;
		for (int32 Level = 1; Level <= Depth && Layer.Num(); ++Level)
		{
			TArray<FName> Next;
			for (const FName From : Layer)
			{
				TArray<FName> Found;
				Related(From, bReferencers, bHardOnly, Found);
				for (const FName Pkg : Found)
				{
					if (Seen.Contains(Pkg))
					{
						continue;
					}
					Seen.Add(Pkg);
					Next.Add(Pkg);
					++Total;
					if (Items.Num() < Limit)
					{
						TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
						O->SetStringField(TEXT("package"), Pkg.ToString());
						O->SetNumberField(TEXT("level"), Level);
						if (Level > 1)
						{
							O->SetStringField(TEXT("via"), From.ToString());
						}
						Items.Add(JObj(O));
					}
				}
			}
			Layer = MoveTemp(Next);
		}
		return Items;
	}

	/** 导出时一个对象的内容：路径、类和全部属性（对象引用只给路径） */
	TSharedPtr<FJsonObject> ExportObject(UObject* Obj, const UObject* Root, int32 MaxChars)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetStringField(TEXT("path"), Obj->GetPathName());
		O->SetStringField(TEXT("name"), Obj->GetName());
		O->SetStringField(TEXT("class"), Obj->GetClass()->GetName());
		if (Obj != Root && Obj->GetOuter() && Obj->GetOuter() != Root)
		{
			O->SetStringField(TEXT("outer"), Obj->GetOuter()->GetPathName());
		}
		O->SetArrayField(TEXT("props"), ListProps(Obj->GetClass(), Obj, FString(), MaxChars, Obj));
		return O;
	}

	int32 OuterDepth(const UObject* Obj, const UObject* Root)
	{
		int32 Depth = 0;
		for (const UObject* O = Obj; O && O != Root; O = O->GetOuter())
		{
			++Depth;
		}
		return Depth;
	}

	/** 新建蓝图时按父类定蓝图类型；控件蓝图、动画蓝图有自己的资源类，这里不建 */
	bool BlueprintTypeFor(UClass* Parent, EBlueprintType& Type, FString& Error)
	{
		static const TCHAR* Special[] = { TEXT("/Script/UMG.UserWidget"), TEXT("/Script/Engine.AnimInstance") };
		for (const TCHAR* Path : Special)
		{
			UClass* C = FindObject<UClass>(nullptr, Path);
			if (C && Parent->IsChildOf(C))
			{
				Error = FString::Printf(TEXT("父类是 %s：控件蓝图和动画蓝图现在还不能用这个工具新建，请在编辑器里建好再改"), *Parent->GetName());
				return false;
			}
		}
		if (!FKismetEditorUtilities::CanCreateBlueprintOfClass(Parent))
		{
			Error = FString::Printf(TEXT("%s 不能当蓝图的父类（没有标 Blueprintable）"), *Parent->GetName());
			return false;
		}
		Type = BPTYPE_Normal;
		if (Parent == UInterface::StaticClass())
		{
			Type = BPTYPE_Interface;
		}
		else if (Parent->IsChildOf(UBlueprintFunctionLibrary::StaticClass()))
		{
			Type = BPTYPE_FunctionLibrary;
		}
		return true;
	}
}

void AgentPanelEd::RegisterAssetOps()
{
	FAgentPanelBridge& B = FAgentPanelBridge::Get();

	// ---------- 摘要：类、文件、标签、子对象、引用数量 ----------
	B.AddOp(TEXT("asset.describe"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Obj = LoadAsset(Str(A, TEXT("path")), Error);
		if (!Obj)
		{
			return false;
		}
		UPackage* Pkg = Obj->GetOutermost();
		Out->SetStringField(TEXT("path"), Obj->GetPathName());
		Out->SetStringField(TEXT("name"), Obj->GetName());
		Out->SetStringField(TEXT("class"), Obj->GetClass()->GetName());
		TArray<TSharedPtr<FJsonValue>> Chain;
		for (UClass* C = Obj->GetClass()->GetSuperClass(); C && Chain.Num() < 12; C = C->GetSuperClass())
		{
			Chain.Add(JStr(C->GetName()));
		}
		Out->SetArrayField(TEXT("classChain"), Chain);
		Out->SetStringField(TEXT("package"), Pkg->GetName());
		const FString File = PackageFile(Pkg);
		Out->SetStringField(TEXT("file"), File);
		const int64 Size = IFileManager::Get().FileSize(*File);
		Out->SetBoolField(TEXT("onDisk"), Size >= 0);
		if (Size >= 0)
		{
			Out->SetNumberField(TEXT("size"), static_cast<double>(Size));
			Out->SetBoolField(TEXT("readOnly"), IFileManager::Get().IsReadOnly(*File));
		}
		Out->SetBoolField(TEXT("dirty"), Pkg->IsDirty());

		// 蓝图：父类和编译状态放在摘要里，细节用 bp.outline
		if (UBlueprint* BP = Cast<UBlueprint>(Obj))
		{
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("parent"), BP->ParentClass ? BP->ParentClass->GetPathName() : FString());
			O->SetStringField(TEXT("status"), StatusText(BP));
			O->SetStringField(TEXT("generatedClass"), BP->GeneratedClass ? BP->GeneratedClass->GetPathName() : FString());
			O->SetNumberField(TEXT("graphs"), AllGraphs(BP).Num());
			O->SetNumberField(TEXT("variables"), BP->NewVariables.Num());
			Out->SetObjectField(TEXT("blueprint"), O);
		}
		if (UDataTable* DT = Cast<UDataTable>(Obj))
		{
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("rowStruct"), DT->GetRowStruct() ? DT->GetRowStruct()->GetPathName() : FString());
			O->SetNumberField(TEXT("rows"), DT->GetRowMap().Num());
			Out->SetObjectField(TEXT("dataTable"), O);
		}

		// 资源注册表里的标签（不用加载资源就能搜的那些）
		TSharedPtr<FJsonObject> Tags = MakeShared<FJsonObject>();
		const FAssetData Data = Registry().GetAssetByObjectPath(FSoftObjectPath(Obj));
		if (Data.IsValid())
		{
			Data.TagsAndValues.ForEach([&Tags](const TPair<FName, FAssetTagValueRef>& Pair)
			{
				const FString Value = Pair.Value.AsString();
				// 蓝图内搜索的索引数据很长、不是给人看的
				if (Value.Len() <= 300)
				{
					Tags->SetStringField(Pair.Key.ToString(), Value);
				}
			});
		}
		Out->SetObjectField(TEXT("tags"), Tags);

		// 子对象大纲（内嵌的实例化对象、组件模板等），只列名字和类
		TArray<UObject*> Subs;
		GetObjectsWithOuter(Obj, Subs, AGENT_PANEL_OUTER_NESTED(false));
		TArray<TSharedPtr<FJsonValue>> SubList;
		for (UObject* S : Subs)
		{
			if (S && !S->HasAnyFlags(RF_Transient) && SubList.Num() < 60)
			{
				SubList.Add(JObj(Brief(S)));
			}
		}
		Out->SetArrayField(TEXT("subobjects"), SubList);
		Out->SetNumberField(TEXT("subobjectCount"), Subs.Num());

		TArray<FName> Deps;
		TArray<FName> Refs;
		Related(Pkg->GetFName(), false, false, Deps);
		Related(Pkg->GetFName(), true, false, Refs);
		Out->SetNumberField(TEXT("dependencies"), Deps.Num());
		Out->SetNumberField(TEXT("referencers"), Refs.Num());
		return true;
	});

	// ---------- 引用关系 ----------
	B.AddOp(TEXT("asset.refs"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		FString Package;
		FString Name;
		if (!SplitAssetPath(Str(A, TEXT("path")), Package, Name, Error))
		{
			return false;
		}
		if (!PackageExists(Package))
		{
			Error = FString::Printf(TEXT("找不到资源 %s"), *Package);
			return false;
		}
		const FString Direction = Str(A, TEXT("direction"), TEXT("both"));
		const bool bHard = Bool(A, TEXT("hard"), false);
		const int32 Depth = FMath::Clamp(Int(A, TEXT("depth"), 1), 1, 4);
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 100), 1, 1000);
		Out->SetStringField(TEXT("package"), Package);
		if (Direction != TEXT("referencers"))
		{
			int32 Total = 0;
			Out->SetArrayField(TEXT("dependencies"), RelatedTree(FName(*Package), false, bHard, Depth, Limit, Total));
			Out->SetNumberField(TEXT("dependencyTotal"), Total);
		}
		if (Direction != TEXT("dependencies"))
		{
			int32 Total = 0;
			Out->SetArrayField(TEXT("referencers"), RelatedTree(FName(*Package), true, bHard, Depth, Limit, Total));
			Out->SetNumberField(TEXT("referencerTotal"), Total);
		}
		return true;
	});

	// ---------- 整份导出：自己的属性 + 内嵌子对象的属性；蓝图导出类默认值和组件模板（图表用 bp.graph） ----------
	B.AddOp(TEXT("asset.export"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Asset = LoadAsset(Str(A, TEXT("path")), Error);
		if (!Asset)
		{
			return false;
		}
		const bool bToFile = Bool(A, TEXT("file"), false);
		const int32 MaxChars = bToFile ? 0 : FMath::Clamp(Int(A, TEXT("maxChars"), 2000), 200, 20000);
		const int32 Depth = FMath::Clamp(Int(A, TEXT("depth"), 2), 0, 8);
		const int32 Offset = FMath::Max(Int(A, TEXT("offset"), 0), 0);
		const int32 Limit = bToFile ? MAX_int32 : FMath::Clamp(Int(A, TEXT("limit"), 20), 1, 200);

		UObject* Root = Asset;
		TArray<UObject*> Subs;
		if (UBlueprint* BP = Cast<UBlueprint>(Asset))
		{
			if (!BP->GeneratedClass)
			{
				Error = TEXT("这个蓝图还没有生成的类（没编译过），先编译");
				return false;
			}
			Root = BP->GeneratedClass->GetDefaultObject();
			Out->SetStringField(TEXT("note"), TEXT("蓝图导出的是类默认值和组件模板；变量定义、函数和图表用 ue_blueprint 的 outline / graph"));
			Root->GetDefaultSubobjects(Subs);
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
		}
		else
		{
			TArray<UObject*> All;
			GetObjectsWithOuter(Asset, All, AGENT_PANEL_OUTER_NESTED(true));
			for (UObject* S : All)
			{
				if (S && !S->HasAnyFlags(RF_Transient) && OuterDepth(S, Asset) <= Depth)
				{
					Subs.Add(S);
				}
			}
		}

		Out->SetNumberField(TEXT("format"), 1);
		Out->SetStringField(TEXT("engine"), FEngineVersion::Current().ToString(EVersionComponent::Patch));
		Out->SetStringField(TEXT("asset"), Asset->GetPathName());
		Out->SetStringField(TEXT("assetClass"), Asset->GetClass()->GetName());
		if (Offset == 0)
		{
			Out->SetObjectField(TEXT("root"), ExportObject(Root, Root, MaxChars));
		}
		TArray<TSharedPtr<FJsonValue>> List;
		for (int32 i = Offset; i < Subs.Num() && List.Num() < Limit; ++i)
		{
			List.Add(JObj(ExportObject(Subs[i], Root, MaxChars)));
		}
		Out->SetArrayField(TEXT("subobjects"), List);
		Out->SetNumberField(TEXT("subobjectTotal"), Subs.Num());
		if (Offset + List.Num() < Subs.Num())
		{
			Out->SetNumberField(TEXT("next"), Offset + List.Num());
		}

		if (bToFile)
		{
			// 写成文件：整份不截断，放在 Saved/AgentPanel/export/ 下，回包里只给文件路径
			FString Text;
			const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Text);
			FJsonSerializer::Serialize(Out.ToSharedRef(), Writer);
			const FString File = FPaths::ConvertRelativePathToFull(FPaths::ProjectSavedDir() / TEXT("AgentPanel") / TEXT("export") / (Asset->GetName() + TEXT(".json")));
			if (!FFileHelper::SaveStringToFile(Text, *File, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
			{
				Error = FString::Printf(TEXT("写不了文件 %s"), *File);
				return false;
			}
			Out = MakeShared<FJsonObject>();
			Out->SetStringField(TEXT("file"), File);
			Out->SetNumberField(TEXT("chars"), Text.Len());
			Out->SetNumberField(TEXT("subobjectTotal"), Subs.Num());
		}
		return true;
	});

	// ---------- 数据校验：资源自己的 IsDataValid；蓝图另报编译状态 ----------
	B.AddOp(TEXT("asset.validate"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Obj = LoadAsset(Str(A, TEXT("path")), Error);
		if (!Obj)
		{
			return false;
		}
		FDataValidationContext Context;
		const EDataValidationResult Result = Obj->IsDataValid(Context);
		Out->SetStringField(TEXT("result"), Result == EDataValidationResult::Invalid ? TEXT("invalid") : Result == EDataValidationResult::Valid ? TEXT("valid") : TEXT("notValidated"));
		TArray<TSharedPtr<FJsonValue>> Issues;
		for (const FDataValidationContext::FIssue& I : Context.GetIssues())
		{
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("severity"), I.Severity == EMessageSeverity::Error ? TEXT("error") : TEXT("warning"));
			O->SetStringField(TEXT("text"), I.Message.ToString());
			Issues.Add(JObj(O));
		}
		Out->SetArrayField(TEXT("issues"), Issues);
		if (UBlueprint* BP = Cast<UBlueprint>(Obj))
		{
			Out->SetObjectField(TEXT("compile"), CompileReport(BP, false));
		}
		return true;
	});

	// ---------- 新建：蓝图（parent）、DataTable（rowStruct）、DataAsset 子类、曲线、曲线表 ----------
	B.AddOp(TEXT("asset.create"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		FString Package;
		FString Name;
		if (!SplitAssetPath(Str(A, TEXT("path")), Package, Name, Error))
		{
			return false;
		}
		if (PackageExists(Package))
		{
			Error = FString::Printf(TEXT("%s 已经存在"), *Package);
			return false;
		}
		const FString ClassName = Str(A, TEXT("class"), TEXT("Blueprint"));
		const bool bDryRun = Bool(A, TEXT("dryRun"), false);
		const bool bBlueprint = ClassName.Equals(TEXT("Blueprint"), ESearchCase::IgnoreCase);
		UClass* Class = bBlueprint ? UBlueprint::StaticClass() : FindClassByName(ClassName);
		if (!Class)
		{
			Error = FString::Printf(TEXT("找不到类 %s"), *ClassName);
			return false;
		}

		UClass* Parent = nullptr;
		EBlueprintType BPType = BPTYPE_Normal;
		UScriptStruct* RowStruct = nullptr;
		if (bBlueprint)
		{
			const FString ParentName = Str(A, TEXT("parent"), TEXT("Actor"));
			Parent = FindClassByName(ParentName);
			if (!Parent)
			{
				Error = FString::Printf(TEXT("找不到父类 %s"), *ParentName);
				return false;
			}
			if (!BlueprintTypeFor(Parent, BPType, Error))
			{
				return false;
			}
		}
		else if (Class->IsChildOf(UDataTable::StaticClass()))
		{
			const FString StructName = Str(A, TEXT("rowStruct"));
			RowStruct = FindStructByName(StructName);
			if (!RowStruct)
			{
				Error = FString::Printf(TEXT("新建 DataTable 要给行结构 rowStruct，找不到：%s"), *StructName);
				return false;
			}
		}
		else if (Class->HasAnyClassFlags(CLASS_Abstract) || !(Class->IsChildOf(UDataAsset::StaticClass()) || Class->IsChildOf(UCurveBase::StaticClass()) || Class->IsChildOf(UCurveTable::StaticClass())))
		{
			Error = FString::Printf(TEXT("现在能新建的类型：Blueprint（给 parent）、DataTable（给 rowStruct）、DataAsset 的非抽象子类、曲线和曲线表；%s 不在其中"), *Class->GetName());
			return false;
		}
		Out->SetStringField(TEXT("path"), Package + TEXT(".") + Name);
		Out->SetStringField(TEXT("class"), Class->GetName());
		if (bDryRun)
		{
			Out->SetBoolField(TEXT("dryRun"), true);
			return true;
		}

		UPackage* Pkg = CreatePackage(*Package);
		UObject* Created = nullptr;
		if (bBlueprint)
		{
			Created = FKismetEditorUtilities::CreateBlueprint(Parent, Pkg, FName(*Name), BPType, UBlueprint::StaticClass(), UBlueprintGeneratedClass::StaticClass(), FName(TEXT("AgentPanel")));
		}
		else
		{
			Created = NewObject<UObject>(Pkg, Class, FName(*Name), RF_Public | RF_Standalone | RF_Transactional);
			if (UDataTable* DT = Cast<UDataTable>(Created))
			{
				DT->RowStruct = RowStruct;
			}
		}
		if (!Created)
		{
			Error = TEXT("引擎没有建出这个资源");
			return false;
		}
		FAssetRegistryModule::AssetCreated(Created);
		NoteDirty(Created);
		Out->SetStringField(TEXT("path"), Created->GetPathName());
		Out->SetStringField(TEXT("package"), Pkg->GetName());
		Out->SetStringField(TEXT("file"), PackageFile(Pkg));
		return true;
	});

	// ---------- 复制到新路径 ----------
	B.AddOp(TEXT("asset.duplicate"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Src = LoadAsset(Str(A, TEXT("path")), Error);
		if (!Src)
		{
			return false;
		}
		FString Package;
		FString Name;
		if (!SplitAssetPath(Str(A, TEXT("to")), Package, Name, Error))
		{
			return false;
		}
		if (PackageExists(Package))
		{
			Error = FString::Printf(TEXT("%s 已经存在"), *Package);
			return false;
		}
		Out->SetStringField(TEXT("path"), Package + TEXT(".") + Name);
		if (Bool(A, TEXT("dryRun"), false))
		{
			Out->SetBoolField(TEXT("dryRun"), true);
			return true;
		}
		ObjectTools::FPackageGroupName PGN;
		PGN.PackageName = Package;
		PGN.GroupName = FString();
		PGN.ObjectName = Name;
		TSet<UPackage*> Refused;
		UObject* Copy = ObjectTools::DuplicateSingleObject(Src, PGN, Refused, false);
		if (!Copy)
		{
			Error = TEXT("引擎没有复制成功");
			return false;
		}
		FAssetRegistryModule::AssetCreated(Copy);
		NoteDirty(Copy);
		Out->SetStringField(TEXT("path"), Copy->GetPathName());
		Out->SetStringField(TEXT("package"), Copy->GetOutermost()->GetName());
		Out->SetStringField(TEXT("file"), PackageFile(Copy->GetOutermost()));
		return true;
	});

	// ---------- 改名 / 移动：引擎会把引用它的资源一起改掉，改不到的留重定向器 ----------
	B.AddOp(TEXT("asset.rename"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Obj = LoadAsset(Str(A, TEXT("path")), Error);
		if (!Obj)
		{
			return false;
		}
		FString Package;
		FString Name;
		if (!SplitAssetPath(Str(A, TEXT("to")), Package, Name, Error))
		{
			return false;
		}
		if (PackageExists(Package))
		{
			Error = FString::Printf(TEXT("%s 已经存在"), *Package);
			return false;
		}
		const FName OldPackage = Obj->GetOutermost()->GetFName();
		TArray<FName> Refs;
		Related(OldPackage, true, false, Refs);
		TArray<TSharedPtr<FJsonValue>> RefList;
		for (const FName R : Refs)
		{
			if (RefList.Num() < 100)
			{
				RefList.Add(JStr(R.ToString()));
			}
		}
		Out->SetArrayField(TEXT("referencers"), RefList);
		Out->SetNumberField(TEXT("referencerTotal"), Refs.Num());
		Out->SetStringField(TEXT("path"), Package + TEXT(".") + Name);
		Out->SetStringField(TEXT("oldFile"), PackageFile(Obj->GetOutermost()));
		if (Bool(A, TEXT("dryRun"), false))
		{
			Out->SetBoolField(TEXT("dryRun"), true);
			return true;
		}
		const TSet<UPackage*> Before = DirtyNow();
		IAssetTools& Tools = FModuleManager::LoadModuleChecked<FAssetToolsModule>(TEXT("AssetTools")).Get();
		TArray<FAssetRenameData> Renames;
		Renames.Emplace(Obj, FPackageName::GetLongPackagePath(Package), Name);
		if (!Tools.RenameAssets(Renames))
		{
			Error = TEXT("引擎没有改名成功（详细原因在 UE 的输出日志里，用 ue_log 看）");
			return false;
		}
		NoteDirty(Obj);
		TrackNewDirty(Before, Out);
		Out->SetStringField(TEXT("path"), Obj->GetPathName());
		Out->SetStringField(TEXT("package"), Obj->GetOutermost()->GetName());
		Out->SetStringField(TEXT("file"), PackageFile(Obj->GetOutermost()));
		// 旧包里还有东西说明留了重定向器（有引用方没改到），保存后旧文件还在
		Out->SetBoolField(TEXT("redirector"), FindPackage(nullptr, *OldPackage.ToString()) != nullptr);
		return true;
	});

	// ---------- 删除：有引用方时默认拒绝并列出来；force=true 强删（引用方会断开） ----------
	B.AddOp(TEXT("asset.delete"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Obj = LoadAsset(Str(A, TEXT("path")), Error);
		if (!Obj)
		{
			return false;
		}
		UPackage* Pkg = Obj->GetOutermost();
		const FString PackageName = Pkg->GetName();
		const FString File = PackageFile(Pkg);
		TArray<FName> Refs;
		Related(Pkg->GetFName(), true, false, Refs);
		TArray<TSharedPtr<FJsonValue>> RefList;
		TArray<FString> RefNames;
		for (const FName R : Refs)
		{
			if (RefList.Num() < 100)
			{
				RefList.Add(JStr(R.ToString()));
				RefNames.Add(R.ToString());
			}
		}
		Out->SetStringField(TEXT("package"), PackageName);
		Out->SetStringField(TEXT("file"), File);
		Out->SetBoolField(TEXT("onDisk"), IFileManager::Get().FileExists(*File));
		Out->SetArrayField(TEXT("referencers"), RefList);
		Out->SetNumberField(TEXT("referencerTotal"), Refs.Num());
		const bool bForce = Bool(A, TEXT("force"), false);
		if (Bool(A, TEXT("dryRun"), false))
		{
			Out->SetBoolField(TEXT("dryRun"), true);
			Out->SetBoolField(TEXT("blocked"), Refs.Num() > 0 && !bForce);
			return true;
		}
		if (Refs.Num() && !bForce)
		{
			Error = FString::Printf(TEXT("%s 还被 %d 个资源引用，没有删：%s%s。先改掉引用，或确认后用 force=true 强删"), *PackageName, Refs.Num(), *FString::Join(RefNames.Num() > 10 ? TArray<FString>(RefNames.GetData(), 10) : RefNames, TEXT("、")), RefNames.Num() > 10 ? TEXT(" …") : TEXT(""));
			return false;
		}
		const TSet<UPackage*> Before = DirtyNow();
		TArray<UObject*> List;
		List.Add(Obj);
		const int32 Deleted = bForce ? ObjectTools::ForceDeleteObjects(List, false) : ObjectTools::DeleteObjects(List, false);
		if (Deleted < 1)
		{
			Error = TEXT("引擎没有删成功（资源可能正在被使用，详细原因在 UE 的输出日志里）");
			return false;
		}
		DirtyPackages().Remove(PackageName);
		TrackNewDirty(Before, Out);
		Out->SetBoolField(TEXT("fileRemoved"), !IFileManager::Get().FileExists(*File));
		return true;
	});

	// ---------- 撤回一步（事务 id 是写入时回的 tx）；蓝图撤回后重新编译，让生成的类跟上 ----------
	B.AddOp(TEXT("asset.undo"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		if (!UndoTx(Str(A, TEXT("tx")), Error))
		{
			return false;
		}
		const FString Path = Str(A, TEXT("path"));
		if (!Path.IsEmpty())
		{
			FString Ignored;
			if (UBlueprint* BP = Cast<UBlueprint>(LoadAsset(Path, Ignored)))
			{
				Out->SetObjectField(TEXT("compile"), CompileReport(BP, true));
			}
		}
		Out->SetBoolField(TEXT("undone"), true);
		return true;
	});
}
