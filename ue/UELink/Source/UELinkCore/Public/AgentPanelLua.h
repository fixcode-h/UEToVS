// 装了 UnLua 的项目：面板能搜到绑了 Lua 的对象、看和改 Lua 实例表的字段、按路径看 Lua 全局表、执行 Lua 代码。
// 桥接只用 Lua 的 C 接口（lua_next、lua_rawgetp…）读写，不依赖 UnLua 的内部结构，UnLua 各版本和改过的分支都能用；
// 唯一和 UnLua 版本有关的是「拿到 lua_State」这一步：Build.cs 按 UnLua 头文件里有哪些接口自动选，
// 改动太大、自动选不到时，项目在自己的模块里调 SetStateProvider 告诉桥接用哪个 lua_State。
#pragma once

#include "CoreMinimal.h"

struct lua_State;
class UObject;

class UELINKCORE_API FAgentPanelLua
{
public:
	/**
	 * 项目自己提供 lua_State（优先于自动选的）：Context 是要读的对象，或目标世界的 GameInstance，可能为空。
	 * 多个 Lua 环境（例如每个 GameInstance 一个）时按 Context 返回它所在的环境；返回空表示没有，桥接再试自动的方式。
	 * 例：FAgentPanelLua::SetStateProvider([](UObject* Ctx) { return MyLuaManager::Get().GetState(Ctx); });
	 */
	static void SetStateProvider(TFunction<lua_State*(UObject* Context)> Provider);

	/** 这个桥接编进了 Lua 支持（项目里有 UnLua） */
	static bool IsCompiledIn();
};
