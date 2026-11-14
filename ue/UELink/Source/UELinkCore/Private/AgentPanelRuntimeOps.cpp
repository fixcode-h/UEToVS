// 运行时模块的操作：控制台、控制台变量、GM、Actor（按名字前缀）、组件、属性读写、函数调用、运行时对象、资源查询和 DataTable 读、
// 日志、截图。编辑器专用的（资源写入保存、PIE、Live Coding、编辑器视口截图）在编辑器模块里登记。
#include "AgentPanelBridge.h"
#include "AgentPanelBridgeUtil.h"
#include "AgentPanelGMLibrary.h"
#include "AgentPanelLog.h"
#include "AgentPanelLuaOps.h"
#include "AgentPanelPerf.h"
#include "AgentPanelUmg.h"

#include "Algo/Sort.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetRegistry/IAssetRegistry.h"
#include "Misc/Base64.h"
#include "Components/ActorComponent.h"
#include "Components/SceneComponent.h"
#include "Engine/DataTable.h"
#include "Engine/Engine.h"
#include "Engine/GameViewportClient.h"
#include "Engine/Level.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "GameFramework/Actor.h"
#include "GameFramework/CheatManager.h"
#include "GameFramework/GameModeBase.h"
#include "GameFramework/HUD.h"
#include "GameFramework/Pawn.h"
#include "GameFramework/PlayerController.h"
#include "HAL/IConsoleManager.h"
#include "ImageUtils.h"
#include "JsonObjectConverter.h"
#include "Templates/Function.h"
#include "UObject/UObjectHash.h"
#include "UObject/UnrealType.h"
#include "UObject/UObjectIterator.h"
#include "UnrealClient.h"

using namespace AgentPanel;

namespace
{
	/** 收集一条命令直接回的文字 */
	struct FCapture : public FOutputDevice
	{
		FString Text;
		virtual void Serialize(const TCHAR* V, ELogVerbosity::Type, const FName&) override
		{
			Text += V;
			Text += LINE_TERMINATOR;
		}
	};

	TSharedPtr<FJsonValue> Obj(const TSharedPtr<FJsonObject>& O) { return MakeShared<FJsonValueObject>(O); }

	/** 控制台变量是谁设的（「改过的」筛选和界面上的来源标签） */
	FString SetByName(EConsoleVariableFlags F)
	{
		switch (F & ECVF_SetByMask)
		{
		case ECVF_SetByConstructor: return TEXT("default");
		case ECVF_SetByScalability: return TEXT("scalability");
		case ECVF_SetByGameSetting: return TEXT("game");
		case ECVF_SetByProjectSetting: return TEXT("project");
		case ECVF_SetBySystemSettingsIni: return TEXT("ini");
		case ECVF_SetByDeviceProfile: return TEXT("deviceProfile");
		case ECVF_SetByConsoleVariablesIni: return TEXT("ini");
		case ECVF_SetByCommandline: return TEXT("commandline");
		case ECVF_SetByCode: return TEXT("code");
		case ECVF_SetByConsole: return TEXT("console");
		default: return TEXT("other");
		}
	}

	TSharedPtr<FJsonObject> CVarInfo(const TCHAR* Name, IConsoleObject* CObj)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetStringField(TEXT("name"), Name);
		O->SetStringField(TEXT("help"), FString(CObj->GetHelp()).Left(300));
		if (IConsoleVariable* V = CObj->AsVariable())
		{
			O->SetStringField(TEXT("kind"), TEXT("var"));
			O->SetStringField(TEXT("value"), V->GetString());
			O->SetStringField(TEXT("setBy"), SetByName(V->GetFlags()));
		}
		else
		{
			O->SetStringField(TEXT("kind"), TEXT("cmd"));
		}
		return O;
	}

	/** 这个世界里能接控制台命令的视口（PIE 和游戏世界才有），没有时走 GEngine->Exec */
	FString RunConsole(UWorld* World, const FString& Command)
	{
		if (UGameViewportClient* VC = World ? World->GetGameViewport() : nullptr)
		{
			// 和游戏里按 ~ 输入一样：先给本地玩家（PlayerController、CheatManager 的 Exec 函数），再给引擎
			return VC->ConsoleCommand(Command);
		}
		FCapture Out;
		if (GEngine)
		{
			GEngine->Exec(World, *Command, Out);
		}
		return Out.Text;
	}

	/** 能当 GM 用的类：这些类上的 Exec 函数在控制台里输名字就能执行 */
	bool IsExecHost(UClass* C)
	{
		return C->IsChildOf(UCheatManager::StaticClass()) || C->IsChildOf(APlayerController::StaticClass()) ||
			C->IsChildOf(AGameModeBase::StaticClass()) || C->IsChildOf(APawn::StaticClass()) || C->IsChildOf(AHUD::StaticClass());
	}

	bool IsTempClass(UClass* C)
	{
		const FString N = C->GetName();
		return N.StartsWith(TEXT("SKEL_")) || N.StartsWith(TEXT("REINST_")) || N.StartsWith(TEXT("TRASHCLASS_")) || N.StartsWith(TEXT("HOTRELOADED_"));
	}

	FString CategoryOf(UFunction* F, UClass* Owner)
	{
#if WITH_EDITORONLY_DATA
		const FString Cat = F->GetMetaData(TEXT("Category"));
		if (!Cat.IsEmpty())
		{
			return Cat;
		}
#endif
		return Owner->GetName();
	}

	FString TooltipOf(UFunction* F)
	{
#if WITH_EDITORONLY_DATA
		return F->GetMetaData(TEXT("ToolTip")).Left(400);
#else
		return FString();
#endif
	}

	TSharedPtr<FJsonObject> GmInfo(UFunction* F, UClass* Owner, const TCHAR* Source)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetStringField(TEXT("name"), F->GetName());
		O->SetStringField(TEXT("owner"), Owner->GetName());
		O->SetStringField(TEXT("category"), CategoryOf(F, Owner));
		O->SetStringField(TEXT("tooltip"), TooltipOf(F));
		O->SetStringField(TEXT("source"), Source);
		O->SetBoolField(TEXT("server"), F->HasAnyFunctionFlags(FUNC_NetServer));
		O->SetArrayField(TEXT("params"), ParamsOf(F));
		return O;
	}

	UClass* FindClassByName(const FString& Name)
	{
		if (Name.IsEmpty())
		{
			return nullptr;
		}
		if (Name.StartsWith(TEXT("/")))
		{
			return FindObject<UClass>(nullptr, *Name);
		}
		UClass* C = FindFirstObject<UClass>(*Name, EFindFirstObjectOptions::NativeFirst);
		if (!C && !Name.EndsWith(TEXT("_C")))
		{
			C = FindFirstObject<UClass>(*(Name + TEXT("_C")), EFindFirstObjectOptions::None);
		}
		return C;
	}

	FViewport* GameViewport(UWorld* World)
	{
		UGameViewportClient* VC = World ? World->GetGameViewport() : nullptr;
		return VC ? VC->Viewport : nullptr;
	}

	/**
	 * 细节面板数组那几个按钮（加一个、插入、复制、删除、清空）：直接改容器，不走 JSON 整体替换。
	 * 整体替换会丢掉实例化子对象（Triggers、Modifiers 这种 Instanced 数组）里的数据，元素也构造不出默认值。
	 * Change 给 PostEditChangeProperty 用，编辑器按它刷新细节面板
	 */
	static bool EditContainer(FProperty* Prop, void* Value, UObject* Owner, const FString& Op, int32 Index, EPropertyChangeType::Type& Change, FString& Error)
	{
		if (FArrayProperty* AP = CastField<FArrayProperty>(Prop))
		{
			FScriptArrayHelper H(AP, Value);
			if (Op != TEXT("clear") && Prop->HasAnyPropertyFlags(CPF_EditFixedSize))
			{
				Error = FString::Printf(TEXT("%s 是固定长度的数组（EditFixedSize），不能加删元素"), *Prop->GetName());
				return false;
			}
			if (Op == TEXT("add"))
			{
				H.AddValue();
				Change = EPropertyChangeType::ArrayAdd;
				return true;
			}
			if (Op == TEXT("clear"))
			{
				H.EmptyValues();
				Change = EPropertyChangeType::ArrayClear;
				return true;
			}
			if (!H.IsValidIndex(Index))
			{
				Error = FString::Printf(TEXT("%s 只有 %d 个元素，没有 [%d]"), *Prop->GetName(), H.Num(), Index);
				return false;
			}
			if (Op == TEXT("insert"))
			{
				H.InsertValues(Index, 1);
				Change = EPropertyChangeType::ArrayAdd;
				return true;
			}
			if (Op == TEXT("remove"))
			{
				H.RemoveValues(Index, 1);
				Change = EPropertyChangeType::ArrayRemove;
				return true;
			}
			if (Op == TEXT("duplicate"))
			{
				H.InsertValues(Index + 1, 1);
				AP->Inner->CopyCompleteValue(H.GetRawPtr(Index + 1), H.GetRawPtr(Index));
				// 实例化子对象：复制出一个新对象，不让两个元素指向同一个（和编辑器里「复制」一样）
				if (FObjectProperty* OP = CastField<FObjectProperty>(AP->Inner))
				{
					if (OP->HasAnyPropertyFlags(CPF_InstancedReference | CPF_PersistentInstance) || AP->HasAnyPropertyFlags(CPF_ContainsInstancedReference))
					{
						if (UObject* Src = OP->GetObjectPropertyValue(H.GetRawPtr(Index)))
						{
							OP->SetObjectPropertyValue(H.GetRawPtr(Index + 1), DuplicateObject(Src, Owner));
						}
					}
				}
				Change = EPropertyChangeType::Duplicate;
				return true;
			}
			Error = FString::Printf(TEXT("不认识的数组操作 %s（add / insert / duplicate / remove / clear）"), *Op);
			return false;
		}
		// 集合和映射只给清空：加元素要先有不重复的键，交给按值整体改
		if (Op == TEXT("clear"))
		{
			if (FSetProperty* SP = CastField<FSetProperty>(Prop))
			{
				FScriptSetHelper(SP, Value).EmptyElements();
				Change = EPropertyChangeType::ArrayClear;
				return true;
			}
			if (FMapProperty* MP = CastField<FMapProperty>(Prop))
			{
				FScriptMapHelper(MP, Value).EmptyValues();
				Change = EPropertyChangeType::ArrayClear;
				return true;
			}
		}
		Error = FString::Printf(TEXT("%s 不是数组，不能 %s"), *Prop->GetName(), *Op);
		return false;
	}
}

