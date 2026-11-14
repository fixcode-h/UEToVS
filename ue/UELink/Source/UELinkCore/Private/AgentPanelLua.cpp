// UnLua 项目的 Lua 操作（用户 2026-11-10 要的）：找绑了 Lua 的对象、读写 Lua 实例表和全局表的字段、执行 Lua 代码。
//
// 只用 Lua 的 C 接口，不碰 UnLua 的内部结构（用户的 UnLua 是改过的分支，版本不确定）：
// - UnLua 各版本都在注册表里放一张「UObject 指针（lightuserdata）→ Lua 值」的表（1.x 叫 ObjectMap，2.x 叫 UnLua_ObjectMap），
//   绑了 Lua 模块的对象对应的值就是它的实例表，没绑的是 userdata。名字对不上时扫注册表，挑键里活着的 UObject 最多的那张表。
// - 模块名：对象实现了 UnLuaInterface 时按反射调它的 GetModuleName；否则拿实例表的元表去 package.loaded 里反查。
// - 会触发元方法的读写（__index、__newindex）都放进 lua_pcall，Lua 报错只变成错误信息，不会跳出 C++ 栈。
// 只有拿 lua_State 和 UnLua 版本有关，见 Build.cs 和 FAgentPanelLua::SetStateProvider。
#include "AgentPanelLua.h"
#include "AgentPanelLuaOps.h"

#include "AgentPanelBridge.h"

static TFunction<lua_State*(UObject*)>& LuaStateProvider()
{
	static TFunction<lua_State*(UObject*)> Provider;
	return Provider;
}

void FAgentPanelLua::SetStateProvider(TFunction<lua_State*(UObject* Context)> Provider)
{
	LuaStateProvider() = MoveTemp(Provider);
}

bool FAgentPanelLua::IsCompiledIn()
{
	return AGENT_PANEL_WITH_LUA != 0;
}

#if !AGENT_PANEL_WITH_LUA

void AgentPanelRegisterLuaOps(FAgentPanelBridge&) {}
void AgentPanelLuaInfo(const TSharedPtr<FJsonObject>&) {}

#else

#include "AgentPanelBridgeUtil.h"
#include "AgentPanelLog.h"
#include "Engine/GameInstance.h"
#include "Engine/World.h"
#include "Modules/ModuleManager.h"
#include "UObject/UObjectArray.h"
#include "lua.hpp"

#if (AGENT_PANEL_UNLUA_ENV_BY_OBJECT || AGENT_PANEL_UNLUA_ENV) && __has_include("IUnLuaModule.h") && __has_include("LuaEnv.h")
#include "IUnLuaModule.h"
#include "LuaEnv.h"
#define AP_LUA_USE_ENV 1
#else
#define AP_LUA_USE_ENV 0
#endif
#if !AP_LUA_USE_ENV && AGENT_PANEL_UNLUA_GETSTATE && __has_include("UnLua.h")
#include "UnLua.h"
#define AP_LUA_USE_GETSTATE 1
#else
#define AP_LUA_USE_GETSTATE 0
#endif

#if LUA_VERSION_NUM < 503
#error "UELink 的 Lua 支持要 Lua 5.3 及以上（UnLua 自带的就是）"
#endif

using namespace AgentPanel;

namespace
{
	/** 一层表最多列多少项（再多的分页取）；扫描超过这么多项只排前面这些 */
	constexpr int32 MaxScan = 5000;
	/** 字符串值最多回多少字符 */
	constexpr int32 MaxStr = 2000;

	FString FromUtf8(const char* S, size_t N)
	{
		if (!S)
		{
			return FString();
		}
		FUTF8ToTCHAR Conv(S, (int32)N);
		return FString(Conv.Length(), Conv.Get());
	}

	void PushStr(lua_State* L, const FString& S)
	{
		FTCHARToUTF8 U(*S);
		lua_pushlstring(L, U.Get(), U.Length());
	}

	/** 退出作用域时把 Lua 栈恢复原样：中途出错 return 也不会留垃圾在 UnLua 的主线程栈上 */
	struct FTop
	{
		lua_State* L;
		int Top;
		explicit FTop(lua_State* InL) : L(InL), Top(lua_gettop(InL)) {}
		~FTop() { lua_settop(L, Top); }
	};

	/** UObject 指针还活着：UnLua 在对象销毁时会从表里删掉，这里再校验一次，免得改过的分支漏删时读到野指针 */
	UObject* LiveObject(const void* P)
	{
		if (!P)
		{
			return nullptr;
		}
		const UObjectBase* Base = static_cast<const UObjectBase*>(P);
		if (!GUObjectArray.IsValid(Base))
		{
			return nullptr;
		}
		UObject* Obj = static_cast<UObject*>(const_cast<UObjectBase*>(Base));
		return IsValid(Obj) ? Obj : nullptr;
	}

	lua_State* StateFor(UObject* Context, FString& Error)
	{
		if (LuaStateProvider())
		{
			if (lua_State* L = LuaStateProvider()(Context))
			{
				return L;
			}
		}
		if (!FModuleManager::Get().IsModuleLoaded(TEXT("UnLua")))
		{
			Error = TEXT("UnLua 模块没有加载");
			return nullptr;
		}
#if AP_LUA_USE_ENV
#if AGENT_PANEL_UNLUA_ENV_BY_OBJECT
		if (Context)
		{
			if (auto* Env = IUnLuaModule::Get().GetEnv(Context))
			{
				if (lua_State* L = Env->GetMainState())
				{
					return L;
				}
			}
		}
#endif
#if AGENT_PANEL_UNLUA_ENV
		if (auto* Env = IUnLuaModule::Get().GetEnv())
		{
			if (lua_State* L = Env->GetMainState())
			{
				return L;
			}
		}
#endif
#elif AP_LUA_USE_GETSTATE
		if (lua_State* L = UnLua::GetState())
		{
			return L;
		}
#endif
#if !AP_LUA_USE_ENV && !AP_LUA_USE_GETSTATE
		if (!LuaStateProvider())
		{
			Error = TEXT("这个 UnLua 分支里没找到拿 lua_State 的接口（GetEnv / GetState），请在项目模块里调 FAgentPanelLua::SetStateProvider 提供");
			return nullptr;
		}
#endif
		Error = TEXT("UnLua 的 Lua 环境还没建好（一般开始游戏 / PIE 后才有）");
		return nullptr;
	}

