using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Logic.Performance
{
    /// <summary>Host-side (devenv.exe) resource sample. The extension's own increment cannot be isolated from VS's memory,
    /// so the panel shows devenv private bytes, the delta since UEToVS loaded, and the managed heap.</summary>
    public sealed class HostResourceSample
    {
        public DateTime TakenUtc { get; set; }
        public long PrivateBytes { get; set; }
        public long WorkingSetBytes { get; set; }
        public long ManagedHeapBytes { get; set; }

        /// <summary>devenv private bytes when the UEToVS package loaded (baseline for the delta).</summary>
        public long PrivateBytesAtLoad { get; set; }

        public long PrivateBytesDelta => PrivateBytes - PrivateBytesAtLoad;
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public int ThreadCount { get; set; }
    }

    /// <summary>
    /// Everything the "UEToVS 性能" tool window shows, assembled by the host's PerformanceMonitor once per second while the
    /// window is visible. This is the host's model; the UI component library's PerformancePanel has its own view-data
    /// type, and the mapping happens in one place (IPerformancePanelContent.Update, see Integration).
    /// </summary>
    public sealed class PerformanceData
    {
        public DateTime TakenUtc { get; set; }

        public HostResourceSample Host { get; set; }

        /// <summary>What devenv's memory is made of (refreshed every few seconds), or null before the first walk.</summary>
        public HostMemoryMap HostMemory { get; set; }

        /// <summary>The last devenv memory jump of this session, or null.</summary>
        public HostMemorySpike HostSpike { get; set; }

        /// <summary>Recent host samples, oldest first (≈1/s, up to 10 minutes).</summary>
        public IReadOnlyList<HostResourceSample> HostHistory { get; set; } = new HostResourceSample[0];

        /// <summary>Host operation latency since start (Log.LatencySinceStart), with §9.1 budgets where known.</summary>
        public List<OperationStats> HostOperations { get; set; } = new List<OperationStats>();

        /// <summary>Engine snapshot, or null when the engine is unavailable (see <see cref="EngineState"/>).</summary>
        public PerformanceSnapshot Engine { get; set; }

        /// <summary>Connection state of the engine client (Connected, Starting, Unavailable, ...).</summary>
        public string EngineState { get; set; }

        /// <summary>Why the engine snapshot is missing (timeout, not running, error text), or null.</summary>
        public string EngineError { get; set; }

        /// <summary>Latency of the GetPerformanceSnapshotAsync call itself.</summary>
        public double EngineRoundTripMs { get; set; }
    }

    /// <summary>Pure helpers for <see cref="PerformanceData"/>.</summary>
    public static class PerformanceDataBuilder
    {
        /// <summary>UI-thread entry points are logged as "ui.*" events (see UiThread in the host): hard limit 16 ms (§9.1).</summary>
        public const double UiThreadBudgetMs = 16;

        /// <summary>Budgets by key fragment (first match wins). Keys are "Module/event" as recorded by PerfScope.</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, double>> Budgets = new[]
        {
            new KeyValuePair<string, double>("/ui.", UiThreadBudgetMs),
            new KeyValuePair<string, double>("rpc.search", 100),
            new KeyValuePair<string, double>("rpc.getfilesymbols", 100),
            new KeyValuePair<string, double>("rpc.findsymbols", 200),
        };

        public static double BudgetFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            foreach (var kv in Budgets)
                if (key.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
            return 0;
        }

        public static List<OperationStats> FromLatency(IEnumerable<LatencyStats.Summary> summaries)
        {
            var list = new List<OperationStats>();
            if (summaries == null) return list;
            foreach (var s in summaries)
            {
                list.Add(new OperationStats
                {
                    Name = s.Key,
                    Count = s.Count,
                    Errors = s.Errors,
                    P50Ms = Math.Round(s.P50, 2),
                    P95Ms = Math.Round(s.P95, 2),
                    MaxMs = Math.Round(s.Max, 2),
                    BudgetMs = BudgetFor(s.Key),
                });
            }
            return list.OrderByDescending(o => o.Count).ThenBy(o => o.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>A row is highlighted when its p95 exceeds its budget.</summary>
        public static bool IsOverBudget(OperationStats op) => op != null && op.BudgetMs > 0 && op.P95Ms > op.BudgetMs;
    }

    /// <summary>Fixed-capacity ring buffer, oldest first. Thread-safe.</summary>
    public sealed class SampleHistory<T>
    {
        private readonly T[] _items;
        private readonly object _gate = new object();
        private int _start;
        private int _count;

        public SampleHistory(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new T[capacity];
        }

        public int Capacity => _items.Length;

        public int Count
        {
            get { lock (_gate) return _count; }
        }

        public void Add(T item)
        {
            lock (_gate)
            {
                int idx = (_start + _count) % _items.Length;
                _items[idx] = item;
                if (_count < _items.Length) _count++;
                else _start = (_start + 1) % _items.Length;
            }
        }

        public T[] ToArray()
        {
            lock (_gate)
            {
                var a = new T[_count];
                for (int i = 0; i < _count; i++) a[i] = _items[(_start + i) % _items.Length];
                return a;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _start = 0;
                _count = 0;
                Array.Clear(_items, 0, _items.Length);
            }
        }
    }
}
