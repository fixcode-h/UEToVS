#include "AgentPanelBridgeUtil.h"

#include "Engine/Blueprint.h"
#include "Misc/PackageName.h"
#include "JsonObjectConverter.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/EnumProperty.h"
#include "UObject/Package.h"
#include "UObject/TextProperty.h"
#include "UObject/UnrealType.h"

namespace AgentPanel
{
	FString Str(const TSharedPtr<FJsonObject>& Args, const TCHAR* Key, const FString& Default)
	{
		FString V;
		return Args.IsValid() && Args->TryGetStringField(Key, V) ? V : Default;
	}

	int32 Int(const TSharedPtr<FJsonObject>& Args, const TCHAR* Key, int32 Default)
	{
		double V = 0;
		return Args.IsValid() && Args->TryGetNumberField(Key, V) ? (int32)V : Default;
	}

	bool Bool(const TSharedPtr<FJsonObject>& Args, const TCHAR* Key, bool Default)
	{
		bool V = false;
		return Args.IsValid() && Args->TryGetBoolField(Key, V) ? V : Default;
	}

	bool StartsWithNoCase(const FString& S, const FString& Prefix)
	{
		return Prefix.IsEmpty() || S.StartsWith(Prefix, ESearchCase::IgnoreCase);
	}

	UObject* FindObjectByPath(const FString& Path, bool bLoad, FString& Error, bool bKeepBlueprint)
	{
		if (Path.IsEmpty())
		{
			Error = TEXT("没有给对象路径");
			return nullptr;
		}
		UObject* Obj = StaticFindObject(UObject::StaticClass(), nullptr, *Path);
		if (!Obj && bLoad)
		{
			// 资源路径可能只写了包名（/Game/Data/DT_Items），补上对象名再试
			FString Full = Path;
			if (!Full.Contains(TEXT(".")))
			{
				Full = Path + TEXT(".") + FPackageName::GetShortName(Path);
			}
			Obj = LoadObject<UObject>(nullptr, *Full, nullptr, LOAD_NoWarn | LOAD_Quiet);
		}
		if (!Obj)
		{
			Error = FString::Printf(TEXT("找不到对象：%s"), *Path);
			return nullptr;
		}
		// 读改属性时蓝图资源换成它生成的类的默认对象（类默认值）；读图表、变量定义的操作要蓝图本身
		if (bKeepBlueprint)
		{
			return Obj;
		}
		if (UBlueprint* BP = Cast<UBlueprint>(Obj))
		{
			if (BP->GeneratedClass)
			{
				return BP->GeneratedClass->GetDefaultObject();
			}
		}
		return Obj;
	}

	/** 拆一段：Name[2]["key"] → 名字和下标列表（数字下标或带引号的键） */
	static bool SplitSegment(const FString& Seg, FString& Name, TArray<FString>& Subs, FString& Error)
	{
		int32 Bracket = INDEX_NONE;
		Seg.FindChar(TEXT('['), Bracket);
		Name = Bracket == INDEX_NONE ? Seg : Seg.Left(Bracket);
		FString Rest = Bracket == INDEX_NONE ? FString() : Seg.Mid(Bracket);
		while (!Rest.IsEmpty())
		{
			int32 Close = INDEX_NONE;
			if (!Rest.StartsWith(TEXT("[")) || !Rest.FindChar(TEXT(']'), Close))
			{
				Error = FString::Printf(TEXT("属性路径写法不对：%s"), *Seg);
				return false;
			}
			FString Inner = Rest.Mid(1, Close - 1).TrimStartAndEnd();
			Inner.TrimQuotesInline();
			Subs.Add(Inner);
			Rest = Rest.Mid(Close + 1);
		}
		return true;
	}

