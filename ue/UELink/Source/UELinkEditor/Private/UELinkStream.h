// 推送通道：从 UEToVSLink（UEToVS 的编辑器插件，协议 2）合进来的本机 TCP 服务，一行一个 JSON。
//
// 一次性命令回一行就关连接：
//   {"cmd":"ping","protocol":2} -> {"ok":true,"project":..,"engine":..,"protocol":2,"plugin":"2.0.0","features":[...]}
//   {"cmd":"openAsset"|"browseAsset","path":"/Game/..."}、{"cmd":"liveCoding"}、{"cmd":"runTests","id":"r1","filter":"Project.Foo"}
// {"cmd":"subscribe","topics":["log","tests","liveCoding"]} 之后连接一直开着，服务主动推事件行
// （{"event":"log"|"tests"|"liveCoding"|"subscribed",...}），客户端可以再发 subscribe 换主题。
//
// 令牌：请求里带 "token" 时必须和名片里的一致，不一致直接拒绝；不带令牌的是旧 UEToVS（兼容层，三期去掉，到时一律要求令牌）。
// 端口写在名片的 stream 字段，同时写 Saved/UEToVSLink/port.txt 给旧 UEToVS。
// 全部在游戏线程上跑（核心 ticker，20 Hz），套接字都是非阻塞的：客户端卡住或消失都不会卡编辑器（每个订阅者的发送缓冲有上限，
// 超过软上限丢日志并计数，超过硬上限断开）。
//
// 日志不再自己挂 GLog，统一从 UELinkCore 的 FAgentPanelLog 环形缓冲按序号取。
// Live Coding 的状态一直跟踪（只是轮询 IsCompiling，很便宜），这样 HTTP 发起的编译也能在 livecoding.status 里拿到结果。
#pragma once

#include "CoreMinimal.h"
#include "AgentPanelLog.h"
#include "Containers/Queue.h"
#include "Containers/Ticker.h"
#include "Dom/JsonObject.h"

class FSocket;
class FTcpListener;
class FAutomationTestBase;
struct FIPv4Endpoint;

class FUELinkStream
{
public:
	static FUELinkStream& Get();

	/** 编辑器里引擎初始化完成后起（commandlet、游戏进程不起） */
	void Start();
	void Stop();

	/** 推送通道和旧 UEToVS 协议里 ping 报的能力 */
	static TArray<FString> Features();

	// ---- 给 HTTP 操作用（UELinkStreamOps.cpp），和推送通道共用同一份状态
	/** 开始跑自动化测试；返回 false 时 Error 是原因。结果按 tests 事件推送，也记进 TestsStatus */
	bool StartTests(const FString& RunId, const FString& Filter, FString& Error);
	/** 当前或最近一次测试运行的状态和每条测试的结果 */
	TSharedPtr<FJsonObject> TestsStatus() const;
	/** 当前或最近一次 Live Coding 的状态、结果和编译器输出的末尾 */
	TSharedPtr<FJsonObject> LiveCodingStatus() const;
	/** 触发 Live Coding；Message 非空时是附带说明（例如 already compiling） */
	bool TriggerLiveCoding(FString& Message, FString& Error);
	/** 打开资源（bBrowse 时只在内容浏览器里定位）；Message 是附带说明 */
	bool OpenAsset(const FString& Path, bool bBrowse, FString& Message, FString& Error);

private:
	struct FClient
	{
		FSocket* Socket = nullptr;
		TArray<uint8> Buffer;
		double StartTime = 0.0;
	};

	struct FSubscriber
	{
		FSocket* Socket = nullptr;
		TArray<uint8> In;
		TArray<uint8> Out;
		int32 OutOffset = 0;
		bool bLog = false;
		bool bTests = false;
		bool bLiveCoding = false;
		bool bDead = false;
		int64 Dropped = 0;
	};

	/** 自动化测试框架的回调：在调用线程上拷出来，Tick 里再处理 */
	struct FTestEvent
	{
		bool bStart = false;
		FString Name;
		uint64 Seq = 0;
		double Time = 0.0;
		TArray<FString> Errors;
		TArray<FString> Warnings;
	};

