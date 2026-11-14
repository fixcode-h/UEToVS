// 给项目加 GM 的口子：继承这个类、写 UFUNCTION（建议带 Category），Agent 面板的 GM 页和 ue_gm 工具会自动列出来。
// 不用改 CheatManager；函数在目标世界里以这个类的一个临时对象执行，GetWorld() 拿得到当前世界。
//
// 例：
//   UCLASS() class UMyGM : public UAgentPanelGMLibrary {
//     GENERATED_BODY()
//     UFUNCTION(Category = "物品与货币", meta = (ToolTip = "给当前玩家加物品"))
//     void AddItem(int32 ItemId, int32 Count);
//   };
#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "AgentPanelGMLibrary.generated.h"

UCLASS(Abstract)
class UELINKCORE_API UAgentPanelGMLibrary : public UObject
{
	GENERATED_BODY()

public:
	virtual UWorld* GetWorld() const override { return World.Get(); }

	TWeakObjectPtr<UWorld> World;

	/** 当前世界里第一个本地玩家的控制器（给 GM 函数用） */
	UFUNCTION(BlueprintCallable, Category = "AgentPanel")
	APlayerController* GetLocalPlayerController() const;
};
