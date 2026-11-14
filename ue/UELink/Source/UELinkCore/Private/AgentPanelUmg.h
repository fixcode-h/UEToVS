// 运行时对象的分类、运行时改值（走 setter 并刷新表现）、UMG 控件树。从属性读写里拆出来，免得 RuntimeOps 越写越长
#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonValue.h"

class UObject;
class FAgentPanelBridge;

namespace AgentPanel
{
	struct FResolved;

	/** 对象是哪一类：class（类本身）、asset（资源、类默认值、模板）、instance（关卡里、PIE、游戏里活着的实例） */
	FString ObjectKind(const UObject* Obj);

	/** 是不是 UMG 控件（UUserWidget 或它里面的控件），网页据此显示控件树 */
	bool IsUmgWidget(const UObject* Obj);

	/**
	 * 改运行时对象的值：属性有 setter（C++ 的 Setter 或蓝图的 BlueprintSetter）就走 setter，否则直接写；
	 * 写完让 UMG 控件、组件把新值刷到屏幕上
	 */
	bool SetRuntimeValue(UObject* Owner, const FResolved& R, const TSharedPtr<FJsonValue>& Json, FString& Error);

	/** 登记 umg.tree：一个 UserWidget 的控件树 */
	void RegisterUmgOps(FAgentPanelBridge& Bridge);
}
