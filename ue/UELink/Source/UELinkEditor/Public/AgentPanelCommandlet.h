// 后台编辑器：UnrealEditor-Cmd <项目> -run=AgentPanel -nullrhi -unattended [-AgentPanelIdle=10]
// 编辑器没开时，面板用它读资源、DataTable、蓝图默认值；不开窗口，闲置到时间自己退出。
#pragma once

#include "CoreMinimal.h"
#include "Commandlets/Commandlet.h"
#include "AgentPanelCommandlet.generated.h"

UCLASS()
class UAgentPanelCommandlet : public UCommandlet
{
	GENERATED_BODY()

public:
	UAgentPanelCommandlet();
	virtual int32 Main(const FString& Params) override;
};
