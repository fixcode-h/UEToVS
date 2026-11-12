using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Health
{
    /// <summary>Logs requests that run longer than <see cref="Threshold"/> (once per request, while still running).</summary>
    public sealed class Watchdog : IDisposable
    {
        private static readonly Logger L = Log.For("Watchdog");

        private sealed class Entry
        {
            public string Method;
            public string RequestId;
            public long Start;
            public bool Reported;
        }

        private readonly ConcurrentDictionary<long, Entry> _inFlight = new ConcurrentDictionary<long, Entry>();
        private readonly Timer _timer;
        private long _next;

        public Watchdog(TimeSpan? threshold = null, TimeSpan? interval = null)
        {
            Threshold = threshold ?? TimeSpan.FromSeconds(5);
            var i = interval ?? TimeSpan.FromSeconds(1);
            _timer = new Timer(_ => Check(), null, i, i);
        }

        public TimeSpan Threshold { get; }

        /// <summary>Extra context for the stuck-request line (queue lengths...).</summary>
        public Func<IEnumerable<(string Key, object Value)>> Context { get; set; }

        public int InFlight => _inFlight.Count;
        public long Reported;

        public IDisposable Track(string method)
        {
            long id = Interlocked.Increment(ref _next);
            _inFlight[id] = new Entry { Method = method, RequestId = Log.RequestId, Start = Stopwatch.GetTimestamp() };
            return new Releaser(this, id);
        }

        private sealed class Releaser : IDisposable
        {
            private readonly Watchdog _owner;
            private readonly long _id;

            public Releaser(Watchdog owner, long id)
            {
                _owner = owner;
                _id = id;
            }

            public void Dispose() => _owner._inFlight.TryRemove(_id, out _);
        }

        internal void Check()
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                foreach (var kv in _inFlight)
                {
                    var e = kv.Value;
                    double ms = (now - e.Start) * 1000.0 / Stopwatch.Frequency;
                    if (e.Reported || ms < Threshold.TotalMilliseconds) continue;
                    e.Reported = true;
                    Interlocked.Increment(ref Reported);
                    var data = new List<(string, object)> { ("method", e.Method), ("req", e.RequestId), ("elapsedMs", Math.Round(ms)), ("inFlight", _inFlight.Count) };
                    var extra = Context?.Invoke();
                    if (extra != null) data.AddRange(extra);
                    L.Warn("rpc.stuck", $"{e.Method} still running after {ms / 1000:F1} s", data.ToArray());
                }
            }
            catch (Exception ex)
            {
                L.Warn("watchdog.failed", "Watchdog check failed", ex);
            }
        }

        public void Dispose() => _timer.Dispose();
    }

    /// <summary>
    /// 1 s CPU / memory sampler with a 10-minute ring buffer for the performance panel. CPU% is of the whole machine
    /// (process CPU delta / wall delta / cores).
    /// </summary>
    public sealed class PerformanceMonitor : IDisposable
    {
        public const int Capacity = 600;
        private readonly ResourceSample[] _ring = new ResourceSample[Capacity];
        private readonly object _gate = new object();
        private readonly Timer _timer;
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private int _head;
        private int _count;
        private TimeSpan _lastCpu;
        private long _lastTs;

        public PerformanceMonitor(TimeSpan? interval = null)
        {
            var p = Process.GetCurrentProcess();
            _lastCpu = p.TotalProcessorTime;
            _lastTs = Stopwatch.GetTimestamp();
            var i = interval ?? TimeSpan.FromSeconds(1);
            _timer = new Timer(_ => Sample(), null, i, i);
        }

        public double CpuPercent { get; private set; }
        /// <summary>Process uptime (falls back to monitor lifetime when the start time is unavailable).</summary>
        public double UptimeSeconds
        {
            get
            {
                try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds; }
                catch (Exception) { return _uptime.Elapsed.TotalSeconds; }
            }
        }
        public long LastPrivateBytes { get; private set; }

        public void Sample()
        {
            try
            {
                var p = Process.GetCurrentProcess();
                var cpu = p.TotalProcessorTime;
                long ts = Stopwatch.GetTimestamp();
                double wallMs = (ts - _lastTs) * 1000.0 / Stopwatch.Frequency;
                double cpuPct = 0;
                lock (_gate)
                {
                    if (wallMs > 0) cpuPct = Math.Clamp((cpu - _lastCpu).TotalMilliseconds / wallMs / Environment.ProcessorCount * 100.0, 0, 100);
                    _lastCpu = cpu;
                    _lastTs = ts;
                    CpuPercent = Math.Round(cpuPct, 1);
                    LastPrivateBytes = p.PrivateMemorySize64;
                    _ring[_head] = new ResourceSample
                    {
                        TimeUtc = DateTime.UtcNow,
                        CpuPercent = CpuPercent,
                        PrivateBytes = LastPrivateBytes,
                        ManagedHeapBytes = GC.GetTotalMemory(false),
                    };
                    _head = (_head + 1) % Capacity;
                    if (_count < Capacity) _count++;
                }
            }
            catch (Exception)
            {
                // sampling must never break the process; the next tick retries
            }
        }

        /// <summary>Oldest first.</summary>
        public List<ResourceSample> History()
        {
            lock (_gate)
            {
                var list = new List<ResourceSample>(_count);
                int start = (_head - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++) list.Add(_ring[(start + i) % Capacity]);
                return list;
            }
        }

        public static ResourceSnapshot Resources(long indexBytesOnDisk)
        {
            var p = Process.GetCurrentProcess();
            return new ResourceSnapshot
            {
                PrivateBytes = p.PrivateMemorySize64,
                WorkingSetBytes = p.WorkingSet64,
                ManagedHeapBytes = GC.GetTotalMemory(false),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                ThreadCount = p.Threads.Count,
                IndexBytesOnDisk = indexBytesOnDisk,
            };
        }

        public void Dispose() => _timer.Dispose();
    }

    /// <summary>Exits the engine when the host process (VS) goes away.</summary>
    public static class ParentWatcher
    {
        private static readonly Logger L = Log.For("Process");

        /// <summary>Calls <paramref name="onExit"/> with a reason when the parent exits or cannot be found.</summary>
        public static void Watch(int pid, Action<string> onExit, CancellationToken ct)
        {
            Process parent;
            try
            {
                parent = Process.GetProcessById(pid);
            }
            catch (Exception)
            {
                L.Warn("process.parent.missing", "Parent process not found at startup", ("parentPid", pid));
                onExit("parent-missing");
                return;
            }
            L.Info("process.parent.watch", null, ("parentPid", pid), ("parentName", SafeName(parent)));
            _ = Task.Run(async () =>
            {
                try
                {
                    await parent.WaitForExitAsync(ct).ConfigureAwait(false);
                    L.Info("process.parent.exited", "Parent process exited", ("parentPid", pid));
                    onExit("parent-exited");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    L.Warn("process.parent.watch_failed", "Parent watch failed; polling instead", ex);
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        try { if (Process.GetProcessById(pid).HasExited) break; }
                        catch (ArgumentException) { break; }
                    }
                    if (!ct.IsCancellationRequested) onExit("parent-exited");
                }
            });
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName; } catch { return null; }
        }
    }
}
