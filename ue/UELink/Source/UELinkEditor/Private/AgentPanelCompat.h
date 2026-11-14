// 引擎版本差异集中在这里（5.4 起），各操作里不写版本判断。
#pragma once

#include "Misc/EngineVersionComparison.h"
#include "Runtime/Launch/Resources/Version.h"

// 5.6 起部分图表节点的坐标接口（如 FEdGraphSchemaAction::PerformAction）换成了 FVector2f 系列。
// 注意不是全部：UBlueprintNodeSpawner::Invoke 到 5.8 仍收 FVector2D，而 FVector2f 不能隐式转 FVector2D，
// 用这个宏前先对照目标版本的函数签名。
#if ENGINE_MAJOR_VERSION > 5 || (ENGINE_MAJOR_VERSION == 5 && ENGINE_MINOR_VERSION >= 6)
#define AGENT_PANEL_GRAPH_POS(X, Y) FVector2f(static_cast<float>(X), static_cast<float>(Y))
#else
#define AGENT_PANEL_GRAPH_POS(X, Y) FVector2D(static_cast<double>(X), static_cast<double>(Y))
#endif

// 5.8 起 FJsonObject::Values 的键从 FString 换成了 UE::FSharedString，不能隐式转 FString。
// 两者的 operator* 都返回 const TCHAR*，按这个取键在各版本都能编过。
#define AGENT_PANEL_JSON_KEY(Key) FString(*(Key))

// 5.8 起 GetObjectsWithOuter 的 bool bIncludeNestedObjects 弃用，改收 EGetObjectsFlags（UObject/FindObjectFlags.h）
#if ENGINE_MAJOR_VERSION > 5 || (ENGINE_MAJOR_VERSION == 5 && ENGINE_MINOR_VERSION >= 8)
#include "UObject/FindObjectFlags.h"
#define AGENT_PANEL_OUTER_NESTED(bNested) ((bNested) ? EGetObjectsFlags::IncludeNestedObjects : EGetObjectsFlags::None)
#else
#define AGENT_PANEL_OUTER_NESTED(bNested) (bNested)
#endif
