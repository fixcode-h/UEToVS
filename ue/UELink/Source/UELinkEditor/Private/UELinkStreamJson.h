// 推送通道几个文件共用的小工具：手写 JSON 字符串（日志每行都要拼，不能每行建一个 FJsonObject）和上限常量。
#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace UELinkStream
{
	static constexpr int32 MaxSubscribers = 8;
	/** 每次 tick 最多推多少行日志（20 Hz，约每秒 1 万行），多的计入 dropped */
	static constexpr int32 MaxLogLinesPerTick = 500;
	/** 每个订阅者的发送缓冲：超过软上限丢日志批次，超过硬上限断开 */
	static constexpr int32 SoftOutCap = 4 * 1024 * 1024;
	static constexpr int32 HardOutCap = 16 * 1024 * 1024;
	static constexpr int32 MaxRunLogs = 5000;
	static constexpr int32 MaxTestLogs = 200;
	static constexpr int32 MaxTestMessageChars = 1000;
	static constexpr int32 MaxFinishedTests = 4000;
	static constexpr double TestIdleBeforeFirstSeconds = 90.0;
	static constexpr double TestIdleSeconds = 30.0;
	static constexpr int64 MaxConsoleTailBytes = 2 * 1024 * 1024;
	static constexpr int32 MaxConsoleTailLines = 4000;
	static constexpr int32 MaxMessageChars = 4000;
	/** livecoding.status 里留多少行编译输出 */
	static constexpr int32 MaxStatusOutputLines = 400;

	inline FString ToJson(const TSharedRef<FJsonObject>& Object)
	{
		FString Out;
		TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Out);
		FJsonSerializer::Serialize(Object, Writer);
		return Out;
	}

	inline FString Result(bool bOk, const FString& Message = FString())
	{
		TSharedRef<FJsonObject> Obj = MakeShared<FJsonObject>();
		Obj->SetBoolField(TEXT("ok"), bOk);
		if (!Message.IsEmpty())
		{
			Obj->SetStringField(TEXT("message"), Message);
		}
		return ToJson(Obj);
	}

	/** 把 Value 写成带引号的 JSON 字符串追加到 Out */
	inline void AppendJsonString(FString& Out, const TCHAR* Value, int32 Len)
	{
		Out.AppendChar(TEXT('"'));
		for (int32 i = 0; i < Len; ++i)
		{
			const TCHAR C = Value[i];
			switch (C)
			{
			case TEXT('"'): Out.Append(TEXT("\\\"")); break;
			case TEXT('\\'): Out.Append(TEXT("\\\\")); break;
			case TEXT('\n'): Out.Append(TEXT("\\n")); break;
			case TEXT('\r'): Out.Append(TEXT("\\r")); break;
			case TEXT('\t'): Out.Append(TEXT("\\t")); break;
			default:
				if (C < 0x20)
				{
					Out.Appendf(TEXT("\\u%04x"), static_cast<uint32>(C));
				}
				else
				{
					Out.AppendChar(C);
				}
				break;
			}
		}
		Out.AppendChar(TEXT('"'));
	}

	inline void AppendJsonString(FString& Out, const FString& Value)
	{
		AppendJsonString(Out, *Value, Value.Len());
	}

	inline void AppendJsonStringArray(FString& Out, const TArray<FString>& Values)
	{
		Out.AppendChar(TEXT('['));
		for (int32 i = 0; i < Values.Num(); ++i)
		{
			if (i > 0)
			{
				Out.AppendChar(TEXT(','));
			}
			AppendJsonString(Out, Values[i]);
		}
		Out.AppendChar(TEXT(']'));
	}

	inline FString Clip(const FString& Text, int32 MaxChars)
	{
		return Text.Len() <= MaxChars ? Text : Text.Left(MaxChars) + TEXT("...");
	}

	inline TArray<TSharedPtr<FJsonValue>> StringValues(const TArray<FString>& Values)
	{
		TArray<TSharedPtr<FJsonValue>> Out;
		Out.Reserve(Values.Num());
		for (const FString& V : Values)
		{
			Out.Add(MakeShared<FJsonValueString>(V));
		}
		return Out;
	}
}
