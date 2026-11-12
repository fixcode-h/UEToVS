using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using UEToVS.Engine.Health;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Rpc;
using UEToVS.Engine.Scheduling;
using UEToVS.Engine.Search;
using UEToVS.Engine.Unreal;
using UEToVS.Engine.Vfs;

namespace UEToVS.Engine
{
    /// <summary>
    /// Composition root of the engine process: scheduler, index orchestration, search, file watching, health.
    /// Independent of transport (the pipe server and the CLI both drive it). Logging is set up by the caller.
    /// </summary>
    public sealed class EngineHost : IDisposable
    {
        private static readonly Logger L = Log.For("Engine");

        /// <summary>§9.2: Engine private bytes budget.</summary>
        public const long PrivateBytesBudget = 400L * 1024 * 1024;

        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly object _gate = new object();
        private long _indexBytesOnDisk;
        private DateTime _indexBytesSampledUtc;
        private IndexPhase _indexBytesPhase;
        private int _stopped;

        public EngineHost(EngineOptions options, ISymbolIndexBackend backend = null, IFileParser parser = null)
        {
            Options = options;
            Paths = StoragePaths.ForSolution(options.SolutionDir).EnsureCreated();
            Scheduler = new RequestScheduler(options.MaxParallelism);
            Index = new IndexOrchestrator(Paths.Index, Scheduler, backend ?? new SegmentedIndexBackend(), parser ?? new SyntaxFileParser());
            Search = new SearchService(Index);
            Changes = new FileChangeTracker(batch => Index.ApplyChangesAsync(batch, _shutdown.Token));
            Watchdog = new Watchdog { Context = () => QueueContext() };
            Performance = new PerformanceMonitor();
            Index.WorkspaceDiscovered += OnWorkspaceDiscovered;
        }

        public EngineOptions Options { get; }
        public StoragePaths Paths { get; }
        public RequestScheduler Scheduler { get; }
        public IndexOrchestrator Index { get; }
        public SearchService Search { get; }
        public FileChangeTracker Changes { get; }
        public Watchdog Watchdog { get; }
        public PerformanceMonitor Performance { get; }

        public CancellationToken ShutdownToken => _shutdown.Token;
        public string ShutdownReason { get; private set; }
        public int ExitCode { get; private set; }

        public string LogDirectory => Paths.Logs;

