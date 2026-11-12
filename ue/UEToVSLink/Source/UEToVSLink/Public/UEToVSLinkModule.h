#pragma once

#include "CoreMinimal.h"
#include "Containers/Queue.h"
#include "Containers/Ticker.h"
#include "HAL/CriticalSection.h"
#include "Misc/OutputDevice.h"
#include "Modules/ModuleInterface.h"

#include <atomic>

class FSocket;
class FTcpListener;
class FAutomationTestBase;
struct FIPv4Endpoint;

/** One captured log line (any thread -> game thread). */
struct FUEToVSLinkLogEntry
{
	FString Message;
	FName Category;
	double Time = 0.0;
	uint64 Seq = 0;
	uint8 Verbosity = 0;
};

/**
 * Output device registered with GLog while the module runs. Serialize() may be called from any thread: it only copies
 * the line (truncated) into a small locked buffer, and only while capture is enabled (a subscriber wants logs, a test
 * run or a Live Coding subscriber is active). It never logs itself and never touches sockets.
 */
class FUEToVSLinkLogCapture : public FOutputDevice
{
public:
	static constexpr int32 MaxPending = 20000;
	static constexpr int32 MaxMessageChars = 4000;

	virtual void Serialize(const TCHAR* V, ELogVerbosity::Type Verbosity, const FName& Category) override;
	virtual bool CanBeUsedOnAnyThread() const override { return true; }
	virtual bool CanBeUsedOnMultipleThreads() const override { return true; }

	void SetEnabled(bool bInEnabled) { bEnabled.store(bInEnabled, std::memory_order_relaxed); }
	bool IsEnabled() const { return bEnabled.load(std::memory_order_relaxed); }

	/** Sequence number the next captured line will get (lines logged before this call have a smaller one). */
	uint64 NextSeq();

	/** Moves the captured lines out; OutDropped = lines dropped because the buffer was full since the last drain. */
	void Drain(TArray<FUEToVSLinkLogEntry>& Out, int64& OutDropped);

	double StartSeconds = 0.0;

private:
	std::atomic<bool> bEnabled{ false };
	FCriticalSection Lock;
	TArray<FUEToVSLinkLogEntry> Pending;
	uint64 Seq = 1;
	int64 Dropped = 0;
};

/**
 * Localhost TCP server for the UEToVS Visual Studio extension (protocol 2, backward compatible with protocol 1).
 * One JSON object per line. One-shot commands get one reply line, then the connection is closed:
 *   {"cmd":"ping","protocol":2} -> {"ok":true,"project":..,"engine":..,"protocol":2,"plugin":"0.2.0","features":[...]}
 *   {"cmd":"openAsset"|"browseAsset","path":"/Game/..."}, {"cmd":"liveCoding"}, {"cmd":"runTests","id":"r1","filter":"Project.Foo"}
 * {"cmd":"subscribe","topics":["log","tests","liveCoding"]} keeps the connection open and pushes event lines
 * ({"event":"log"|"tests"|"liveCoding"|"subscribed",...}); the client may send another subscribe line to change topics.
 * Everything runs on the game thread (core ticker, 20 Hz) with non-blocking sockets: a slow or vanished client never
 * blocks the editor (its outgoing buffer is capped; log lines beyond the cap are dropped and counted).
 * The listening port is written to <Project>/Saved/UEToVSLink/port.txt.
 */
class FUEToVSLinkModule : public IModuleInterface
{
public:
	virtual void StartupModule() override;
	virtual void ShutdownModule() override;

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

	/** Automation framework callback, copied out on the calling thread and processed in Tick. */
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
	};

	bool OnConnection(FSocket* Socket, const FIPv4Endpoint& Endpoint);
	bool Tick(float DeltaTime);
	FString Handle(const FString& Line, bool& bOutSubscribe, TArray<FString>& OutTopics);
	void Reply(FSocket* Socket, const FString& Json);
	FString PortFilePath() const;

	// ---- v0.4.5 streaming (protocol 2)
	void TickSubscribers();
	void ApplyTopics(FSubscriber& Sub, const TArray<FString>& Topics);
	void HandleSubscriberLine(FSubscriber& Sub, const FString& Line);
	void Enqueue(FSubscriber& Sub, const FString& JsonLine);
	void Broadcast(const FString& JsonLine, bool bTestsTopic, bool bLiveCodingTopic);
	void BroadcastLogs(const FString& LinesJson, int32 LineCount, int64 Dropped);
	void Flush(FSubscriber& Sub);
	void UpdateCapture();
	void ProcessLogs(const TArray<FUEToVSLinkLogEntry>& Entries, int64 Dropped);

	FString StartTests(const FString& RunId, const FString& Filter);
	void OnTestStart(FAutomationTestBase* Test);
	void OnTestEnd(FAutomationTestBase* Test);
	void QueueTestEvent(FAutomationTestBase* Test, bool bStart);
	void TickTests(const TArray<FUEToVSLinkLogEntry>& Entries);
	void FinishTestRun(const FString& Message);

	void TickLiveCoding(const TArray<FUEToVSLinkLogEntry>& Entries);
	void BindLiveCoding();
	void UnbindLiveCoding();
	void OnLiveCodingPatchComplete();
	FString FindLiveCodingConsoleLog() const;
	void ReadLiveCodingConsoleTail(TArray<FString>& OutLines) const;

	TUniquePtr<FTcpListener> Listener;
	TQueue<FSocket*, EQueueMode::Mpsc> Pending;
	TArray<FClient> Clients;
	TArray<FSubscriber> Subscribers;
	FTSTicker::FDelegateHandle TickHandle;
	TUniquePtr<FUEToVSLinkLogCapture> Capture;

	// tests
	TQueue<FTestEvent, EQueueMode::Mpsc> TestEvents;
	bool bTestRunActive = false;
	FString TestRunId;
	FString TestFilter;
	double TestRunStart = 0.0;
	double TestLastActivity = 0.0;
	bool bTestStarted = false;
	bool bTestInProgress = false;
	FString CurrentTestName;
	uint64 CurrentTestStartSeq = 0;
	double CurrentTestStartTime = 0.0;
	int32 TestsPassed = 0;
	int32 TestsFailed = 0;
	TArray<FUEToVSLinkLogEntry> RunLogs;
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
};
