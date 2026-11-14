// Live Coding：不管是谁触发的（VS 里按快捷键、面板的 Agent、编辑器里点按钮），都跟踪到结束，
// 把 LogLiveCoding 的输出和 LiveCodingConsole 日志末尾的编译器输出推给订阅了 liveCoding 的客户端，结果记下来给 livecoding.status。
// 从 UEToVSLink 搬过来；原来只在有订阅者时才跟踪，合并后一直跟踪（每 tick 只问一次 IsCompiling），这样 HTTP 触发的编译也有结果。
#include "UELinkStream.h"
#include "UELinkStreamJson.h"

#include "HAL/FileManager.h"
#include "HAL/PlatformTime.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Serialization/Archive.h"

#if AGENT_PANEL_LIVE_CODING
#include "ILiveCodingModule.h"
#endif

// 共用工具一律写全 UELinkStream::：单体构建（unity）会把同模块的 .cpp 拼在一起，别的文件的 using namespace 会漏进来

void FUELinkStream::BindLiveCoding()
{
#if AGENT_PANEL_LIVE_CODING
	if (bLiveCodingBound)
	{
		return;
	}
	if (ILiveCodingModule* LiveCoding = FModuleManager::GetModulePtr<ILiveCodingModule>(LIVE_CODING_MODULE_NAME))
	{
		PatchCompleteHandle = LiveCoding->GetOnPatchCompleteDelegate().AddRaw(this, &FUELinkStream::OnLiveCodingPatchComplete);
		bLiveCodingBound = true;
	}
#endif
}

void FUELinkStream::UnbindLiveCoding()
{
#if AGENT_PANEL_LIVE_CODING
	if (bLiveCodingBound)
	{
		if (ILiveCodingModule* LiveCoding = FModuleManager::GetModulePtr<ILiveCodingModule>(LIVE_CODING_MODULE_NAME))
		{
			LiveCoding->GetOnPatchCompleteDelegate().Remove(PatchCompleteHandle);
		}
		PatchCompleteHandle.Reset();
		bLiveCodingBound = false;
	}
#endif
}

void FUELinkStream::OnLiveCodingPatchComplete()
{
	bLiveCodingPatched = true;
}

FString FUELinkStream::FindLiveCodingConsoleLog() const
{
	// LiveCodingConsole 是单独的程序，它的 Saved 目录在哪取决于源码版还是安装版引擎：几处都看，取最新的
	const TCHAR* Relative = TEXT("Programs/LiveCodingConsole/Saved/Logs/LiveCodingConsole.log");
	const FString Candidates[] = {
		FPaths::Combine(FPaths::EngineDir(), Relative),
		FPaths::Combine(FPaths::EngineUserDir(), Relative),
		FPaths::Combine(FPaths::EngineVersionAgnosticUserDir(), Relative),
	};
	FString Best;
	FDateTime BestTime = FDateTime::MinValue();
	for (const FString& Candidate : Candidates)
	{
		const FDateTime Stamp = IFileManager::Get().GetTimeStamp(*Candidate);
		if (Stamp > BestTime)
		{
			BestTime = Stamp;
			Best = Candidate;
		}
	}
	return Best;
}

