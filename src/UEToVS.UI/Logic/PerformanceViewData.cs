using System.Collections.Generic;
using UEToVS.UI.Logic;

namespace UEToVS.UI.Perf
{
    // View data of PerformancePanel (UI-local, no Contracts dependency, no WPF). Lives under Logic/ so the host's pure
    // mapping (PerformanceData → PerformanceViewData) can be unit-tested on net8.0 by compiling this file directly.
    // Namespace stays UEToVS.UI.Perf next to the panel.

    /// <summary>Everything the performance panel shows. The host maps engine snapshots to this ~1/s.</summary>
    public sealed class PerformanceViewData
    {
        public double EngineMemoryBytes { get; set; } = double.NaN;
        /// <summary>Optional budget; the tile turns warning at 85% and error above it.</summary>
        public double EngineMemoryBudgetBytes { get; set; }
        /// <summary>Optional secondary line of the engine memory tile (e.g. working set / managed heap), host-localized.</summary>
        public string EngineMemoryDetail { get; set; }
        public double HostMemoryBytes { get; set; } = double.NaN;
        public double HostMemoryBudgetBytes { get; set; }
        /// <summary>Optional secondary line of the host memory tile (e.g. delta since load / managed heap), host-localized.</summary>
        public string HostMemoryDetail { get; set; }
        /// <summary>What the host process memory is made of (kinds, then the largest heaps / DLLs / files); null hides the section.</summary>
        public IReadOnlyList<HostMemoryRowView> HostMemoryRows { get; set; }
        /// <summary>Optional line under the breakdown (last memory jump and the busiest modules), host-localized.</summary>
        public string HostMemoryNote { get; set; }
        /// <summary>Engine CPU, 0–100 % of the machine.</summary>
        public double CpuPercent { get; set; } = double.NaN;

        /// <summary>Short index state ("就绪", "索引中", "已暂停"…); host-localized.</summary>
        public string IndexState { get; set; }
        public Severity IndexSeverity { get; set; } = Severity.Info;
        /// <summary>0..1, null when not indexing.</summary>
        public double? IndexProgress { get; set; }
        public string IndexDetail { get; set; }

        /// <summary>Oldest first, one sample per <see cref="SampleIntervalSeconds"/> (max 600 shown).</summary>
        public IReadOnlyList<double> CpuHistory { get; set; }
        /// <summary>Bytes, same timeline as <see cref="CpuHistory"/>.</summary>
        public IReadOnlyList<double> MemoryHistory { get; set; }
        /// <summary>Optional second chart: host-process memory in bytes, oldest first (null hides the chart).</summary>
        public IReadOnlyList<double> HostMemoryHistory { get; set; }
        public double SampleIntervalSeconds { get; set; } = 1;

        public IReadOnlyList<ActiveWorkView> Active { get; set; }
        /// <summary>Optional line under the active-work list (queue lengths, paused state), host-localized.</summary>
        public string ActiveDetail { get; set; }
        public IReadOnlyList<PhaseView> Phases { get; set; }
        public IReadOnlyList<MemoryPartView> MemoryParts { get; set; }
        public IReadOnlyList<OperationView> Operations { get; set; }

        public bool BackgroundPaused { get; set; }

        /// <summary>Optional banner at the top of the panel (e.g. "engine unavailable: …"); null hides it.</summary>
        public string Notice { get; set; }
        public Severity NoticeSeverity { get; set; } = Severity.Warning;
    }

    public sealed class ActiveWorkView
    {
        public string Name { get; set; }
        public string Lane { get; set; }
        public long Done { get; set; }
        public long Total { get; set; }
        public double ElapsedMs { get; set; }
    }

    public sealed class PhaseView
    {
        public string Name { get; set; }
        public double DurationMs { get; set; }
        public double CpuMs { get; set; }
        public long Items { get; set; }
        public string Detail { get; set; }
        public bool Running { get; set; }
    }

    public sealed class MemoryPartView
    {
        public string Name { get; set; }
        public long ManagedBytes { get; set; }
        public long MappedBytes { get; set; }
        public long DiskBytes { get; set; }
    }

    public sealed class HostMemoryRowView
    {
        public string Name { get; set; }
        public long Bytes { get; set; }
        /// <summary>0..1 of the process total.</summary>
        public double Share { get; set; }
        public string Detail { get; set; }
        /// <summary>A part of the kind row above it (heap, DLL, file).</summary>
        public bool IsChild { get; set; }
    }

    public sealed class OperationView
    {
        public string Name { get; set; }
        public long Count { get; set; }
        public long Errors { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double MaxMs { get; set; }
        /// <summary>0 = no budget.</summary>
        public double BudgetMs { get; set; }

        /// <summary>Over-budget decision made by the data owner (host); null = p95 &gt; budget.</summary>
        public bool? OverBudget { get; set; }

        public bool IsOverBudget => OverBudget ?? (BudgetMs > 0 && P95Ms > BudgetMs);
    }
}
