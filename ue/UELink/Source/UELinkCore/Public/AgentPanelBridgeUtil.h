// 桥接操作共用的小工具：读参数、属性路径、反射读写、调函数。运行时模块和编辑器模块都用。
#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"

class UObject;
class UStruct;
class UFunction;
class FProperty;
class FViewport;

/** 截一个视口的图：缩到 MaxWidth 以内，PNG 转 base64 写进 Out（png、width、height） */
UELINKCORE_API bool AgentPanelCaptureViewport(FViewport* VP, int32 MaxWidth, TSharedPtr<FJsonObject>& Out, FString& Error);

namespace AgentPanel
{
	UELINKCORE_API FString Str(const TSharedPtr<FJsonObject>& Args, const TCHAR* Key, const FString& Default = FString());
	UELINKCORE_API int32 Int(const TSharedPtr<FJsonObject>& Args, const TCHAR* Key, int32 Default);
	UELINKCORE_API bool Bool(const TSharedPtr<FJsonObject>& Args, const TCHAR* Key, bool Default);

	/** 不区分大小写的前缀匹配（Actor、对象、资源名的搜索都按前缀，用户 2026-11-10 定） */
	UELINKCORE_API bool StartsWithNoCase(const FString& S, const FString& Prefix);

	/**
	 * 按路径找对象：完整对象路径；找不到时按资源加载（编辑器、后台编辑器里能读没打开的资源）。
	 * 蓝图资源默认换成它的类默认对象（读改属性看的是类默认值）；bKeepBlueprint 为真时给蓝图资源本身（读图表、变量定义、组件树用）
	 */
	UELINKCORE_API UObject* FindObjectByPath(const FString& Path, bool bLoad, FString& Error, bool bKeepBlueprint = false);

	/** 属性路径解析的结果：Prop 是最后一段的属性，Value 指向它的值，Owner 是值所在的对象（改资源时标脏、做撤销用） */
	struct FResolved
	{
		FProperty* Prop = nullptr;
		void* Value = nullptr;
		UObject* Owner = nullptr;
		/** 最后一段属性所在的容器（对象或结构体）；路径最后一段带 [下标] 时为空，这时没有 setter 可走 */
		void* Container = nullptr;
		/** 路径上每一段都允许改（见 CanEditProp）；不允许时 ReadOnlyWhy 写原因 */
		bool bEditable = true;
		FString ReadOnlyWhy;
	};

	/**
	 * 这个属性能不能在面板上改：EditConst、纯 C++ 内部状态（编辑器和蓝图都看不见）、只能在类默认值 / 只能在实例上改的，都不让改。
	 * Owner 是值所在的对象（判断是类默认值还是实例），结构体成员只看 EditConst。Why 写不让改的原因
	 */
	UELINKCORE_API bool CanEditProp(const FProperty* Prop, const UObject* Owner, FString* Why = nullptr);

	/**
	 * 属性路径：Stats.MaxHP、Items[2].Count、Tags["Fire"]、Comp.Owner.Health（对象引用会跟进去）。
	 * Struct / Container 是起点（对象就是它的类和它自己；DataTable 的一行是行结构和行数据）
	 */
	UELINKCORE_API bool ResolvePath(UStruct* Struct, void* Container, UObject* Owner, const FString& Path, FResolved& Out, FString& Error);

	/** 值转 JSON：结构体、数组、Map 都转；太大的（序列化后超过 MaxChars）换成摘要，按路径单独取全量 */
	UELINKCORE_API TSharedPtr<FJsonValue> ToJson(FProperty* Prop, const void* Value, int32 MaxChars = 4000);

	/** JSON 写进属性：先按 JSON 转，转不了的字符串按 UE 的文本格式（ImportText）写 */
	UELINKCORE_API bool FromJson(FProperty* Prop, void* Value, const TSharedPtr<FJsonValue>& Json, FString& Error);

	/** 属性的类型描述：int32、float、FVector、TArray<FItem>、EItemQuality… */
	UELINKCORE_API FString TypeOf(FProperty* Prop);

	/** 一个结构的属性列表（名字、类型、分类、同步、只读、值），Filter 是名字前缀 */
	UELINKCORE_API TArray<TSharedPtr<FJsonValue>> ListProps(UStruct* Struct, const void* Container, const FString& Filter, int32 MaxChars, const UObject* Owner = nullptr);

	/** 函数的参数说明：[{name, type, enum?:[]}]，不含返回值 */
	UELINKCORE_API TArray<TSharedPtr<FJsonValue>> ParamsOf(UFunction* Func);

	/**
	 * 调用函数：Args 是对象（按参数名）或数组（按顺序，GM 的写法），每个值可以是 JSON 或文本。
	 * 返回值和输出参数写进 Out（ret、out）
	 */
	UELINKCORE_API bool CallFunction(UObject* Target, UFunction* Func, const TSharedPtr<FJsonValue>& Args, TSharedPtr<FJsonObject>& Out, FString& Error);

	/** 面板改过、还没保存的资源包（「放弃改动」「签出并保存」按它列） */
	UELINKCORE_API TSet<FString>& DirtyPackages();

	/** 一个对象的简要信息：path、name、class */
	UELINKCORE_API TSharedPtr<FJsonObject> Brief(const UObject* Obj);
}