/** 截一个视口的图：缩到 MaxWidth 以内，PNG 转 base64（编辑器模块截编辑器视口时也用） */
bool AgentPanelCaptureViewport(FViewport* VP, int32 MaxWidth, TSharedPtr<FJsonObject>& Out, FString& Error)
{
	if (!VP)
	{
		Error = TEXT("这个世界没有视口（专用服务器、后台编辑器截不了图）");
		return false;
	}
	TArray<FColor> Pixels;
	if (!VP->ReadPixels(Pixels) || Pixels.Num() == 0)
	{
		Error = TEXT("读不到视口像素（窗口可能最小化了）");
		return false;
	}
	FIntPoint Size = VP->GetSizeXY();
	for (FColor& C : Pixels)
	{
		C.A = 255;
	}
	if (MaxWidth > 0 && Size.X > MaxWidth)
	{
		const int32 W = MaxWidth;
		const int32 H = FMath::Max(1, Size.Y * MaxWidth / Size.X);
		TArray<FColor> Small;
		FImageUtils::ImageResize(Size.X, Size.Y, Pixels, W, H, Small, false);
		Pixels = MoveTemp(Small);
		Size = FIntPoint(W, H);
	}
	TArray64<uint8> Png;
	FImageUtils::PNGCompressImageArray(Size.X, Size.Y, TArrayView64<const FColor>(Pixels.GetData(), Pixels.Num()), Png);
	Out->SetStringField(TEXT("png"), FBase64::Encode(Png.GetData(), (uint32)Png.Num()));
	Out->SetNumberField(TEXT("width"), Size.X);
	Out->SetNumberField(TEXT("height"), Size.Y);
	return true;
}

