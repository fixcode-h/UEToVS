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

        /// <summary>Index pipeline throughput per stage (running stages first, then the most recent finished ones).</summary>
        public List<IndexThroughput> Throughput { get; set; } = new List<IndexThroughput>();

        /// <summary>Indexing settings in effect (thread counts resolved to real numbers).</summary>
        public IndexingSettings IndexingSettings { get; set; }
    }

    /// <summary>
    /// Throughput of one index pipeline stage (C++ parse of project / engine sources, asset scan), for the
    /// performance panel and logs. Thread times are summed across worker threads.
    /// </summary>
    public sealed class IndexThroughput
    {
        /// <summary>Stable stage code: "parse.project", "parse.engine", "assets"…</summary>
        public string Stage { get; set; }
        public bool Running { get; set; }
        public DateTime StartedUtc { get; set; }
        public double ElapsedMs { get; set; }

        /// <summary>Threads allowed to run right now (after the build policy / user pause).</summary>
        public int Threads { get; set; }

        /// <summary>Configured threads for this stage.</summary>
        public int ConfiguredThreads { get; set; }

        public int FilesDone { get; set; }
        public int FilesTotal { get; set; }
        public int FilesParsed { get; set; }
        public int CacheHits { get; set; }
        public long BytesRead { get; set; }

        public double FilesPerSecond { get; set; }
        public double MegabytesPerSecond { get; set; }

        /// <summary>Per-file parse time percentiles (parsed files only).</summary>
        public double ParseP50Ms { get; set; }
        public double ParseP95Ms { get; set; }
        public double ParseMaxMs { get; set; }

        /// <summary>Summed thread time spent reading files, parsing (incl. hashing) and handing results to the index.</summary>
        public double ReadMs { get; set; }
        public double ParseMs { get; set; }
        public double ApplyMs { get; set; }

        /// <summary>Share of busy thread time spent in I/O (0–1); the rest is CPU (parse + apply).</summary>
        public double IoShare { get; set; }

        /// <summary>Busy thread time / thread time available while not throttled (0–1).</summary>
        public double ThreadUtilization { get; set; }

        /// <summary>Summed thread time workers waited because of the build policy, the user pause or segment-write back-pressure.</summary>
        public double ThrottledMs { get; set; }

        /// <summary>Segments written by the pipeline (bulk mode) and their total write time.</summary>
        public int SegmentsWritten { get; set; }
        public double SegmentWriteMs { get; set; }

        /// <summary>Slowest files (read + parse), slowest first; at most 20.</summary>
        public List<SlowFile> SlowestFiles { get; set; } = new List<SlowFile>();
    }

    public sealed class SlowFile
    {
        public string Path { get; set; }
        public double Ms { get; set; }
        public long Bytes { get; set; }
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

        /// <summary>Items / s over the phase (0 when not meaningful).</summary>
        public double ItemsPerSecond { get; set; }

        /// <summary>Input MB / s over the phase (parsing phases; 0 otherwise).</summary>
        public double MegabytesPerSecond { get; set; }
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