	/** 目标世界的 GameInstance：多个 Lua 环境（按 GameInstance 分）时用它选环境 */
	UObject* WorldContext(const TSharedPtr<FJsonObject>& A)
	{
		FString Ignore;
		UWorld* World = FAgentPanelBridge::ResolveWorld(Str(A, TEXT("world")), Ignore);
		return World ? static_cast<UObject*>(World->GetGameInstance()) : nullptr;
	}

	/** 数一张表前 Limit 项里键是活着的 UObject 的个数（认对象表用） */
	int32 CountObjectKeys(lua_State* L, int T, int32 Limit)
	{
		int32 Hits = 0;
		int32 Seen = 0;
		lua_pushnil(L);
		while (lua_next(L, T))
		{
			lua_pop(L, 1);
			if (lua_type(L, -1) == LUA_TLIGHTUSERDATA && LiveObject(lua_touserdata(L, -1)))
			{
				++Hits;
			}
			if (++Seen >= Limit)
			{
				lua_pop(L, 1);
				break;
			}
		}
		return Hits;
	}

	/** 找到的对象表在注册表里的键，按 lua_State 记住 */
	TMap<lua_State*, FString>& MapKeys()
	{
		static TMap<lua_State*, FString> Keys;
		return Keys;
	}

	/** 把对象表压栈；没有时什么也不压，返回 false */
	bool PushObjectMap(lua_State* L)
	{
		auto TryKey = [L](const FString& Key)
		{
			FTCHARToUTF8 K(*Key);
			lua_getfield(L, LUA_REGISTRYINDEX, K.Get());
			if (lua_istable(L, -1))
			{
				return true;
			}
			lua_pop(L, 1);
			return false;
		};
		if (const FString* Known = MapKeys().Find(L))
		{
			if (TryKey(*Known))
			{
				return true;
			}
			MapKeys().Remove(L);
		}
		static const TCHAR* const KnownNames[] = { TEXT("UnLua_ObjectMap"), TEXT("ObjectMap") };
		for (const TCHAR* Name : KnownNames)
		{
			if (TryKey(Name))
			{
				if (CountObjectKeys(L, lua_gettop(L), 64) > 0)
				{
					MapKeys().Add(L, Name);
					return true;
				}
				lua_pop(L, 1);
			}
		}
		// 改过名字的分支：注册表里键是字符串的表，挑键里活着的 UObject 最多的
		FString Best;
		int32 BestHits = 0;
		lua_pushnil(L);
		while (lua_next(L, LUA_REGISTRYINDEX))
		{
			if (lua_type(L, -2) == LUA_TSTRING && lua_istable(L, -1))
			{
				const int32 Hits = CountObjectKeys(L, lua_gettop(L), 64);
				if (Hits > BestHits)
				{
					size_t N = 0;
					const char* S = lua_tolstring(L, -2, &N);
					Best = FromUtf8(S, N);
					BestHits = Hits;
				}
			}
			lua_pop(L, 1);
		}
		if (BestHits > 0 && TryKey(Best))
		{
			MapKeys().Add(L, Best);
			return true;
		}
		return false;
	}

	/** 一次请求里用到的 Lua 上下文：对象表、值→对象、模块表→模块名的反查（按需建一次） */
	struct FLuaCtx
	{
		lua_State* L = nullptr;
		int Map = 0;
		bool bRev = false;
		TMap<const void*, const void*> Rev;
		bool bMods = false;
		TMap<const void*, FString> Mods;

		bool Open(UObject* Context, FString& Error)
		{
			L = StateFor(Context, Error);
			if (!L)
			{
				return false;
			}
			if (PushObjectMap(L))
			{
				Map = lua_gettop(L);
			}
			return true;
		}

		/** 表 / userdata 是哪个 UObject 的（对象表里反查） */
		UObject* ObjectOf(int Idx)
		{
			if (!Map)
			{
				return nullptr;
			}
			if (!bRev)
			{
				bRev = true;
				int32 N = 0;
				lua_pushnil(L);
				while (lua_next(L, Map))
				{
					const int VT = lua_type(L, -1);
					if (lua_type(L, -2) == LUA_TLIGHTUSERDATA && (VT == LUA_TTABLE || VT == LUA_TUSERDATA))
					{
						Rev.Add(lua_topointer(L, -1), lua_touserdata(L, -2));
					}
					lua_pop(L, 1);
					if (++N >= 500000)
					{
						lua_pop(L, 1);
						break;
					}
				}
			}
			const void* const* P = Rev.Find(lua_topointer(L, Idx));
			return P ? LiveObject(*P) : nullptr;
		}

		FString ModuleOfTable(const void* P)
		{
			if (!bMods)
			{
				bMods = true;
				lua_getfield(L, LUA_REGISTRYINDEX, LUA_LOADED_TABLE);
				if (lua_istable(L, -1))
				{
					lua_pushnil(L);
					while (lua_next(L, -2))
					{
						if (lua_type(L, -2) == LUA_TSTRING && lua_istable(L, -1))
						{
							size_t N = 0;
							const char* S = lua_tolstring(L, -2, &N);
							Mods.Add(lua_topointer(L, -1), FromUtf8(S, N));
						}
						lua_pop(L, 1);
					}
				}
				lua_pop(L, 1);
			}
			const FString* Name = Mods.Find(P);
			return Name ? *Name : FString();
		}