void FUELinkStream::ReadLiveCodingConsoleTail(TArray<FString>& OutLines) const
{
	if (LiveCodingConsolePath.IsEmpty())
	{
		return;
	}
	TUniquePtr<FArchive> Reader(IFileManager::Get().CreateFileReader(*LiveCodingConsolePath, FILEREAD_AllowWrite));
	if (!Reader.IsValid())
	{
		return;
	}
	const int64 Size = Reader->TotalSize();
	if (Size <= 0)
	{
		return;
	}
	uint8 Bom[2] = {0, 0};
	if (Size >= 2)
	{
		Reader->Serialize(Bom, 2);
	}
	const bool bUtf16 = Bom[0] == 0xFF && Bom[1] == 0xFE;

	int64 Start = LiveCodingConsoleStartBytes <= Size ? LiveCodingConsoleStartBytes : 0; // 日志轮转或被截短了：从头读
	Start = FMath::Max(Start, Size - UELinkStream::MaxConsoleTailBytes);
	if (bUtf16)
	{
		Start = FMath::Max<int64>(2, Start & ~static_cast<int64>(1));
	}
	const int64 Count = Size - Start;
	if (Count <= 0)
	{
		return;
	}
	TArray<uint8> Bytes;
	Bytes.SetNumUninitialized(static_cast<int32>(Count));
	Reader->Seek(Start);
	Reader->Serialize(Bytes.GetData(), Count);
	if (Reader->IsError())
	{
		return;
	}

	FString Text;
	if (bUtf16)
	{
		const int32 Chars = static_cast<int32>(Count / 2);
		Text.Reserve(Chars);
		const uint8* Data = Bytes.GetData();
		for (int32 i = 0; i < Chars; ++i)
		{
			Text.AppendChar(static_cast<TCHAR>(Data[i * 2] | (Data[i * 2 + 1] << 8)));
		}
	}
	else
	{
		FUTF8ToTCHAR Converted(reinterpret_cast<const ANSICHAR*>(Bytes.GetData()), Bytes.Num());
		Text = FString(Converted.Length(), Converted.Get());
	}

	TArray<FString> Lines;
	Text.ParseIntoArrayLines(Lines);
	const int32 First = FMath::Max(0, Lines.Num() - UELinkStream::MaxConsoleTailLines);
	for (int32 i = First; i < Lines.Num(); ++i)
	{
		OutLines.Add(UELinkStream::Clip(Lines[i], UELinkStream::MaxMessageChars));
	}
}

TSharedPtr<FJsonObject> FUELinkStream::LiveCodingStatus() const
{
	TSharedPtr<FJsonObject> Out = MakeShared<FJsonObject>();
#if AGENT_PANEL_LIVE_CODING
	Out->SetBoolField(TEXT("available"), FModuleManager::GetModulePtr<ILiveCodingModule>(LIVE_CODING_MODULE_NAME) != nullptr);
#else
	Out->SetBoolField(TEXT("available"), false);
#endif
	Out->SetStringField(TEXT("phase"), LiveCodingPhase);
	if (!LiveCodingResult.IsEmpty())
	{
		// success / failed / noChanges / cancelled / unknown，和推送的 finished 事件一样
		Out->SetStringField(TEXT("result"), LiveCodingResult);
	}
	if (!LiveCodingMessage.IsEmpty())
	{
		Out->SetStringField(TEXT("message"), LiveCodingMessage);
	}
	Out->SetNumberField(TEXT("durationMs"), bLiveCodingCompiling ? (FPlatformTime::Seconds() - LiveCodingStart) * 1000.0 : LiveCodingDurationMs);
	Out->SetArrayField(TEXT("output"), UELinkStream::StringValues(LiveCodingOutput));
	return Out;
}