	/** 路径按点分段，但引号里的点不算（Map 的键可能带点） */
	static TArray<FString> SplitPath(const FString& Path)
	{
		TArray<FString> Out;
		FString Cur;
		bool bQuote = false;
		for (TCHAR C : Path)
		{
			if (C == TEXT('"'))
			{
				bQuote = !bQuote;
			}
			if (C == TEXT('.') && !bQuote)
			{
				Out.Add(Cur);
				Cur.Reset();
				continue;
			}
			Cur.AppendChar(C);
		}
		if (!Cur.IsEmpty())
		{
			Out.Add(Cur);
		}
		return Out;
	}

	bool CanEditProp(const FProperty* Prop, const UObject* Owner, FString* Why)
	{
		auto No = [Why](const TCHAR* Reason)
		{
			if (Why)
			{
				*Why = Reason;
			}
			return false;
		};
		if (Prop->HasAnyPropertyFlags(CPF_EditConst))
		{
			return No(TEXT("只读属性（EditConst）"));
		}
		// 结构体成员（DataTable 的列、结构体里的字段）跟着外层属性走，这里只拦 EditConst
		if (!Prop->GetOwner<UClass>())
		{
			return true;
		}
		const bool bEdit = Prop->HasAnyPropertyFlags(CPF_Edit);
		const bool bBlueprintWrite = Prop->HasAnyPropertyFlags(CPF_BlueprintVisible) && !Prop->HasAnyPropertyFlags(CPF_BlueprintReadOnly);
		if (!bEdit && !bBlueprintWrite)
		{
			return No(TEXT("编辑器和蓝图里都不能改的内部状态，只能看"));
		}
		if (Owner && !bBlueprintWrite)
		{
			if (Owner->IsTemplate() && Prop->HasAnyPropertyFlags(CPF_DisableEditOnTemplate))
			{
				return No(TEXT("只能在实例上改，类默认值里不能改"));
			}
			if (!Owner->IsTemplate() && Prop->HasAnyPropertyFlags(CPF_DisableEditOnInstance))
			{
				return No(TEXT("只能在类默认值里改，实例上不能改"));
			}
		}
		return true;
	}

