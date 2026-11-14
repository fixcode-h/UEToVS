// Lua 相关操作的登记入口（AgentPanelLua.cpp）。没编进 Lua 支持时两个函数都什么也不做
#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"

class FAgentPanelBridge;

/** 登记 lua.status、lua.objects、lua.get、lua.set、lua.exec */
void AgentPanelRegisterLuaOps(FAgentPanelBridge& Bridge);

/** /info 里的 lua 字段：{available, error?, version}，面板按它决定显示不显示 Lua 的入口 */
void AgentPanelLuaInfo(const TSharedPtr<FJsonObject>& Info);