void FAgentPanelBridge::RegisterRuntimeOps()
{
	// ---------- 控制台 ----------
	AddOp(TEXT("console"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const FString Cmd = Str(A, TEXT("command")).TrimStartAndEnd();
		if (Cmd.IsEmpty())
		{
			Error = TEXT("没有给命令");
			return false;
		}
		UWorld* World = ResolveWorld(Str(A, TEXT("world")), Error);
		if (!World && !Str(A, TEXT("world")).IsEmpty())
		{
			return false;
		}
		Error.Reset();
		const uint64 Before = FAgentPanelLog::Get().LastSeq();
		const FString Direct = RunConsole(World, Cmd);
		// 输出 = 命令自己回的文字 + 执行期间打的日志（很多命令只往日志里写）
		TArray<TSharedPtr<FJsonValue>> Lines;
		TArray<FString> Split;
		Direct.ParseIntoArrayLines(Split, false);
		for (const FString& L : Split)
		{
			if (!L.IsEmpty())
			{
				Lines.Add(MakeShared<FJsonValueString>(L));
			}
		}
		for (const FString& L : FAgentPanelLog::Get().TextSince(Before, 400))
		{
			Lines.Add(MakeShared<FJsonValueString>(L));
		}
		Out->SetArrayField(TEXT("output"), Lines);
		Out->SetStringField(TEXT("world"), WorldId(World));
		return true;
	});

	AddOp(TEXT("cvars"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const FString Query = Str(A, TEXT("query"));
		const bool bChanged = Bool(A, TEXT("changed"), false);
		const bool bPrefix = Bool(A, TEXT("prefix"), true);
		const int32 Limit = Int(A, TEXT("limit"), 50);
		TArray<TSharedPtr<FJsonValue>> Items;
		int32 Total = 0;
		auto Visit = FConsoleObjectVisitor::CreateLambda([&](const TCHAR* Name, IConsoleObject* CObj)
		{
			if (CObj->TestFlags(ECVF_Unregistered))
			{
				return;
			}
			if (bChanged)
			{
				IConsoleVariable* V = CObj->AsVariable();
				const uint32 By = V ? (uint32)(V->GetFlags() & ECVF_SetByMask) : 0;
				// 「改过的」：控制台、代码、命令行设过的（默认值、画质档位、配置文件里的不算）
				if (!V || (By != ECVF_SetByConsole && By != ECVF_SetByCode && By != ECVF_SetByCommandline))
				{
					return;
				}
			}
			++Total;
			if (Items.Num() < Limit)
			{
				Items.Add(Obj(CVarInfo(Name, CObj)));
			}
		});
		if (bPrefix)
		{
			IConsoleManager::Get().ForEachConsoleObjectThatStartsWith(Visit, *Query);
		}
		else
		{
			IConsoleManager::Get().ForEachConsoleObjectThatContains(Visit, *Query);
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetNumberField(TEXT("total"), Total);
		return true;
	});

	AddOp(TEXT("cvar.set"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const FString Name = Str(A, TEXT("name"));
		IConsoleVariable* V = IConsoleManager::Get().FindConsoleVariable(*Name);
		if (!V)
		{
			Error = FString::Printf(TEXT("没有控制台变量 %s"), *Name);
			return false;
		}
		Out->SetStringField(TEXT("prev"), V->GetString());
		Out->SetStringField(TEXT("prevSetBy"), SetByName(V->GetFlags()));
		V->Set(*Str(A, TEXT("value")), ECVF_SetByConsole);
		Out->SetStringField(TEXT("value"), V->GetString());
		return true;
	});

	// ---------- GM ----------
	AddOp(TEXT("gm.list"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		TArray<TSharedPtr<FJsonValue>> Items;
		TSet<FString> Seen;
		for (TObjectIterator<UClass> It; It; ++It)
		{
			UClass* C = *It;
			if (IsTempClass(C) || C->HasAnyClassFlags(CLASS_NewerVersionExists | CLASS_Deprecated))
			{
				continue;
			}
			const bool bLibrary = C->IsChildOf(UAgentPanelGMLibrary::StaticClass()) && C != UAgentPanelGMLibrary::StaticClass();
			if (!bLibrary && !IsExecHost(C))
			{
				continue;
			}
			for (TFieldIterator<UFunction> F(C, EFieldIteratorFlags::ExcludeSuper); F; ++F)
			{
				if (bLibrary ? F->GetOwnerClass() == UAgentPanelGMLibrary::StaticClass() : !F->HasAnyFunctionFlags(FUNC_Exec))
				{
					continue;
				}
				// 同名的 Exec 函数在控制台里只会执行一个（先找到的那个），列一次
				const FString Key = bLibrary ? C->GetName() + TEXT(".") + F->GetName() : F->GetName();
				if (Seen.Contains(Key))
				{
					continue;
				}
				Seen.Add(Key);
				Items.Add(Obj(GmInfo(*F, C, bLibrary ? TEXT("library") : TEXT("exec"))));
			}
		}
		Out->SetArrayField(TEXT("items"), Items);
		return true;
	});

	AddOp(TEXT("gm.run"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UWorld* World = ResolveWorld(Str(A, TEXT("world")), Error);
		if (!World)
		{
			return false;
		}
		const FString Name = Str(A, TEXT("name"));
		const FString Owner = Str(A, TEXT("owner"));
		const TArray<TSharedPtr<FJsonValue>>* ArgsArr = nullptr;
		TArray<TSharedPtr<FJsonValue>> Args;
		if (A->TryGetArrayField(TEXT("args"), ArgsArr))
		{
			Args = *ArgsArr;
		}
		UClass* OwnerClass = FindClassByName(Owner);
		if (OwnerClass && OwnerClass->IsChildOf(UAgentPanelGMLibrary::StaticClass()))
		{
			// GM 库：在目标世界里建一个临时对象执行
			UFunction* F = OwnerClass->FindFunctionByName(FName(*Name));
			if (!F)
			{
				Error = FString::Printf(TEXT("%s 上没有 %s"), *Owner, *Name);
				return false;
			}
			UAgentPanelGMLibrary* Lib = NewObject<UAgentPanelGMLibrary>(GetTransientPackage(), OwnerClass);
			Lib->World = World;
			const uint64 Before = FAgentPanelLog::Get().LastSeq();
			const bool bOk = CallFunction(Lib, F, MakeShared<FJsonValueArray>(Args), Out, Error);
			TArray<TSharedPtr<FJsonValue>> Lines;
			for (const FString& L : FAgentPanelLog::Get().TextSince(Before, 400))
			{
				Lines.Add(MakeShared<FJsonValueString>(L));
			}
			Out->SetArrayField(TEXT("output"), Lines);
			return bOk;
		}
		// Exec 函数：拼成控制台命令执行（参数按 UE 的写法用空格隔开，带空格的加引号）
		FString Cmd = Name;
		for (const TSharedPtr<FJsonValue>& V : Args)
		{
			FString S = V->Type == EJson::String ? V->AsString() : V->Type == EJson::Boolean ? (V->AsBool() ? TEXT("true") : TEXT("false")) : FString::SanitizeFloat(V->AsNumber(), 0);
			if (S.Contains(TEXT(" ")))
			{
				S = TEXT("\"") + S + TEXT("\"");
			}
			Cmd += TEXT(" ") + S;
		}
		const uint64 Before = FAgentPanelLog::Get().LastSeq();
		const FString Direct = RunConsole(World, Cmd);
		TArray<TSharedPtr<FJsonValue>> Lines;
		TArray<FString> Split;
		Direct.ParseIntoArrayLines(Split, false);
		for (const FString& L : Split)
		{
			Lines.Add(MakeShared<FJsonValueString>(L));
		}
		for (const FString& L : FAgentPanelLog::Get().TextSince(Before, 400))
		{
			Lines.Add(MakeShared<FJsonValueString>(L));
		}
		Out->SetArrayField(TEXT("output"), Lines);
		Out->SetStringField(TEXT("command"), Cmd);
		return true;
	});

	// ---------- Actor 和组件（不做场景树：只按名字前缀找，用户 2026-11-10 定） ----------
	AddOp(TEXT("actors"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UWorld* World = ResolveWorld(Str(A, TEXT("world")), Error);
		if (!World)
		{
			return false;
		}
		const FString Prefix = Str(A, TEXT("prefix")).TrimStartAndEnd();
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 200), 1, 1000);
		TArray<TSharedPtr<FJsonValue>> Items;
		bool bMore = false;
		if (!Prefix.IsEmpty())
		{
			for (TActorIterator<AActor> It(World); It; ++It)
			{
				AActor* Act = *It;
				if (!IsValid(Act))
				{
					continue;
				}
				FString Label;
#if WITH_EDITOR
				Label = Act->GetActorLabel(false);
#endif
				if (!StartsWithNoCase(Act->GetName(), Prefix) && !StartsWithNoCase(Label, Prefix))
				{
					continue;
				}
				if (Items.Num() >= Limit)
				{
					bMore = true;
					break;
				}
				TSharedPtr<FJsonObject> O = Brief(Act);
				O->SetStringField(TEXT("label"), Label);
				O->SetStringField(TEXT("level"), Act->GetLevel() ? Act->GetLevel()->GetOuter()->GetName() : FString());
				APawn* Pawn = Cast<APawn>(Act);
				O->SetBoolField(TEXT("player"), Pawn && Pawn->IsPlayerControlled());
				Items.Add(Obj(O));
			}
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetBoolField(TEXT("more"), bMore);
		Out->SetStringField(TEXT("world"), WorldId(World));
		return true;
	});

	AddOp(TEXT("components"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		AActor* Act = Cast<AActor>(FindObjectByPath(Str(A, TEXT("path")), false, Error));
		if (!Act)
		{
			if (Error.IsEmpty())
			{
				Error = TEXT("这不是 Actor");
			}
			return false;
		}
		TArray<UActorComponent*> Comps;
		Act->GetComponents(Comps);
		TArray<TSharedPtr<FJsonValue>> Items;
		for (UActorComponent* C : Comps)
		{
			TSharedPtr<FJsonObject> O = Brief(C);
			USceneComponent* S = Cast<USceneComponent>(C);
			O->SetStringField(TEXT("parent"), S && S->GetAttachParent() ? S->GetAttachParent()->GetPathName() : FString());
			O->SetBoolField(TEXT("root"), S && S == Act->GetRootComponent());
			// 网页照 UE 组件面板画树：场景组件按挂接关系，非场景组件（移动组件这种）放分隔线下面；
			// 右边标「C++ / 蓝图 / 实例」，和 UE 的「在 C++ 中编辑 / 在蓝图中编辑」对应
			O->SetBoolField(TEXT("scene"), S != nullptr);
			const TCHAR* Created = TEXT("native");
			switch (C->CreationMethod)
			{
			case EComponentCreationMethod::SimpleConstructionScript:
				Created = TEXT("blueprint");
				break;
			case EComponentCreationMethod::UserConstructionScript:
				Created = TEXT("script");
				break;
			case EComponentCreationMethod::Instance:
				Created = TEXT("instance");
				break;
			default:
				break;
			}
			O->SetStringField(TEXT("created"), Created);
			Items.Add(Obj(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		return true;
	});

	// ---------- 属性 ----------
	AddOp(TEXT("props"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Target = FindObjectByPath(Str(A, TEXT("path")), Bool(A, TEXT("load"), true), Error);
		if (!Target)
		{
			return false;
		}
		Out->SetObjectField(TEXT("object"), Brief(Target));
		Out->SetStringField(TEXT("kind"), ObjectKind(Target));
		Out->SetBoolField(TEXT("widget"), IsUmgWidget(Target));
		if (Cast<UClass>(Target))
		{
			Out->SetStringField(TEXT("readOnlyWhy"), TEXT("这是类本身，不能改。改默认值打开它的资源，改运行中的表现选运行时实例"));
		}
		Out->SetArrayField(TEXT("props"), ListProps(Target->GetClass(), Target, Str(A, TEXT("filter")), Int(A, TEXT("maxChars"), 2000), Target));
		return true;
	});

	AddOp(TEXT("get"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Target = FindObjectByPath(Str(A, TEXT("path")), Bool(A, TEXT("load"), true), Error);
		if (!Target)
		{
			return false;
		}
		FResolved R;
		if (!ResolvePath(Target->GetClass(), Target, Target, Str(A, TEXT("prop")), R, Error))
		{
			return false;
		}
		Out->SetField(TEXT("value"), ToJson(R.Prop, R.Value, Int(A, TEXT("maxChars"), 20000)));
		Out->SetStringField(TEXT("type"), TypeOf(R.Prop));
		return true;
	});

	AddOp(TEXT("set"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Target = FindObjectByPath(Str(A, TEXT("path")), Bool(A, TEXT("load"), true), Error);
		if (!Target)
		{
			return false;
		}
		if (Cast<UClass>(Target))
		{
			Error = TEXT("这是类本身，不能改：改默认值打开它的资源，改运行中的表现选运行时实例");
			return false;
		}
		FResolved R;
		if (!ResolvePath(Target->GetClass(), Target, Target, Str(A, TEXT("prop")), R, Error))
		{
			return false;
		}
		if (!R.bEditable)
		{
			Error = FString::Printf(TEXT("不能改 %s"), *R.ReadOnlyWhy);
			return false;
		}
		Out->SetField(TEXT("prev"), ToJson(R.Prop, R.Value, 0));
		UObject* Owner = R.Owner ? R.Owner : Target;
		// array 给了就是数组按钮（add / insert / duplicate / remove / clear，index 是元素下标），不给就是按 value 整体写
		const FString ArrayOp = Str(A, TEXT("array"));
		const int32 Index = Int(A, TEXT("index"), -1);
		EPropertyChangeType::Type Change = EPropertyChangeType::ValueSet;
		// 资源（不在任何世界里、包不是临时的）：编辑器里按编辑器的方式改（可撤销、通知编辑器刷新、标脏）
		const bool bAsset = !Owner->GetWorld() && !Owner->GetOutermost()->HasAnyPackageFlags(PKG_PlayInEditor) && Owner->GetOutermost() != GetTransientPackage();
		bool bOk = false;
		if (bAsset)
		{
#if WITH_EDITOR
			Owner->Modify();
			Owner->PreEditChange(R.Prop);
#endif
			bOk = ArrayOp.IsEmpty() ? FromJson(R.Prop, R.Value, A->TryGetField(TEXT("value")), Error)
									: EditContainer(R.Prop, R.Value, Owner, ArrayOp, Index, Change, Error);
#if WITH_EDITOR
			FPropertyChangedEvent Ev(R.Prop, Change);
			Owner->PostEditChangeProperty(Ev);
#endif
			if (bOk)
			{
				Owner->MarkPackageDirty();
				DirtyPackages().Add(Owner->GetOutermost()->GetName());
			}
		}
		else
		{
			// 运行时对象：走属性的 setter，界面和逻辑才会跟着变（UMG 的 Visibility、颜色等只写内存不会刷新到屏幕上）
			// 数组按钮直接改容器（数组属性很少有 setter）
			bOk = ArrayOp.IsEmpty() ? SetRuntimeValue(Owner, R, A->TryGetField(TEXT("value")), Error)
									: EditContainer(R.Prop, R.Value, Owner, ArrayOp, Index, Change, Error);
		}
		Out->SetField(TEXT("value"), ToJson(R.Prop, R.Value, 0));
		Out->SetBoolField(TEXT("asset"), bAsset);
		Out->SetBoolField(TEXT("replicated"), R.Prop->HasAnyPropertyFlags(CPF_Net));
		UWorld* W = Owner->GetWorld();
		Out->SetBoolField(TEXT("client"), W && W->GetNetMode() == NM_Client);
		return bOk;
	});

	AddOp(TEXT("watch"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		TArray<TSharedPtr<FJsonValue>> Values;
		if (A->TryGetArrayField(TEXT("items"), Arr))
		{
			for (const TSharedPtr<FJsonValue>& V : *Arr)
			{
				const TSharedPtr<FJsonObject> Item = V->AsObject();
				TSharedPtr<FJsonObject> R = MakeShared<FJsonObject>();
				FString Err;
				UObject* Target = FindObjectByPath(Str(Item, TEXT("path")), false, Err);
				FResolved Res;
				if (Target && ResolvePath(Target->GetClass(), Target, Target, Str(Item, TEXT("prop")), Res, Err))
				{
					R->SetField(TEXT("value"), ToJson(Res.Prop, Res.Value, 500));
				}
				else
				{
					R->SetStringField(TEXT("error"), Err);
				}
				Values.Add(Obj(R));
			}
		}
		Out->SetArrayField(TEXT("values"), Values);
		return true;
	});

	// ---------- 函数 ----------
	AddOp(TEXT("functions"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Target = FindObjectByPath(Str(A, TEXT("path")), true, Error);
		if (!Target)
		{
			return false;
		}
		// all=true（面板网页，桥接 1.9.0 起）：列所有 UFUNCTION，按公有 / 受保护 / 私有分组，标出不是蓝图可调的；
		// 不给 all（Agent 的 ue_call、旧面板）照旧只列蓝图开放的
		const bool bAll = Bool(A, TEXT("all"), false);
		TArray<TSharedPtr<FJsonValue>> Items;
		for (TFieldIterator<UFunction> F(Target->GetClass()); F; ++F)
		{
			bool bAllowed = F->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_Exec);
#if WITH_EDITORONLY_DATA
			bAllowed = bAllowed || F->HasMetaData(TEXT("AgentPanel"));
#endif
			if ((!bAllowed && !bAll) || F->HasAnyFunctionFlags(FUNC_Delegate) || F->GetName().StartsWith(TEXT("ExecuteUbergraph")))
			{
				continue;
			}
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("access"), F->HasAnyFunctionFlags(FUNC_Private) ? TEXT("private") : F->HasAnyFunctionFlags(FUNC_Protected) ? TEXT("protected") : TEXT("public"));
			O->SetBoolField(TEXT("blueprint"), bAllowed);
			O->SetStringField(TEXT("name"), F->GetName());
			O->SetStringField(TEXT("owner"), F->GetOwnerClass()->GetName());
			O->SetStringField(TEXT("category"), CategoryOf(*F, F->GetOwnerClass()));
			O->SetStringField(TEXT("tooltip"), TooltipOf(*F));
			O->SetArrayField(TEXT("params"), ParamsOf(*F));
			FProperty* Ret = F->GetReturnProperty();
			O->SetStringField(TEXT("ret"), Ret ? TypeOf(Ret) : FString());
			O->SetBoolField(TEXT("pure"), F->HasAnyFunctionFlags(FUNC_BlueprintPure | FUNC_Const));
			Items.Add(Obj(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		return true;
	});

	AddOp(TEXT("call"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Target = FindObjectByPath(Str(A, TEXT("path")), true, Error);
		if (!Target)
		{
			return false;
		}
		const FString Name = Str(A, TEXT("function"));
		UFunction* F = Target->FindFunction(FName(*Name));
		if (!F)
		{
			Error = FString::Printf(TEXT("%s 上没有函数 %s"), *Target->GetClass()->GetName(), *Name);
			return false;
		}
		bool bAllowed = F->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_Exec) || Bool(A, TEXT("any"), false);
#if WITH_EDITORONLY_DATA
		bAllowed = bAllowed || F->HasMetaData(TEXT("AgentPanel"));
#endif
		if (!bAllowed)
		{
			Error = FString::Printf(TEXT("%s 不是 BlueprintCallable / Exec 函数，也没标 AgentPanel，不开放调用"), *Name);
			return false;
		}
		return CallFunction(Target, F, A->TryGetField(TEXT("args")), Out, Error);
	});

	// ---------- 运行时对象（按类、名字前缀） ----------
	AddOp(TEXT("objects"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const FString ClassName = Str(A, TEXT("class"));
		const FString Prefix = Str(A, TEXT("prefix"));
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 200), 1, 1000);
		UWorld* World = nullptr;
		if (!Str(A, TEXT("world")).IsEmpty())
		{
			World = ResolveWorld(Str(A, TEXT("world")), Error);
			if (!World)
			{
				return false;
			}
		}
		UClass* C = ClassName.IsEmpty() ? UObject::StaticClass() : FindClassByName(ClassName);
		if (!C)
		{
			Error = FString::Printf(TEXT("找不到类 %s"), *ClassName);
			return false;
		}
		if (ClassName.IsEmpty() && Prefix.IsEmpty())
		{
			Error = TEXT("要给类名或名字前缀");
			return false;
		}
		TArray<TSharedPtr<FJsonValue>> Items;
		bool bMore = false;
		bool bPartial = false;
		// 接口类没有自己的对象：按「类实现了这个接口」扫（同样限时）
		const bool bInterface = C->HasAnyClassFlags(CLASS_Interface);
		// 给函数参数选值时资源也要：只把「在别的世界里」的对象筛掉，不在任何世界里的资源留着
		const bool bAssets = Bool(A, TEXT("assets"), false);
		auto Visit = [&](UObject* O)
		{
			if (!IsValid(O) || O->HasAnyFlags(RF_ClassDefaultObject | RF_ArchetypeObject))
			{
				return true;
			}
			if (bInterface && !O->GetClass()->ImplementsInterface(C))
			{
				return true;
			}
			// Actor 另按编辑器里的显示名比一次（大纲里看到的名字和对象名常常不一样）
			AActor* Act = Cast<AActor>(O);
			FString Label;
#if WITH_EDITOR
			if (Act)
			{
				Label = Act->GetActorLabel(false);
			}
#endif
			const bool bNameHit = StartsWithNoCase(O->GetName(), Prefix) || (!Label.IsEmpty() && StartsWithNoCase(Label, Prefix));
			const UWorld* OW = O->GetWorld();
			if (!bNameHit || (World && OW != World && !(bAssets && !OW)))
			{
				return true;
			}
			if (Items.Num() >= Limit)
			{
				bMore = true;
				return false;
			}
			TSharedPtr<FJsonObject> B = Brief(O);
			B->SetStringField(TEXT("kind"), ObjectKind(O));
			if (Act)
			{
				// 面板的 Object 页按这几项把 Actor 画成原来 Actor 页的样子（显示名、所在关卡、是不是玩家）
				B->SetBoolField(TEXT("actor"), true);
				B->SetStringField(TEXT("label"), Label);
				B->SetStringField(TEXT("level"), Act->GetLevel() ? Act->GetLevel()->GetOuter()->GetName() : FString());
				APawn* Pawn = Cast<APawn>(Act);
				B->SetBoolField(TEXT("player"), Pawn && Pawn->IsPlayerControlled());
			}
			Items.Add(Obj(B));
			return true;
		};
		if (!ClassName.IsEmpty() && !bInterface)
		{
			// 有类名：按类的哈希只取这个类（含子类）的对象，快
			TArray<UObject*> Objs;
			GetObjectsOfClass(C, Objs, true);
			for (UObject* O : Objs)
			{
				if (!Visit(O))
				{
					break;
				}
			}
		}
		else
		{
			// 只有名字前缀：要扫全部 UObject（大项目几百万个）。在游戏线程上限时 50 毫秒，超时返回已找到的并标 partial，
			// 面板提示「加上类名搜得更快更全」，不让一次搜索卡住游戏帧
			const double Deadline = FPlatformTime::Seconds() + 0.05;
			int32 Count = 0;
			for (TObjectIterator<UObject> It; It; ++It)
			{
				if (!Visit(*It))
				{
					break;
				}
				if ((++Count & 4095) == 0 && FPlatformTime::Seconds() > Deadline)
				{
					bPartial = true;
					break;
				}
			}
		}
		// assets=true（函数执行弹窗给对象参数选值，桥接 1.9.0 起）：再从资源注册表补上这个类还没加载的资源，标 unloaded，
		// 执行时才加载。没写类名不补（那就是全部资源）
		if (bAssets && !ClassName.IsEmpty() && !bInterface && !bMore)
		{
			IAssetRegistry& AR = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
			FARFilter Filter;
			Filter.ClassPaths.Add(C->GetClassPathName());
			Filter.bRecursiveClasses = true;
			TArray<FAssetData> Found;
			AR.GetAssets(Filter, Found);
			for (const FAssetData& D : Found)
			{
				if (D.IsAssetLoaded() || !StartsWithNoCase(D.AssetName.ToString(), Prefix))
				{
					continue;
				}
				if (Items.Num() >= Limit)
				{
					bMore = true;
					break;
				}
				TSharedPtr<FJsonObject> B = MakeShared<FJsonObject>();
				B->SetStringField(TEXT("path"), D.GetObjectPathString());
				B->SetStringField(TEXT("name"), D.AssetName.ToString());
				B->SetStringField(TEXT("class"), D.AssetClassPath.GetAssetName().ToString());
				B->SetStringField(TEXT("kind"), TEXT("unloaded"));
				Items.Add(Obj(B));
			}
		}
		Out->SetBoolField(TEXT("partial"), bPartial);
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetBoolField(TEXT("more"), bMore);
		return true;
	});

	// ---------- 对象属性里放着的对象（函数执行弹窗「从对象的属性里选」） ----------
	// 列 path 这个对象的属性里现在放着的对象：单个引用、数组 / 集合元素、Map 的值、结构体里一层，Actor 再加上它的组件；
	// 给了 class 只列能赋给它的（是这个类或子类，接口按实现）
	AddOp(TEXT("object.refs"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UObject* Src = FindObjectByPath(Str(A, TEXT("path")), true, Error);
		if (!Src)
		{
			return false;
		}
		UClass* Want = nullptr;
		if (!Str(A, TEXT("class")).IsEmpty())
		{
			Want = FindClassByName(Str(A, TEXT("class")));
			if (!Want)
			{
				Error = FString::Printf(TEXT("找不到类 %s"), *Str(A, TEXT("class")));
				return false;
			}
		}
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 300), 1, 2000);
		TArray<TSharedPtr<FJsonValue>> Items;
		TSet<FString> Seen;
		auto Add = [&](UObject* O, const FString& Via)
		{
			if (!IsValid(O) || Items.Num() >= Limit)
			{
				return;
			}
			if (Want && !(Want->HasAnyClassFlags(CLASS_Interface) ? O->GetClass()->ImplementsInterface(Want) : O->IsA(Want)))
			{
				return;
			}
			const FString Key = O->GetPathName() + TEXT("|") + Via;
			if (Seen.Contains(Key))
			{
				return;
			}
			Seen.Add(Key);
			TSharedPtr<FJsonObject> B = Brief(O);
			B->SetStringField(TEXT("kind"), ObjectKind(O));
			B->SetStringField(TEXT("via"), Via);
			Items.Add(Obj(B));
		};
		// 一个属性值里的对象：对象引用、接口，数组 / 集合 / Map 值逐个看，结构体往里看（Depth 限两层，免得大结构体扫太深）
		TFunction<void(FProperty*, const void*, const FString&, int32)> Walk = [&](FProperty* P, const void* V, const FString& Via, int32 Depth)
		{
			if (const FObjectPropertyBase* OP = CastField<FObjectPropertyBase>(P))
			{
				if (!CastField<FSoftObjectProperty>(P) && !CastField<FClassProperty>(P))
				{
					Add(OP->GetObjectPropertyValue(V), Via);
				}
			}
			else if (const FInterfaceProperty* IP = CastField<FInterfaceProperty>(P))
			{
				Add(IP->GetPropertyValue(V).GetObject(), Via);
			}
			else if (FArrayProperty* AP = CastField<FArrayProperty>(P))
			{
				FScriptArrayHelper H(AP, V);
				for (int32 i = 0; i < H.Num(); ++i)
				{
					Walk(AP->Inner, H.GetRawPtr(i), FString::Printf(TEXT("%s[%d]"), *Via, i), Depth);
				}
			}
			else if (FSetProperty* SP = CastField<FSetProperty>(P))
			{
				FScriptSetHelper H(SP, V);
				for (int32 i = 0, n = 0; i < H.GetMaxIndex(); ++i)
				{
					if (H.IsValidIndex(i))
					{
						Walk(SP->ElementProp, H.GetElementPtr(i), FString::Printf(TEXT("%s{%d}"), *Via, n++), Depth);
					}
				}
			}
			else if (FMapProperty* MP = CastField<FMapProperty>(P))
			{
				FScriptMapHelper H(MP, V);
				for (int32 i = 0; i < H.GetMaxIndex(); ++i)
				{
					if (H.IsValidIndex(i))
					{
						FString K;
						MP->KeyProp->ExportTextItem_Direct(K, H.GetKeyPtr(i), nullptr, nullptr, PPF_None);
						Walk(MP->ValueProp, H.GetValuePtr(i), FString::Printf(TEXT("%s[%s]"), *Via, *K.Left(40)), Depth);
					}
				}
			}
			else if (FStructProperty* StP = CastField<FStructProperty>(P))
			{
				if (Depth < 2)
				{
					for (TFieldIterator<FProperty> It(StP->Struct); It; ++It)
					{
						Walk(*It, It->ContainerPtrToValuePtr<void>(V), Via + TEXT(".") + It->GetAuthoredName(), Depth + 1);
					}
				}
			}
		};
		for (TFieldIterator<FProperty> It(Src->GetClass()); It && Items.Num() < Limit; ++It)
		{
			Walk(*It, It->ContainerPtrToValuePtr<void>(Src), It->GetAuthoredName(), 0);
		}
		if (AActor* Act = Cast<AActor>(Src))
		{
			TArray<UActorComponent*> Comps;
			Act->GetComponents(Comps);
			for (UActorComponent* Comp : Comps)
			{
				Add(Comp, FString::Printf(TEXT("组件 %s"), *Comp->GetName()));
			}
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetBoolField(TEXT("more"), Items.Num() >= Limit);
		return true;
	});

	// ---------- 类（面板里「选类型」的可搜索下拉） ----------
	// 按名字找已加载的类：名字开头匹配的排前面，其余是名字里包含的；不给 q 不列（几万个类）
	AddOp(TEXT("classes"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const FString Q = Str(A, TEXT("q")).TrimStartAndEnd();
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 50), 1, 500);
		// base（桥接 1.9.0 起）：只列能赋给它的类（它自己和子类，接口按实现），函数执行弹窗按参数类型限定；给了 base 时空 q 也列
		UClass* Base = nullptr;
		if (!Str(A, TEXT("base")).IsEmpty())
		{
			Base = FindClassByName(Str(A, TEXT("base")));
			if (!Base)
			{
				Error = FString::Printf(TEXT("找不到类 %s"), *Str(A, TEXT("base")));
				return false;
			}
		}
		TArray<UClass*> Head;
		TArray<UClass*> Tail;
		if (!Q.IsEmpty() || Base)
		{
			for (TObjectIterator<UClass> It; It; ++It)
			{
				UClass* C = *It;
				if (IsTempClass(C) || C->HasAnyClassFlags(CLASS_NewerVersionExists))
				{
					continue;
				}
				if (Base && !(Base->HasAnyClassFlags(CLASS_Interface) ? (C == Base || C->ImplementsInterface(Base)) : C->IsChildOf(Base)))
				{
					continue;
				}
				const FString N = C->GetName();
				if (StartsWithNoCase(N, Q))
				{
					Head.Add(C);
				}
				else if (N.Contains(Q, ESearchCase::IgnoreCase))
				{
					Tail.Add(C);
				}
			}
		}
		// 名字短的更接近输入的字，排前面
		auto ByName = [](const UClass* L, const UClass* R)
		{
			const FString LN = L->GetName();
			const FString RN = R->GetName();
			return LN.Len() != RN.Len() ? LN.Len() < RN.Len() : LN < RN;
		};
		Algo::Sort(Head, ByName);
		Algo::Sort(Tail, ByName);
		Head.Append(Tail);
		// 参数本身的类型排第一个（下拉默认就是它）
		if (Base && Head.Remove(Base) > 0)
		{
			Head.Insert(Base, 0);
		}
		TArray<TSharedPtr<FJsonValue>> Items;
		for (int32 i = 0; i < Head.Num() && i < Limit; ++i)
		{
			UClass* C = Head[i];
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("name"), C->GetName());
			O->SetStringField(TEXT("path"), C->GetPathName());
			O->SetStringField(TEXT("parent"), C->GetSuperClass() ? C->GetSuperClass()->GetName() : FString());
			O->SetBoolField(TEXT("actor"), C->IsChildOf(AActor::StaticClass()));
			Items.Add(Obj(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetBoolField(TEXT("more"), Head.Num() > Limit);
		return true;
	});

	// 资源类型：资源注册表里真有资源的类（带个数）和它们的父类；有 q 时后面再接上名字对得上的其他类，
	// 这样引擎里任何类型都选得到。扫一遍注册表在大项目里要上百毫秒，结果留 30 秒，打字时不反复扫
	AddOp(TEXT("asset.classes"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		static TMap<FTopLevelAssetPath, int32> Counts;
		static double CountsAt = 0;
		const double Now = FPlatformTime::Seconds();
		if (Counts.Num() == 0 || Now - CountsAt > 30.0)
		{
			Counts.Reset();
			IAssetRegistry& AR = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
			AR.EnumerateAllAssets([](const FAssetData& D)
			{
				Counts.FindOrAdd(D.AssetClassPath)++;
				return true;
			});
			// 父类也算资源类型：按 DataAsset 搜能搜出所有子类的资源
			TArray<FTopLevelAssetPath> Direct;
			Counts.GetKeys(Direct);
			for (const FTopLevelAssetPath& P : Direct)
			{
				UClass* C = FindObject<UClass>(nullptr, *P.ToString());
				for (C = C ? C->GetSuperClass() : nullptr; C && C != UObject::StaticClass(); C = C->GetSuperClass())
				{
					Counts.FindOrAdd(C->GetClassPathName());
				}
			}
			CountsAt = Now;
		}
		const FString Q = Str(A, TEXT("q")).TrimStartAndEnd();
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 200), 1, 2000);
		struct FRow
		{
			FString Name;
			FString Path;
			int32 Count = 0;
			// 0 名字开头匹配，1 名字里包含，2 注册表里没有资源的其他类
			int32 Rank = 0;
		};
		TArray<FRow> Rows;
		for (const TPair<FTopLevelAssetPath, int32>& P : Counts)
		{
			const FString N = P.Key.GetAssetName().ToString();
			const bool bHead = Q.IsEmpty() || StartsWithNoCase(N, Q);
			if (!bHead && !N.Contains(Q, ESearchCase::IgnoreCase))
			{
				continue;
			}
			FRow R;
			R.Name = N;
			R.Path = P.Key.ToString();
			R.Count = P.Value;
			R.Rank = bHead ? 0 : 1;
			Rows.Add(MoveTemp(R));
		}
		if (!Q.IsEmpty())
		{
			for (TObjectIterator<UClass> It; It; ++It)
			{
				UClass* C = *It;
				// Actor、组件存不成单独的资源，不往资源类型里放
				if (IsTempClass(C) || C->HasAnyClassFlags(CLASS_NewerVersionExists) || C->IsChildOf(AActor::StaticClass()) ||
					C->IsChildOf(UActorComponent::StaticClass()) || Counts.Contains(C->GetClassPathName()))
				{
					continue;
				}
				const FString N = C->GetName();
				if (!N.Contains(Q, ESearchCase::IgnoreCase))
				{
					continue;
				}
				FRow R;
				R.Name = N;
				R.Path = C->GetPathName();
				R.Rank = 2;
				Rows.Add(MoveTemp(R));
			}
		}
		const bool bByCount = Q.IsEmpty();
		Algo::Sort(Rows, [bByCount](const FRow& L, const FRow& R)
		{
			if (L.Rank != R.Rank)
			{
				return L.Rank < R.Rank;
			}
			if (bByCount && L.Count != R.Count)
			{
				return L.Count > R.Count;
			}
			return L.Name.Len() != R.Name.Len() ? L.Name.Len() < R.Name.Len() : L.Name < R.Name;
		});
		TArray<TSharedPtr<FJsonValue>> Items;
		for (int32 i = 0; i < Rows.Num() && i < Limit; ++i)
		{
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("name"), Rows[i].Name);
			O->SetStringField(TEXT("path"), Rows[i].Path);
			O->SetNumberField(TEXT("count"), Rows[i].Count);
			Items.Add(Obj(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetBoolField(TEXT("more"), Rows.Num() > Limit);
		return true;
	});

	// ---------- 资源 ----------
	AddOp(TEXT("assets"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		IAssetRegistry& AR = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
		FARFilter Filter;
		Filter.bRecursivePaths = true;
		Filter.PackagePaths.Add(FName(*Str(A, TEXT("path"), TEXT("/Game"))));
		const FString ClassName = Str(A, TEXT("class"));
		if (!ClassName.IsEmpty())
		{
			UClass* C = FindClassByName(ClassName);
			if (!C)
			{
				Error = FString::Printf(TEXT("找不到类 %s"), *ClassName);
				return false;
			}
			Filter.ClassPaths.Add(C->GetClassPathName());
			Filter.bRecursiveClasses = true;
		}
		TArray<FAssetData> Found;
		AR.GetAssets(Filter, Found);
		const FString Prefix = Str(A, TEXT("prefix"));
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 300), 1, 2000);
		TArray<TSharedPtr<FJsonValue>> Items;
		int32 Total = 0;
		for (const FAssetData& D : Found)
		{
			if (!StartsWithNoCase(D.AssetName.ToString(), Prefix))
			{
				continue;
			}
			++Total;
			if (Items.Num() >= Limit)
			{
				continue;
			}
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("path"), D.GetObjectPathString());
			O->SetStringField(TEXT("name"), D.AssetName.ToString());
			O->SetStringField(TEXT("class"), D.AssetClassPath.GetAssetName().ToString());
			O->SetStringField(TEXT("package"), D.PackageName.ToString());
			Items.Add(Obj(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetNumberField(TEXT("total"), Total);
		return true;
	});

	AddOp(TEXT("datatable"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UDataTable* DT = Cast<UDataTable>(FindObjectByPath(Str(A, TEXT("path")), true, Error));
		if (!DT)
		{
			if (Error.IsEmpty())
			{
				Error = TEXT("这不是 DataTable");
			}
			return false;
		}
		const UScriptStruct* RowStruct = DT->GetRowStruct();
		if (!RowStruct)
		{
			Error = TEXT("DataTable 没有行结构（行结构的类可能没编译进来）");
			return false;
		}
		TArray<TSharedPtr<FJsonValue>> Columns;
		for (TFieldIterator<FProperty> It(RowStruct); It; ++It)
		{
			TSharedPtr<FJsonObject> C = MakeShared<FJsonObject>();
			C->SetStringField(TEXT("name"), It->GetAuthoredName());
			C->SetStringField(TEXT("type"), TypeOf(*It));
			C->SetBoolField(TEXT("complex"), It->IsA<FStructProperty>() || It->IsA<FArrayProperty>() || It->IsA<FMapProperty>() || It->IsA<FSetProperty>());
			Columns.Add(Obj(C));
		}
		const FString Filter = Str(A, TEXT("filter"));
		const int32 Offset = FMath::Max(0, Int(A, TEXT("offset"), 0));
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 100), 1, 1000);
		TArray<TSharedPtr<FJsonValue>> Rows;
		int32 Total = 0;
		// 性能：上万行的表每翻一页都把整张表转 JSON 太慢。只有当前页的行才转 JSON；
		// 有筛选时才逐行拼搜索文本（导出成文本，比转 JSON 便宜），没筛选只数行数
		for (const TPair<FName, uint8*>& Row : DT->GetRowMap())
		{
			if (!Filter.IsEmpty() && !StartsWithNoCase(Row.Key.ToString(), Filter))
			{
				bool bHit = false;
				for (TFieldIterator<FProperty> It(RowStruct); It && !bHit; ++It)
				{
					FString Cell;
					It->ExportTextItem_Direct(Cell, It->ContainerPtrToValuePtr<void>(Row.Value), nullptr, nullptr, PPF_None);
					bHit = Cell.Contains(Filter, ESearchCase::IgnoreCase);
				}
				if (!bHit)
				{
					continue;
				}
			}
			++Total;
			if (Total <= Offset || Rows.Num() >= Limit)
			{
				continue;
			}
			TSharedPtr<FJsonObject> Values = MakeShared<FJsonObject>();
			for (TFieldIterator<FProperty> It(RowStruct); It; ++It)
			{
				Values->SetField(It->GetAuthoredName(), ToJson(*It, It->ContainerPtrToValuePtr<void>(Row.Value), 200));
			}
			TSharedPtr<FJsonObject> R = MakeShared<FJsonObject>();
			R->SetStringField(TEXT("name"), Row.Key.ToString());
			R->SetObjectField(TEXT("values"), Values);
			Rows.Add(Obj(R));
		}
		Out->SetStringField(TEXT("struct"), RowStruct->GetName());
		Out->SetArrayField(TEXT("columns"), Columns);
		Out->SetArrayField(TEXT("rows"), Rows);
		Out->SetNumberField(TEXT("total"), Total);
		Out->SetBoolField(TEXT("dirty"), DT->GetOutermost()->IsDirty());
		return true;
	});

	AddOp(TEXT("datatable.row"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UDataTable* DT = Cast<UDataTable>(FindObjectByPath(Str(A, TEXT("path")), true, Error));
		if (!DT || !DT->GetRowStruct())
		{
			if (Error.IsEmpty())
			{
				Error = TEXT("这不是 DataTable");
			}
			return false;
		}
		uint8* Row = DT->FindRowUnchecked(FName(*Str(A, TEXT("row"))));
		if (!Row)
		{
			Error = FString::Printf(TEXT("%s 里没有行 %s"), *DT->GetName(), *Str(A, TEXT("row")));
			return false;
		}
		Out->SetArrayField(TEXT("props"), ListProps(const_cast<UScriptStruct*>(DT->GetRowStruct()), Row, Str(A, TEXT("filter")), 20000));
		return true;
	});

	// ---------- 日志 ----------
	AddOp(TEXT("log.tail"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		double Since = 0;
		A->TryGetNumberField(TEXT("since"), Since);
		TSharedPtr<FJsonObject> R = FAgentPanelLog::Get().Tail((uint64)Since, FMath::Clamp(Int(A, TEXT("limit"), 300), 1, 5000),
			Str(A, TEXT("category")), Str(A, TEXT("verbosity")), Str(A, TEXT("contains")));
		// 5.8 起 Values 的键是 UE::FSharedString，不能绑成 TPair<FString, …>；*Key 在各版本都是 const TCHAR*
		for (const auto& P : R->Values)
		{
			Out->SetField(FString(*P.Key), P.Value);
		}
		return true;
	});

	AddOp(TEXT("log.clear"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		return FAgentPanelLog::Get().ClearFile(Bool(A, TEXT("backup"), false), Out, Error);
	});

	// ---------- 截图（游戏视口；编辑器视口由编辑器模块接管这个操作） ----------
	AddOp(TEXT("screenshot"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		UWorld* World = ResolveWorld(Str(A, TEXT("world")), Error);
		if (!World)
		{
			return false;
		}
		Out->SetStringField(TEXT("world"), WorldId(World));
		return AgentPanelCaptureViewport(GameViewport(World), Int(A, TEXT("maxWidth"), 1280), Out, Error);
	});

	// ---------- Lua（项目里有 UnLua 才编进来，见 AgentPanelLua.cpp） ----------
	AgentPanelRegisterLuaOps(*this);
	RegisterUmgOps(*this);
	RegisterPerfOps(*this);
}