	bool ResolvePath(UStruct* Struct, void* Container, UObject* Owner, const FString& Path, FResolved& Out, FString& Error)
	{
		TArray<FString> Segs = SplitPath(Path);
		if (Segs.Num() == 0)
		{
			Error = TEXT("没有给属性路径");
			return false;
		}
		UStruct* CurStruct = Struct;
		void* CurContainer = Container;
		for (int32 i = 0; i < Segs.Num(); ++i)
		{
			FString Name;
			TArray<FString> Subs;
			if (!SplitSegment(Segs[i], Name, Subs, Error))
			{
				return false;
			}
			FProperty* Prop = CurStruct ? CurStruct->FindPropertyByName(FName(*Name)) : nullptr;
			if (!Prop)
			{
				// 蓝图变量的显示名和内部名可能不同，再按不区分大小写找一遍
				for (TFieldIterator<FProperty> It(CurStruct); It; ++It)
				{
					if (It->GetName().Equals(Name, ESearchCase::IgnoreCase) || It->GetAuthoredName().Equals(Name, ESearchCase::IgnoreCase))
					{
						Prop = *It;
						break;
					}
				}
			}
			if (!Prop)
			{
				Error = FString::Printf(TEXT("%s 上没有属性 %s"), CurStruct ? *CurStruct->GetName() : TEXT("?"), *Name);
				return false;
			}
			if (Out.bEditable && !CanEditProp(Prop, Owner, &Out.ReadOnlyWhy))
			{
				Out.bEditable = false;
				Out.ReadOnlyWhy = FString::Printf(TEXT("%s：%s"), *Name, *Out.ReadOnlyWhy);
			}
			void* Value = Prop->ContainerPtrToValuePtr<void>(CurContainer);
			for (const FString& Sub : Subs)
			{
				if (FArrayProperty* AP = CastField<FArrayProperty>(Prop))
				{
					FScriptArrayHelper H(AP, Value);
					const int32 Idx = FCString::Atoi(*Sub);
					if (!H.IsValidIndex(Idx))
					{
						Error = FString::Printf(TEXT("%s 只有 %d 个元素，没有 [%d]"), *Name, H.Num(), Idx);
						return false;
					}
					Value = H.GetRawPtr(Idx);
					Prop = AP->Inner;
				}
				else if (FMapProperty* MP = CastField<FMapProperty>(Prop))
				{
					FScriptMapHelper H(MP, Value);
					TArray<uint8> Key;
					Key.SetNumZeroed(MP->KeyProp->GetSize());
					MP->KeyProp->InitializeValue(Key.GetData());
					MP->KeyProp->ImportText_Direct(*Sub, Key.GetData(), nullptr, PPF_None);
					const int32 Idx = H.FindMapIndexWithKey(Key.GetData());
					MP->KeyProp->DestroyValue(Key.GetData());
					if (Idx == INDEX_NONE)
					{
						Error = FString::Printf(TEXT("%s 里没有键 %s"), *Name, *Sub);
						return false;
					}
					Value = H.GetValuePtr(Idx);
					Prop = MP->ValueProp;
				}
				else
				{
					Error = FString::Printf(TEXT("%s 不是数组或 Map，不能用 [%s]"), *Name, *Sub);
					return false;
				}
			}
			if (i == Segs.Num() - 1)
			{
				Out.Prop = Prop;
				Out.Value = Value;
				Out.Owner = Owner;
				Out.Container = Subs.Num() == 0 ? CurContainer : nullptr;
				return true;
			}
			// 还有下一段：结构体往里走，对象引用跟到那个对象上
			if (FStructProperty* SP = CastField<FStructProperty>(Prop))
			{
				CurStruct = SP->Struct;
				CurContainer = Value;
			}
			else if (FObjectPropertyBase* OP = CastField<FObjectPropertyBase>(Prop))
			{
				UObject* Next = OP->GetObjectPropertyValue(Value);
				if (!Next)
				{
					Error = FString::Printf(TEXT("%s 是空引用"), *Name);
					return false;
				}
				CurStruct = Next->GetClass();
				CurContainer = Next;
				Owner = Next;
			}
			else
			{
				Error = FString::Printf(TEXT("%s 不是结构体或对象，不能再往下取"), *Name);
				return false;
			}
		}
		return false;
	}

