using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Scheduling
{
    /// <summary>§7.3 request lanes. Lower value = served first.</summary>
    public enum WorkPriority
    {
        /// <summary>The user is waiting: search, go to definition, completion.</summary>
        Interactive = 0,

        /// <summary>Visible but not blocking: file structure, colouring, highlights, single-file re-index after save.</summary>
        Visible = 1,

        /// <summary>Indexing, merging, inspections. BelowNormal threads, paused during builds.</summary>
        Background = 2,
    }

    /// <summary>
    /// Three-lane priority scheduler on dedicated worker threads (count = max parallelism, default cores-1).
    /// Workers always take Interactive before Visible before Background. Background items:
    ///  - run at <see cref="ThreadPriority.BelowNormal"/>;
    ///  - use at most <see cref="BackgroundLimit"/> workers (one worker stays free for foreground work when there are ≥ 2);
    ///  - follow <see cref="BuildPolicy"/> while <see cref="IsBuildInProgress"/> (a VS/UBT build) is set: paused, one at a
    ///    time (default) or unchanged, and run at <see cref="ThreadPriority.Lowest"/> meanwhile;
    ///  - should be small chunks that poll <see cref="ShouldYield"/> and re-schedule their remainder.
    /// Cancellation: an item whose token is cancelled before it starts is dropped (its task is cancelled); a running item
    /// receives the token. The caller's ExecutionContext (e.g. <see cref="Log.RequestId"/>) flows to the worker.
    /// </summary>
    public sealed class RequestScheduler : IDisposable
    {
        private static readonly Logger L = Log.For("Scheduler");

        private sealed class WorkItem
        {
            public string Name;
            public WorkPriority Priority;
            public Action<CancellationToken> Run;
            public CancellationToken Token;
            public CancellationTokenRegistration Registration;
            public ExecutionContext Context;
            public Func<bool> IsCompleted;
            public Action Cancel;
            public long StartTimestamp;
        }

        private readonly object _gate = new object();
        private readonly Queue<WorkItem>[] _queues = { new Queue<WorkItem>(), new Queue<WorkItem>(), new Queue<WorkItem>() };
        private readonly List<WorkItem> _running = new List<WorkItem>();
        private readonly Thread[] _workers;
        private int _runningBackground;
        private bool _buildInProgress;
        private Indexing.BuildPolicy _buildPolicy = Indexing.BuildPolicy.SingleThread;
        private bool _userPaused;
        private bool _disposed;
        private long _completed;

        public RequestScheduler(int maxParallelism = 0)
        {
            MaxParallelism = maxParallelism > 0 ? maxParallelism : Math.Max(1, Environment.ProcessorCount - 1);
            BackgroundLimit = MaxParallelism >= 2 ? MaxParallelism - 1 : 1;
            _workers = new Thread[MaxParallelism];
            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = "UEToVS worker " + i };
                _workers[i].Start();
            }
        }

        public int MaxParallelism { get; }
        public int BackgroundLimit { get; }

        public bool IsBuildInProgress
        {
            get { lock (_gate) return _buildInProgress; }
        }

        /// <summary>
        /// True while foreground work waits, background work is held (build policy, user pause) or over its build-time
        /// limit, or the scheduler shuts down: background chunks should stop and re-queue.
        /// </summary>
        public bool ShouldYield
        {
            get
            {
                lock (_gate)
                    return _queues[0].Count > 0 || _queues[1].Count > 0 || _userPaused || _disposed ||
                           (_buildInProgress && _buildPolicy == Indexing.BuildPolicy.Pause) ||
                           (_buildInProgress && _buildPolicy == Indexing.BuildPolicy.SingleThread && _runningBackground > 1);
            }
        }

        /// <summary>What the background lane does during a build (default: one item at a time).</summary>
        public Indexing.BuildPolicy BuildPolicy
        {
            get { lock (_gate) return _buildPolicy; }
            set
            {
                lock (_gate)
                {
                    if (_buildPolicy == value) return;
                    _buildPolicy = value;
                    Monitor.PulseAll(_gate);
                }
                RaiseStateChanged();
            }
        }

        /// <summary>Raised after the build flag, the user pause or the build policy changed.</summary>
        public event Action StateChanged;

        private void RaiseStateChanged()
        {
            try { StateChanged?.Invoke(); }
            catch (Exception ex) { L.Warn("scheduler.state_handler_failed", "State change handler failed", ex); }
        }

        /// <summary>Background items allowed to run at the same time right now.</summary>
        private int BackgroundAllowedLocked()
        {
            if (_userPaused) return 0;
            if (!_buildInProgress) return BackgroundLimit;
            return _buildPolicy switch
            {
                Indexing.BuildPolicy.Pause => 0,
                Indexing.BuildPolicy.SingleThread => 1,
                _ => BackgroundLimit,
            };
        }

        /// <summary>Host calls this when a VS / UBT build starts and ends; background lanes pause meanwhile.</summary>
        public void SetBuildInProgress(bool building)
        {
            lock (_gate)
            {
                if (_buildInProgress == building) return;
                _buildInProgress = building;
                Monitor.PulseAll(_gate);
            }
            L.Info(building ? "scheduler.paused" : "scheduler.resumed", building ? "Build in progress: background work throttled by the build policy" : "Build finished: background work resumed",
                ("queuedBackground", QueueLength(WorkPriority.Background)), ("policy", BuildPolicy.ToString()));
            RaiseStateChanged();
        }

        /// <summary>True when background work is held, by a build (policy Pause) or by the user (performance panel).</summary>
        public bool IsBackgroundPaused
        {
            get { lock (_gate) return BackgroundAllowedLocked() == 0; }
        }

        /// <summary>User pause from the performance panel ("暂停后台索引"); independent of builds.</summary>
        public void SetUserPaused(bool paused)
        {
            lock (_gate)
            {
                if (_userPaused == paused) return;
                _userPaused = paused;
                Monitor.PulseAll(_gate);
            }
            L.Info(paused ? "scheduler.user_paused" : "scheduler.user_resumed", null,
                ("queuedBackground", QueueLength(WorkPriority.Background)));
            RaiseStateChanged();
        }

        public bool IsUserPaused
        {
            get { lock (_gate) return _userPaused; }
        }

        public int QueueLength(WorkPriority p)
        {
            lock (_gate) return _queues[(int)p].Count;
        }

        public long CompletedCount => Interlocked.Read(ref _completed);

        /// <summary>Running items (name, lane, elapsed ms) for the performance panel.</summary>
        public List<(string Name, WorkPriority Priority, double ElapsedMs)> RunningItems()
        {
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                var list = new List<(string, WorkPriority, double)>(_running.Count);
                foreach (var w in _running) list.Add((w.Name, w.Priority, (now - w.StartTimestamp) * 1000.0 / Stopwatch.Frequency));
                return list;
            }
        }

        public Task RunAsync(WorkPriority priority, string name, Action<CancellationToken> work, CancellationToken ct = default) =>
            RunAsync<bool>(priority, name, c => { work(c); return true; }, ct);

        public Task<T> RunAsync<T>(WorkPriority priority, string name, Func<CancellationToken, T> work, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return tcs.Task;
            }
            var item = new WorkItem
            {
                Name = name,
                Priority = priority,
                Token = ct,
                Context = ExecutionContext.Capture(),
                IsCompleted = () => tcs.Task.IsCompleted,
            };
            item.Run = c =>
            {
                try { tcs.TrySetResult(work(c)); }
                catch (OperationCanceledException oce) when (c.IsCancellationRequested) { tcs.TrySetCanceled(oce.CancellationToken); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };
            item.Cancel = () => tcs.TrySetCanceled(ct);
            lock (_gate)
            {
                if (_disposed)
                {
                    tcs.TrySetException(new ObjectDisposedException(nameof(RequestScheduler)));
                    return tcs.Task;
                }
                _queues[(int)priority].Enqueue(item);
                Monitor.Pulse(_gate);
                // Pulse wakes one worker; if that one may not take background work, others must re-check.
                if (priority != WorkPriority.Background) Monitor.PulseAll(_gate);
            }
            if (ct.CanBeCanceled) item.Registration = ct.Register(() => item.Cancel());
            return tcs.Task;
        }

        private WorkItem TakeNext()
        {
            // caller holds _gate
            for (int p = 0; p < 3; p++)
            {
                var q = _queues[p];
                if (p == 2 && _runningBackground >= BackgroundAllowedLocked()) break;
                while (q.Count > 0)
                {
                    var item = q.Dequeue();
                    if (item.IsCompleted()) continue; // cancelled while queued
                    if (p == 2) _runningBackground++;
                    return item;
                }
            }
            return null;
        }

        private void WorkerLoop()
        {
            while (true)
            {
                WorkItem item;
                lock (_gate)
                {
                    while ((item = TakeNext()) == null)
                    {
                        if (_disposed) return;
                        Monitor.Wait(_gate);
                    }
                    item.StartTimestamp = Stopwatch.GetTimestamp();
                    _running.Add(item);
                }

                bool building;
                lock (_gate) building = _buildInProgress;
                SetPriority(item.Priority != WorkPriority.Background ? ThreadPriority.Normal : building ? ThreadPriority.Lowest : ThreadPriority.BelowNormal);
                try
                {
                    if (item.Context != null) ExecutionContext.Run(item.Context, _ => item.Run(item.Token), null);
                    else item.Run(item.Token);
                }
                catch (Exception ex)
                {
                    // item.Run catches everything; this is a guard against bugs in the scheduler itself.
                    L.Error("scheduler.worker.failed", "Worker failed running " + item.Name, ex);
                }
                finally
                {
                    item.Registration.Dispose();
                    Interlocked.Increment(ref _completed);
                    lock (_gate)
                    {
                        _running.Remove(item);
                        if (item.Priority == WorkPriority.Background) _runningBackground--;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
        }

        private static void SetPriority(ThreadPriority p)
        {
            try
            {
                if (Thread.CurrentThread.Priority != p) Thread.CurrentThread.Priority = p;
            }
            catch (Exception)
            {
                // not permitted on some platforms; the lane ordering still applies
            }
        }

        public void Dispose()
        {
            List<WorkItem> dropped = new List<WorkItem>();
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var q in _queues)
                {
                    dropped.AddRange(q);
                    q.Clear();
                }
                Monitor.PulseAll(_gate);
            }
            foreach (var d in dropped) d.Cancel();
            foreach (var w in _workers) w.Join(2000);
        }
    }
}
