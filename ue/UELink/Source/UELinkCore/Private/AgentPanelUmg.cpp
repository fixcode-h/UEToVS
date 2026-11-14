#include "AgentPanelUmg.h"
#include "AgentPanelBridge.h"
#include "AgentPanelBridgeUtil.h"

#include "Blueprint/UserWidget.h"
#include "Blueprint/WidgetTree.h"
#include "Components/ActorComponent.h"
#include "Components/PanelSlot.h"
#include "Components/PanelWidget.h"
#include "Components/SceneComponent.h"
#include "Components/Widget.h"
#include "Engine/World.h"
#include "UObject/Package.h"
#include "UObject/UnrealType.h"

namespace AgentPanel
{
	FString ObjectKind(const UObject* Obj)
	{
		if (Cast<UClass>(Obj))
		{
			return TEXT("class");
		}
		if (Obj->HasAnyFlags(RF_ClassDefaultObject | RF_ArchetypeObject))
		{
			return TEXT("asset");
		}
		const UPackage* Pkg = Obj->GetOutermost();
		if (Obj->GetWorld() || Pkg == GetTransientPackage() || Pkg->HasAnyPackageFlags(PKG_PlayInEditor))
		{
			return TEXT("instance");
		}
		return TEXT("asset");
	}

	bool IsUmgWidget(const UObject* Obj)
	{
		return Obj && Obj->IsA<UWidget>() && !Obj->HasAnyFlags(RF_ClassDefaultObject);
	}

	/** 把改过的值刷到表现上：UMG 控件和 Slot 同步给 Slate，组件重算变换、重建渲染状态 */
	static void Refresh(UObject* Owner)
	{
		if (UWidget* W = Cast<UWidget>(Owner))
		{
			// 还没建出 Slate 控件时不用同步（有些控件的 SynchronizeProperties 不检查空）
			if (W->GetCachedWidget().IsValid())
			{
				W->SynchronizeProperties();
				W->InvalidateLayoutAndVolatility();
			}
		}
		else if (UPanelSlot* S = Cast<UPanelSlot>(Owner))
		{
			if (S->Content && S->Content->GetCachedWidget().IsValid())
			{
				S->SynchronizeProperties();
				S->Content->InvalidateLayoutAndVolatility();
			}
		}
		else if (USceneComponent* SC = Cast<USceneComponent>(Owner))
		{
			if (SC->IsRegistered())
			{
				SC->UpdateComponentToWorld();
				SC->MarkRenderStateDirty();
			}
		}
		else if (UActorComponent* AC = Cast<UActorComponent>(Owner))
		{
			if (AC->IsRegistered())
			{
				AC->MarkRenderStateDirty();
			}
		}
	}

	bool SetRuntimeValue(UObject* Owner, const FResolved& R, const TSharedPtr<FJsonValue>& Json, FString& Error)
	{
		// 只有对象自己的属性（不是结构体里的字段、不是数组元素）才有 setter
		const bool bOnObject = R.Container != nullptr && R.Container == static_cast<void*>(Owner);
		if (bOnObject && R.Prop->HasSetter())
		{
			void* Temp = R.Prop->AllocateAndInitializeValue();
			R.Prop->CopyCompleteValue(Temp, R.Value);
			const bool bOk = FromJson(R.Prop, Temp, Json, Error);
			if (bOk)
			{
				R.Prop->CallSetter(R.Container, Temp);
			}
			R.Prop->DestroyAndFreeValue(Temp);
			if (bOk)
			{
				Refresh(Owner);
			}
			return bOk;
		}
#if WITH_EDITORONLY_DATA
		if (bOnObject)
		{
			const FString SetterName = R.Prop->GetMetaData(TEXT("BlueprintSetter"));
			UFunction* Setter = SetterName.IsEmpty() ? nullptr : Owner->FindFunction(FName(*SetterName));
			if (Setter)
			{
				TArray<TSharedPtr<FJsonValue>> Args;
				Args.Add(Json);
				TSharedPtr<FJsonObject> Ignored = MakeShared<FJsonObject>();
				if (!CallFunction(Owner, Setter, MakeShared<FJsonValueArray>(Args), Ignored, Error))
				{
					return false;
				}
				Refresh(Owner);
				return true;
			}
		}
#endif
		if (!FromJson(R.Prop, R.Value, Json, Error))
		{
			return false;
		}
		Refresh(Owner);
		return true;
	}

	/** 控件树的一个节点；嵌套的 UserWidget 也展开它自己的树。Visited 和深度上限防止异常的环把栈撑爆 */
	static TSharedPtr<FJsonObject> WidgetNode(UWidget* W, TSet<UWidget*>& Visited, int32 Depth, int32& Budget)
	{
		TSharedPtr<FJsonObject> N = Brief(W);
		const UEnum* VisEnum = StaticEnum<ESlateVisibility>();
		N->SetStringField(TEXT("visibility"), VisEnum ? VisEnum->GetNameStringByValue((int64)W->GetVisibility()) : FString());
		N->SetBoolField(TEXT("user"), W->IsA<UUserWidget>());
		TArray<UWidget*> Kids;
		if (UUserWidget* U = Cast<UUserWidget>(W))
		{
			if (U->WidgetTree && U->WidgetTree->RootWidget)
			{
				Kids.Add(U->WidgetTree->RootWidget);
			}
		}
		else if (UPanelWidget* P = Cast<UPanelWidget>(W))
		{
			Kids = P->GetAllChildren();
		}
		TArray<TSharedPtr<FJsonValue>> Children;
		for (UWidget* K : Kids)
		{
			if (!IsValid(K) || Visited.Contains(K))
			{
				continue;
			}
			if (Depth >= 64 || Budget <= 0)
			{
				N->SetBoolField(TEXT("truncated"), true);
				break;
			}
			Visited.Add(K);
			--Budget;
			Children.Add(MakeShared<FJsonValueObject>(WidgetNode(K, Visited, Depth + 1, Budget)));
		}
		N->SetArrayField(TEXT("children"), Children);
		return N;
	}

	void RegisterUmgOps(FAgentPanelBridge& Bridge)
	{
		Bridge.AddOp(TEXT("umg.tree"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
		{
			UObject* Target = FindObjectByPath(Str(A, TEXT("path")), Bool(A, TEXT("load"), true), Error);
			if (!Target)
			{
				return false;
			}
			UWidget* W = IsUmgWidget(Target) ? Cast<UWidget>(Target) : nullptr;
			if (!W)
			{
				// 不是控件：网页照常只显示属性
				Out->SetField(TEXT("root"), MakeShared<FJsonValueNull>());
				return true;
			}
			// 选中的是树里的某个控件：从包着它的那个 UserWidget 起整棵树（WidgetTree 的 Outer 就是它）
			UUserWidget* Root = Cast<UUserWidget>(W);
			if (!Root)
			{
				Root = W->GetTypedOuter<UUserWidget>();
			}
			if (!Root)
			{
				Out->SetField(TEXT("root"), MakeShared<FJsonValueNull>());
				return true;
			}
			TSet<UWidget*> Visited;
			Visited.Add(Root);
			int32 Budget = 5000;
			Out->SetObjectField(TEXT("root"), WidgetNode(Root, Visited, 0, Budget));
			// 外面还套着一层 UserWidget 时给出它，网页上能往上一层看
			if (const UUserWidget* Up = Root->GetTypedOuter<UUserWidget>())
			{
				Out->SetStringField(TEXT("parent"), Up->GetPathName());
			}
			return true;
		});
	}
}
