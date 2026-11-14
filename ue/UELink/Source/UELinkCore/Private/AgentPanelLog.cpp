#include "AgentPanelLog.h"

#include "HAL/FileManager.h"
#include "HAL/PlatformOutputDevices.h"
#include "Misc/OutputDeviceFile.h"
#include "Misc/OutputDeviceRedirector.h"
#include "Misc/Paths.h"
#include "Misc/DateTime.h"
#include "Misc/FileHelper.h"
#include "Dom/JsonValue.h"
#include "Algo/Reverse.h"

DEFINE_LOG_CATEGORY_STATIC(LogAgentPanel, Log, All);

FAgentPanelLog& FAgentPanelLog::Get()
{
	static FAgentPanelLog Instance;
	return Instance;
}

void FAgentPanelLog::Attach()
{
	if (!bAttached && GLog)
	{
		GLog->AddOutputDevice(this);
		bAttached = true;
	}
}

void FAgentPanelLog::Detach()
{
	if (bAttached && GLog)
	{
		GLog->RemoveOutputDevice(this);
		bAttached = false;
	}
}

void FAgentPanelLog::Serialize(const TCHAR* V, ELogVerbosity::Type Verbosity, const FName& Category)
{
	// SetColor 是控制台改颜色的控制行，不是日志内容（UEToVSLink 原来也跳过）
	if (V == nullptr || Verbosity == ELogVerbosity::SetColor)
	{
		return;
	}
	const ELogVerbosity::Type Level = (ELogVerbosity::Type)(Verbosity & ELogVerbosity::VerbosityMask);
	if (Level == ELogVerbosity::NoLogging)
	{
		return;
	}
	// 性能：日志刷屏时每秒上万行，这里在任意线程上被调用。锁外先拷好（单行最多 MaxLineChars，超长的大段转储只留开头），
	// 锁里只做序号和环形缓冲的替换，不分配大块内存
	const int32 Len = FCString::Strlen(V);
	FString Text = Len > MaxLineChars ? FString::ConstructFromPtrSize(V, MaxLineChars) + TEXT(" …") : FString(V);
	const double Now = FPlatformTime::Seconds();
	FScopeLock Guard(&Lock);
	FLine Line{++Seq, Now, MoveTemp(Text), Category, Level};
	if (Level == ELogVerbosity::Error || Level == ELogVerbosity::Fatal)
	{
		++Errors;
	}
	else if (Level == ELogVerbosity::Warning)
	{
		++Warnings;
	}
	if (Lines.Num() < Capacity)
	{
		Lines.Add(MoveTemp(Line));
	}
	else
	{
		Lines[Head] = MoveTemp(Line);
		Head = (Head + 1) % Capacity;
	}
}

uint64 FAgentPanelLog::LastSeq() const
{
	FScopeLock Guard(&Lock);
	return Seq;
}

static const TCHAR* VerbosityName(ELogVerbosity::Type V)
{
	switch (V)
	{
	case ELogVerbosity::Fatal: return TEXT("Fatal");
	case ELogVerbosity::Error: return TEXT("Error");
	case ELogVerbosity::Warning: return TEXT("Warning");
	case ELogVerbosity::Display: return TEXT("Display");
	case ELogVerbosity::Log: return TEXT("Log");
	case ELogVerbosity::Verbose: return TEXT("Verbose");
	default: return TEXT("VeryVerbose");
	}
}

static ELogVerbosity::Type ParseVerbosity(const FString& S)
{
	if (S.Equals(TEXT("Error"), ESearchCase::IgnoreCase)) return ELogVerbosity::Error;
	if (S.Equals(TEXT("Warning"), ESearchCase::IgnoreCase)) return ELogVerbosity::Warning;
	if (S.Equals(TEXT("Display"), ESearchCase::IgnoreCase)) return ELogVerbosity::Display;
	if (S.Equals(TEXT("Log"), ESearchCase::IgnoreCase)) return ELogVerbosity::Log;
	return ELogVerbosity::VeryVerbose;
}

TSharedPtr<FJsonObject> FAgentPanelLog::Tail(uint64 Since, int32 Limit, const FString& Category, const FString& MinVerbosity, const FString& Contains) const
{
	const ELogVerbosity::Type Min = ParseVerbosity(MinVerbosity);
	FScopeLock Guard(&Lock);
	TArray<TSharedPtr<FJsonValue>> Out;
	const int32 N = Lines.Num();
	// 缓冲里最早的一行；游标比它还旧说明中间的行已经被挤掉了，告诉面板跳过了多少行
	const uint64 Oldest = N ? Lines[Head % N].Seq : Seq + 1;
	const uint64 Dropped = (Since > 0 && Since + 1 < Oldest) ? Oldest - Since - 1 : 0;
	// 从新往旧找够 Limit 行，再倒回来按时间顺序给
	for (int32 k = N - 1; k >= 0 && Out.Num() < Limit; --k)
	{
		const FLine& L = Lines[(Head + k) % N];
		if (L.Seq <= Since)
		{
			break;
		}
		if (L.Verbosity > Min)
		{
			continue;
		}
		if (!Category.IsEmpty() && !L.Category.ToString().StartsWith(Category, ESearchCase::IgnoreCase))
		{
			continue;
		}
		if (!Contains.IsEmpty() && !L.Text.Contains(Contains, ESearchCase::IgnoreCase))
		{
			continue;
		}
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetNumberField(TEXT("seq"), (double)L.Seq);
		O->SetNumberField(TEXT("time"), L.Time);
		O->SetStringField(TEXT("category"), L.Category.ToString());
		O->SetStringField(TEXT("verbosity"), VerbosityName(L.Verbosity));
		O->SetStringField(TEXT("text"), L.Text);
		Out.Add(MakeShared<FJsonValueObject>(O));
	}
	Algo::Reverse(Out);
	TSharedPtr<FJsonObject> R = MakeShared<FJsonObject>();
	R->SetArrayField(TEXT("lines"), Out);
	R->SetNumberField(TEXT("cursor"), (double)Seq);
	// 行里的 time 是 FPlatformTime::Seconds，面板拿这个 now 换算成钟表时间（1.1.1 起）
	R->SetNumberField(TEXT("now"), FPlatformTime::Seconds());
	R->SetNumberField(TEXT("dropped"), (double)Dropped);
	R->SetNumberField(TEXT("errors"), Errors);
	R->SetNumberField(TEXT("warnings"), Warnings);
	R->SetStringField(TEXT("file"), FilePath());
	return R;
}

