// trace 分析服务（性能页设计 3.2、二期）：面板录下的 .utrace 交给 UE 自己的 TraceServices 分析，
// 网页的时间轴、计时器表、调用者 / 被调用者、调用堆栈、计数器、日志都按「这个范围、这个精度」来这里查。
// 用 Unreal Insights 同一套分析代码：数字和层级跟 Insights 一样，trace 格式随引擎版本变也跟着走，不自己解析二进制。
// 放在编辑器模块：后台编辑器（-run=AgentPanel）和编辑器都加载它，游戏进程不加载，分析不拖慢要测的帧率。
//
// 只用最基础的几个接口（线程列表、时间线进出事件、计时器名字、帧、书签、计数器、日志），
// TraceServices 的类型一律写全名（TraceServices::FFrame 等）：UObject 也有 FFrame（蓝图调用栈），只写短名在 5.8 上会撞名。
// 计时器汇总和调用树都在这里自己按进出事件算，不用 CreateAggregation 之类各版本签名不一样的接口。
#include "AgentPanelBridge.h"
#include "AgentPanelBridgeUtil.h"
#include "AgentPanelEditorShared.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformTime.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "TraceServices/AnalysisService.h"
#include "TraceServices/ITraceServicesModule.h"
#include "TraceServices/Model/AnalysisSession.h"
#include "TraceServices/Model/Bookmarks.h"
#include "TraceServices/Model/Counters.h"
#include "TraceServices/Model/Frames.h"
#include "TraceServices/Model/Log.h"
#include "TraceServices/Model/Threads.h"
#include "TraceServices/Model/TimingProfiler.h"

using namespace AgentPanel;
using namespace TraceServices;

namespace
{
	/** 时间轴一次最多回这么多块（设计 6）：再多网页画不过来，也说明范围太大、该放大了 */
	constexpr int32 MaxBlocks = 20000;
	/** GPU 泳道在请求里的编号（CPU 泳道用线程 id） */
	constexpr int32 GpuLane = -1;

	struct FEntry
	{
		TSharedPtr<const IAnalysisSession> Session;
		int64 Size = 0;
		FDateTime Stamp;
		double LastUse = 0;
	};

	/** 分析过的会话：留最近两个（网页来回切两份 trace 时不用重分析），文件变了（还在写）就重新分析 */
	TMap<FString, FEntry>& Sessions()
	{
		static TMap<FString, FEntry> Map;
		return Map;
	}

	TSharedPtr<const IAnalysisSession> SessionFor(const FString& File, FString& Error)
	{
		IFileManager& FM = IFileManager::Get();
		const int64 Size = FM.FileSize(*File);
		if (Size <= 0)
		{
			Error = FString::Printf(TEXT("trace 文件不存在或是空的：%s"), *File);
			return nullptr;
		}
		const FDateTime Stamp = FM.GetTimeStamp(*File);
		TMap<FString, FEntry>& Map = Sessions();
		if (FEntry* Hit = Map.Find(File))
		{
			// 文件长了（录制还在写）而上次已经分析完：重新分析一遍；还在分析就继续等它
			const bool bChanged = Hit->Size != Size || Hit->Stamp != Stamp;
			if (!bChanged || !Hit->Session->IsAnalysisComplete())
			{
				Hit->LastUse = FPlatformTime::Seconds();
				return Hit->Session;
			}
			Map.Remove(File);
		}
		ITraceServicesModule& Module = FModuleManager::LoadModuleChecked<ITraceServicesModule>(TEXT("TraceServices"));
		TSharedPtr<IAnalysisService> Service = Module.GetAnalysisService();
		if (!Service.IsValid())
		{
			Error = TEXT("这个引擎的 TraceServices 没有提供分析服务");
			return nullptr;
		}
		// StartAnalysis 在自己的线程里分析，马上返回；查询时没分析完就回 analyzing，网页过一会儿再来
		TSharedPtr<const IAnalysisSession> Session = Service->StartAnalysis(*File);
		if (!Session.IsValid())
		{
			Error = FString::Printf(TEXT("打不开这份 trace（不是 .utrace，或者是更新版本的引擎录的）：%s"), *File);
			return nullptr;
		}
		while (Map.Num() >= 2)
		{
			FString Oldest;
			double OldestUse = TNumericLimits<double>::Max();
			for (const TPair<FString, FEntry>& P : Map)
			{
				if (P.Value.LastUse < OldestUse)
				{
					OldestUse = P.Value.LastUse;
					Oldest = P.Key;
				}
			}
			Map.Remove(Oldest);
		}
		FEntry& E = Map.Add(File);
		E.Session = Session;
		E.Size = Size;
		E.Stamp = Stamp;
		E.LastUse = FPlatformTime::Seconds();
		return Session;
	}

	double Num(const TSharedPtr<FJsonObject>& A, const TCHAR* Key, double Default)
	{
		double V = Default;
		return A->TryGetNumberField(Key, V) ? V : Default;
	}