		/** 实例表的模块名：元表本身、元表的 __index、元表的元表，哪个在 package.loaded 里就是哪个 */
		FString ModuleOf(int Idx)
		{
			FTop Guard(L);
			if (!lua_getmetatable(L, Idx))
			{
				return FString();
			}
			const int MT = lua_gettop(L);
			FString Name = ModuleOfTable(lua_topointer(L, MT));
			if (Name.IsEmpty())
			{
				lua_pushstring(L, "__index");
				lua_rawget(L, MT);
				if (lua_istable(L, -1))
				{
					Name = ModuleOfTable(lua_topointer(L, -1));
				}
				lua_pop(L, 1);
			}
			if (Name.IsEmpty() && lua_getmetatable(L, MT))
			{
				Name = ModuleOfTable(lua_topointer(L, -1));
			}
			return Name;
		}
	};

	/** 实现了 UnLuaInterface 的对象：按反射调 GetModuleName（蓝图里填的模块名） */
	FString InterfaceModule(UObject* Obj)
	{
		bool bImpl = false;
		for (UClass* C = Obj->GetClass(); C && !bImpl; C = C->GetSuperClass())
		{
			for (const FImplementedInterface& I : C->Interfaces)
			{
				if (I.Class && I.Class->GetFName() == TEXT("UnLuaInterface"))
				{
					bImpl = true;
					break;
				}
			}
		}
		UFunction* F = bImpl ? Obj->FindFunction(TEXT("GetModuleName")) : nullptr;
		if (!F)
		{
			return FString();
		}
		TSharedPtr<FJsonObject> Out = MakeShared<FJsonObject>();
		FString Error;
		if (!CallFunction(Obj, F, MakeShared<FJsonValueArray>(TArray<TSharedPtr<FJsonValue>>()), Out, Error))
		{
			return FString();
		}
		FString Name;
		Out->TryGetStringField(TEXT("ret"), Name);
		return Name;
	}

	int GetTableFn(lua_State* L)
	{
		lua_gettable(L, 1);
		return 1;
	}

	int SetTableFn(lua_State* L)
	{
		lua_settable(L, 1);
		return 0;
	}

	FString PopError(lua_State* L)
	{
		size_t N = 0;
		const char* S = lua_tolstring(L, -1, &N);
		FString E = S ? FromUtf8(S, N) : FString(TEXT("（错误不是字符串）"));
		lua_pop(L, 1);
		return E;
	}

	/** t[k]，T 是表 / userdata 的绝对位置，键在栈顶；成功时键换成值 */
	bool PGet(lua_State* L, int T, FString& Error)
	{
		lua_pushcfunction(L, GetTableFn);
		lua_pushvalue(L, T);
		lua_pushvalue(L, -3);
		if (lua_pcall(L, 2, 1, 0) != LUA_OK)
		{
			Error = PopError(L);
			lua_pop(L, 1);
			return false;
		}
		lua_remove(L, -2);
		return true;
	}

	/** t[k] = v，键和值在栈顶（键在下），完成后两者都出栈 */
	bool PSet(lua_State* L, int T, FString& Error)
	{
		lua_pushcfunction(L, SetTableFn);
		lua_insert(L, -3);
		lua_pushvalue(L, T);
		lua_insert(L, -3);
		if (lua_pcall(L, 3, 0, 0) != LUA_OK)
		{
			Error = PopError(L);
			return false;
		}
		return true;
	}

	bool PushJsonKey(lua_State* L, const TSharedPtr<FJsonValue>& K)
	{
		if (!K.IsValid())
		{
			return false;
		}
		if (K->Type == EJson::Number)
		{
			const double D = K->AsNumber();
			if (FMath::Frac(D) == 0 && FMath::Abs(D) < 9007199254740992.0)
			{
				lua_pushinteger(L, (lua_Integer)D);
			}
			else
			{
				lua_pushnumber(L, D);
			}
			return true;
		}
		if (K->Type == EJson::String)
		{
			PushStr(L, K->AsString());
			return true;
		}
		if (K->Type == EJson::Boolean)
		{
			lua_pushboolean(L, K->AsBool());
			return true;
		}
		return false;
	}

	FString KeyText(const TSharedPtr<FJsonValue>& K)
	{
		if (!K.IsValid())
		{
			return TEXT("?");
		}
		if (K->Type == EJson::String)
		{
			return K->AsString();
		}
		if (K->Type == EJson::Number)
		{
			return FString::Printf(TEXT("[%s]"), *FString::SanitizeFloat(K->AsNumber(), 0));
		}
		return K->Type == EJson::Boolean ? (K->AsBool() ? TEXT("[true]") : TEXT("[false]")) : TEXT("?");
	}

	/**
	 * 压起点：给了对象路径是它的 Lua 实例表（或 UnLua 给它的 userdata）；没给是全局表 _G。
	 * Obj 返回对象（全局时为空）
	 */
	bool PushRoot(FLuaCtx& C, const FString& Path, UObject* Obj, FString& Error)
	{
		if (Path.IsEmpty())
		{
			lua_pushglobaltable(C.L);
			return true;
		}
		if (!C.Map)
		{
			Error = TEXT("Lua 注册表里没找到 UnLua 的对象表（这个 UnLua 分支可能改了存法）");
			return false;
		}
		lua_rawgetp(C.L, C.Map, Obj);
		if (lua_isnil(C.L, -1))
		{
			lua_pop(C.L, 1);
			Error = FString::Printf(TEXT("%s 还没进过 Lua：没绑定 Lua 模块，Lua 里也还没用到它"), *Obj->GetName());
			return false;
		}
		return true;
	}