	struct FTestRecord
	{
		FString Name;
		bool bPassed = false;
		double DurationMs = 0.0;
		TArray<FString> Errors;
	};

	bool OnConnection(FSocket* Socket, const FIPv4Endpoint& Endpoint);
	bool Tick(float DeltaTime);
	FString Handle(const FString& Line, bool& bOutSubscribe, TArray<FString>& OutTopics);
	void Reply(FSocket* Socket, const FString& Json);
	FString PortFilePath() const;
	void PublishPort();

	// 订阅（UELinkStream.cpp）
	void TickSubscribers();
	void ApplyTopics(FSubscriber& Sub, const TArray<FString>& Topics);
	void HandleSubscriberLine(FSubscriber& Sub, const FString& Line);
	void Enqueue(FSubscriber& Sub, const FString& JsonLine);
	void Broadcast(const FString& JsonLine, bool bTestsTopic, bool bLiveCodingTopic);
	void BroadcastLogs(const FString& LinesJson, int32 LineCount, int64 Dropped);
	void Flush(FSubscriber& Sub);
	bool WantsLogs() const;
	void ProcessLogs(const TArray<FAgentPanelLog::FEntry>& Entries, int64 Dropped);

	// 自动化测试（UELinkStreamTests.cpp）
	void OnTestStart(FAutomationTestBase* Test);
	void OnTestEnd(FAutomationTestBase* Test);
	void QueueTestEvent(FAutomationTestBase* Test, bool bStart);
	void TickTests(const TArray<FAgentPanelLog::FEntry>& Entries);
	void FinishTestRun(const FString& Message);

	// Live Coding（UELinkStreamLiveCoding.cpp）
	void TickLiveCoding(const TArray<FAgentPanelLog::FEntry>& Entries);
	void BindLiveCoding();
	void UnbindLiveCoding();
	void OnLiveCodingPatchComplete();
	FString FindLiveCodingConsoleLog() const;
	void ReadLiveCodingConsoleTail(TArray<FString>& OutLines) const;

	TUniquePtr<FTcpListener> Listener;
	int32 Port = 0;
	TQueue<FSocket*, EQueueMode::Mpsc> Pending;
	TArray<FClient> Clients;
	TArray<FSubscriber> Subscribers;
	FTSTicker::FDelegateHandle TickHandle;
	/** 从日志缓冲取到哪了；日志行里的时间按 StartSeconds 换成相对秒数（和 UEToVSLink 一样） */
	uint64 LogCursor = 0;
	double StartSeconds = 0.0;

	// 测试
	TQueue<FTestEvent, EQueueMode::Mpsc> TestEvents;
	bool bTestRunActive = false;
	FString TestRunId;
	FString TestFilter;
	double TestRunStart = 0.0;
	double TestRunEnd = 0.0;
	double TestLastActivity = 0.0;
	bool bTestStarted = false;
	bool bTestInProgress = false;
	FString TestRunMessage;
	FString CurrentTestName;
	uint64 CurrentTestStartSeq = 0;
	double CurrentTestStartTime = 0.0;
	int32 TestsPassed = 0;
	int32 TestsFailed = 0;
	TArray<FAgentPanelLog::FEntry> RunLogs;
	TArray<FTestRecord> FinishedTests;
	FDelegateHandle TestStartHandle;
	FDelegateHandle TestEndHandle;

	// Live Coding
	bool bLiveCodingBound = false;
	bool bLiveCodingCompiling = false;
	bool bLiveCodingPatched = false;
	double LiveCodingStart = 0.0;
	FString LiveCodingConsolePath;
	int64 LiveCodingConsoleStartBytes = 0;
	TArray<FString> LiveCodingHints;
	bool bLiveCodingSawError = false;
	FDelegateHandle PatchCompleteHandle;
	/** 最近一次编译的结果（livecoding.status 用）：phase idle / compiling / finished */
	FString LiveCodingPhase = TEXT("idle");
	FString LiveCodingResult;
	FString LiveCodingMessage;
	double LiveCodingDurationMs = 0.0;
	TArray<FString> LiveCodingOutput;
};
