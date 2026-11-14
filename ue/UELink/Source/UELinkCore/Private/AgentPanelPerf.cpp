// 实时性能读数（性能页设计 3.1）：数和游戏里 stat unit 读的是同一批全局变量，配色阈值也取引擎自己的控制台变量，
// 这样面板上看到的数字、颜色和 UE 里一致。只在有人看的时候采：最后一次 perf.live 之后 10 秒没人要就摘掉每帧回调。
#include "AgentPanelPerf.h"
#include "AgentPanelBridge.h"
#include "AgentPanelBridgeUtil.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "HAL/IConsoleManager.h"
#include "HAL/PlatformMemory.h"
#include "HAL/PlatformTime.h"
#include "Misc/App.h"
#include "Misc/CoreDelegates.h"
#include "RenderCore.h"
#include "RHI.h"
#if __has_include("RHIStats.h")
#include "RHIStats.h"
#endif
#include "UObject/UObjectArray.h"

// Draw Calls / 图元数的全局数组在 5.4–5.6 一直在；之后的版本 RHI 统计在改，没在真实引擎验证过的版本先不读，网页显示「—」
#if ENGINE_MAJOR_VERSION == 5 && ENGINE_MINOR_VERSION <= 6
#define AGENT_PANEL_DRAW_STATS 1
#else
#define AGENT_PANEL_DRAW_STATS 0
#endif

using namespace AgentPanel;

namespace
{
	struct FSample
	{
		double At = 0;		// FPlatformTime::Seconds()
		float Frame = 0;	// 毫秒，下同
		float Game = 0;
		float Draw = 0;
		float RHIT = 0;
		float GPU = 0;
		int32 Draws = -1;	// -1：这个引擎版本不读
		int32 Prims = -1;
		float MemMB = 0;	// 已用物理内存（stat unit 的 Mem）
		int32 Objects = 0;
	};

	/** 约 1 分钟（60 帧/秒）的环形缓冲，每条几十字节，总共几百 KB */
	constexpr int32 Capacity = 3600;
	constexpr double IdleSeconds = 10.0;
	/** 读内存要进系统调用：不每帧读，隔 0.25 秒读一次，中间的帧沿用上一次的值 */
	constexpr double MemEvery = 0.25;

	class FPerfLive
	{
	public:
		static FPerfLive& Get()
		{
			static FPerfLive Instance;
			return Instance;
		}

		/** 有人来取：没在采就开始采，并记下时间（闲置判断用） */
		void Touch()
		{
			LastPoll = FPlatformTime::Seconds();
			if (!Handle.IsValid())
			{
				Handle = FCoreDelegates::OnEndFrame.AddRaw(this, &FPerfLive::OnEndFrame);
				StartedAt = LastPoll;
			}
		}

		TSharedPtr<FJsonObject> Read(uint64 Since, int32 Limit)
		{
			TSharedPtr<FJsonObject> R = MakeShared<FJsonObject>();
			const uint64 Oldest = Seq > (uint64)Capacity ? Seq - Capacity : 0;
			uint64 From = FMath::Max(Since, Oldest);
			// 一次最多给 Limit 帧：落后太多时只给最近的，网页不需要补齐历史
			if (Seq - From > (uint64)Limit)
			{
				From = Seq - Limit;
			}
			TArray<TSharedPtr<FJsonValue>> At, Frame, Game, Draw, RHIT, GPU, Draws, Prims, Mem, Objects;
			const double Now = FPlatformTime::Seconds();
			for (uint64 I = From; I < Seq; ++I)
			{
				const FSample& S = Ring[I % Capacity];
				// 时间给「距现在多少秒前」：网页不用知道 UE 进程的时钟
				At.Add(MakeShared<FJsonValueNumber>(FMath::RoundToDouble((S.At - Now) * 1000.0) / 1000.0));
				auto Ms = [](float V) { return MakeShared<FJsonValueNumber>(FMath::RoundToDouble(V * 100.0) / 100.0); };
				Frame.Add(Ms(S.Frame));
				Game.Add(Ms(S.Game));
				Draw.Add(Ms(S.Draw));
				RHIT.Add(Ms(S.RHIT));
				GPU.Add(Ms(S.GPU));
				Draws.Add(MakeShared<FJsonValueNumber>(S.Draws));
				Prims.Add(MakeShared<FJsonValueNumber>(S.Prims));
				Mem.Add(MakeShared<FJsonValueNumber>(FMath::RoundToDouble(S.MemMB * 10.0) / 10.0));
				Objects.Add(MakeShared<FJsonValueNumber>(S.Objects));
			}
			TSharedPtr<FJsonObject> F = MakeShared<FJsonObject>();
			F->SetArrayField(TEXT("at"), At);
			F->SetArrayField(TEXT("frame"), Frame);
			F->SetArrayField(TEXT("game"), Game);
			F->SetArrayField(TEXT("draw"), Draw);
			F->SetArrayField(TEXT("rhi"), RHIT);
			F->SetArrayField(TEXT("gpu"), GPU);
			F->SetArrayField(TEXT("draws"), Draws);
			F->SetArrayField(TEXT("prims"), Prims);
			F->SetArrayField(TEXT("mem"), Mem);
			F->SetArrayField(TEXT("objects"), Objects);
			R->SetObjectField(TEXT("frames"), F);
			R->SetNumberField(TEXT("cursor"), (double)Seq);
			R->SetNumberField(TEXT("from"), (double)From);
			// 网页的游标比缓冲里最老的还旧：中间丢了帧（网页停了一会儿没取）
			R->SetBoolField(TEXT("gap"), Since > 0 && Since < Oldest);
			R->SetNumberField(TEXT("sampling"), FMath::RoundToDouble((Now - StartedAt) * 10.0) / 10.0);

			// stat unit 的配色阈值：超过 Target 变黄、超过 Unacceptable 变红
			TSharedPtr<FJsonObject> Th = MakeShared<FJsonObject>();
			Th->SetNumberField(TEXT("target"), CVarFloat(TEXT("t.TargetFrameTimeThreshold"), 33.9f));
			Th->SetNumberField(TEXT("unacceptable"), CVarFloat(TEXT("t.UnacceptableFrameTimeThreshold"), 50.0f));
			R->SetObjectField(TEXT("thresholds"), Th);

			const FPlatformMemoryStats M = FPlatformMemory::GetStats();
			TSharedPtr<FJsonObject> MO = MakeShared<FJsonObject>();
			MO->SetNumberField(TEXT("usedPhysicalMB"), ToMB(M.UsedPhysical));
			MO->SetNumberField(TEXT("peakPhysicalMB"), ToMB(M.PeakUsedPhysical));
			MO->SetNumberField(TEXT("usedVirtualMB"), ToMB(M.UsedVirtual));
			MO->SetNumberField(TEXT("peakVirtualMB"), ToMB(M.PeakUsedVirtual));
			MO->SetNumberField(TEXT("availablePhysicalMB"), ToMB(M.AvailablePhysical));
			MO->SetNumberField(TEXT("totalPhysicalMB"), ToMB(M.TotalPhysical));
			R->SetObjectField(TEXT("memory"), MO);
			R->SetBoolField(TEXT("drawStats"), AGENT_PANEL_DRAW_STATS != 0);
			return R;
		}