uint64 FAgentPanelLog::EntriesSince(uint64 Since, int32 Limit, TArray<FEntry>& Out, int64& OutDropped) const
{
	FScopeLock Guard(&Lock);
	OutDropped = 0;
	const int32 N = Lines.Num();
	if (N == 0 || Seq <= Since)
	{
		return Seq;
	}
	const uint64 Oldest = Lines[Head % N].Seq;
	if (Since + 1 < Oldest)
	{
		OutDropped += (int64)(Oldest - Since - 1);
	}
	// 缓冲里比 Since 新的有几行（缓冲按序号递增，从最旧的往后数）
	const int32 Newer = (int32)FMath::Min<uint64>(Seq - FMath::Max(Since, Oldest - 1), (uint64)N);
	const int32 Take = FMath::Min(Newer, FMath::Max(Limit, 0));
	OutDropped += Newer - Take;
	Out.Reserve(Out.Num() + Take);
	for (int32 k = N - Take; k < N; ++k)
	{
		const FLine& L = Lines[(Head + k) % N];
		FEntry& E = Out.AddDefaulted_GetRef();
		E.Seq = L.Seq;
		E.Time = L.Time;
		E.Text = L.Text;
		E.Category = L.Category;
		E.Verbosity = L.Verbosity;
	}
	return Seq;
}

TArray<FString> FAgentPanelLog::TextSince(uint64 Since, int32 Limit) const
{
	FScopeLock Guard(&Lock);
	TArray<FString> Out;
	const int32 N = Lines.Num();
	for (int32 k = 0; k < N && Out.Num() < Limit; ++k)
	{
		const FLine& L = Lines[(Head + k) % N];
		if (L.Seq > Since)
		{
			Out.Add(FString::Printf(TEXT("%s: %s"), *L.Category.ToString(), *L.Text));
		}
	}
	return Out;
}

/** UE 写日志文件的那个输出设备（GenericPlatformOutputDevices 里的单例，类型就是 FOutputDeviceFile） */
static FOutputDeviceFile* LogFileDevice()
{
	return static_cast<FOutputDeviceFile*>(FPlatformOutputDevices::GetLog());
}

FString FAgentPanelLog::FilePath() const
{
	FOutputDeviceFile* Dev = LogFileDevice();
	const FString Name = Dev ? FString(Dev->GetFilename()) : FString();
	return Name.IsEmpty() ? FString() : FPaths::ConvertRelativePathToFull(Name);
}

bool FAgentPanelLog::ClearFile(bool bBackup, TSharedPtr<FJsonObject>& Out, FString& Error)
{
	FOutputDeviceFile* Dev = LogFileDevice();
	const FString File = Dev ? FString(Dev->GetFilename()) : FString();
	if (File.IsEmpty())
	{
		Error = TEXT("这个进程没有写日志文件（可能带了 -nolog 启动）");
		return false;
	}
	const FString Full = FPaths::ConvertRelativePathToFull(File);
	// 先把缓冲里的都写进文件，再关掉：清空前另存的那份是完整的
	GLog->Flush();
	const int64 Size = IFileManager::Get().FileSize(*Full);
	FString Backup;
	if (bBackup)
	{
		Backup = FPaths::Combine(FPaths::GetPath(Full), TEXT("Cleared"),
			FString::Printf(TEXT("%s-%s.log"), *FPaths::GetBaseFilename(Full), *FDateTime::Now().ToString(TEXT("%Y%m%d-%H%M%S"))));
	}
	// 关掉 → 清空 → 在原文件名上重开（SetFilename 会让下一行日志重新建写入器）
	Dev->TearDown();
	bool bOk = true;
	if (!Backup.IsEmpty() && IFileManager::Get().Copy(*Backup, *Full) != COPY_OK)
	{
		Backup.Reset();
	}
	if (!FFileHelper::SaveStringToFile(FString(), *Full))
	{
		bOk = false;
		Error = FString::Printf(TEXT("清不空 %s（可能被别的程序占着）"), *Full);
	}
	Dev->SetFilename(*File);
	{
		FScopeLock Guard(&Lock);
		Errors = 0;
		Warnings = 0;
	}
	UE_LOG(LogAgentPanel, Display, TEXT("日志已于 %s 由 Agent 面板清空"), *FDateTime::Now().ToString(TEXT("%H:%M:%S")));
	GLog->Flush();
	Out->SetStringField(TEXT("file"), Full);
	Out->SetNumberField(TEXT("size"), (double)FMath::Max<int64>(Size, 0));
	Out->SetStringField(TEXT("backup"), Backup);
	Out->SetNumberField(TEXT("cursor"), (double)LastSeq());
	return bOk;
}