	static FString JsonString(const TSharedPtr<FJsonValue>& V)
	{
		FString S;
		TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> W = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&S);
		FJsonSerializer::Serialize(V, FString(), W);
		return S;
	}

	/**
	 * 对象引用一律只给路径，不往里展开。
	 * 为什么：引擎的 JSON 转换遇到 Instanced 子对象会把整个子对象的属性递归转出来，UMG 控件的 Slot、Lua 绑定的对象
	 * 互相引用时会一直递归到栈溢出，编辑器直接崩（EXCEPTION_STACK_OVERFLOW，2026-11-10 用户报）。
	 * 要看子对象，网页上点路径跳过去单独读。
	 */
	static TSharedPtr<FJsonValue> PathJson(const UObject* Obj)
	{
		if (!Obj)
		{
			return MakeShared<FJsonValueNull>();
		}
		return MakeShared<FJsonValueString>(Obj->GetPathName());
	}

	static TSharedPtr<FJsonValue> ExportObjectRef(FProperty* Prop, const void* Value)
	{
		if (CastField<FSoftObjectProperty>(Prop))
		{
			FString Text;
			Prop->ExportTextItem_Direct(Text, Value, nullptr, nullptr, PPF_None);
			return MakeShared<FJsonValueString>(Text);
		}
		if (const FObjectPropertyBase* OP = CastField<FObjectPropertyBase>(Prop))
		{
			const UObject* Obj = OP->GetObjectPropertyValue(Value);
			return PathJson(Obj);
		}
		if (const FInterfaceProperty* IP = CastField<FInterfaceProperty>(Prop))
		{
			const UObject* Obj = IP->GetPropertyValue(Value).GetObject();
			return PathJson(Obj);
		}
		return TSharedPtr<FJsonValue>();
	}

	TSharedPtr<FJsonValue> ToJson(FProperty* Prop, const void* Value, int32 MaxChars)
	{
		TSharedPtr<FJsonValue> V = ExportObjectRef(Prop, Value);
		if (!V.IsValid())
		{
			// 结构体、数组、Map 里的对象引用也走同一个回调（引擎把回调一路传给里面每一层）
			FJsonObjectConverter::CustomExportCallback Cb = FJsonObjectConverter::CustomExportCallback::CreateStatic(&ExportObjectRef);
			V = FJsonObjectConverter::UPropertyToJsonValue(Prop, Value, 0, 0, &Cb);
		}
		if (!V.IsValid())
		{
			FString Text;
			Prop->ExportTextItem_Direct(Text, Value, nullptr, nullptr, PPF_None);
			return MakeShared<FJsonValueString>(Text);
		}
		if (MaxChars > 0)
		{
			const FString S = JsonString(V);
			if (S.Len() > MaxChars)
			{
				// 太大：只回摘要，Agent / 网页再按路径取具体字段，免得一次把上下文撑满
				TSharedPtr<FJsonObject> Sum = MakeShared<FJsonObject>();
				Sum->SetBoolField(TEXT("_truncated"), true);
				Sum->SetNumberField(TEXT("_chars"), S.Len());
				if (const FArrayProperty* AP = CastField<FArrayProperty>(Prop))
				{
					FScriptArrayHelper H(AP, Value);
					Sum->SetNumberField(TEXT("_count"), H.Num());
				}
				else if (const FMapProperty* MP = CastField<FMapProperty>(Prop))
				{
					FScriptMapHelper H(MP, Value);
					Sum->SetNumberField(TEXT("_count"), H.Num());
				}
				Sum->SetStringField(TEXT("_preview"), S.Left(300));
				return MakeShared<FJsonValueObject>(Sum);
			}
		}
		return V;
	}

	bool FromJson(FProperty* Prop, void* Value, const TSharedPtr<FJsonValue>& Json, FString& Error)
	{
		if (!Json.IsValid())
		{
			Error = TEXT("没有给值");
			return false;
		}
		// 字符串类属性和字符串值：JSON 转换就够了；其他类型给的是字符串时，按 UE 文本格式（"(X=1,Y=2,Z=3)"、枚举名）写
		const bool bTextual = Prop->IsA<FStrProperty>() || Prop->IsA<FNameProperty>() || Prop->IsA<FTextProperty>();
		if (Json->Type == EJson::String && !bTextual)
		{
			const FString Text = Json->AsString();
			if (Prop->ImportText_Direct(*Text, Value, nullptr, PPF_None) != nullptr)
			{
				return true;
			}
		}
		if (FJsonObjectConverter::JsonValueToUProperty(Json, Prop, Value))
		{
			return true;
		}
		Error = FString::Printf(TEXT("值写不进 %s（类型 %s）：%s"), *Prop->GetName(), *TypeOf(Prop), *JsonString(Json));
		return false;
	}

	FString TypeOf(FProperty* Prop)
	{
		FString Extended;
		FString Cpp = Prop->GetCPPType(&Extended);
		return Cpp + Extended;
	}

	/** 枚举的值名和显示名（细节面板下拉里显示的是 DisplayName，例如 Axis1D (float)），编辑器里标了 Hidden 的值不列 */
	static void AddEnumInfo(TSharedPtr<FJsonObject>& P, const UEnum* Enum)
	{
		TArray<TSharedPtr<FJsonValue>> Names;
		TArray<TSharedPtr<FJsonValue>> Labels;
		for (int32 i = 0; i < Enum->NumEnums() - 1; ++i)
		{
#if WITH_EDITORONLY_DATA
			if (Enum->HasMetaData(TEXT("Hidden"), i))
			{
				continue;
			}
#endif
			Names.Add(MakeShared<FJsonValueString>(Enum->GetNameStringByIndex(i)));
			Labels.Add(MakeShared<FJsonValueString>(Enum->GetDisplayNameTextByIndex(i).ToString()));
		}
		P->SetArrayField(TEXT("enum"), Names);
		P->SetArrayField(TEXT("enumLabels"), Labels);
	}

	static TSharedPtr<FJsonObject> PropInfo(FProperty* Prop, const void* Container, int32 MaxChars, const UObject* Owner)
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		P->SetStringField(TEXT("name"), Prop->GetAuthoredName());
		P->SetStringField(TEXT("type"), TypeOf(Prop));