	/** 从栈顶的值沿着 Keys 的前 Count 段往下走，栈顶换成最后的值 */
	bool Walk(FLuaCtx& C, const TArray<TSharedPtr<FJsonValue>>& Keys, int32 Count, FString& Error)
	{
		lua_State* L = C.L;
		FString Where;
		for (int32 I = 0; I < Count; ++I)
		{
			const int T = lua_gettop(L);
			const int Type = lua_type(L, T);
			if (Type != LUA_TTABLE && Type != LUA_TUSERDATA)
			{
				Error = FString::Printf(TEXT("%s 是 %s，不能再往下取"), Where.IsEmpty() ? TEXT("起点") : *Where, UTF8_TO_TCHAR(lua_typename(L, Type)));
				return false;
			}
			if (!PushJsonKey(L, Keys[I]))
			{
				Error = TEXT("路径里的键只能是字符串、数字或布尔");
				return false;
			}
			Where += (Where.IsEmpty() || Keys[I]->Type != EJson::String ? TEXT("") : TEXT(".")) + KeyText(Keys[I]);
			if (!PGet(L, T, Error))
			{
				Error = FString::Printf(TEXT("读 %s 出错：%s"), *Where, *Error);
				return false;
			}
			lua_remove(L, T);
		}
		return true;
	}

	/** 表的项数，最多数到 Cap（再多标 more） */
	int32 CountEntries(lua_State* L, int T, int32 Cap, bool& bMore)
	{
		int32 N = 0;
		bMore = false;
		lua_pushnil(L);
		while (lua_next(L, T))
		{
			lua_pop(L, 1);
			if (++N >= Cap)
			{
				bMore = true;
				lua_pop(L, 1);
				break;
			}
		}
		return N;
	}

	/** 一个 Lua 值的描述：t 类型；标量放 v；表放项数 n；对应 UObject 的放 obj（路径）和 module；Lua 函数放定义位置 src */
	void Describe(FLuaCtx& C, int Idx, const TSharedPtr<FJsonObject>& F)
	{
		lua_State* L = C.L;
		Idx = lua_absindex(L, Idx);
		const int Type = lua_type(L, Idx);
		F->SetStringField(TEXT("t"), UTF8_TO_TCHAR(lua_typename(L, Type)));
		switch (Type)
		{
		case LUA_TBOOLEAN:
			F->SetBoolField(TEXT("v"), lua_toboolean(L, Idx) != 0);
			break;
		case LUA_TNUMBER:
			if (lua_isinteger(L, Idx))
			{
				const lua_Integer I = lua_tointeger(L, Idx);
				F->SetBoolField(TEXT("int"), true);
				if (I > -9007199254740992LL && I < 9007199254740992LL)
				{
					F->SetNumberField(TEXT("v"), (double)I);
				}
				else
				{
					F->SetStringField(TEXT("v"), FString::Printf(TEXT("%lld"), (long long)I));
				}
			}
			else
			{
				const double D = lua_tonumber(L, Idx);
				if (FMath::IsFinite(D))
				{
					F->SetNumberField(TEXT("v"), D);
				}
				else
				{
					F->SetStringField(TEXT("v"), FMath::IsNaN(D) ? TEXT("nan") : D > 0 ? TEXT("inf") : TEXT("-inf"));
				}
			}
			break;
		case LUA_TSTRING:
		{
			size_t N = 0;
			const char* S = lua_tolstring(L, Idx, &N);
			FString V = FromUtf8(S, N);
			if (V.Len() > MaxStr)
			{
				F->SetNumberField(TEXT("len"), V.Len());
				V = V.Left(MaxStr);
			}
			F->SetStringField(TEXT("v"), V);
			break;
		}
		case LUA_TTABLE:
		case LUA_TUSERDATA:
		{
			if (Type == LUA_TTABLE)
			{
				bool bMore = false;
				F->SetNumberField(TEXT("n"), CountEntries(L, Idx, 10000, bMore));
				if (bMore)
				{
					F->SetBoolField(TEXT("nMore"), true);
				}
			}
			if (UObject* Obj = C.ObjectOf(Idx))
			{
				F->SetStringField(TEXT("obj"), Obj->GetPathName());
				F->SetStringField(TEXT("class"), Obj->GetClass()->GetName());
				if (Type == LUA_TTABLE)
				{
					F->SetStringField(TEXT("module"), C.ModuleOf(Idx));
				}
			}
			break;
		}
		case LUA_TFUNCTION:
		{
			lua_Debug Ar;
			lua_pushvalue(L, Idx);
			if (lua_getinfo(L, ">S", &Ar) && Ar.what && FCStringAnsi::Strcmp(Ar.what, "C") != 0)
			{
				F->SetStringField(TEXT("src"), FString::Printf(TEXT("%s:%d"), UTF8_TO_TCHAR(Ar.short_src), Ar.linedefined));
			}
			break;
		}
		default:
			break;
		}
	}

	/** 键的显示：字符串原样，数字写成 [3]；kt 是键的类型，数字键另给 kn 方便面板拼路径 */
	void DescribeKey(lua_State* L, int Idx, const TSharedPtr<FJsonObject>& F)
	{
		const int Type = lua_type(L, Idx);
		F->SetStringField(TEXT("kt"), UTF8_TO_TCHAR(lua_typename(L, Type)));
		if (Type == LUA_TSTRING)
		{
			size_t N = 0;
			const char* S = lua_tolstring(L, Idx, &N);
			F->SetStringField(TEXT("k"), FromUtf8(S, N));
		}
		else if (Type == LUA_TNUMBER)
		{
			const double D = lua_tonumber(L, Idx);
			F->SetNumberField(TEXT("kn"), D);
			F->SetStringField(TEXT("k"), lua_isinteger(L, Idx) ? FString::Printf(TEXT("%lld"), (long long)lua_tointeger(L, Idx)) : FString::SanitizeFloat(D));
		}
		else if (Type == LUA_TBOOLEAN)
		{
			F->SetStringField(TEXT("k"), lua_toboolean(L, Idx) ? TEXT("true") : TEXT("false"));
		}
		else
		{
			F->SetStringField(TEXT("k"), FString::Printf(TEXT("<%s %p>"), UTF8_TO_TCHAR(lua_typename(L, Type)), lua_topointer(L, Idx)));
		}
	}