void FUELinkStream::TickLiveCoding(const TArray<FAgentPanelLog::FEntry>& Entries)
{
#if AGENT_PANEL_LIVE_CODING
	ILiveCodingModule* LiveCoding = FModuleManager::GetModulePtr<ILiveCodingModule>(LIVE_CODING_MODULE_NAME);
	if (LiveCoding == nullptr)
	{
		return;
	}
	BindLiveCoding();
	const bool bCompiling = LiveCoding->IsCompiling();

	if (bCompiling && !bLiveCodingCompiling)
	{
		bLiveCodingCompiling = true;
		bLiveCodingPatched = false;
		bLiveCodingSawError = false;
		LiveCodingHints.Reset();
		LiveCodingOutput.Reset();
		LiveCodingPhase = TEXT("compiling");
		LiveCodingResult.Reset();
		LiveCodingMessage.Reset();
		LiveCodingStart = FPlatformTime::Seconds();
		LiveCodingConsolePath = FindLiveCodingConsoleLog();
		LiveCodingConsoleStartBytes = LiveCodingConsolePath.IsEmpty() ? 0 : FMath::Max<int64>(0, IFileManager::Get().FileSize(*LiveCodingConsolePath));
		Broadcast(TEXT("{\"event\":\"liveCoding\",\"phase\":\"started\"}"), false, true);
		// 这一 tick 的日志是在知道开始编译之前取的（Tick 里只在编译中才取），下一 tick 起才有 LogLiveCoding 的行
		return;
	}
	if (!bLiveCodingCompiling)
	{
		return;
	}

	TArray<FString> Output;
	for (const FAgentPanelLog::FEntry& Entry : Entries)
	{
		if (Entry.Category != FName(TEXT("LogLiveCoding")))
		{
			continue;
		}
		Output.Add(Entry.Text);
		LiveCodingHints.Add(Entry.Text);
		bLiveCodingSawError |= Entry.Verbosity <= ELogVerbosity::Error;
	}
	if (Output.Num() > 0)
	{
		LiveCodingOutput.Append(Output);
		FString Json = TEXT("{\"event\":\"liveCoding\",\"phase\":\"output\",\"lines\":");
		UELinkStream::AppendJsonStringArray(Json, Output);
		Json.AppendChar(TEXT('}'));
		Broadcast(Json, false, true);
	}

	if (bCompiling)
	{
		return;
	}

	// 编完了：编译器的输出在 Live Coding 控制台的日志里，不在编辑器日志里
	TArray<FString> Tail;
	ReadLiveCodingConsoleTail(Tail);
	bool bTailError = false;
	for (const FString& Line : Tail)
	{
		bTailError |= Line.Contains(TEXT(": error ")) || Line.Contains(TEXT(": fatal error "));
	}
	if (Tail.Num() > 0)
	{
		LiveCodingOutput.Append(Tail);
		FString Json = TEXT("{\"event\":\"liveCoding\",\"phase\":\"output\",\"source\":\"console\",\"lines\":");
		UELinkStream::AppendJsonStringArray(Json, Tail);
		Json.AppendChar(TEXT('}'));
		Broadcast(Json, false, true);
	}
	if (LiveCodingOutput.Num() > UELinkStream::MaxStatusOutputLines)
	{
		LiveCodingOutput.RemoveAt(0, LiveCodingOutput.Num() - UELinkStream::MaxStatusOutputLines);
	}

	FString Result = TEXT("unknown");
	FString Message;
	for (const FString& Hint : LiveCodingHints)
	{
		if (Hint.Contains(TEXT("No changes"), ESearchCase::IgnoreCase) || Hint.Contains(TEXT("No code changes"), ESearchCase::IgnoreCase))
		{
			Result = TEXT("noChanges");
			Message = Hint;
		}
		else if (Hint.Contains(TEXT("cancel"), ESearchCase::IgnoreCase))
		{
			Result = TEXT("cancelled");
			Message = Hint;
		}
		else if (Hint.Contains(TEXT("fail"), ESearchCase::IgnoreCase))
		{
			Result = TEXT("failed");
			Message = Hint;
		}
		else if (Hint.Contains(TEXT("succeeded"), ESearchCase::IgnoreCase) && Result == TEXT("unknown"))
		{
			Result = TEXT("success");
			Message = Hint;
		}
	}
	if (bLiveCodingPatched && Result != TEXT("failed"))
	{
		Result = TEXT("success");
	}
	if (bTailError || (bLiveCodingSawError && Result == TEXT("unknown")))
	{
		Result = TEXT("failed");
	}

	LiveCodingDurationMs = (FPlatformTime::Seconds() - LiveCodingStart) * 1000.0;
	FString Json = TEXT("{\"event\":\"liveCoding\",\"phase\":\"finished\",\"result\":");
	UELinkStream::AppendJsonString(Json, Result);
	Json.Appendf(TEXT(",\"durationMs\":%.0f"), LiveCodingDurationMs);
	if (!Message.IsEmpty())
	{
		Json.Append(TEXT(",\"message\":"));
		UELinkStream::AppendJsonString(Json, Message);
	}
	Json.AppendChar(TEXT('}'));
	Broadcast(Json, false, true);

	LiveCodingPhase = TEXT("finished");
	LiveCodingResult = Result;
	LiveCodingMessage = Message;
	bLiveCodingCompiling = false;
	LiveCodingHints.Empty();
#endif
}
