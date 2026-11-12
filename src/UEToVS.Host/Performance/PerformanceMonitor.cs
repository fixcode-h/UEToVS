using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Engine;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Performance;

namespace UEToVS.Host.Performance
{
    /// <summary>
    /// Feeds the performance panel (§9.8). Polls only while at least one panel is visible (<see cref="Start"/>/<see cref="Stop"/>
    /// are reference counted), once per second, on a background timer: engine GetPerformanceSnapshotAsync (2 s timeout)
    /// plus a host sample (devenv private bytes / working set, managed heap, GC counts) and host operation latency from
    /// Log.LatencySinceStart. Ticks never overlap; nothing runs on the UI thread here.
    /// </summary>
    internal sealed class PerformanceMonitor : IDisposable
    {
        private static readonly Logger L = Log.For("Performance");
        private readonly Func<EngineClient> _engine;
        private readonly SampleHistory<HostResourceSample> _history = new SampleHistory<HostResourceSample>(600);
        private readonly object _gate = new object();
        private Timer _timer;
        private int _subscribers;
        private int _ticking;
        private int _engineFailures;

        public PerformanceMonitor(Func<EngineClient> engine)
        {
            _engine = engine;
            PrivateBytesAtLoad = SafePrivateBytes();
        }

        private TimeSpan _interval = TimeSpan.FromSeconds(1);

        /// <summary>Sampling period (settings: 性能 > 采样间隔); a running timer switches to the new period at once.</summary>
        public TimeSpan Interval
        {
            get { lock (_gate) return _interval; }
            set
            {
                if (value < TimeSpan.FromMilliseconds(250)) value = TimeSpan.FromMilliseconds(250);
                lock (_gate)
                {
                    if (_interval == value) return;
                    _interval = value;
                    _timer?.Change(value, value);
                }
                L.Info("perf.monitor.interval", null, ("intervalMs", value.TotalMilliseconds));
            }
        }

        /// <summary>devenv private bytes when the package loaded (baseline for the "since load" delta).</summary>
        public long PrivateBytesAtLoad { get; }

        public PerformanceData Last { get; private set; }

        /// <summary>Raised on a background thread after each tick.</summary>
        public event EventHandler<PerformanceData> Updated;

        public bool IsRunning
        {
            get { lock (_gate) return _timer != null; }
        }

        public void Start()
        {
            lock (_gate)
            {
                _subscribers++;
                if (_timer != null) return;
                _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.Zero, Interval);
            }
            L.Info("perf.monitor.start", null, ("intervalMs", Interval.TotalMilliseconds));
        }

        public void Stop()
        {
            Timer t;
            lock (_gate)
            {
                if (_subscribers > 0) _subscribers--;
                if (_subscribers > 0 || _timer == null) return;
                t = _timer;
                _timer = null;
            }
            t.Dispose();
            L.Info("perf.monitor.stop", null);
        }

        /// <summary>One sample now (also used by the diagnostics package).</summary>
        public async Task<PerformanceData> SampleAsync(CancellationToken ct)
        {
            var data = new PerformanceData { TakenUtc = DateTime.UtcNow, Host = SampleHost() };
            _history.Add(data.Host);
            data.HostHistory = _history.ToArray();
            data.HostOperations = PerformanceDataBuilder.FromLatency(Log.LatencySinceStart.Snapshot());

            var engine = _engine?.Invoke();
            data.EngineState = engine?.State.ToString() ?? "None";
            if (engine != null && engine.IsConnected)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    data.Engine = await engine.InvokeAsync("getPerformanceSnapshot", (s, c) => s.GetPerformanceSnapshotAsync(c), ct,
                                                           TimeSpan.FromSeconds(2), 100).ConfigureAwait(false);
                    _engineFailures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    data.EngineError = ex.Message;
                    // Log the first failure of a streak only (polling every second would flood the log).
                    if (Interlocked.Increment(ref _engineFailures) == 1)
                        L.Warn("perf.engine.snapshot.failed", "Engine performance snapshot failed", ex);
                }
                data.EngineRoundTripMs = sw.Elapsed.TotalMilliseconds;
            }
            else
            {
                data.EngineError = engine == null ? Strings.PerfEngineNotStarted : engine.LastError ?? Strings.PerfEngineState(engine.State.ToString());
            }
            Last = data;
            return data;
        }

        private async Task TickAsync()
        {
            if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    var data = await SampleAsync(cts.Token).ConfigureAwait(false);
                    Updated?.Invoke(this, data);
                }
            }
            catch (Exception ex)
            {
                L.Error("perf.monitor.tick.failed", "Performance sampling failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _ticking, 0);
            }
        }

        private HostResourceSample SampleHost()
        {
            var s = new HostResourceSample
            {
                TakenUtc = DateTime.UtcNow,
                ManagedHeapBytes = GC.GetTotalMemory(false),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                PrivateBytesAtLoad = PrivateBytesAtLoad,
            };
            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    s.PrivateBytes = p.PrivateMemorySize64;
                    s.WorkingSetBytes = p.WorkingSet64;
                    s.ThreadCount = p.Threads.Count;
                }
            }
            catch (Exception ex)
            {
                L.Debug("perf.host.sample.failed", ex.Message);
            }
            return s;
        }

        private static long SafePrivateBytes()
        {
            try
            {
                using (var p = Process.GetCurrentProcess()) return p.PrivateMemorySize64;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _subscribers = 0;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