        public static string Version =>
            typeof(EngineHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(EngineHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        /// <summary>Number of open host connections (set by the pipe server).</summary>
        public int Connections;

        public WorkspaceOptions CurrentWorkspaceOptions { get; private set; }

        public WorkspaceOptions BuildWorkspaceOptions(string projectFile = null, string engineRoot = null) => new WorkspaceOptions
        {
            SolutionDir = Options.SolutionDir,
            ProjectFile = projectFile ?? Options.ProjectFile,
            EngineRootOverride = engineRoot ?? Options.EngineRoot,
            IncludeEngine = !Options.SkipEngine,
        };

        public Task StartIndexing()
        {
            CurrentWorkspaceOptions = BuildWorkspaceOptions();
            return Index.Start(CurrentWorkspaceOptions, _shutdown.Token);
        }

        /// <summary>Handshake supplied a different project / engine: re-run discovery and indexing.</summary>
        public void Reconfigure(string projectFile, string engineRoot)
        {
            var cur = CurrentWorkspaceOptions ?? BuildWorkspaceOptions();
            bool projectChanged = !string.IsNullOrEmpty(projectFile) && !SamePath(projectFile, Index.Workspace?.ProjectFile ?? cur.ProjectFile);
            bool engineChanged = !string.IsNullOrEmpty(engineRoot) && !SamePath(engineRoot, Index.Workspace?.EngineRoot ?? cur.EngineRootOverride);
            if (!projectChanged && !engineChanged) return;
            L.Info("workspace.reconfigure", "Host requested a different project/engine; re-indexing",
                ("project", projectFile), ("engine", engineRoot));
            CurrentWorkspaceOptions = BuildWorkspaceOptions(projectChanged ? projectFile : cur.ProjectFile, engineChanged ? engineRoot : cur.EngineRootOverride);
            _ = Index.Start(CurrentWorkspaceOptions, _shutdown.Token);
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('/', '\\'), System.IO.Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        private void OnWorkspaceDiscovered(Workspace ws)
        {
            if (Options.NoWatch) return;
            var dirs = IndexOrchestrator.WatchDirectories(ws, Options.WatchEngine && !Options.SkipEngine).ToList();
            ReplaceWatchers(dirs);
        }

        private FileChangeTracker _watchTracker;

        private void ReplaceWatchers(List<string> dirs)
        {
            // The tracker that receives host notifications (Changes) never goes away; watchers live in their own
            // tracker instance feeding the same apply function so restarts can replace them.
            lock (_gate)
            {
                _watchTracker?.Dispose();
                _watchTracker = new FileChangeTracker(batch => Index.ApplyChangesAsync(batch, _shutdown.Token));
                _watchTracker.Watch(dirs);
                L.Info("fs.watching", null, ("dirs", dirs.Count), ("watchers", _watchTracker.WatcherCount), ("engine", Options.WatchEngine && !Options.SkipEngine));
            }
        }

        public EngineStatus GetStatus()
        {
            var ws = Index.Workspace;
            var stats = Index.Backend.GetStats();
            return new EngineStatus
            {
                Phase = Index.Phase,
                FileCount = Index.Files.Count,
                SymbolCount = stats.Symbols,
                AssetCount = Index.Assets.Count,
                FilesParsed = Index.FilesParsed,
                FilesToParse = Index.FilesToParse,
                ProjectRoot = ws?.ProjectRoot,
                EngineRoot = ws?.EngineRoot,
                UnrealVersion = ws?.EngineVersion?.ToString(),
                Resources = PerformanceMonitor.Resources(IndexBytesOnDisk()),
                LastError = Index.LastError,
            };
        }

        /// <summary>Index folder size, refreshed at most every 10 s (it walks the directory) or when the phase changes.</summary>
        public long IndexBytesOnDisk()
        {
            var now = DateTime.UtcNow;
            var phase = Index.Phase;
            if ((now - _indexBytesSampledUtc).TotalSeconds > 10 || phase != _indexBytesPhase)
            {
                _indexBytesPhase = phase;
                _indexBytesOnDisk = IndexStateStore.DirectoryBytes(Paths.Index);
                _indexBytesSampledUtc = now;
            }
            return _indexBytesOnDisk;
        }

        public PerformanceSnapshot GetPerformanceSnapshot()
        {
            var snap = new PerformanceSnapshot
            {
                TakenUtc = DateTime.UtcNow,
                ProcessId = Environment.ProcessId,
                UptimeSeconds = Math.Round(Performance.UptimeSeconds, 1),
                CpuPercent = Performance.CpuPercent,
                History = Performance.History(),
                Status = GetStatus(),
                Phases = Index.Phases.Snapshot(),
                ScheduledInteractive = Scheduler.QueueLength(WorkPriority.Interactive) + Scheduler.QueueLength(WorkPriority.Visible),
                ScheduledBackground = Scheduler.QueueLength(WorkPriority.Background),
                BackgroundPaused = Scheduler.IsBackgroundPaused,
            };
            snap.Resources = snap.Status.Resources;

            snap.IndexParts.AddRange(Index.Backend.GetMemoryParts());
            long fileTable = Index.Files.Count * 360L; // ListedFile + entry + path string, estimated
            snap.IndexParts.Add(new MemoryPart { Name = "file-table", ManagedBytes = fileTable });
            snap.IndexParts.Add(new MemoryPart { Name = "asset-table", ManagedBytes = Index.Assets.Count * 300L });
            snap.IndexParts.Add(new MemoryPart { Name = "index-folder", DiskBytes = IndexBytesOnDisk() });

            foreach (var s in Log.LatencySinceStart.Snapshot())
            {
                snap.Operations.Add(new OperationStats
                {
                    Name = s.Key,
                    Count = s.Count,
                    Errors = s.Errors,
                    P50Ms = Math.Round(s.P50, 2),
                    P95Ms = Math.Round(s.P95, 2),
                    MaxMs = Math.Round(s.Max, 2),
                    BudgetMs = RpcBudgets.ForKey(s.Key),
                });
            }

            snap.Active.AddRange(Index.ActiveWork());
            foreach (var (name, priority, ms) in Scheduler.RunningItems())
            {
                if (priority == WorkPriority.Background && name.StartsWith("index.parse", StringComparison.Ordinal)) continue; // summarised above
                snap.Active.Add(new ActiveWork { Name = name, Lane = priority.ToString(), ElapsedMs = Math.Round(ms, 1) });
            }
            return snap;
        }

        /// <summary>Extra fields for the 60 s resource.sample line.</summary>
        public IEnumerable<(string Key, object Value)> ResourceExtras()
        {
            var stats = Index.Backend.GetStats();
            return new (string, object)[]
            {
                ("phase", Index.Phase.ToString()),
                ("files", Index.Files.Count),
                ("symbols", stats.Symbols),
                ("assets", Index.Assets.Count),
                ("indexManagedMB", Math.Round(stats.ManagedBytes / 1048576.0, 1)),
                ("indexDiskMB", Math.Round(IndexBytesOnDisk() / 1048576.0, 1)),
                ("cpuPct", Performance.CpuPercent),
                ("queueInteractive", Scheduler.QueueLength(WorkPriority.Interactive)),
                ("queueVisible", Scheduler.QueueLength(WorkPriority.Visible)),
                ("queueBackground", Scheduler.QueueLength(WorkPriority.Background)),
                ("buildPaused", Scheduler.IsBuildInProgress),
                ("connections", Volatile.Read(ref Connections)),
                ("inFlight", Watchdog.InFlight),
            };
        }

        private IEnumerable<(string Key, object Value)> QueueContext() => new (string, object)[]
        {
            ("queueInteractive", Scheduler.QueueLength(WorkPriority.Interactive)),
            ("queueVisible", Scheduler.QueueLength(WorkPriority.Visible)),
            ("queueBackground", Scheduler.QueueLength(WorkPriority.Background)),
            ("phase", Index.Phase.ToString()),
        };

        /// <summary>Asks the process to stop (RPC Shutdown, parent exit, Ctrl+C, SIGTERM). First reason wins.</summary>
        public void RequestShutdown(string reason, int exitCode = 0)
        {
            lock (_gate)
            {
                if (ShutdownReason != null) return;
                ShutdownReason = reason;
                ExitCode = exitCode;
            }
            L.Info("process.shutdown.requested", null, ("reason", reason), ("exitCode", exitCode));
            try { _shutdown.Cancel(); }
            catch (Exception ex) { L.Warn("process.shutdown.cancel_failed", "Cancelling work failed", ex); }
        }

        /// <summary>Graceful stop: cancel indexing, wait briefly, save the index, release watchers and threads.</summary>
        public async Task StopAsync(TimeSpan timeout)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
            RequestShutdown(ShutdownReason ?? "stop");
            try
            {
                var run = Index.RunTask;
                var done = await Task.WhenAny(run, Task.Delay(timeout)).ConfigureAwait(false);
                if (done != run) L.Warn("process.shutdown.index_timeout", "Index run did not stop in time", ("timeoutMs", timeout.TotalMilliseconds));
            }
            catch (Exception ex)
            {
                L.Warn("process.shutdown.wait_failed", "Waiting for the index run failed", ex);
            }
            Index.SaveState();
            Dispose();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _stopped, 1);
            try { _shutdown.Cancel(); } catch { }
            lock (_gate) _watchTracker?.Dispose();
            Changes.Dispose();
            Watchdog.Dispose();
            Performance.Dispose();
            Scheduler.Dispose();
            Index.Dispose();
        }
    }
}
