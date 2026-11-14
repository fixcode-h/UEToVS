// 桥接服务的对外接口：编辑器模块、后台编辑器（commandlet）通过它登记自己的操作、启动服务。
// 协议：POST /rpc，请求头 X-AgentPanel-Token，正文 {"op": "...", "args": {...}}，回 {"ok": true, ...} 或 {"ok": false, "error": "..."}；
// GET /info 回进程和世界列表。只监听 127.0.0.1，令牌每次启动随机生成、写在 Saved/UELink/instances/<PID>.json 的名片里（兼容旧面板，Saved/AgentPanel/instances 下也写一份）。
#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"

class UWorld;

/** 一个操作：args 是请求里的 args，往 Out 里写结果字段；出错时返回 false 并写 Error（原样显示给用户和 Agent） */
using FAgentPanelOp = TFunction<bool(const TSharedPtr<FJsonObject>& Args, TSharedPtr<FJsonObject>& Out, FString& Error)>;

class UELINKCORE_API FAgentPanelBridge
{
public:
	static FAgentPanelBridge& Get();

	/** 登记操作（同名的后登记的覆盖前面的：编辑器模块用它接管编辑器世界的截图等） */
	void AddOp(const FString& Name, FAgentPanelOp Op);

	/** 起服务、写名片；Kind 是 editor / game / server / background。已经起过就什么都不做 */
	bool Start(const FString& Kind);
	void Stop();
	bool IsRunning() const { return Port != 0; }

	/** 最近一次收到请求的时间（后台编辑器按它判断闲置多久） */
	double LastRequestTime() const { return LastRequest; }

	/** 按世界 id 找世界：editor、pie0、pie1…、game；空表示默认（有 PIE 用 PIE 的服务器 / 单机世界，否则游戏世界，否则编辑器世界） */
	static UWorld* ResolveWorld(const FString& WorldId, FString& Error);
	static FString WorldId(const UWorld* World);

	/** 往名片里加一个字段（编辑器模块用它登记推送通道的端口 stream、features）；服务已经起来时立刻重写名片 */
	void SetCardField(const FString& Name, const TSharedPtr<FJsonValue>& Value);

	/** 本次启动的令牌（推送通道用同一个令牌认新客户端） */
	const FString& GetToken() const { return Token; }

private:
	TMap<FString, FAgentPanelOp> Ops;
	int32 Port = 0;
	FString Token;
	FString Kind;
	FString StartedAt;
	TArray<FString> CardFiles;
	TMap<FString, TSharedPtr<FJsonValue>> CardExtra;
	double LastRequest = 0;

	void WriteCard();
	TSharedPtr<FJsonObject> Info() const;
	TSharedPtr<FJsonObject> Dispatch(const TSharedPtr<FJsonObject>& Body);
	void RegisterRuntimeOps();
};