	TArray<int32> Lanes(const TSharedPtr<FJsonObject>& A)
	{
		TArray<int32> Out;
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		if (A->TryGetArrayField(TEXT("lanes"), Arr))
		{
			for (const TSharedPtr<FJsonValue>& V : *Arr)
			{
				Out.Add(static_cast<int32>(V->AsNumber()));
			}
		}
		return Out;
	}

	/** 所有泳道：GPU 在前，再按线程登记的顺序（Insights 的顺序） */
	TArray<int32> AllLanes(const IAnalysisSession& S, const ITimingProfilerProvider& TP)
	{
		TArray<int32> Out;
		uint32 Index = 0;
		if (TP.GetGpuTimelineIndex(Index))
		{
			Out.Add(GpuLane);
		}
		ReadThreadProvider(S).EnumerateThreads([&](const TraceServices::FThreadInfo& T)
		{
			uint32 TL = 0;
			if (TP.GetCpuThreadTimelineIndex(T.Id, TL))
			{
				Out.Add(static_cast<int32>(T.Id));
			}
		});
		return Out;
	}

	inline const ITimingProfilerTimerReader* AsReader(const ITimingProfilerTimerReader& R) { return &R; }
	inline const ITimingProfilerTimerReader* AsReader(const ITimingProfilerTimerReader* R) { return R; }

	/** 计时器 id → 名字。带元数据的计时器（id 最高位是 1）换成原始计时器，和 Insights 一样合在一起算 */
	struct FTimers
	{
		const ITimingProfilerTimerReader* Reader = nullptr;

		explicit FTimers(const ITimingProfilerProvider& TP)
		{
#if ENGINE_MAJOR_VERSION > 5 || (ENGINE_MAJOR_VERSION == 5 && ENGINE_MINOR_VERSION >= 8)
			// 5.8 起 ReadTimers 弃用，改 GetTimerReader()；返回引用还是指针都接得住
			Reader = AsReader(TP.GetTimerReader());
#else
			TP.ReadTimers([this](const ITimingProfilerTimerReader& R) { Reader = &R; });
#endif
		}

		uint32 Resolve(uint32 Index) const
		{
			if (Reader && static_cast<int32>(Index) < 0)
			{
				return Reader->GetOriginalTimerIdFromMetadata(Index);
			}
			return Index;
		}

		FString Name(uint32 Id) const
		{
			const TraceServices::FTimingProfilerTimer* T = Reader ? Reader->GetTimer(Id) : nullptr;
			return T && T->Name ? FString(T->Name) : FString::Printf(TEXT("<timer %u>"), Id);
		}
	};

	/** 一块（一次作用域）走完时的回调：开始、结束（秒）、深度、计时器 id、它外面的调用栈（从根往里，不含自己） */
	struct FOpen
	{
		uint32 Timer;
		double Start;
		double Child;	// 子块的包含时间之和（算独占时间）
	};
	using FBlockFn = TFunctionRef<void(double Start, double End, int32 Depth, uint32 Timer, double ChildTime, const TArray<FOpen>& Parents)>;

	/**
	 * 按进出事件走一条泳道 [T0, T1]，自己维护调用栈。范围外开始、范围内还没结束的块也会给（时间按真实的）。
	 * 返回 false 表示这条泳道不存在
	 */
	bool Walk(const ITimingProfilerProvider& TP, const FTimers& Timers, int32 Lane, double T0, double T1, FBlockFn Fn)
	{
		uint32 Index = 0;
		const bool bOk = Lane == GpuLane ? TP.GetGpuTimelineIndex(Index) : TP.GetCpuThreadTimelineIndex(static_cast<uint32>(Lane), Index);
		if (!bOk)
		{
			return false;
		}
		TP.ReadTimeline(Index, [&](const ITimingProfilerProvider::Timeline& TL)
		{
			TArray<FOpen> Stack;
			double Last = T0;
			TL.EnumerateEvents(T0, T1, [&](bool bStart, double Time, const TraceServices::FTimingProfilerEvent& Ev)
			{
				Last = Time;
				if (bStart)
				{
					Stack.Add({ Timers.Resolve(Ev.TimerIndex), Time, 0.0 });
					return EEventEnumerate::Continue;
				}
				if (Stack.Num() == 0)
				{
					return EEventEnumerate::Continue;
				}
				const FOpen Top = Stack.Pop(EAllowShrinking::No);
				const double Dur = Time - Top.Start;
				if (Stack.Num() > 0)
				{
					Stack.Last().Child += Dur;
				}
				Fn(Top.Start, Time, Stack.Num(), Top.Timer, Top.Child, Stack);
				return EEventEnumerate::Continue;
			});
			// 录制被截断时最后几层没有结束事件：按最后一个事件的时间收尾
			while (Stack.Num() > 0)
			{
				const FOpen Top = Stack.Pop(EAllowShrinking::No);
				if (Stack.Num() > 0)
				{
					Stack.Last().Child += FMath::Max(0.0, Last - Top.Start);
				}
				Fn(Top.Start, FMath::Max(Last, Top.Start), Stack.Num(), Top.Timer, Top.Child, Stack);
			}
		});
		return true;
	}