	/**
	 * 列一张表的项：按键排序（数字键在前、从小到大，然后字符串键按字母），Offset / Limit 分页。
	 * 只排前 MaxScan 项（超过标 truncated），总数照样数完
	 */
	void ListTable(FLuaCtx& C, int T, int32 Offset, int32 Limit, const TSharedPtr<FJsonObject>& Out)
	{
		lua_State* L = C.L;
		FTop Guard(L);
		struct FEntry
		{
			int32 Slot;
			int32 Rank;
			double Num;
			FString Str;
		};
		TArray<FEntry> Entries;
		lua_createtable(L, FMath::Min(MaxScan, 256), 0);
		const int KT = lua_gettop(L);
		int32 Total = 0;
		lua_pushnil(L);
		while (lua_next(L, T))
		{
			lua_pop(L, 1);
			++Total;
			if (Entries.Num() < MaxScan)
			{
				FEntry E{ Entries.Num() + 1, 2, 0, FString() };
				const int KType = lua_type(L, -1);
				if (KType == LUA_TNUMBER)
				{
					E.Rank = 0;
					E.Num = lua_tonumber(L, -1);
				}
				else if (KType == LUA_TSTRING)
				{
					size_t N = 0;
					const char* S = lua_tolstring(L, -1, &N);
					E.Rank = 1;
					E.Str = FromUtf8(S, N);
				}
				lua_pushvalue(L, -1);
				lua_rawseti(L, KT, E.Slot);
				Entries.Add(MoveTemp(E));
			}
		}
		Entries.Sort([](const FEntry& A, const FEntry& B)
		{
			if (A.Rank != B.Rank)
			{
				return A.Rank < B.Rank;
			}
			if (A.Rank == 0)
			{
				return A.Num < B.Num;
			}
			return A.Str.Compare(B.Str, ESearchCase::IgnoreCase) < 0;
		});
		TArray<TSharedPtr<FJsonValue>> Fields;
		for (int32 I = Offset; I < Entries.Num() && Fields.Num() < Limit; ++I)
		{
			lua_rawgeti(L, KT, Entries[I].Slot);
			const int K = lua_gettop(L);
			lua_pushvalue(L, K);
			lua_rawget(L, T);
			TSharedPtr<FJsonObject> F = MakeShared<FJsonObject>();
			DescribeKey(L, K, F);
			Describe(C, -1, F);
			Fields.Add(MakeShared<FJsonValueObject>(F));
			lua_settop(L, K - 1);
		}
		Out->SetArrayField(TEXT("fields"), Fields);
		Out->SetNumberField(TEXT("total"), Total);
		Out->SetNumberField(TEXT("offset"), Offset);
		Out->SetBoolField(TEXT("more"), Offset + Fields.Num() < FMath::Min(Total, MaxScan));
		Out->SetBoolField(TEXT("truncated"), Total > MaxScan);
	}

	/** 模块里的函数（实例表元表这条链上，最多三层）：给 Agent 和面板看这个对象在 Lua 里能做什么 */
	void ListFunctions(FLuaCtx& C, int T, const TSharedPtr<FJsonObject>& Out)
	{
		lua_State* L = C.L;
		FTop Guard(L);
		TArray<TSharedPtr<FJsonValue>> Fns;
		TSet<FString> Seen;
		lua_pushvalue(L, T);
		for (int32 Depth = 0; Depth < 3 && lua_getmetatable(L, -1); ++Depth)
		{
			const int MT = lua_gettop(L);
			lua_pushstring(L, "__index");
			lua_rawget(L, MT);
			const int Src = lua_istable(L, -1) ? lua_gettop(L) : MT;
			lua_pushnil(L);
			while (lua_next(L, Src) && Fns.Num() < 300)
			{
				if (lua_type(L, -2) == LUA_TSTRING && lua_isfunction(L, -1))
				{
					size_t N = 0;
					const char* S = lua_tolstring(L, -2, &N);
					const FString Name = FromUtf8(S, N);
					if (!Name.StartsWith(TEXT("__")) && !Seen.Contains(Name))
					{
						Seen.Add(Name);
						TSharedPtr<FJsonObject> F = MakeShared<FJsonObject>();
						F->SetStringField(TEXT("name"), Name);
						Describe(C, -1, F);
						F->RemoveField(TEXT("t"));
						Fns.Add(MakeShared<FJsonValueObject>(F));
					}
				}
				lua_pop(L, 1);
			}
			// 栈顶留下这一层的表，下一轮取它的元表
			lua_settop(L, Src);
		}
		Out->SetArrayField(TEXT("functions"), Fns);
	}

	/** JSON 标量 → Lua；PrevInt：原来是整数时整数值仍按整数写 */
	bool PushJsonValue(lua_State* L, const TSharedPtr<FJsonValue>& V, bool bPrevFloat, FString& Error)
	{
		if (!V.IsValid() || V->Type == EJson::Null || V->Type == EJson::None)
		{
			lua_pushnil(L);
			return true;
		}
		switch (V->Type)
		{
		case EJson::Boolean:
			lua_pushboolean(L, V->AsBool());
			return true;
		case EJson::Number:
		{
			const double D = V->AsNumber();
			if (!bPrevFloat && FMath::Frac(D) == 0 && FMath::Abs(D) < 9007199254740992.0)
			{
				lua_pushinteger(L, (lua_Integer)D);
			}
			else
			{
				lua_pushnumber(L, D);
			}
			return true;
		}
		case EJson::String:
			PushStr(L, V->AsString());
			return true;
		default:
			Error = TEXT("Lua 字段只能改成数字、字符串、布尔或 nil");
			return false;
		}
	}

