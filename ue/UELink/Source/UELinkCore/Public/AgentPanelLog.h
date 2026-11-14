// 进程里的日志：挂在 GLog 上收最近的日志行（环形缓冲，带序号），面板按序号增量取；
// 以及清空 UE 正占着的日志文件（外面删不掉，只能进程自己关掉、清空、在原文件名上重开，用户 2026-11-10 要的）。
#pragma once

#include "CoreMinimal.h"
#include "Misc/OutputDevice.h"
#include "Dom/JsonObject.h"

/** 进程里唯一的日志缓冲：HTTP 的 log.tail 和推送通道（UEToVS 的日志窗口、测试、Live Coding）都从这里取，GLog 只挂这一个 */
class UELINKCORE_API FAgentPanelLog : public FOutputDevice
{
public:
	static FAgentPanelLog& Get();

	virtual void Serialize(const TCHAR* V, ELogVerbosity::Type Verbosity, const FName& Category) override;
	virtual bool CanBeUsedOnAnyThread() const override { return true; }
	virtual bool CanBeUsedOnMultipleThreads() const override { return true; }

	void Attach();
	void Detach();

	/** 当前最大序号（控制台命令执行前记一下，执行后取这之后的行就是它的输出） */
	uint64 LastSeq() const;

	/** 取 Since 之后的行：Category 前缀、最低级别（Error / Warning / Display / Log / Verbose）、包含的文字 */
	TSharedPtr<FJsonObject> Tail(uint64 Since, int32 Limit, const FString& Category, const FString& MinVerbosity, const FString& Contains) const;

	/** Since 之后的行的纯文本（控制台输出用） */
	TArray<FString> TextSince(uint64 Since, int32 Limit) const;

	/** 清空日志文件；bBackup 先复制一份到 Saved/Logs/Cleared/。返回文件路径和清空前的大小 */
	bool ClearFile(bool bBackup, TSharedPtr<FJsonObject>& Out, FString& Error);

	/** 正在写的日志文件 */
	FString FilePath() const;

	/** 一行日志的原样数据（推送通道按序号增量取，自己拼 JSON，不经过 FJsonObject） */
	struct FEntry
	{
		uint64 Seq = 0;
		double Time = 0;
		FString Text;
		FName Category;
		ELogVerbosity::Type Verbosity = ELogVerbosity::Log;
	};

	/**
	 * 取 Since 之后的行，按时间顺序追加到 Out，最多 Limit 行（超出的取最新的）；
	 * OutDropped 是游标落后太多、已经被环形缓冲挤掉的行数加上因 Limit 没给的行数。返回当前最大序号，下次拿它当 Since
	 */
	uint64 EntriesSince(uint64 Since, int32 Limit, TArray<FEntry>& Out, int64& OutDropped) const;

private:
	struct FLine
	{
		uint64 Seq;
		double Time;
		FString Text;
		FName Category;
		ELogVerbosity::Type Verbosity;
	};
	mutable FCriticalSection Lock;
	TArray<FLine> Lines;
	int32 Head = 0;
	uint64 Seq = 0;
	int32 Errors = 0;
	int32 Warnings = 0;
	bool bAttached = false;
	static constexpr int32 Capacity = 8000;
	/** 单行最多留多少字符：大段转储（memreport、obj list）只留开头，环形缓冲的内存有上限 */
	static constexpr int32 MaxLineChars = 4000;
};
