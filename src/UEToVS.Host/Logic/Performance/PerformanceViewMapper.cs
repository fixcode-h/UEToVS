using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;
using UEToVS.UI.Logic;
using UEToVS.UI.Perf;

namespace UEToVS.Host.Logic.Performance
{
    /// <summary>
    /// THE mapping from the host's <see cref="PerformanceData"/> to UEToVS.UI's <see cref="PerformanceViewData"/>
    /// (field by field as documented on IPerformancePanelContentProvider). Pure: no WPF/VS types (PerformanceViewData
    /// lives in UEToVS.UI/Logic), unit-tested in tests/UEToVS.Host.Logic.Tests. All texts are localized here: engine
    /// codes through <see cref="EngineCodes"/>, everything else through <see cref="Strings"/>.
    /// </summary>
    public static class PerformanceViewMapper
    {
        public static PerformanceViewData Map(PerformanceData d)
        {
            var v = new PerformanceViewData { SampleIntervalSeconds = 1 };
            if (d == null) return v;

            // ---- VS-side card + host trend
            if (d.Host != null)
            {
                v.HostMemoryBytes = d.Host.PrivateBytes;
                v.HostMemoryDetail = Strings.PerfViewHostMemoryDetail(SignedMb(d.Host.PrivateBytesDelta), Mb(d.Host.ManagedHeapBytes));
            }
            v.HostMemoryHistory = (d.HostHistory ?? Array.Empty<HostResourceSample>())
                .Where(h => h != null).Select(h => (double)h.PrivateBytes).ToArray();

            var hostOps = (d.HostOperations ?? new List<OperationStats>()).Where(o => o != null).Select(o => Op(o, false));
            var e = d.Engine;
            if (e == null)
            {
                // ---- engine unavailable: state + reason, everything engine-side empty.
                string state = Strings.EngineStateName(d.EngineState);
                Severity sev = EngineStateSeverity(d.EngineState);
                v.IndexState = state;
                v.IndexSeverity = sev;
                v.IndexDetail = d.EngineError;
                v.Notice = Strings.PerfViewEngineUnavailable(state, d.EngineError);
                v.NoticeSeverity = sev == Severity.Error ? Severity.Error : Severity.Warning;
                v.CpuHistory = Array.Empty<double>();
                v.MemoryHistory = Array.Empty<double>();
                v.Active = Array.Empty<ActiveWorkView>();
                v.Phases = Array.Empty<PhaseView>();
                v.MemoryParts = Array.Empty<MemoryPartView>();
                v.Operations = hostOps.ToList();
                return v;
            }

            // ---- engine card
            var res = e.Resources ?? e.Status?.Resources;
            if (res != null)
            {
                v.EngineMemoryBytes = res.PrivateBytes;
                v.EngineMemoryDetail = Strings.PerfViewEngineMemoryDetail(Mb(res.WorkingSetBytes), Mb(res.ManagedHeapBytes));
            }
            v.CpuPercent = e.CpuPercent;

            // ---- index state card (+ errors)
            var st = e.Status;
            if (st != null)
            {
                v.IndexState = Strings.PhaseName((int)st.Phase);
                bool working = st.Phase != IndexPhase.NotStarted && st.Phase != IndexPhase.Ready && st.Phase != IndexPhase.Failed;
                if (working && st.FilesToParse > 0)
                {
                    v.IndexProgress = Math.Max(0, Math.Min(1, st.FilesParsed / (double)st.FilesToParse));
                    v.IndexDetail = Strings.PerfViewIndexProgress(st.FilesParsed, st.FilesToParse, st.SymbolCount);
                }
                else
                {
                    v.IndexDetail = Strings.PerfViewIndexSummary(st.SymbolCount, st.FileCount);
                }
                string error = EngineCodes.Error(st.LastError);
                if (st.Phase == IndexPhase.Failed || error != null)
                {
                    v.IndexSeverity = Severity.Error;
                    if (error != null) v.IndexDetail = error;
                }
                else if (st.Phase == IndexPhase.Ready)
                {
                    v.IndexSeverity = Severity.Success;
                }
            }
            else
            {
                v.IndexState = Strings.EngineStateName(d.EngineState);
            }

            // ---- trends (engine)
            var history = (e.History ?? new List<ResourceSample>()).Where(h => h != null).ToList();
            v.CpuHistory = history.Select(h => h.CpuPercent).ToArray();
            v.MemoryHistory = history.Select(h => (double)h.PrivateBytes).ToArray();

            // ---- 正在进行
            v.Active = (e.Active ?? new List<ActiveWork>()).Where(a => a != null).Select(a => new ActiveWorkView
            {
                Name = EngineCodes.WorkName(a.Name),
                Lane = a.Lane,
                Done = a.Done,
                Total = a.Total,
                ElapsedMs = a.ElapsedMs,
            }).ToList();
            v.ActiveDetail = Strings.PerfViewQueues(e.ScheduledInteractive, e.ScheduledBackground, e.BackgroundPaused);
            v.BackgroundPaused = e.BackgroundPaused;

            // ---- 索引阶段 / 索引内存构成
            v.Phases = (e.Phases ?? new List<PhaseTiming>()).Where(p => p != null).Select(p => new PhaseView
            {
                Name = Strings.PhaseName((int)p.Phase),
                DurationMs = p.DurationMs,
                CpuMs = p.CpuMs,
                Items = p.Items,
                Detail = EngineCodes.Detail(p.Detail),
                Running = p.Running,
            }).ToList();
            v.MemoryParts = (e.IndexParts ?? new List<MemoryPart>()).Where(m => m != null).Select(m => new MemoryPartView
            {
                Name = m.Name,
                ManagedBytes = m.ManagedBytes,
                MappedBytes = m.MappedBytes,
                DiskBytes = m.DiskBytes,
            }).ToList();

            // ---- 操作延迟: engine + host, highlight decided by the host's budget rule.
            v.Operations = (e.Operations ?? new List<OperationStats>()).Where(o => o != null).Select(o => Op(o, true))
                .Concat(hostOps).ToList();
            return v;
        }

        /// <summary>Severity of the engine-state tile/banner while no snapshot is available.</summary>
        public static Severity EngineStateSeverity(string engineState)
        {
            switch (engineState)
            {
                case "Unavailable":
                case "Incompatible":
                case "NotInstalled":
                    return Severity.Error;
                default:
                    return Severity.Warning; // None, Stopped, Starting, Restarting, Connected-but-snapshot-failed
            }
        }

        private static OperationView Op(OperationStats o, bool engine) => new OperationView
        {
            Name = engine ? Strings.PerfViewOpEngine(o.Name) : Strings.PerfViewOpHost(o.Name),
            Count = o.Count,
            Errors = o.Errors,
            P50Ms = o.P50Ms,
            P95Ms = o.P95Ms,
            MaxMs = o.MaxMs,
            BudgetMs = o.BudgetMs,
            OverBudget = PerformanceDataBuilder.IsOverBudget(o),
        };

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";

        private static string SignedMb(long bytes) => (bytes >= 0 ? "+" : "") + Mb(bytes);
    }
}
