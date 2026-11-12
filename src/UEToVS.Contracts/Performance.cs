using System;
using System.Collections.Generic;

namespace UEToVS.Contracts
{
    /// <summary>Everything the performance panel shows about the engine process.</summary>
    public sealed class PerformanceSnapshot
    {
        public DateTime TakenUtc { get; set; }
        public int ProcessId { get; set; }
        public double UptimeSeconds { get; set; }

        /// <summary>Current CPU usage of the engine process, 0–100 % of the whole machine.</summary>
        public double CpuPercent { get; set; }

        public ResourceSnapshot Resources { get; set; }

        /// <summary>Recent samples (oldest first, about 1 per second, up to 10 minutes) for the panel's charts.</summary>
        public List<ResourceSample> History { get; set; } = new List<ResourceSample>();

        public EngineStatus Status { get; set; }

        /// <summary>Index work broken down by phase (listing, cache load, parsing, merging…), most recent run first.</summary>
        public List<PhaseTiming> Phases { get; set; } = new List<PhaseTiming>();

        /// <summary>Breakdown of index memory / disk usage by part (string pool, symbols, occurrences, delta…).</summary>
        public List<MemoryPart> IndexParts { get; set; } = new List<MemoryPart>();

        /// <summary>Per-operation latency since start (search, file symbols, parse, …).</summary>
        public List<OperationStats> Operations { get; set; } = new List<OperationStats>();

        /// <summary>Currently running background work (name, progress) for the "what is it doing now" list.</summary>
        public List<ActiveWork> Active { get; set; } = new List<ActiveWork>();

        public int ScheduledInteractive { get; set; }
        public int ScheduledBackground { get; set; }
        public bool BackgroundPaused { get; set; }
    }

    public sealed class ResourceSample
    {
        public DateTime TimeUtc { get; set; }
        public double CpuPercent { get; set; }
        public long PrivateBytes { get; set; }
        public long ManagedHeapBytes { get; set; }
    }

    public sealed class PhaseTiming
    {
        public IndexPhase Phase { get; set; }
        public DateTime StartedUtc { get; set; }
        public double DurationMs { get; set; }
        public bool Running { get; set; }
        public int Items { get; set; }

        /// <summary>CPU time used by the phase across all threads.</summary>
        public double CpuMs { get; set; }

        /// <summary>Free-form extras, e.g. "cacheHits=5321".</summary>
        public string Detail { get; set; }
    }

    public sealed class MemoryPart
    {
        public string Name { get; set; }
        public long ManagedBytes { get; set; }
        public long MappedBytes { get; set; }
        public long DiskBytes { get; set; }
    }

    public sealed class OperationStats
    {
        public string Name { get; set; }
        public long Count { get; set; }
        public long Errors { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double MaxMs { get; set; }

        /// <summary>Latency budget from the design (§9.1); 0 = none.</summary>
        public double BudgetMs { get; set; }
    }

    public sealed class ActiveWork
    {
        public string Name { get; set; }
        public string Lane { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public double ElapsedMs { get; set; }
    }
}