	/**
	 * 无穷大 / NaN 换成 0：UE 的 JSON 写出来是 inf / nan，不是合法 JSON，面板整个回复都解析不了。
	 * 录制停在半帧时最后一帧的 EndTime 就是无穷大（帧条一直显示加载，就是这一帧把 frames 的回复弄坏了）
	 */
	inline double Fin(double X)
	{
		return FMath::IsFinite(X) ? X : 0.0;
	}

	TSharedPtr<FJsonValue> NumArr(const TArray<double>& V)
	{
		TArray<TSharedPtr<FJsonValue>> Out;
		Out.Reserve(V.Num());
		for (double X : V)
		{
			Out.Add(MakeShared<FJsonValueNumber>(Fin(X)));
		}
		return MakeShared<FJsonValueArray>(Out);
	}

	/** 毫秒保留 3 位小数：JSON 小一半，网页也用不到更细 */
	double Ms(double Seconds)
	{
		return Fin(FMath::RoundToDouble(Seconds * 1000000.0) / 1000.0);
	}

	// ---------- 各个查询 ----------

	void QueryInfo(const IAnalysisSession& S, TSharedPtr<FJsonObject>& Out)
	{
		TArray<TSharedPtr<FJsonValue>> Threads;
		const ITimingProfilerProvider* TP = ReadTimingProfilerProvider(S);
		uint32 Index = 0;
		if (TP && TP->GetGpuTimelineIndex(Index))
		{
			TSharedPtr<FJsonObject> G = MakeShared<FJsonObject>();
			G->SetNumberField(TEXT("id"), GpuLane);
			G->SetStringField(TEXT("name"), TEXT("GPU"));
			G->SetStringField(TEXT("group"), TEXT("GPU"));
			Threads.Add(MakeShared<FJsonValueObject>(G));
		}
		ReadThreadProvider(S).EnumerateThreads([&](const TraceServices::FThreadInfo& T)
		{
			uint32 TL = 0;
			if (!TP || !TP->GetCpuThreadTimelineIndex(T.Id, TL))
			{
				return;	// 没有 CPU 事件的线程不列，免得一屏空泳道
			}
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetNumberField(TEXT("id"), T.Id);
			O->SetStringField(TEXT("name"), T.Name ? T.Name : TEXT(""));
			O->SetStringField(TEXT("group"), T.GroupName ? T.GroupName : TEXT(""));
			Threads.Add(MakeShared<FJsonValueObject>(O));
		});
		Out->SetArrayField(TEXT("threads"), Threads);
		const IFrameProvider& FP = ReadFrameProvider(S);
		Out->SetNumberField(TEXT("frames"), static_cast<double>(FP.GetFrameCount(TraceFrameType_Game)));
	}

	void QueryFrames(const IAnalysisSession& S, TSharedPtr<FJsonObject>& Out)
	{
		const IFrameProvider& FP = ReadFrameProvider(S);
		const uint64 Count = FP.GetFrameCount(TraceFrameType_Game);
		// 10 分钟 60 帧也才 3.6 万帧：整段给，网页自己按宽度画
		const uint64 End = FMath::Min<uint64>(Count, 200000);
		TArray<double> Start;
		TArray<double> Dur;
		Start.Reserve(End);
		Dur.Reserve(End);
		// 5.8 起 FrameProvider 另有按时间（double）取的重载：起点写成 uint64，不然 0 两边都能转、重载不明确
		const double Last = S.GetDurationSeconds();
		FP.EnumerateFrames(TraceFrameType_Game, uint64(0), End, [&](const TraceServices::FFrame& F)
		{
			if (!FMath::IsFinite(F.StartTime))
			{
				return;
			}
			// 还没结束的帧（录制停在半帧）EndTime 是无穷大：按 trace 结尾收
			const double FrameEnd = FMath::IsFinite(F.EndTime) ? F.EndTime : FMath::Max(F.StartTime, Last);
			Start.Add(FMath::RoundToDouble(F.StartTime * 100000.0) / 100000.0);
			Dur.Add(Ms(FrameEnd - F.StartTime));
		});
		Out->SetField(TEXT("start"), NumArr(Start));
		Out->SetField(TEXT("ms"), NumArr(Dur));
		Out->SetNumberField(TEXT("total"), static_cast<double>(Count));
	}

