using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Indexing
{
    /// <summary>What background indexing does while VS / UBT builds (<see cref="IndexingSettings.DuringBuild"/>).</summary>
    public enum BuildPolicy
    {
        Pause = 0,
        SingleThread = 1,
        FullSpeed = 2,
    }

    /// <summary>
    /// Resolved indexing settings: thread counts are real numbers (0 = default = max(1, logical cores / 4)), the build
    /// policy is an enum, excluded folders a set. Immutable; replaced as a whole when the host pushes new settings.
    /// </summary>
    public sealed class IndexingConfig
    {
        public const int MaxThreads = 64;

        public static readonly IReadOnlyList<string> DefaultExcludedFolders = new[] { "Intermediate", "Binaries", "Saved", "DerivedDataCache" };

        public int CppThreads { get; private set; } = DefaultThreads();
        public int AssetThreads { get; private set; } = DefaultThreads();
        public BuildPolicy DuringBuild { get; private set; } = BuildPolicy.SingleThread;
        public bool IncludeEngine { get; private set; } = true;
        public bool SharedEngineCache { get; private set; }

        /// <summary>Shared engine cache root; null = <see cref="Indexing.SharedEngineCache.DefaultRoot"/>.</summary>
        public string EngineCacheDirectory { get; private set; }

        /// <summary>Null = the lister's defaults.</summary>
        public IReadOnlyCollection<string> ExcludedFolders { get; private set; }

        /// <summary>Default thread count for C++ and asset indexing: a quarter of the logical cores, at least one.</summary>
        public static int DefaultThreads(int? logicalCores = null) => Math.Max(1, (logicalCores ?? Environment.ProcessorCount) / 4);

        public static int ResolveThreads(int requested, int? logicalCores = null) =>
            requested <= 0 ? DefaultThreads(logicalCores) : Math.Min(requested, MaxThreads);

        public static IndexingConfig From(IndexingSettings s, int? logicalCores = null)
        {
            s ??= new IndexingSettings();
            return new IndexingConfig
            {
                CppThreads = ResolveThreads(s.CppThreads, logicalCores),
                AssetThreads = ResolveThreads(s.AssetThreads, logicalCores),
                DuringBuild = s.DuringBuild switch { 0 => BuildPolicy.Pause, 2 => BuildPolicy.FullSpeed, _ => BuildPolicy.SingleThread },
                IncludeEngine = s.IncludeEngine,
                SharedEngineCache = s.SharedEngineCache,
                EngineCacheDirectory = string.IsNullOrWhiteSpace(s.EngineCacheDirectory) ? null : s.EngineCacheDirectory.Trim(),
                ExcludedFolders = s.ExcludedFolders == null || s.ExcludedFolders.Count == 0
                    ? null
                    : s.ExcludedFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim().Trim('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }

        /// <summary>Back to the contract (thread counts resolved), e.g. for the performance snapshot.</summary>
        public IndexingSettings ToSettings() => new IndexingSettings
        {
            CppThreads = CppThreads,
            AssetThreads = AssetThreads,
            DuringBuild = (int)DuringBuild,
            IncludeEngine = IncludeEngine,
            SharedEngineCache = SharedEngineCache,
            EngineCacheDirectory = EngineCacheDirectory,
            ExcludedFolders = ExcludedFolders?.ToList(),
        };

        public bool SameExcludedFolders(IndexingConfig other)
        {
            var a = ExcludedFolders ?? Array.Empty<string>();
            var b = other?.ExcludedFolders ?? Array.Empty<string>();
            return a.Count == b.Count && a.All(x => b.Contains(x, StringComparer.OrdinalIgnoreCase));
        }

        public override string ToString() =>
            $"cpp={CppThreads} assets={AssetThreads} duringBuild={DuringBuild} engine={IncludeEngine} sharedEngineCache={SharedEngineCache} cacheDir={EngineCacheDirectory ?? "default"} exclude={(ExcludedFolders == null ? "default" : string.Join(",", ExcludedFolders))}";
    }

    /// <summary>
    /// Dynamic concurrency limit for one kind of indexing work (C++ parse or asset scan). Worker i may run while
    /// i &lt; <see cref="Effective"/>; the limit follows the configured thread count, the user pause and the build
    /// policy, and changes take effect immediately (waiting workers wake up, surplus workers park at their next file).
    /// </summary>
    public sealed class WorkerThrottle
    {
        private static readonly Logger L = Log.For("Index");
        private readonly object _gate = new object();
        private int _configured;
        private BuildPolicy _policy = BuildPolicy.SingleThread;
        private bool _building;
        private bool _userPaused;

        public WorkerThrottle(string name, int configured)
        {
            Name = name;
            _configured = Math.Max(1, configured);
        }

        public string Name { get; }

        /// <summary>Raised (outside the lock) after any change.</summary>
        public event Action Changed;

        public int Configured
        {
            get { lock (_gate) return _configured; }
        }

        public bool IsBuilding
        {
            get { lock (_gate) return _building; }
        }

        /// <summary>Workers allowed to run now.</summary>
        public int Effective
        {
            get { lock (_gate) return EffectiveLocked(); }
        }

        private int EffectiveLocked()
        {
            if (_userPaused) return 0;
            if (!_building) return _configured;
            return _policy switch
            {
                BuildPolicy.Pause => 0,
                BuildPolicy.SingleThread => 1,
                _ => _configured,
            };
        }

        /// <summary>OS priority for workers: Lowest while a build runs (never slow the compiler down), BelowNormal otherwise.</summary>
        public ThreadPriority Priority
        {
            get { lock (_gate) return _building ? ThreadPriority.Lowest : ThreadPriority.BelowNormal; }
        }

        public void SetConfigured(int threads) => Update(() => _configured = Math.Max(1, threads));
        public void SetPolicy(BuildPolicy policy) => Update(() => _policy = policy);
        public void SetBuilding(bool building) => Update(() => _building = building);
        public void SetUserPaused(bool paused) => Update(() => _userPaused = paused);

        private void Update(Action change)
        {
            int before, after;
            lock (_gate)
            {
                before = EffectiveLocked();
                change();
                after = EffectiveLocked();
                Monitor.PulseAll(_gate);
            }
            if (before != after) L.Debug("index.throttle.changed", null, ("throttle", Name), ("from", before), ("to", after));
            try { Changed?.Invoke(); }
            catch (Exception ex) { L.Warn("index.throttle.handler_failed", "Throttle change handler failed", ex); }
        }

        /// <summary>
        /// Blocks worker <paramref name="index"/> until it may run. Returns the ticks spent waiting. Throws on cancellation.
        /// </summary>
        public long WaitForTurn(int index, CancellationToken ct)
        {
            lock (_gate)
            {
                if (index < EffectiveLocked()) return 0;
            }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            using (ct.Register(() => { lock (_gate) Monitor.PulseAll(_gate); }))
            {
                lock (_gate)
                {
                    while (index >= EffectiveLocked())
                    {
                        ct.ThrowIfCancellationRequested();
                        Monitor.Wait(_gate, 1000);
                    }
                }
            }
            return System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }
    }
}
