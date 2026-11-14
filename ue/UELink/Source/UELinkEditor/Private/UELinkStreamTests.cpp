// 自动化测试：用控制台命令 Automation RunTests 在本进程里跑，测试框架的开始 / 结束回调逐条推 tests 事件，
// 结果同时记下来给 HTTP 的 tests.status（面板的 Agent 拿它当测试步骤）。从 UEToVSLink 搬过来。
#include "UELinkStream.h"
#include "UELinkStreamJson.h"

#include "Editor.h"
#include "Engine/Engine.h"
#include "HAL/PlatformTime.h"
#include "Misc/AutomationTest.h"
#include "Modules/ModuleManager.h"

DEFINE_LOG_CATEGORY_STATIC(LogUELinkTests, Log, All);

// 共用工具一律写全 UELinkStream::：单体构建（unity）会把同模块的 .cpp 拼在一起，别的文件的 using namespace 会漏进来

namespace
{
	/** Automation RunTests 的命令行必须是一条命令：去掉分隔符和换行 */
	FString SanitizeTestFilter(const FString& Filter)
	{
		FString Out;
		Out.Reserve(Filter.Len());
		for (TCHAR C : Filter)
		{
			if (C == TEXT(';') || C == TEXT('|') || C == TEXT('\r') || C == TEXT('\n') || C == TEXT('"'))
			{
				continue;
			}
			Out.AppendChar(C);
		}
		return Out.TrimStartAndEnd();
	}

	FString TestName(FAutomationTestBase* Test)
	{
		FString Name = Test->GetBeautifiedTestName();
		const FString Context = Test->GetTestContext();
		if (!Context.IsEmpty())
		{
			Name += TEXT(" ");
			Name += Context;
		}
		return Name;
	}
}

bool FUELinkStream::StartTests(const FString& RunId, const FString& Filter, FString& Error)
{
	// 报错用英文：旧 UEToVS 原样显示
	const FString Clean = SanitizeTestFilter(Filter);
	if (Clean.IsEmpty())
	{
		Error = TEXT("missing filter");
		return false;
	}
	if (bTestRunActive)
	{
		Error = TEXT("a test run is already active");
		return false;
	}
	if (GEngine == nullptr || !TickHandle.IsValid())
	{
		Error = TEXT("engine is not ready");
		return false;
	}

	// Automation RunTests 在 AutomationController 模块里，AutomationWorker 在本进程里执行测试
	FModuleManager::Get().LoadModule(TEXT("AutomationController"));
	FModuleManager::Get().LoadModule(TEXT("AutomationWorker"));

	bTestRunActive = true;
	TestRunId = RunId;
	TestFilter = Clean;
	TestRunStart = FPlatformTime::Seconds();
	TestRunEnd = 0.0;
	TestLastActivity = TestRunStart;
	TestRunMessage.Reset();
	bTestStarted = false;
	bTestInProgress = false;
	TestsPassed = 0;
	TestsFailed = 0;
	RunLogs.Reset();
	FinishedTests.Reset();

	FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"runStarted\",\"id\":");
	UELinkStream::AppendJsonString(Json, TestRunId);
	Json.Append(TEXT(",\"filter\":"));
	UELinkStream::AppendJsonString(Json, TestFilter);
	Json.AppendChar(TEXT('}'));
	Broadcast(Json, true, false);

	UWorld* World = GEditor != nullptr ? GEditor->GetEditorWorldContext().World() : nullptr;
	const FString Command = FString::Printf(TEXT("Automation RunTests %s"), *Clean);
	UE_LOG(LogUELinkTests, Log, TEXT("UELink：%s"), *Command);
	GEngine->Exec(World, *Command);
	return true;
}

TSharedPtr<FJsonObject> FUELinkStream::TestsStatus() const
{
	TSharedPtr<FJsonObject> Out = MakeShared<FJsonObject>();
	Out->SetBoolField(TEXT("running"), bTestRunActive);
	Out->SetStringField(TEXT("id"), TestRunId);
	Out->SetStringField(TEXT("filter"), TestFilter);
	Out->SetNumberField(TEXT("passed"), TestsPassed);
	Out->SetNumberField(TEXT("failed"), TestsFailed);
	if (bTestInProgress)
	{
		Out->SetStringField(TEXT("current"), CurrentTestName);
	}
	if (TestRunStart > 0)
	{
		const double End = bTestRunActive || TestRunEnd <= 0 ? FPlatformTime::Seconds() : TestRunEnd;
		Out->SetNumberField(TEXT("durationMs"), (End - TestRunStart) * 1000.0);
	}
	if (!TestRunMessage.IsEmpty())
	{
		Out->SetStringField(TEXT("message"), TestRunMessage);
	}
	TArray<TSharedPtr<FJsonValue>> Tests;
	for (const FTestRecord& Record : FinishedTests)
	{
		TSharedPtr<FJsonObject> T = MakeShared<FJsonObject>();
		T->SetStringField(TEXT("name"), Record.Name);
		T->SetStringField(TEXT("result"), Record.bPassed ? TEXT("passed") : TEXT("failed"));
		T->SetNumberField(TEXT("durationMs"), Record.DurationMs);
		T->SetArrayField(TEXT("errors"), UELinkStream::StringValues(Record.Errors));
		Tests.Add(MakeShared<FJsonValueObject>(T));
	}
	Out->SetArrayField(TEXT("tests"), Tests);
	return Out;
}