#if WITH_EDITORONLY_DATA
		P->SetStringField(TEXT("category"), Prop->GetMetaData(TEXT("Category")));
		P->SetStringField(TEXT("tooltip"), Prop->GetMetaData(TEXT("ToolTip")).Left(300));
		// 细节面板上显示的名字（bTriggerWhenPaused → Trigger when Paused，DisplayName 元数据优先）
		P->SetStringField(TEXT("displayName"), Prop->GetDisplayNameText().ToString());
#endif
		P->SetBoolField(TEXT("replicated"), Prop->HasAnyPropertyFlags(CPF_Net));
		// 细节面板的「高级」折叠区，和细节面板里根本不出现的内部状态（没有 EditAnywhere 这类标记），网页按这两个分组
		P->SetBoolField(TEXT("advanced"), Prop->HasAnyPropertyFlags(CPF_AdvancedDisplay));
		P->SetBoolField(TEXT("edit"), Prop->HasAnyPropertyFlags(CPF_Edit));
		FString Why;
		const bool bEditable = CanEditProp(Prop, Owner, &Why);
		P->SetBoolField(TEXT("readOnly"), !bEditable);
		if (!bEditable)
		{
			P->SetStringField(TEXT("readOnlyWhy"), Why);
		}
		if (const FEnumProperty* EP = CastField<FEnumProperty>(Prop))
		{
			AddEnumInfo(P, EP->GetEnum());
		}
		else if (const FByteProperty* BP = CastField<FByteProperty>(Prop))
		{
			if (BP->Enum)
			{
				AddEnumInfo(P, BP->Enum);
			}
		}
		const void* Value = Prop->ContainerPtrToValuePtr<void>(Container);
		// 容器：元素个数（值被截成摘要时网页也能显示「3 个元素」）、元素类型；EditFixedSize 的数组不能加删
		if (const FArrayProperty* AP = CastField<FArrayProperty>(Prop))
		{
			FScriptArrayHelper H(AP, Value);
			P->SetStringField(TEXT("container"), TEXT("array"));
			P->SetNumberField(TEXT("count"), H.Num());
			P->SetStringField(TEXT("inner"), TypeOf(AP->Inner));
			P->SetBoolField(TEXT("fixedSize"), Prop->HasAnyPropertyFlags(CPF_EditFixedSize));
		}
		else if (const FSetProperty* SP = CastField<FSetProperty>(Prop))
		{
			FScriptSetHelper H(SP, Value);
			P->SetStringField(TEXT("container"), TEXT("set"));
			P->SetNumberField(TEXT("count"), H.Num());
			P->SetStringField(TEXT("inner"), TypeOf(SP->ElementProp));
		}
		else if (const FMapProperty* MP = CastField<FMapProperty>(Prop))
		{
			FScriptMapHelper H(MP, Value);
			P->SetStringField(TEXT("container"), TEXT("map"));
			P->SetNumberField(TEXT("count"), H.Num());
			P->SetStringField(TEXT("inner"), TypeOf(MP->ValueProp));
		}
		P->SetField(TEXT("value"), ToJson(Prop, Value, MaxChars));
		return P;
	}

	TArray<TSharedPtr<FJsonValue>> ListProps(UStruct* Struct, const void* Container, const FString& Filter, int32 MaxChars, const UObject* Owner)
	{
		TArray<TSharedPtr<FJsonValue>> Out;
		for (TFieldIterator<FProperty> It(Struct); It; ++It)
		{
			FProperty* Prop = *It;
			// 只列 UPROPERTY 里编辑器或蓝图看得见的；纯 C++ 内部状态（没有这些标记）也列出来，排查时常要看，但跳过 Transient 的大缓存不做特殊处理
			if (!Filter.IsEmpty() && !StartsWithNoCase(Prop->GetAuthoredName(), Filter))
			{
				continue;
			}
			Out.Add(MakeShared<FJsonValueObject>(PropInfo(Prop, Container, MaxChars, Owner)));
		}
		return Out;
	}

	TArray<TSharedPtr<FJsonValue>> ParamsOf(UFunction* Func)
	{
		TArray<TSharedPtr<FJsonValue>> Out;
		for (TFieldIterator<FProperty> It(Func); It && It->HasAnyPropertyFlags(CPF_Parm); ++It)
		{
			if (It->HasAnyPropertyFlags(CPF_ReturnParm))
			{
				continue;
			}
			TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
			P->SetStringField(TEXT("name"), It->GetName());
			P->SetStringField(TEXT("type"), TypeOf(*It));
			P->SetBoolField(TEXT("out"), It->HasAnyPropertyFlags(CPF_OutParm) && !It->HasAnyPropertyFlags(CPF_ReferenceParm));
			UEnum* Enum = nullptr;
			if (const FEnumProperty* EP = CastField<FEnumProperty>(*It))
			{
				Enum = EP->GetEnum();
			}
			else if (const FByteProperty* BP = CastField<FByteProperty>(*It))
			{
				Enum = BP->Enum;
			}
			if (Enum)
			{
				TArray<TSharedPtr<FJsonValue>> Names;
				for (int32 i = 0; i < Enum->NumEnums() - 1; ++i)
				{
					Names.Add(MakeShared<FJsonValueString>(Enum->GetNameStringByIndex(i)));
				}
				P->SetArrayField(TEXT("enum"), Names);
			}
			// 对象参数（UObject*、TObjectPtr、接口）：带上能赋给它的类，面板的执行弹窗按它搜对象、只列能赋值的（桥接 1.9.0 起）
			UClass* ObjClass = nullptr;
			if (const FObjectPropertyBase* OP = CastField<FObjectPropertyBase>(*It))
			{
				if (!CastField<FClassProperty>(*It) && !CastField<FSoftClassProperty>(*It))
				{
					ObjClass = OP->PropertyClass;
				}
			}
			else if (const FInterfaceProperty* IP = CastField<FInterfaceProperty>(*It))
			{
				ObjClass = IP->InterfaceClass;
			}
			if (ObjClass)
			{
				P->SetStringField(TEXT("objectClass"), ObjClass->GetName());
				P->SetStringField(TEXT("objectClassPath"), ObjClass->GetPathName());
			}
			Out.Add(MakeShared<FJsonValueObject>(P));
		}
		return Out;
	}

	bool CallFunction(UObject* Target, UFunction* Func, const TSharedPtr<FJsonValue>& Args, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		TArray<uint8> Buf;
		Buf.SetNumZeroed(FMath::Max<int32>(Func->ParmsSize, 1));
		for (TFieldIterator<FProperty> It(Func); It && It->HasAnyPropertyFlags(CPF_Parm); ++It)
		{
			It->InitializeValue_InContainer(Buf.GetData());
		}
		bool bOk = true;
		int32 Index = 0;
		for (TFieldIterator<FProperty> It(Func); It && It->HasAnyPropertyFlags(CPF_Parm); ++It)
		{
			FProperty* P = *It;
			if (P->HasAnyPropertyFlags(CPF_ReturnParm))
			{
				continue;
			}
			TSharedPtr<FJsonValue> V;
			if (Args.IsValid() && Args->Type == EJson::Array)
			{
				const TArray<TSharedPtr<FJsonValue>>& Arr = Args->AsArray();
				if (Arr.IsValidIndex(Index))
				{
					V = Arr[Index];
				}
			}
			else if (Args.IsValid() && Args->Type == EJson::Object)
			{
				V = Args->AsObject()->TryGetField(P->GetName());
			}
			++Index;
			if (!V.IsValid() || V->IsNull())
			{
				continue; // 没给的参数用默认值（零值）
			}
			// 对象参数给的是路径：自己找（没加载的资源这时加载），并检查类型对不对，比 ImportText 报的错看得懂
			if (V->Type == EJson::String && (CastField<FObjectProperty>(P) || CastField<FInterfaceProperty>(P)) && !CastField<FClassProperty>(P))
			{
				const FString Ref = V->AsString().TrimStartAndEnd();
				void* Dest = P->ContainerPtrToValuePtr<void>(Buf.GetData());
				UObject* Obj = nullptr;
				if (!Ref.IsEmpty() && Ref != TEXT("None"))
				{
					Obj = FindObjectByPath(Ref, true, Error, true);
					if (!Obj)
					{
						bOk = false;
						break;
					}
				}
				if (FObjectProperty* OP = CastField<FObjectProperty>(P))
				{
					if (Obj && !Obj->IsA(OP->PropertyClass))
					{
						Error = FString::Printf(TEXT("参数 %s 要 %s，%s 是 %s"), *P->GetName(), *OP->PropertyClass->GetName(), *Obj->GetName(), *Obj->GetClass()->GetName());
						bOk = false;
						break;
					}
					OP->SetObjectPropertyValue(Dest, Obj);
				}
				else if (FInterfaceProperty* IP = CastField<FInterfaceProperty>(P))
				{
					if (Obj && !Obj->GetClass()->ImplementsInterface(IP->InterfaceClass))
					{
						Error = FString::Printf(TEXT("参数 %s 要实现 %s 接口，%s 没实现"), *P->GetName(), *IP->InterfaceClass->GetName(), *Obj->GetName());
						bOk = false;
						break;
					}
					FScriptInterface* SI = static_cast<FScriptInterface*>(Dest);
					SI->SetObject(Obj);
					SI->SetInterface(Obj ? Obj->GetInterfaceAddress(IP->InterfaceClass) : nullptr);
				}
				continue;
			}
			if (!FromJson(P, P->ContainerPtrToValuePtr<void>(Buf.GetData()), V, Error))
			{
				bOk = false;
				break;
			}
		}
		if (bOk)
		{
			const double Start = FPlatformTime::Seconds();
			Target->ProcessEvent(Func, Buf.GetData());
			Out->SetNumberField(TEXT("ms"), (FPlatformTime::Seconds() - Start) * 1000.0);
			TSharedPtr<FJsonObject> OutParams = MakeShared<FJsonObject>();
			for (TFieldIterator<FProperty> It(Func); It && It->HasAnyPropertyFlags(CPF_Parm); ++It)
			{
				void* V = It->ContainerPtrToValuePtr<void>(Buf.GetData());
				if (It->HasAnyPropertyFlags(CPF_ReturnParm))
				{
					Out->SetField(TEXT("ret"), ToJson(*It, V));
				}
				else if (It->HasAnyPropertyFlags(CPF_OutParm))
				{
					OutParams->SetField(It->GetName(), ToJson(*It, V));
				}
			}
			Out->SetObjectField(TEXT("out"), OutParams);
		}
		for (TFieldIterator<FProperty> It(Func); It && It->HasAnyPropertyFlags(CPF_Parm); ++It)
		{
			It->DestroyValue_InContainer(Buf.GetData());
		}
		return bOk;
	}

	TSet<FString>& DirtyPackages()
	{
		static TSet<FString> Set;
		return Set;
	}

	TSharedPtr<FJsonObject> Brief(const UObject* Obj)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetStringField(TEXT("path"), Obj->GetPathName());
		O->SetStringField(TEXT("name"), Obj->GetName());
		O->SetStringField(TEXT("class"), Obj->GetClass()->GetName());
		return O;
	}
}