	bool QueryTiming(const IAnalysisSession& S, const ITimingProfilerProvider& TP, const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const double T0 = Num(A, TEXT("t0"), 0);
		const double T1 = Num(A, TEXT("t1"), T0 + 0.05);
		const double Px = FMath::Max(100.0, Num(A, TEXT("px"), 1200));
		// 比半个像素还短的块画出来也看不见：不回，泳道上记个数（Insights 是合并成灰块，网页按 hidden 画一条细线提示）
		const double MinDur = (T1 - T0) / Px * 0.5;
		TArray<int32> Want = Lanes(A);
		if (Want.Num() == 0)
		{
			Want = AllLanes(S, TP);
		}
		FTimers Timers(TP);
		TSet<uint32> Used;
		TArray<TSharedPtr<FJsonValue>> LanesOut;
		int32 Total = 0;
		bool bCut = false;
		for (int32 Lane : Want)
		{
			TArray<double> St, Du, De, Ti, Ex;
			int32 Hidden = 0;
			Walk(TP, Timers, Lane, T0, T1, [&](double Start, double End, int32 Depth, uint32 Timer, double Child, const TArray<FOpen>&)
			{
				if (End - Start < MinDur)
				{
					++Hidden;
					return;
				}
				if (Total >= MaxBlocks)
				{
					bCut = true;
					return;
				}
				++Total;
				St.Add(Start);
				Du.Add(Ms(End - Start));
				De.Add(Depth);
				Ti.Add(Timer);
				// 独占时间（悬浮提示要的 Exclusive Time）：子块再短、没回给网页也算在里面，网页自己减不准
				Ex.Add(Ms(FMath::Max(0.0, End - Start - Child)));
				Used.Add(Timer);
			});
			TSharedPtr<FJsonObject> L = MakeShared<FJsonObject>();
			L->SetNumberField(TEXT("id"), Lane);
			L->SetField(TEXT("s"), NumArr(St));
			L->SetField(TEXT("ms"), NumArr(Du));
			L->SetField(TEXT("d"), NumArr(De));
			L->SetField(TEXT("t"), NumArr(Ti));
			L->SetField(TEXT("x"), NumArr(Ex));
			L->SetNumberField(TEXT("hidden"), Hidden);
			LanesOut.Add(MakeShared<FJsonValueObject>(L));
		}
		TSharedPtr<FJsonObject> Names = MakeShared<FJsonObject>();
		for (uint32 Id : Used)
		{
			Names->SetStringField(FString::FromInt(static_cast<int32>(Id)), Timers.Name(Id));
		}
		Out->SetArrayField(TEXT("lanes"), LanesOut);
		Out->SetObjectField(TEXT("names"), Names);
		Out->SetBoolField(TEXT("cut"), bCut);
		return true;
	}

	struct FAgg
	{
		int64 Count = 0;
		double Incl = 0;
		double InclMax = 0;
		double Excl = 0;
		bool bGpu = false;
	};