void FUELinkStream::OnTestStart(FAutomationTestBase* Test)
{
	QueueTestEvent(Test, true);
}

void FUELinkStream::OnTestEnd(FAutomationTestBase* Test)
{
	QueueTestEvent(Test, false);
}

void FUELinkStream::QueueTestEvent(FAutomationTestBase* Test, bool bStart)
{
	if (Test == nullptr)
	{
		return;
	}
	FTestEvent Event;
	Event.bStart = bStart;
	Event.Name = TestName(Test);
	// 下一行日志会拿到的序号：开始事件之后、结束事件之前的日志算这条测试的
	Event.Seq = FAgentPanelLog::Get().LastSeq() + 1;
	Event.Time = FPlatformTime::Seconds();
	if (!bStart)
	{
		FAutomationTestExecutionInfo Info;
		Test->GetExecutionInfo(Info);
		for (const FAutomationExecutionEntry& Entry : Info.GetEntries())
		{
			if (Entry.Event.Type == EAutomationEventType::Error)
			{
				Event.Errors.Add(UELinkStream::Clip(Entry.Event.Message, UELinkStream::MaxTestMessageChars));
			}
			else if (Entry.Event.Type == EAutomationEventType::Warning)
			{
				Event.Warnings.Add(UELinkStream::Clip(Entry.Event.Message, UELinkStream::MaxTestMessageChars));
			}
		}
	}
	TestEvents.Enqueue(MoveTemp(Event));
}