	/** 标量的 JSON（改前值、撤回用）；表、函数这些返回空 */
	TSharedPtr<FJsonValue> ScalarJson(lua_State* L, int Idx)
	{
		switch (lua_type(L, Idx))
		{
		case LUA_TNIL:
			return MakeShared<FJsonValueNull>();
		case LUA_TBOOLEAN:
			return MakeShared<FJsonValueBoolean>(lua_toboolean(L, Idx) != 0);
		case LUA_TNUMBER:
			return MakeShared<FJsonValueNumber>(lua_tonumber(L, Idx));
		case LUA_TSTRING:
		{
			size_t N = 0;
			const char* S = lua_tolstring(L, Idx, &N);
			return MakeShared<FJsonValueString>(FromUtf8(S, N));
		}
		default:
			return nullptr;
		}
	}

	TArray<TSharedPtr<FJsonValue>> KeysOf(const TSharedPtr<FJsonObject>& A)
	{
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		return A->TryGetArrayField(TEXT("keys"), Arr) ? *Arr : TArray<TSharedPtr<FJsonValue>>();
	}

	/** 有对象路径时找对象（Lua 环境按对象选），没有时按目标世界选 */
	bool OpenFor(const TSharedPtr<FJsonObject>& A, FLuaCtx& C, UObject*& Obj, FString& Error)
	{
		const FString Path = Str(A, TEXT("path"));
		Obj = nullptr;
		if (!Path.IsEmpty())
		{
			Obj = FindObjectByPath(Path, false, Error);
			if (!Obj)
			{
				return false;
			}
		}
		return C.Open(Obj ? Obj : WorldContext(A), Error);
	}

	// ---------- 执行 Lua：print 收进输出、超时中止 ----------

	double GDeadline = 0;

	void TimeoutHook(lua_State* L, lua_Debug*)
	{
		if (FPlatformTime::Seconds() > GDeadline)
		{
			luaL_error(L, "执行超过时限，已中止（是不是死循环？）");
		}
	}

	/** 执行期间 print 往这里收；执行完置空。Lua 代码可能把 print 存起来以后再调，那时只写日志，不碰已经释放的数组 */
	TArray<FString>* GPrintSink = nullptr;

	int PrintFn(lua_State* L)
	{
		TArray<FString>* Lines = GPrintSink;
		const int N = lua_gettop(L);
		FString Line;
		for (int I = 1; I <= N; ++I)
		{
			size_t Len = 0;
			const char* S = luaL_tolstring(L, I, &Len);
			if (I > 1)
			{
				Line += TEXT("\t");
			}
			Line += FromUtf8(S, Len);
			lua_pop(L, 1);
		}
		if (!Lines)
		{
			UE_LOG(LogTemp, Log, TEXT("%s"), *Line);
		}
		else if (Lines->Num() < 2000)
		{
			Lines->Add(Line.Left(4000));
		}
		return 0;
	}

	int TracebackFn(lua_State* L)
	{
		const char* Msg = lua_tostring(L, 1);
		if (!Msg)
		{
			Msg = luaL_tolstring(L, 1, nullptr);
		}
		luaL_traceback(L, L, Msg, 1);
		return 1;
	}
}

void AgentPanelLuaInfo(const TSharedPtr<FJsonObject>& Info)
{
	TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
	FString Error;
	FString Ignore;
	UWorld* World = FAgentPanelBridge::ResolveWorld(FString(), Ignore);
	lua_State* L = StateFor(World ? static_cast<UObject*>(World->GetGameInstance()) : nullptr, Error);
	O->SetBoolField(TEXT("available"), L != nullptr);
	O->SetStringField(TEXT("version"), UTF8_TO_TCHAR(LUA_RELEASE));
	if (!L)
	{
		O->SetStringField(TEXT("error"), Error);
	}
	Info->SetObjectField(TEXT("lua"), O);
}