	void QueryTimers(const IAnalysisSession& S, const ITimingProfilerProvider& TP, const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out)
	{
		const double T0 = Num(A, TEXT("t0"), 0);
		const double T1 = Num(A, TEXT("t1"), S.GetDurationSeconds());
		const FString Filter = Str(A, TEXT("filter"));
		const int32 Top = FMath::Clamp(Int(A, TEXT("top"), 200), 1, 2000);
		TArray<int32> Want = Lanes(A);
		if (Want.Num() == 0)
		{
			Want = AllLanes(S, TP);
		}
		FTimers Timers(TP);
		TMap<uint32, FAgg> Agg;
		for (int32 Lane : Want)
		{
			// 同一个计时器递归套自己时，包含时间只算最外层，免得翻倍（和 Insights 一致）
			TMap<uint32, int32> Depths;
			Walk(TP, Timers, Lane, T0, T1, [&](double Start, double End, int32, uint32 Timer, double Child, const TArray<FOpen>& Parents)
			{
				// 只算落在范围内的那部分
				const double S0 = FMath::Max(Start, T0);
				const double S1 = FMath::Min(End, T1);
				if (S1 <= S0)
				{
					return;
				}
				FAgg& G = Agg.FindOrAdd(Timer);
				G.bGpu |= Lane == GpuLane;
				++G.Count;
				bool bNested = false;
				for (const FOpen& P : Parents)
				{
					bNested |= P.Timer == Timer;
				}
				const double Dur = S1 - S0;
				if (!bNested)
				{
					G.Incl += Dur;
				}
				G.InclMax = FMath::Max(G.InclMax, End - Start);
				G.Excl += FMath::Max(0.0, Dur - Child * (Dur / FMath::Max(End - Start, 1e-12)));
			});
		}
		struct FRow
		{
			uint32 Id;
			FString Name;
			FAgg G;
		};
		TArray<FRow> Rows;
		for (const TPair<uint32, FAgg>& P : Agg)
		{
			FString Name = Timers.Name(P.Key);
			if (!Filter.IsEmpty() && !Name.Contains(Filter))
			{
				continue;
			}
			Rows.Add({ P.Key, MoveTemp(Name), P.Value });
		}
		const FString Sort = Str(A, TEXT("sort"), TEXT("excl"));
		Rows.Sort([&](const FRow& X, const FRow& Y)
		{
			if (Sort == TEXT("incl")) return X.G.Incl > Y.G.Incl;
			if (Sort == TEXT("count")) return X.G.Count > Y.G.Count;
			if (Sort == TEXT("max")) return X.G.InclMax > Y.G.InclMax;
			return X.G.Excl > Y.G.Excl;
		});
		TArray<TSharedPtr<FJsonValue>> Items;
		for (int32 i = 0; i < Rows.Num() && i < Top; ++i)
		{
			const FRow& R = Rows[i];
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetNumberField(TEXT("id"), R.Id);
			O->SetStringField(TEXT("name"), R.Name);
			O->SetNumberField(TEXT("count"), static_cast<double>(R.G.Count));
			O->SetNumberField(TEXT("incl"), Ms(R.G.Incl));
			O->SetNumberField(TEXT("inclMax"), Ms(R.G.InclMax));
			O->SetNumberField(TEXT("excl"), Ms(R.G.Excl));
			if (R.G.bGpu)
			{
				O->SetBoolField(TEXT("gpu"), true);
			}
			Items.Add(MakeShared<FJsonValueObject>(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetNumberField(TEXT("total"), Rows.Num());
		Out->SetNumberField(TEXT("seconds"), T1 - T0);
	}

	/** 调用树的一个节点：按计时器 id 合并同一路径上的调用 */
	struct FNode
	{
		int64 Count = 0;
		double Incl = 0;
		double Excl = 0;
		TMap<uint32, TUniquePtr<FNode>> Kids;

		FNode& Kid(uint32 Id)
		{
			TUniquePtr<FNode>& P = Kids.FindOrAdd(Id);
			if (!P)
			{
				P = MakeUnique<FNode>();
			}
			return *P;
		}
	};

	/** 树转 JSON：每层按包含时间倒序，最多 MaxKids 个、MaxDepth 层，其余的合进「其他」的计数里 */
	TSharedPtr<FJsonObject> TreeJson(uint32 Id, const FNode& N, const FTimers& Timers, int32 Depth)
	{
		constexpr int32 MaxKids = 30;
		constexpr int32 MaxDepth = 16;
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetNumberField(TEXT("id"), Id);
		O->SetStringField(TEXT("name"), Timers.Name(Id));
		O->SetNumberField(TEXT("count"), static_cast<double>(N.Count));
		O->SetNumberField(TEXT("incl"), Ms(N.Incl));
		O->SetNumberField(TEXT("excl"), Ms(N.Excl));
		if (Depth >= MaxDepth || N.Kids.Num() == 0)
		{
			if (N.Kids.Num() > 0)
			{
				O->SetNumberField(TEXT("more"), N.Kids.Num());
			}
			return O;
		}
		TArray<TPair<uint32, const FNode*>> Sorted;
		for (const TPair<uint32, TUniquePtr<FNode>>& P : N.Kids)
		{
			Sorted.Add({ P.Key, P.Value.Get() });
		}
		Sorted.Sort([](const TPair<uint32, const FNode*>& X, const TPair<uint32, const FNode*>& Y) { return X.Value->Incl > Y.Value->Incl; });
		TArray<TSharedPtr<FJsonValue>> Kids;
		for (int32 i = 0; i < Sorted.Num() && i < MaxKids; ++i)
		{
			Kids.Add(MakeShared<FJsonValueObject>(TreeJson(Sorted[i].Key, *Sorted[i].Value, Timers, Depth + 1)));
		}
		O->SetArrayField(TEXT("children"), Kids);
		if (Sorted.Num() > MaxKids)
		{
			O->SetNumberField(TEXT("more"), Sorted.Num() - MaxKids);
		}
		return O;
	}

	/**
	 * 调用者 / 被调用者（Insights 的 Butterfly）：选中计时器的每一次调用，
	 * 往外的调用栈并进「调用者」树（自己在根，往下是父、祖父…），往里的子调用并进「被调用者」树。
	 */
	bool QueryButterfly(const IAnalysisSession& S, const ITimingProfilerProvider& TP, const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		double TimerNum = -1;
		if (!A->TryGetNumberField(TEXT("timer"), TimerNum) || TimerNum < 0)
		{
			Error = TEXT("要给 timer（计时器 id，从 timers 查询里拿）");
			return false;
		}
		const uint32 Target = static_cast<uint32>(TimerNum);
		const double T0 = Num(A, TEXT("t0"), 0);
		const double T1 = Num(A, TEXT("t1"), S.GetDurationSeconds());
		TArray<int32> Want = Lanes(A);
		if (Want.Num() == 0)
		{
			Want = AllLanes(S, TP);
		}
		FTimers Timers(TP);
		FNode Callers;
		FNode Callees;
		for (int32 Lane : Want)
		{
			Walk(TP, Timers, Lane, T0, T1, [&](double Start, double End, int32, uint32 Timer, double Child, const TArray<FOpen>& Parents)
			{
				const double Dur = End - Start;
				const double Self = FMath::Max(0.0, Dur - Child);
				// 外面最近一层的选中计时器在栈里的位置：有就说明这一块是它的子孙
				int32 Outer = INDEX_NONE;
				for (int32 i = 0; i < Parents.Num(); ++i)
				{
					if (Parents[i].Timer == Target)
					{
						Outer = i;
						break;
					}
				}
				if (Timer == Target && Outer == INDEX_NONE)
				{
					// 选中的计时器本身（最外层那次）：调用者树沿父链往外走
					// 调用者树里每一层的独占时间写的都是选中计时器自己的独占时间（Insights 的 Callers 也这么读）
					++Callers.Count;
					Callers.Incl += Dur;
					Callers.Excl += Self;
					++Callees.Count;
					Callees.Incl += Dur;
					Callees.Excl += Self;
					FNode* N = &Callers;
					for (int32 i = Parents.Num() - 1; i >= 0; --i)
					{
						N = &N->Kid(Parents[i].Timer);
						++N->Count;
						N->Incl += Dur;
						N->Excl += Self;
					}
				}
				else if (Outer != INDEX_NONE)
				{
					// 它里面的块：从选中的那层往里一路建到自己
					FNode* N = &Callees;
					for (int32 i = Outer + 1; i < Parents.Num(); ++i)
					{
						N = &N->Kid(Parents[i].Timer);
					}
					N = &N->Kid(Timer);
					++N->Count;
					N->Incl += Dur;
					N->Excl += Self;
				}
			});
		}
		// 子块比父块先结束，被调用者树里路径上的中间节点是在子块回调里建的：计数和时间以它自己结束时加的为准
		Out->SetObjectField(TEXT("callers"), TreeJson(Target, Callers, Timers, 0));
		Out->SetObjectField(TEXT("callees"), TreeJson(Target, Callees, Timers, 0));
		return true;
	}

	/**
	 * 一个计时器在范围内的每一次调用（Insights 计时器悬浮提示里的统计 + 「找最慢的一次」）：
	 * 次数、总和、最短 / 平均 / 中位 / 最长，和最慢的 top 次（泳道、开始、耗时、深度），网页点一下跳到那一块
	 */
	bool QueryInstances(const IAnalysisSession& S, const ITimingProfilerProvider& TP, const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		double TimerNum = -1;
		if (!A->TryGetNumberField(TEXT("timer"), TimerNum) || TimerNum < 0)
		{
			Error = TEXT("要给 timer（计时器 id，从 timers 查询里拿）");
			return false;
		}
		const uint32 Target = static_cast<uint32>(TimerNum);
		const double T0 = Num(A, TEXT("t0"), 0);
		const double T1 = Num(A, TEXT("t1"), S.GetDurationSeconds());
		const int32 Top = FMath::Clamp(Int(A, TEXT("top"), 50), 1, 500);
		TArray<int32> Want = Lanes(A);
		if (Want.Num() == 0)
		{
			Want = AllLanes(S, TP);
		}
		FTimers Timers(TP);
		struct FHit
		{
			int32 Lane;
			double Start;
			double Dur;
			double Self;
			int32 Depth;
		};
		TArray<FHit> Hits;
		for (int32 Lane : Want)
		{
			Walk(TP, Timers, Lane, T0, T1, [&](double Start, double End, int32 Depth, uint32 Timer, double Child, const TArray<FOpen>&)
			{
				if (Timer == Target && End > T0 && Start < T1)
				{
					Hits.Add({ Lane, Start, End - Start, FMath::Max(0.0, End - Start - Child), Depth });
				}
			});
		}
		Hits.Sort([](const FHit& X, const FHit& Y) { return X.Dur > Y.Dur; });
		double Sum = 0;
		double SelfSum = 0;
		for (const FHit& H : Hits)
		{
			Sum += H.Dur;
			SelfSum += H.Self;
		}
		const int32 N = Hits.Num();
		Out->SetNumberField(TEXT("id"), Target);
		Out->SetStringField(TEXT("name"), Timers.Name(Target));
		Out->SetNumberField(TEXT("count"), N);
		Out->SetNumberField(TEXT("incl"), Ms(Sum));
		Out->SetNumberField(TEXT("excl"), Ms(SelfSum));
		Out->SetNumberField(TEXT("min"), N ? Ms(Hits.Last().Dur) : 0);
		Out->SetNumberField(TEXT("max"), N ? Ms(Hits[0].Dur) : 0);
		Out->SetNumberField(TEXT("avg"), N ? Ms(Sum / N) : 0);
		Out->SetNumberField(TEXT("median"), N ? Ms(Hits[N / 2].Dur) : 0);
		TArray<TSharedPtr<FJsonValue>> Items;
		for (int32 i = 0; i < N && i < Top; ++i)
		{
			const FHit& H = Hits[i];
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetNumberField(TEXT("lane"), H.Lane);
			O->SetNumberField(TEXT("start"), H.Start);
			O->SetNumberField(TEXT("ms"), Ms(H.Dur));
			O->SetNumberField(TEXT("excl"), Ms(H.Self));
			O->SetNumberField(TEXT("depth"), H.Depth);
			Items.Add(MakeShared<FJsonValueObject>(O));
		}
		Out->SetArrayField(TEXT("items"), Items);
		return true;
	}

	/** 某个时间点、某条泳道上从根到最深一层的调用栈，以及选中那一层的直接子调用（按计时器合并） */
	bool QueryStack(const IAnalysisSession& S, const ITimingProfilerProvider& TP, const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		const int32 Lane = Int(A, TEXT("lane"), 0);
		const double Time = Num(A, TEXT("time"), 0);
		const int32 Want = Int(A, TEXT("depth"), -1);
		FTimers Timers(TP);
		struct FHit
		{
			uint32 Timer;
			double Start;
			double End;
			double Child;
		};
		TArray<FHit> Chain;
		const bool bLane = Walk(TP, Timers, Lane, Time, Time, [&](double Start, double End, int32 Depth, uint32 Timer, double Child, const TArray<FOpen>&)
		{
			if (Start <= Time && End >= Time)
			{
				if (Chain.Num() <= Depth)
				{
					Chain.SetNum(Depth + 1);
				}
				Chain[Depth] = { Timer, Start, End, Child };
			}
		});
		if (!bLane)
		{
			Error = TEXT("这份 trace 里没有这条泳道");
			return false;
		}
		TArray<TSharedPtr<FJsonValue>> Stack;
		for (int32 i = 0; i < Chain.Num(); ++i)
		{
			const FHit& H = Chain[i];
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetNumberField(TEXT("id"), H.Timer);
			O->SetStringField(TEXT("name"), Timers.Name(H.Timer));
			O->SetNumberField(TEXT("start"), H.Start);
			O->SetNumberField(TEXT("ms"), Ms(H.End - H.Start));
			O->SetNumberField(TEXT("excl"), Ms(FMath::Max(0.0, H.End - H.Start - H.Child)));
			O->SetNumberField(TEXT("depth"), i);
			Stack.Add(MakeShared<FJsonValueObject>(O));
		}
		Out->SetArrayField(TEXT("stack"), Stack);
		const int32 Sel = Want >= 0 && Want < Chain.Num() ? Want : Chain.Num() - 1;
		if (Sel < 0)
		{
			Out->SetArrayField(TEXT("children"), TArray<TSharedPtr<FJsonValue>>());
			return true;
		}
		const FHit Picked = Chain[Sel];
		TMap<uint32, FAgg> Kids;
		Walk(TP, Timers, Lane, Picked.Start, Picked.End, [&](double Start, double End, int32 Depth, uint32 Timer, double, const TArray<FOpen>&)
		{
			if (Depth == Sel + 1 && Start >= Picked.Start && End <= Picked.End)
			{
				FAgg& G = Kids.FindOrAdd(Timer);
				++G.Count;
				G.Incl += End - Start;
				G.InclMax = FMath::Max(G.InclMax, End - Start);
			}
		});
		Kids.ValueSort([](const FAgg& X, const FAgg& Y) { return X.Incl > Y.Incl; });
		TArray<TSharedPtr<FJsonValue>> Children;
		for (const TPair<uint32, FAgg>& P : Kids)
		{
			if (Children.Num() >= 100)
			{
				break;
			}
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetNumberField(TEXT("id"), P.Key);
			O->SetStringField(TEXT("name"), Timers.Name(P.Key));
			O->SetNumberField(TEXT("count"), static_cast<double>(P.Value.Count));
			O->SetNumberField(TEXT("incl"), Ms(P.Value.Incl));
			O->SetNumberField(TEXT("inclMax"), Ms(P.Value.InclMax));
			Children.Add(MakeShared<FJsonValueObject>(O));
		}
		Out->SetArrayField(TEXT("children"), Children);
		Out->SetNumberField(TEXT("selected"), Sel);
		return true;
	}

	/** 计数器：不给 names 时列出所有计数器的名字；给了就按 px 个点降采样（每段取最大值，尖峰不丢） */
	void QueryCounters(const IAnalysisSession& S, const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out)
	{
		const ICounterProvider& CP = ReadCounterProvider(S);
		TSet<FString> Names;
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		if (A->TryGetArrayField(TEXT("names"), Arr))
		{
			for (const TSharedPtr<FJsonValue>& V : *Arr)
			{
				Names.Add(V->AsString());
			}
		}
		const double T0 = Num(A, TEXT("t0"), 0);
		const double T1 = Num(A, TEXT("t1"), S.GetDurationSeconds());
		const int32 Px = FMath::Clamp(Int(A, TEXT("px"), 600), 10, 4000);
		const double Step = FMath::Max(1e-9, (T1 - T0) / Px);
		TArray<TSharedPtr<FJsonValue>> List;
		CP.EnumerateCounters([&](uint32, const TraceServices::ICounter& C)
		{
			const FString Name = C.GetName();
			if (Names.Num() == 0)
			{
				TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
				O->SetStringField(TEXT("name"), Name);
				List.Add(MakeShared<FJsonValueObject>(O));
				return;
			}
			if (!Names.Contains(Name))
			{
				return;
			}
			TArray<double> Ts;
			TArray<double> Vs;
			auto Add = [&](double Time, double Value)
			{
				const int32 Slot = static_cast<int32>((Time - T0) / Step);
				if (Ts.Num() > 0 && static_cast<int32>((Ts.Last() - T0) / Step) == Slot)
				{
					Vs.Last() = FMath::Max(Vs.Last(), Value);
					return;
				}
				Ts.Add(Time);
				Vs.Add(Value);
			};
			if (C.IsFloatingPoint())
			{
				C.EnumerateFloatValues(T0, T1, true, [&](double Time, double Value) { Add(Time, Value); });
			}
			else
			{
				C.EnumerateValues(T0, T1, true, [&](double Time, int64 Value) { Add(Time, static_cast<double>(Value)); });
			}
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("name"), Name);
			O->SetField(TEXT("t"), NumArr(Ts));
			O->SetField(TEXT("v"), NumArr(Vs));
			List.Add(MakeShared<FJsonValueObject>(O));
		});
		Out->SetArrayField(Names.Num() == 0 ? TEXT("counters") : TEXT("series"), List);
	}

	void QueryLog(const IAnalysisSession& S, const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out)
	{
		const double T0 = Num(A, TEXT("t0"), 0);
		const double T1 = Num(A, TEXT("t1"), S.GetDurationSeconds());
		const FString Filter = Str(A, TEXT("filter"));
		const int32 Top = FMath::Clamp(Int(A, TEXT("top"), 500), 1, 5000);
		TArray<TSharedPtr<FJsonValue>> Items;
		int32 Total = 0;
		ReadLogProvider(S).EnumerateMessages(T0, T1, [&](const TraceServices::FLogMessageInfo& M)
		{
			const FString Text = M.Message ? M.Message : TEXT("");
			const FString Cat = M.Category && M.Category->Name ? M.Category->Name : TEXT("");
			if (!Filter.IsEmpty() && !Text.Contains(Filter) && !Cat.Contains(Filter))
			{
				return;
			}
			++Total;
			if (Items.Num() >= Top)
			{
				return;
			}
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetNumberField(TEXT("t"), M.Time);
			O->SetStringField(TEXT("v"), ToString(M.Verbosity));
			O->SetStringField(TEXT("c"), Cat);
			O->SetStringField(TEXT("m"), Text.Left(2000));
			Items.Add(MakeShared<FJsonValueObject>(O));
		});
		Out->SetArrayField(TEXT("items"), Items);
		Out->SetNumberField(TEXT("total"), Total);
	}

	void QueryBookmarks(const IAnalysisSession& S, TSharedPtr<FJsonObject>& Out)
	{
		TArray<TSharedPtr<FJsonValue>> Items;
		ReadBookmarkProvider(S).EnumerateBookmarks(0, S.GetDurationSeconds(), [&](const TraceServices::FBookmark& B)
		{
			if (Items.Num() >= 2000)
			{
				return;
			}
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetNumberField(TEXT("t"), B.Time);
			O->SetStringField(TEXT("text"), B.Text ? B.Text : TEXT(""));
			Items.Add(MakeShared<FJsonValueObject>(O));
		});
		Out->SetArrayField(TEXT("items"), Items);
	}
}

namespace AgentPanelEd
{
	void RegisterTraceOps()
	{
		FAgentPanelBridge::Get().AddOp(TEXT("trace.query"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
		{
			const FString File = FPaths::ConvertRelativePathToFull(Str(A, TEXT("file")));
			const FString What = Str(A, TEXT("what"), TEXT("info"));
			TSharedPtr<const IAnalysisSession> Session = SessionFor(File, Error);
			if (!Session.IsValid())
			{
				return false;
			}
			const IAnalysisSession& S = *Session;
			// 分析线程在往里写：读之前拿读锁（Insights 也这么读），读完马上放
			FAnalysisSessionReadScope Scope(S);
			const bool bDone = S.IsAnalysisComplete();
			Out->SetBoolField(TEXT("analyzing"), !bDone);
			Out->SetNumberField(TEXT("duration"), S.GetDurationSeconds());
			if (What == TEXT("info"))
			{
				QueryInfo(S, Out);
				return true;
			}
			if (What == TEXT("frames"))
			{
				QueryFrames(S, Out);
				return true;
			}
			if (What == TEXT("bookmarks"))
			{
				QueryBookmarks(S, Out);
				return true;
			}
			if (What == TEXT("log"))
			{
				QueryLog(S, A, Out);
				return true;
			}
			if (What == TEXT("counters"))
			{
				QueryCounters(S, A, Out);
				return true;
			}
			const ITimingProfilerProvider* TP = ReadTimingProfilerProvider(S);
			if (!TP)
			{
				Error = TEXT("这份 trace 里没有 CPU / GPU 计时数据：录制时要开 cpu 通道");
				return false;
			}
			if (What == TEXT("timing"))
			{
				return QueryTiming(S, *TP, A, Out, Error);
			}
			if (What == TEXT("timers"))
			{
				QueryTimers(S, *TP, A, Out);
				return true;
			}
			if (What == TEXT("butterfly"))
			{
				return QueryButterfly(S, *TP, A, Out, Error);
			}
			if (What == TEXT("stack"))
			{
				return QueryStack(S, *TP, A, Out, Error);
			}
			if (What == TEXT("instances"))
			{
				return QueryInstances(S, *TP, A, Out, Error);
			}
			Error = FString::Printf(TEXT("不认识的查询：%s（info / frames / timing / timers / butterfly / stack / instances / counters / log / bookmarks）"), *What);
			return false;
		});
	}
}