void FUELinkStream::TickTests(const TArray<FAgentPanelLog::FEntry>& Entries)
{
	if (bTestRunActive)
	{
		RunLogs.Append(Entries);
		if (RunLogs.Num() > UELinkStream::MaxRunLogs)
		{
			RunLogs.RemoveAt(0, RunLogs.Num() - UELinkStream::MaxRunLogs);
		}
	}

	FTestEvent Event;
	while (TestEvents.Dequeue(Event))
	{
		TestLastActivity = FPlatformTime::Seconds();
		if (Event.bStart)
		{
			bTestStarted = true;
			bTestInProgress = true;
			CurrentTestName = Event.Name;
			CurrentTestStartSeq = Event.Seq;
			CurrentTestStartTime = Event.Time;
			FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"testStarted\",\"id\":");
			UELinkStream::AppendJsonString(Json, TestRunId);
			Json.Append(TEXT(",\"name\":"));
			UELinkStream::AppendJsonString(Json, Event.Name);
			Json.AppendChar(TEXT('}'));
			Broadcast(Json, true, false);
			continue;
		}

		const bool bPassed = Event.Errors.Num() == 0;
		bTestInProgress = false;
		if (bPassed)
		{
			++TestsPassed;
		}
		else
		{
			++TestsFailed;
		}

		TArray<FString> Logs;
		const uint64 StartSeq = Event.Name == CurrentTestName ? CurrentTestStartSeq : Event.Seq;
		for (const FAgentPanelLog::FEntry& Entry : RunLogs)
		{
			if (Entry.Seq >= StartSeq && Entry.Seq < Event.Seq && Logs.Num() < UELinkStream::MaxTestLogs)
			{
				Logs.Add(FString::Printf(TEXT("%s: %s"), *Entry.Category.ToString(), *UELinkStream::Clip(Entry.Text, UELinkStream::MaxTestMessageChars)));
			}
		}
		RunLogs.RemoveAll([&Event](const FAgentPanelLog::FEntry& Entry) { return Entry.Seq < Event.Seq; });

		const double DurationMs = Event.Name == CurrentTestName ? (Event.Time - CurrentTestStartTime) * 1000.0 : 0.0;
		if (FinishedTests.Num() < UELinkStream::MaxFinishedTests)
		{
			FinishedTests.Add({Event.Name, bPassed, DurationMs, Event.Errors});
		}
		FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"testFinished\",\"id\":");
		UELinkStream::AppendJsonString(Json, TestRunId);
		Json.Append(TEXT(",\"name\":"));
		UELinkStream::AppendJsonString(Json, Event.Name);
		Json.Append(bPassed ? TEXT(",\"result\":\"passed\"") : TEXT(",\"result\":\"failed\""));
		Json.Appendf(TEXT(",\"durationMs\":%.1f,\"errors\":"), DurationMs);
		UELinkStream::AppendJsonStringArray(Json, Event.Errors);
		Json.Append(TEXT(",\"warnings\":"));
		UELinkStream::AppendJsonStringArray(Json, Event.Warnings);
		Json.Append(TEXT(",\"logs\":"));
		UELinkStream::AppendJsonStringArray(Json, Logs);
		Json.AppendChar(TEXT('}'));
		Broadcast(Json, true, false);
	}

	if (!bTestRunActive)
	{
		return;
	}

	// 运行结束：命令行执行器会在日志里报完成；措辞不同的引擎版本靠闲置超时兜底。
	// 控制器报了失败的，覆盖掉「没有错误条目所以算通过」的结果
	for (const FAgentPanelLog::FEntry& Entry : Entries)
	{
		const FString Category = Entry.Category.ToString();
		if (Category == TEXT("LogAutomationController") && Entry.Text.StartsWith(TEXT("Test Completed.")))
		{
			FString Result;
			FString Path;
			const int32 ResultAt = Entry.Text.Find(TEXT("Result={"));
			const int32 PathAt = Entry.Text.Find(TEXT("Path={"));
			if (ResultAt != INDEX_NONE && PathAt != INDEX_NONE)
			{
				Result = Entry.Text.Mid(ResultAt + 8);
				Result = Result.Left(Result.Find(TEXT("}")));
				Path = Entry.Text.Mid(PathAt + 6);
				Path = Path.Left(Path.Find(TEXT("}")));
			}
			if (Result.Contains(TEXT("fail"), ESearchCase::IgnoreCase))
			{
				for (FTestRecord& Record : FinishedTests)
				{
					if (Record.bPassed && (Path == Record.Name || Path.StartsWith(Record.Name + TEXT("."))))
					{
						Record.bPassed = false;
						Record.Errors.Add(Entry.Text);
						--TestsPassed;
						++TestsFailed;
						FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"testFinished\",\"id\":");
						UELinkStream::AppendJsonString(Json, TestRunId);
						Json.Append(TEXT(",\"name\":"));
						UELinkStream::AppendJsonString(Json, Record.Name);
						Json.Append(TEXT(",\"result\":\"failed\",\"errors\":"));
						UELinkStream::AppendJsonStringArray(Json, {Entry.Text});
						Json.AppendChar(TEXT('}'));
						Broadcast(Json, true, false);
						break;
					}
				}
			}
			continue;
		}
		if (Category != TEXT("LogAutomationCommandLine"))
		{
			continue;
		}
		if (Entry.Text.Contains(TEXT("No automation tests matched"), ESearchCase::IgnoreCase))
		{
			FinishTestRun(TEXT("no tests matched"));
			return;
		}
		if (Entry.Text.Contains(TEXT("TEST COMPLETE"), ESearchCase::CaseSensitive))
		{
			FinishTestRun(FString());
			return;
		}
	}

	const double Idle = FPlatformTime::Seconds() - TestLastActivity;
	if (!bTestInProgress && Idle > (bTestStarted ? UELinkStream::TestIdleSeconds : UELinkStream::TestIdleBeforeFirstSeconds))
	{
		FinishTestRun(bTestStarted ? FString() : TEXT("no tests started"));
	}
}

void FUELinkStream::FinishTestRun(const FString& Message)
{
	TestRunEnd = FPlatformTime::Seconds();
	TestRunMessage = Message;
	FString Json = TEXT("{\"event\":\"tests\",\"phase\":\"runFinished\",\"id\":");
	UELinkStream::AppendJsonString(Json, TestRunId);
	Json.Appendf(TEXT(",\"passed\":%d,\"failed\":%d,\"durationMs\":%.0f"), TestsPassed, TestsFailed, (TestRunEnd - TestRunStart) * 1000.0);
	if (!Message.IsEmpty())
	{
		Json.Append(TEXT(",\"message\":"));
		UELinkStream::AppendJsonString(Json, Message);
	}
	Json.AppendChar(TEXT('}'));
	Broadcast(Json, true, false);

	UE_LOG(LogUELinkTests, Log, TEXT("UELink：测试运行 %s 结束（%d 通过，%d 失败）"), *TestRunId, TestsPassed, TestsFailed);
	bTestRunActive = false;
	bTestInProgress = false;
	// TestRunId、FinishedTests 留着给 tests.status 查最近一次的结果，下次开跑时再清
	RunLogs.Empty();
}