void AgentPanelRegisterLuaOps(FAgentPanelBridge& B)
{
	B.AddOp(TEXT("lua.status"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		FLuaCtx C;
		if (!C.Open(WorldContext(A), Error))
		{
			return false;
		}
		FTop Guard(C.L);
		Out->SetStringField(TEXT("version"), UTF8_TO_TCHAR(LUA_RELEASE));
		Out->SetBoolField(TEXT("objectMap"), C.Map != 0);
		const FString* Key = MapKeys().Find(C.L);
		Out->SetStringField(TEXT("objectMapKey"), Key ? *Key : FString());
		Out->SetNumberField(TEXT("memoryKB"), lua_gc(C.L, LUA_GCCOUNT, 0));
		return true;
	});

	// 找绑了 Lua 的对象：遍历对象表里值是实例表的项（绑定的对象通常几百到几千个，不用扫全部 UObject）
	B.AddOp(TEXT("lua.objects"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		FLuaCtx C;
		if (!C.Open(WorldContext(A), Error))
		{
			return false;
		}
		FTop Guard(C.L);
		if (!C.Map)
		{
			Error = TEXT("Lua 注册表里没找到 UnLua 的对象表（这个 UnLua 分支可能改了存法）");
			return false;
		}
		UWorld* World = nullptr;
		if (!Str(A, TEXT("world")).IsEmpty())
		{
			World = FAgentPanelBridge::ResolveWorld(Str(A, TEXT("world")), Error);
			if (!World)
			{
				return false;
			}
		}
		const FString ClassName = Str(A, TEXT("class"));
		UClass* Cls = nullptr;
		if (!ClassName.IsEmpty())
		{
			Cls = ClassName.StartsWith(TEXT("/")) ? FindObject<UClass>(nullptr, *ClassName) : FindFirstObject<UClass>(*ClassName, EFindFirstObjectOptions::NativeFirst);
			if (!Cls && !ClassName.EndsWith(TEXT("_C")))
			{
				Cls = FindFirstObject<UClass>(*(ClassName + TEXT("_C")), EFindFirstObjectOptions::None);
			}
			if (!Cls)
			{
				Error = FString::Printf(TEXT("找不到类 %s"), *ClassName);
				return false;
			}
		}
		const FString Prefix = Str(A, TEXT("prefix"));
		const FString Module = Str(A, TEXT("module"));
		const int32 Limit = FMath::Clamp(Int(A, TEXT("limit"), 200), 1, 1000);
		lua_State* L = C.L;
		// 先把绑定对象的指针收出来再逐个处理：取模块名时 GetModuleName 可能跑进 Lua、往对象表里加东西，
		// 遍历中改表 lua_next 的行为是未定义的
		TArray<void*> Ptrs;
		lua_pushnil(L);
		while (lua_next(L, C.Map))
		{
			if (lua_type(L, -2) == LUA_TLIGHTUSERDATA && lua_istable(L, -1))
			{
				Ptrs.Add(lua_touserdata(L, -2));
			}
			lua_pop(L, 1);
		}
		const int32 Bound = Ptrs.Num();
		TArray<TSharedPtr<FJsonValue>> Items;
		bool bMore = false;
		for (void* P : Ptrs)
		{
			UObject* Obj = LiveObject(P);
			if (!Obj || Obj->HasAnyFlags(RF_ClassDefaultObject) || (Cls && !Obj->IsA(Cls)) || !StartsWithNoCase(Obj->GetName(), Prefix) ||
				(World && Obj->GetWorld() && Obj->GetWorld() != World))
			{
				continue;
			}
			FString Mod = InterfaceModule(Obj);
			if (Mod.IsEmpty())
			{
				lua_rawgetp(L, C.Map, Obj);
				if (lua_istable(L, -1))
				{
					Mod = C.ModuleOf(lua_gettop(L));
				}
				lua_pop(L, 1);
			}
			if (!Module.IsEmpty() && !Mod.Contains(Module, ESearchCase::IgnoreCase))
			{
				continue;
			}
			if (Items.Num() >= Limit)
			{
				bMore = true;
				break;
			}
			TSharedPtr<FJsonObject> O = Brief(Obj);
			O->SetStringField(TEXT("module"), Mod);
			Items.Add(MakeShared<FJsonValueObject>(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetBoolField(TEXT("more"), bMore);
		Out->SetNumberField(TEXT("bound"), Bound);
		return true;
	});

	// 读：对象的实例表 / 全局表，沿 keys 往下走；表列一层（分页），标量直接给值
	B.AddOp(TEXT("lua.get"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		FLuaCtx C;
		UObject* Obj = nullptr;
		if (!OpenFor(A, C, Obj, Error))
		{
			return false;
		}
		FTop Guard(C.L);
		lua_State* L = C.L;
		if (!PushRoot(C, Str(A, TEXT("path")), Obj, Error))
		{
			return false;
		}
		const int Root = lua_gettop(L);
		if (Obj)
		{
			TSharedPtr<FJsonObject> O = Brief(Obj);
			FString Mod = InterfaceModule(Obj);
			if (Mod.IsEmpty() && lua_istable(L, Root))
			{
				Mod = C.ModuleOf(Root);
			}
			O->SetStringField(TEXT("module"), Mod);
			O->SetBoolField(TEXT("bound"), lua_istable(L, Root) != 0);
			Out->SetObjectField(TEXT("object"), O);
		}
		const TArray<TSharedPtr<FJsonValue>> Keys = KeysOf(A);
		if (!Walk(C, Keys, Keys.Num(), Error))
		{
			return false;
		}
		const int V = lua_gettop(L);
		TSharedPtr<FJsonObject> D = MakeShared<FJsonObject>();
		Describe(C, V, D);
		Out->SetObjectField(TEXT("value"), D);
		if (lua_istable(L, V))
		{
			ListTable(C, V, FMath::Max(0, Int(A, TEXT("offset"), 0)), FMath::Clamp(Int(A, TEXT("limit"), 200), 1, 1000), Out);
			if (Bool(A, TEXT("functions"), Keys.Num() == 0 && Obj != nullptr))
			{
				ListFunctions(C, V, Out);
			}
		}
		else if (lua_type(L, V) == LUA_TUSERDATA && Keys.Num() == 0 && Obj)
		{
			Out->SetStringField(TEXT("note"), TEXT("这个对象没绑定 Lua 模块，Lua 里只有它的 UObject 代理，没有实例表"));
		}
		return true;
	});

	// 改：只改原值是 nil / 布尔 / 数字 / 字符串的字段，新值也只能是这几种（表、函数不在网页上改，用执行 Lua）
	B.AddOp(TEXT("lua.set"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const TArray<TSharedPtr<FJsonValue>> Keys = KeysOf(A);
		if (Keys.Num() == 0)
		{
			Error = TEXT("要给字段路径（keys）");
			return false;
		}
		FLuaCtx C;
		UObject* Obj = nullptr;
		if (!OpenFor(A, C, Obj, Error))
		{
			return false;
		}
		FTop Guard(C.L);
		lua_State* L = C.L;
		if (!PushRoot(C, Str(A, TEXT("path")), Obj, Error) || !Walk(C, Keys, Keys.Num() - 1, Error))
		{
			return false;
		}
		const int Parent = lua_gettop(L);
		const int PType = lua_type(L, Parent);
		if (PType != LUA_TTABLE && PType != LUA_TUSERDATA)
		{
			Error = FString::Printf(TEXT("上一级是 %s，不是表"), UTF8_TO_TCHAR(lua_typename(L, PType)));
			return false;
		}
		if (!PushJsonKey(L, Keys.Last()))
		{
			Error = TEXT("路径里的键只能是字符串、数字或布尔");
			return false;
		}
		if (!PGet(L, Parent, Error))
		{
			return false;
		}
		const int Prev = lua_gettop(L);
		TSharedPtr<FJsonValue> PrevJson = ScalarJson(L, Prev);
		if (!PrevJson.IsValid())
		{
			Error = FString::Printf(TEXT("%s 现在是 %s：只能改数字、字符串、布尔或 nil 的字段，表和函数请用执行 Lua"), *KeyText(Keys.Last()), UTF8_TO_TCHAR(lua_typename(L, lua_type(L, Prev))));
			return false;
		}
		const bool bPrevFloat = lua_type(L, Prev) == LUA_TNUMBER && !lua_isinteger(L, Prev);
		PushJsonKey(L, Keys.Last());
		if (!PushJsonValue(L, A->TryGetField(TEXT("value")), bPrevFloat, Error) || !PSet(L, Parent, Error))
		{
			return false;
		}
		PushJsonKey(L, Keys.Last());
		if (!PGet(L, Parent, Error))
		{
			return false;
		}
		Out->SetField(TEXT("prev"), PrevJson);
		TSharedPtr<FJsonValue> Now = ScalarJson(L, -1);
		Out->SetField(TEXT("value"), Now.IsValid() ? Now : MakeShared<FJsonValueNull>());
		Out->SetStringField(TEXT("type"), UTF8_TO_TCHAR(lua_typename(L, lua_type(L, -1))));
		return true;
	});

	// 执行 Lua：self 是给定对象的实例表（没给为 nil）；先按表达式试（能直接看到值），不行按语句执行
	B.AddOp(TEXT("lua.exec"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const FString Code = Str(A, TEXT("code"));
		if (Code.TrimStartAndEnd().IsEmpty())
		{
			Error = TEXT("没有给代码");
			return false;
		}
		FLuaCtx C;
		UObject* Obj = nullptr;
		if (!OpenFor(A, C, Obj, Error))
		{
			return false;
		}
		FTop Guard(C.L);
		lua_State* L = C.L;
		lua_pushcfunction(L, TracebackFn);
		const int Msgh = lua_gettop(L);
		// 前缀和代码在同一行，报错的行号和你写的一致
		FTCHARToUTF8 Expr(*(TEXT("local self = ...; return ") + Code));
		if (luaL_loadbuffer(L, Expr.Get(), Expr.Length(), "=AgentPanel") != LUA_OK)
		{
			lua_pop(L, 1);
			FTCHARToUTF8 Stmt(*(TEXT("local self = ...; ") + Code));
			if (luaL_loadbuffer(L, Stmt.Get(), Stmt.Length(), "=AgentPanel") != LUA_OK)
			{
				Error = PopError(L);
				return false;
			}
		}
		if (Obj)
		{
			if (!PushRoot(C, Str(A, TEXT("path")), Obj, Error))
			{
				return false;
			}
		}
		else
		{
			lua_pushnil(L);
		}
		// 临时换掉 print，收进输出；结束后放回（直接 rawset，_G 有元表也不受影响）
		TArray<FString> Printed;
		lua_pushglobaltable(L);
		const int G = lua_gettop(L);
		lua_pushstring(L, "print");
		lua_rawget(L, G);
		const int OldPrint = lua_gettop(L);
		lua_pushstring(L, "print");
		lua_pushcfunction(L, PrintFn);
		lua_rawset(L, G);
		// 把 chunk 和 self 挪到栈顶调用
		lua_pushvalue(L, OldPrint - 3);
		lua_pushvalue(L, OldPrint - 2);
		const int CallBase = lua_gettop(L) - 1;
		lua_Hook OldHook = lua_gethook(L);
		const int OldMask = lua_gethookmask(L);
		const int OldCount = lua_gethookcount(L);
		const double Timeout = FMath::Clamp(Int(A, TEXT("timeoutMs"), 5000), 100, 60000) / 1000.0;
		GDeadline = FPlatformTime::Seconds() + Timeout;
		GPrintSink = &Printed;
		lua_sethook(L, TimeoutHook, LUA_MASKCOUNT, 10000);
		const uint64 LogBefore = FAgentPanelLog::Get().LastSeq();
		const double Start = FPlatformTime::Seconds();
		const int Status = lua_pcall(L, 1, LUA_MULTRET, Msgh);
		const double Ms = (FPlatformTime::Seconds() - Start) * 1000.0;
		lua_sethook(L, OldHook, OldMask, OldCount);
		GPrintSink = nullptr;
		lua_pushstring(L, "print");
		lua_pushvalue(L, OldPrint);
		lua_rawset(L, G);

		TArray<TSharedPtr<FJsonValue>> Output;
		for (const FString& Line : Printed)
		{
			Output.Add(MakeShared<FJsonValueString>(Line));
		}
		for (const FString& Line : FAgentPanelLog::Get().TextSince(LogBefore, 400))
		{
			Output.Add(MakeShared<FJsonValueString>(Line));
		}
		if (Status != LUA_OK)
		{
			Error = PopError(L);
			for (const TSharedPtr<FJsonValue>& Line : Output)
			{
				Error += TEXT("\n") + Line->AsString();
			}
			return false;
		}
		TArray<TSharedPtr<FJsonValue>> Results;
		for (int I = CallBase; I <= lua_gettop(L) && Results.Num() < 20; ++I)
		{
			TSharedPtr<FJsonObject> R = MakeShared<FJsonObject>();
			Describe(C, I, R);
			if (lua_istable(L, I))
			{
				ListTable(C, I, 0, 50, R);
			}
			Results.Add(MakeShared<FJsonValueObject>(R));
		}
		Out->SetArrayField(TEXT("results"), Results);
		Out->SetArrayField(TEXT("output"), Output);
		Out->SetNumberField(TEXT("ms"), Ms);
		return true;
	});
}

#endif