	private:
		FSample Ring[Capacity];
		uint64 Seq = 0;
		FDelegateHandle Handle;
		double LastPoll = 0;
		double StartedAt = 0;
		double LastMemAt = 0;
		float LastMemMB = 0;

		static double ToMB(uint64 Bytes) { return FMath::RoundToDouble((double)Bytes / (1024.0 * 1024.0) * 10.0) / 10.0; }

		static float CVarFloat(const TCHAR* Name, float Default)
		{
			IConsoleVariable* V = IConsoleManager::Get().FindConsoleVariable(Name);
			return V ? V->GetFloat() : Default;
		}

		void OnEndFrame()
		{
			const double Now = FPlatformTime::Seconds();
			if (Now - LastPoll > IdleSeconds)
			{
				// 没人看了：摘掉回调，下次有人来取再挂上（缓冲留着，重新开始时游标接着往后走）
				FCoreDelegates::OnEndFrame.Remove(Handle);
				Handle.Reset();
				return;
			}
			if (Now - LastMemAt >= MemEvery)
			{
				LastMemAt = Now;
				LastMemMB = (float)((double)FPlatformMemory::GetStats().UsedPhysical / (1024.0 * 1024.0));
			}
			FSample& S = Ring[Seq % Capacity];
			S.At = Now;
			S.Frame = (float)(FApp::GetDeltaTime() * 1000.0);
			// 和 stat unit 读的是同一批变量（引擎里 FStatUnitData），单位是周期数
			S.Game = (float)FPlatformTime::ToMilliseconds(GGameThreadTime);
			S.Draw = (float)FPlatformTime::ToMilliseconds(GRenderThreadTime);
			S.RHIT = (float)FPlatformTime::ToMilliseconds(GRHIThreadTime);
			S.GPU = (float)FPlatformTime::ToMilliseconds(RHIGetGPUFrameCycles(0));
#if AGENT_PANEL_DRAW_STATS
			S.Draws = GNumDrawCallsRHI[0];
			S.Prims = GNumPrimitivesDrawnRHI[0];
#endif
			S.MemMB = LastMemMB;
			S.Objects = GUObjectArray.GetObjectArrayNumMinusAvailable();
			++Seq;
		}
	};
}

void AgentPanel::RegisterPerfOps(FAgentPanelBridge& Bridge)
{
	Bridge.AddOp(TEXT("perf.live"), [](const TSharedPtr<FJsonObject>& A, TSharedPtr<FJsonObject>& Out, FString& Error)
	{
		FPerfLive& L = FPerfLive::Get();
		L.Touch();
		double Since = 0;
		A->TryGetNumberField(TEXT("since"), Since);
		TSharedPtr<FJsonObject> R = L.Read((uint64)FMath::Max(0.0, Since), FMath::Clamp(Int(A, TEXT("limit"), 600), 1, Capacity));
		for (const auto& P : R->Values)
		{
			Out->SetField(FString(*P.Key), P.Value);
		}
		return true;
	});
}
