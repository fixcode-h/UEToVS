using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index;
using UEToVS.Engine.Scheduling;
using UEToVS.Engine.Unreal;
using UEToVS.Engine.Vfs;

namespace UEToVS.Engine.Indexing
{
    /// <summary>Which C++ parts a run drops and re-parses.</summary>
    [Flags]
    public enum RebuildParts
    {
        None = 0,
        Project = 1,
        Engine = 2,
        Cpp = Project | Engine,
    }

    /// <summary>
    /// Index lifecycle: discover → list files (project, then engine) → load cache (+ shared engine cache) → parse
    /// (project, then engine; engine Public/Classes headers before other headers before .cpp, ThirdParty last) on
    /// dedicated worker threads (<see cref="CppThrottle"/>: configured count, build policy, user pause) → results are
    /// batched into base segments by a writer thread (<see cref="BulkSink"/>) → save → ready. Then applies file
    /// changes incrementally through the delta. Every phase is timed through <see cref="PhaseRecorder"/> and every
    /// parse stage reports throughput (<see cref="ThroughputRecorder"/>: index.throughput / index.done).
    /// </summary>
    public sealed class IndexOrchestrator : IDisposable
    {
        private static readonly Logger L = Log.For("Index");
        public const long MaxParseBytes = 4L * 1024 * 1024;
        private const int ChunkSize = 32;

        private readonly string _indexDir;
        private readonly RequestScheduler _scheduler;
        private readonly ISymbolIndexBackend _backend;
        private readonly IFileParser _parser;
        private readonly object _gate = new object();
        private readonly List<ThroughputRecorder> _throughput = new List<ThroughputRecorder>();
        private CancellationTokenSource _runCts;
        private Task _runTask = Task.CompletedTask;
        private CancellationTokenSource _assetCts;
        private Task _assetTask = Task.CompletedTask;
        private TaskCompletionSource<bool> _listed = NewTcs();
        private TaskCompletionSource<bool> _ready = NewTcs();
        private Dictionary<string, IndexStateStore.FileState> _cachedState;
        private List<FoundBuildCs> _buildFiles = new List<FoundBuildCs>();
        private int _runNumber;
        private volatile IndexPhase _phase = IndexPhase.NotStarted;
        private int _filesParsed;
        private int _filesToParse;
        private string _activeStage;
        private long _activeStart;
        private IndexingConfig _config;

        public IndexOrchestrator(string indexDir, RequestScheduler scheduler, ISymbolIndexBackend backend, IFileParser parser, IndexingConfig config = null)
        {
            _indexDir = indexDir;
            _scheduler = scheduler;
            _backend = backend;
            _parser = parser;
            _config = config ?? IndexingConfig.From(null);
            CppThrottle = new WorkerThrottle("cpp", _config.CppThreads);
            AssetThrottle = new WorkerThrottle("assets", _config.AssetThreads);
            CppThrottle.SetPolicy(_config.DuringBuild);
            AssetThrottle.SetPolicy(_config.DuringBuild);
        }

        public WorkspaceOptions Options { get; private set; }
        public Workspace Workspace { get; private set; }
        public ModuleCatalog Modules { get; private set; } = new ModuleCatalog();
        public FileTable Files { get; } = new FileTable();
        public AssetTable Assets { get; } = new AssetTable();
        public PhaseRecorder Phases { get; } = new PhaseRecorder();
        public ISymbolIndexBackend Backend => _backend;
        public IFileParser Parser => _parser;
        public IndexPhase Phase => _phase;
        public int FilesParsed => Volatile.Read(ref _filesParsed);
        public int FilesToParse => Volatile.Read(ref _filesToParse);
        public string LastError { get; private set; }
        public string CacheKey => $"{Workspace?.EngineBuildId ?? "none"}|{_parser.Name}:{_parser.Version}|{_backend.Name}:{_backend.FormatVersion}";

        /// <summary>C++ parse workers: configured threads, build policy, user pause.</summary>
        public WorkerThrottle CppThrottle { get; }

        /// <summary>Asset (Blueprint) scan workers.</summary>
        public WorkerThrottle AssetThrottle { get; }

        public IndexingConfig Config => Volatile.Read(ref _config);

        /// <summary>Shared engine index per the settings (null = off: the engine part stays in Index/engine).</summary>
        public SharedEngineCache SharedCache
        {
            get
            {
                var c = Config;
                return c.SharedEngineCache ? new SharedEngineCache(c.EngineCacheDirectory) : null;
            }
        }

        /// <summary>Raised on phase changes and parse progress (the broadcaster throttles it).</summary>
        public event Action StatusChanged;

        /// <summary>Raised once per run when the workspace is known (the host starts file watchers).</summary>
        public event Action<Workspace> WorkspaceDiscovered;

        private static TaskCompletionSource<bool> NewTcs() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Applies new settings: thread counts and the build policy at once (running workers adapt at their next file);
        /// excluded folders for the next listing. Returns true when the change needs a new index run (engine on/off,
        /// excluded folders) — the caller restarts.
        /// </summary>
        public bool ApplyConfig(IndexingConfig config)
        {
            var old = Interlocked.Exchange(ref _config, config);
            CppThrottle.SetConfigured(config.CppThreads);
            AssetThrottle.SetConfigured(config.AssetThreads);
            CppThrottle.SetPolicy(config.DuringBuild);
            AssetThrottle.SetPolicy(config.DuringBuild);
            FileLister.SkippedDirectories = config.ExcludedFolders?.ToList();
            bool restart = old.IncludeEngine != config.IncludeEngine || !old.SameExcludedFolders(config) ||
                           old.SharedEngineCache != config.SharedEngineCache || !string.Equals(old.EngineCacheDirectory, config.EngineCacheDirectory, StringComparison.OrdinalIgnoreCase);
            L.Info("index.settings.applied", null, ("settings", config.ToString()), ("previous", old.ToString()), ("restart", restart));
            return restart;
        }

        /// <summary>Starts (or restarts, cancelling the previous run) a full index run. <paramref name="rebuild"/> drops those C++ parts first.</summary>
        public Task Start(WorkspaceOptions options, CancellationToken shutdown, RebuildParts rebuild = RebuildParts.None)
        {
            lock (_gate)
            {
                _runCts?.Cancel();
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                Options = options;
                if (_listed.Task.IsCompleted) _listed = NewTcs();
                if (_ready.Task.IsCompleted) _ready = NewTcs();
                var previous = _runTask;
                var ct = _runCts.Token;
                int run = ++_runNumber;
                _runTask = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { /* logged by that run */ }
                    await RunAsync(options, run, rebuild, ct).ConfigureAwait(false);
                });
                return _runTask;
            }
        }

        /// <summary>
        /// Rebuild requested from the settings window. Project / Engine / Cpp (both) / All restart the run with those C++
        /// parts dropped. A running run or rebuild is cancelled and parts it had not dropped yet are carried over, so
        /// requests merge. Assets re-scans content folders (cancelling a previous asset rescan). Returns at once;
        /// progress goes through the status.
        /// </summary>
        public void Rebuild(RebuildScope scope, CancellationToken shutdown)
        {
            L.Info("index.rebuild.requested", null, ("scope", scope.ToString()), ("phase", _phase.ToString()));
            if (Options == null) return;
            var parts = scope switch
            {
                RebuildScope.Assets => RebuildParts.None,
                RebuildScope.Project => RebuildParts.Project,
                RebuildScope.Engine => RebuildParts.Engine,
                _ => RebuildParts.Cpp, // Cpp, All (every run re-lists assets too)
            };
            if (parts == RebuildParts.None)
            {
                RebuildAssets(shutdown);
                return;
            }
            lock (_gate)
            {
                parts |= _pendingRebuild; // a previous rebuild that has not dropped its parts yet
                _pendingRebuild = parts;
            }
            Start(Options, shutdown, parts);
        }

        private RebuildParts _pendingRebuild;

        public Task RebuildAssets(CancellationToken shutdown)
        {
            lock (_gate)
            {
                _assetCts?.Cancel();
                _assetCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                var ct = _assetCts.Token;
                var previous = _assetTask;
                _assetTask = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    try
                    {
                        if (!await _listed.Task.WaitAsync(ct).ConfigureAwait(false)) return;
                        var ws = Workspace;
                        var roots = ws.ContentRoots.Where(r => r.IsProject || Options.IncludeEngine).ToList();
                        var found = ScanAssets(roots, "rebuild", ct);
                        var keep = new HashSet<string>(found.Select(a => a.FilePath), StringComparer.OrdinalIgnoreCase);
                        int removed = 0;
                        foreach (var a in Assets.Snapshot())
                            if (!keep.Contains(a.FilePath) && Assets.Remove(a.FilePath)) removed++;
                        foreach (var a in found) Assets.Upsert(a);
                        L.Info("index.assets.rebuilt", null, ("assets", found.Count), ("removed", removed));
                        RaiseStatus();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        L.Info("index.assets.rebuild_cancelled", null);
                    }
                    catch (Exception ex)
                    {
                        L.Error("index.assets.rebuild_failed", "Asset rescan failed", ex);
                    }
                });
                return _assetTask;
            }
        }

        public Task<bool> WaitForReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

        public Task<bool> WaitForListingAsync(CancellationToken ct) => _listed.Task.WaitAsync(ct);

        public Task RunTask
        {
            get { lock (_gate) return _runTask; }
        }

        public Task AssetTask
        {
            get { lock (_gate) return _assetTask; }
        }

        private void SetPhase(IndexPhase p)
        {
            _phase = p;
            L.Debug("index.phase", null, ("phase", p.ToString()));
            RaiseStatus();
        }

        private void RaiseStatus()
        {
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { L.Warn("index.status.handler_failed", "Status handler failed", ex); }
        }

        private void AddThroughput(ThroughputRecorder r)
        {
            lock (_throughput)
            {
                _throughput.Insert(0, r);
                if (_throughput.Count > 8)
                {
                    _throughput[_throughput.Count - 1].Dispose();
                    _throughput.RemoveAt(_throughput.Count - 1);
                }
            }
        }

        /// <summary>Recent pipeline stages: running ones first, then most recent first.</summary>
        public List<IndexThroughput> Throughput()
        {
            lock (_throughput) return _throughput.Select(r => r.ToContract()).OrderByDescending(t => t.Running).ToList();
        }

        /// <summary>Asset scan with the asset thread count (waits while the asset throttle is paused).</summary>
        private List<AssetItem> ScanAssets(IEnumerable<IndexRoot> roots, string stage, CancellationToken ct)
        {
            var rootList = roots.ToList();
            if (rootList.Count == 0) return new List<AssetItem>();
            using var rec = new ThroughputRecorder("assets." + stage, 0, () => AssetThrottle.Effective, () => AssetThrottle.Configured);
            AddThroughput(rec);
            rec.RecordThrottled(AssetThrottle.WaitForTurn(0, ct));
            long t0 = Stopwatch.GetTimestamp();
            var assets = AssetLister.List(rootList, Math.Max(1, AssetThrottle.Effective), ct);
            long ticks = Stopwatch.GetTimestamp() - t0;
            rec.AddTotal(assets.Count);
            for (int i = 0; i < assets.Count; i++) rec.RecordCacheHit();
            rec.RecordWorker(ticks, ticks, 0);
            return assets;
        }

        private async Task RunAsync(WorkspaceOptions options, int run, RebuildParts rebuild, CancellationToken ct)
        {
            Log.RequestId = "index-" + run;
            var total = Stopwatch.StartNew();
            LastError = null;
            Interlocked.Exchange(ref _filesParsed, 0);
            Interlocked.Exchange(ref _filesToParse, 0);
            var config = Config;
            L.Info("index.run.start", null, ("run", run), ("rebuild", rebuild.ToString()), ("settings", config.ToString()), ("cores", Environment.ProcessorCount));
            try
            {
                // 1. Discover
                SetPhase(IndexPhase.DiscoveringProject);
                Workspace ws;
                using (var p = Phases.Begin(IndexPhase.DiscoveringProject))
                {
                    ws = WorkspaceDiscovery.Discover(options);
                    p.Items = ws.Plugins.Count;
                    p.Add("enabledPlugins", ws.Plugins.Count(x => x.Enabled));
                    p.Add("sourceRoots", ws.SourceRoots.Count);
                    p.Add("contentRoots", ws.ContentRoots.Count);
                    p.Add("engineRoot", ws.EngineRoot);
                    p.Add("ueVersion", ws.EngineVersion?.ToString());
                }
                foreach (var w in ws.Warnings) L.Warn("workspace.warning", w);
                Workspace = ws;
                try { WorkspaceDiscovered?.Invoke(ws); }
                catch (Exception ex) { L.Warn("workspace.handler_failed", "Workspace handler failed", ex); }
                ct.ThrowIfCancellationRequested();

                // 2. List: project first so file search works within the first second, then engine.
                SetPhase(IndexPhase.ListingFiles);
                var listed = new List<ListedFile>();
                var buildFiles = new List<FoundBuildCs>();
                // Paths only (the strings are shared with the asset table): the full AssetItem list of a big project is hundreds of MB.
                var listedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (bool project in new[] { true, false })
                {
                    if (!project && !options.IncludeEngine) break;
                    string stage = project ? "project" : "engine";
                    using var p = Phases.Begin(IndexPhase.ListingFiles, stage);
                    var roots = ws.AllListingRoots().Where(r => r.IsProject == project).ToList();
                    var descriptors = ws.DescriptorFiles.Where(d => (d.Origin == ItemOrigin.Project || d.Origin == ItemOrigin.ProjectPlugin) == project);
                    var listing = FileLister.List(ws, roots, descriptors, _scheduler.MaxParallelism, ct);
                    foreach (var f in listing.Files)
                    {
                        Files.Upsert(f);
                        listed.Add(f);
                    }
                    buildFiles.AddRange(listing.BuildCsFiles);
                    var assets = ScanAssets(ws.ContentRoots.Where(r => r.IsProject == project), stage, ct);
                    foreach (var a in assets)
                    {
                        Assets.Upsert(a);
                        listedAssets.Add(a.FilePath);
                    }
                    p.Items = listing.Files.Count;
                    p.Add("dirs", listing.Directories);
                    p.Add("cpp", listing.Files.Count(f => f.IsCpp));
                    p.Add("assets", assets.Count);
                    assets = null;
                    p.Add("modules", listing.BuildCsFiles.Count);
                    p.Timing.ItemsPerSecond = Rate(listing.Files.Count, listing.ElapsedMs);
                    RaiseStatus();
                }
                RemoveStale(listed);
                RemoveStaleAssets(listedAssets);
                listedAssets = null;
                _buildFiles = buildFiles;
                using (var p = Phases.Begin(IndexPhase.ListingFiles, "modules"))
                {
                    Modules = ModuleCatalog.Build(ws, buildFiles);
                    p.Items = Modules.Count;
                    p.Add("buildCsErrors", Modules.All.Count(m => m.Error != null));
                }
                _listed.TrySetResult(true);
                ct.ThrowIfCancellationRequested();

                // 3. Cache
                SetPhase(IndexPhase.LoadingCache);
                string key = CacheKey;
                bool sharedHit = false;
                using (var p = Phases.Begin(IndexPhase.LoadingCache))
                {
                    bool backendLoaded = false;
                    try
                    {
                        // Always give the backend the key first: a persistent backend opens (or resets) its store here.
                        bool storeLoaded = _backend.TryLoad(_indexDir, key);
                        _cachedState = storeLoaded ? IndexStateStore.TryLoad(_indexDir, key) : null;
                        backendLoaded = storeLoaded;
                        if (rebuild != RebuildParts.None)
                        {
                            bool project = (rebuild & RebuildParts.Project) != 0, engine = (rebuild & RebuildParts.Engine) != 0;
                            _backend.Reset(project, engine);
                            if (_cachedState != null)
                            {
                                foreach (var path in _cachedState.Keys.ToList())
                                {
                                    bool isProject = Files.TryGet(path, out var fe) && InMemorySymbolIndex.IsProject(fe.File.Origin);
                                    if (isProject ? project : engine) _cachedState.Remove(path);
                                }
                            }
                            L.Info("index.rebuild.cpp", "C++ index parts dropped; re-parsing them", ("project", project), ("engine", engine));
                        }
                        lock (_gate) _pendingRebuild &= ~rebuild;
                        LocalizeEngineIfUnshared(config);
                        sharedHit = TryImportSharedEngine(ws, options, config, key, rebuild);
                        SeedStateFromSegments();
                    }
                    catch (Exception ex)
                    {
                        // §10.2: a corrupt cache only costs a rebuild.
                        L.Warn("index.cache.corrupt", "Index cache unreadable; rebuilding", ex);
                        _cachedState = null;
                    }
                    p.Items = _cachedState?.Count ?? 0;
                    p.Add("loaded", backendLoaded);
                    p.Add("sharedEngine", sharedHit);
                    p.Add("rebuild", rebuild.ToString());
                    p.Add("key", key);
                }

                // 4. Parse: project, then engine; C++ files only (never .ini / .uplugin / .cs / shaders).
                SetPhase(IndexPhase.Parsing);
                var toParse = Prioritize(listed.Where(f => f.IsCpp && _parser.CanParse(Context(f))));
                Interlocked.Exchange(ref _filesToParse, toParse.Count);
                int engineParsed = 0;
                using (var sink = new BulkSink(_backend, ct))
                {
                    foreach (bool project in new[] { true, false })
                    {
                        var part = toParse.Where(f => InMemorySymbolIndex.IsProject(f.Origin) == project).ToList();
                        if (part.Count == 0) continue;
                        var c = await ParseStageAsync(part, project ? "project" : "engine", sink, ct).ConfigureAwait(false);
                        if (!project) engineParsed = c.Parsed + c.Failed;
                    }
                }
                if (_cachedState != null)
                {
                    foreach (var path in _cachedState.Keys)
                        if (!Files.TryGet(path, out _)) _backend.RemoveFile(path);
                }

                // 5. Persist
                SetPhase(IndexPhase.Merging);
                using (var p = Phases.Begin(IndexPhase.Merging, "save"))
                {
                    SaveStateCore();
                    p.Items = _backend.GetStats().Files;
                    p.Add("diskBytes", IndexStateStore.DirectoryBytes(_indexDir));
                }
                if (options.IncludeEngine && config.SharedEngineCache) ExportSharedEngine(ws, key, engineParsed);

                SetPhase(IndexPhase.Ready);
                var stats = _backend.GetStats();
                L.Info("index.ready", "Index ready", ("files", Files.Count), ("symbols", stats.Symbols), ("assets", Assets.Count),
                    ("modules", Modules.Count), ("processed", FilesParsed), ("engineParsed", engineParsed), ("sharedEngine", sharedHit),
                    ("totalMs", Math.Round(total.Elapsed.TotalMilliseconds)));
                _ready.TrySetResult(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                L.Info("index.cancelled", "Index run cancelled", ("run", run), ("phase", _phase.ToString()), ("ms", Math.Round(total.Elapsed.TotalMilliseconds)));
            }
            catch (Exception ex)
            {
                // Stable code first ("index.failed:<phase>"), then technical detail; the host localizes the code.
                LastError = $"index.failed:{_phase}: {ex.GetType().Name}: {ex.Message}";
                L.Error("index.failed", "Index run failed in phase " + _phase, ex, ("run", run));
                SetPhase(IndexPhase.Failed);
                _listed.TrySetResult(false);
                _ready.TrySetResult(false);
            }
            finally
            {
                _activeStage = null;
            }
        }

        private static double Rate(int items, double ms) => ms > 0 ? Math.Round(items * 1000.0 / ms, 1) : 0;

        private SymbolIndex Store => (_backend as SegmentedIndexBackend)?.Store;

        /// <summary>The engine part was attached from the shared cache but sharing is now off: copy it into Index/engine.</summary>
        private void LocalizeEngineIfUnshared(IndexingConfig config)
        {
            var store = Store;
            if (config.SharedEngineCache || store == null || !store.AllBasesExternal(SegmentKind.Engine)) return;
            string dir = store.Options.EngineDirectory ?? store.Options.Directory;
            var sw = Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(dir);
                var local = new List<string>();
                store.WithBaseSegments(SegmentKind.Engine, paths =>
                {
                    foreach (var p in paths)
                    {
                        string dest = Path.Combine(dir, "engine-local-" + DateTime.UtcNow.Ticks + "-" + local.Count + ".seg");
                        File.Copy(p, dest + ".tmp", true);
                        File.Move(dest + ".tmp", dest, true);
                        local.Add(dest);
                    }
                });
                if (!store.AttachExternalSegments(SegmentKind.Engine, local, out string detail))
                {
                    L.Warn("index.engine.localize_failed", "Could not use the copied engine segments; re-indexing the engine", ("detail", detail));
                    store.Reset(SegmentKind.Engine);
                }
                else L.Info("index.engine.localized", "Shared engine index copied into the solution (sharing is off)", ("segments", local.Count), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds)));
            }
            catch (Exception ex)
            {
                L.Warn("index.engine.localize_failed", "Copying the shared engine index failed; re-indexing the engine", ex);
                store.Reset(SegmentKind.Engine);
            }
        }

        private bool TryImportSharedEngine(Workspace ws, WorkspaceOptions options, IndexingConfig config, string key, RebuildParts rebuild)
        {
            var shared = SharedCache;
            var store = Store;
            if ((rebuild & RebuildParts.Engine) != 0 || !options.IncludeEngine || shared == null || store == null || string.IsNullOrEmpty(ws.EngineRoot)) return false;
            if (store.BaseSegmentCount(SegmentKind.Engine) > 0) return store.AllBasesExternal(SegmentKind.Engine); // already attached / built locally
            return shared.TryImport(store, ws.EngineBuildId, ws.EngineRoot, key);
        }

        /// <summary>
        /// Per-file state stored in the segments (size, mtime, hash at parse time): the engine part carries its own
        /// state, so a shared or rebuilt engine part needs no project-side bookkeeping.
        /// </summary>
        private void SeedStateFromSegments()
        {
            var store = Store;
            if (store == null) return;
            int added = 0;
            foreach (var kind in new[] { SegmentKind.Engine, SegmentKind.Project })
            {
                foreach (var f in store.BaseFileStates(kind, includeDead: false))
                {
                    if (f.ContentHash == 0 && f.Length == 0) continue; // written without metadata
                    _cachedState ??= new Dictionary<string, IndexStateStore.FileState>(StringComparer.OrdinalIgnoreCase);
                    if (_cachedState.TryAdd(f.Path, new IndexStateStore.FileState { Path = f.Path, Size = f.Length, Ticks = f.MtimeTicks, Hash = f.ContentHash })) added++;
                }
            }
            if (added > 0) L.Debug("index.cache.seeded", null, ("files", added));
        }

        private void ExportSharedEngine(Workspace ws, string key, int engineParsed)
        {
            var shared = SharedCache;
            var store = Store;
            if (shared == null || store == null || string.IsNullOrEmpty(ws.EngineRoot)) return;
            if (store.BaseSegmentCount(SegmentKind.Engine) == 0) return;
            if (engineParsed == 0 && store.AllBasesExternal(SegmentKind.Engine)) return; // nothing new: the shared version is current
            using (var p = Phases.Begin(IndexPhase.Merging, "shared-engine"))
                p.Add("published", shared.Export(store, ws.EngineBuildId, ws.EngineRoot, key));
        }

        /// <summary>
        /// Parse order: project before engine; within each, Public/Classes headers, then other headers, then sources;
        /// ThirdParty after everything else of its side. Stable within a rank (path order = disk locality).
        /// </summary>
        public static List<ListedFile> Prioritize(IEnumerable<ListedFile> files) =>
            files.Select((f, i) => (f, i)).OrderBy(x => Rank(x.f)).ThenBy(x => x.i).Select(x => x.f).ToList();

        public static int Rank(ListedFile f)
        {
            int r = InMemorySymbolIndex.IsProject(f.Origin) ? 0 : 10;
            if (f.Origin == ItemOrigin.ThirdParty) r += 5;
            if (f.Kind == SourceFileKind.Header)
            {
                string p = f.Path ?? "";
                bool api = p.IndexOf("/Public/", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("\\Public\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           p.IndexOf("/Classes/", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("\\Classes\\", StringComparison.OrdinalIgnoreCase) >= 0;
                return r + (api ? 0 : 1);
            }
            return r + 2;
        }

        private void RemoveStale(List<ListedFile> listed)
        {
            var keep = new HashSet<string>(listed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var e in Files.Snapshot())
            {
                if (keep.Contains(e.File.Path)) continue;
                Files.Remove(e.File.Path);
                _backend.RemoveFile(e.File.Path);
            }
        }

        private void RemoveStaleAssets(HashSet<string> keep)
        {
            foreach (var a in Assets.Snapshot())
                if (!keep.Contains(a.FilePath)) Assets.Remove(a.FilePath);
        }

        private sealed class Counters
        {
            public int Parsed, CacheHits, Unchanged, Failed, Skipped, Symbols, Yields;
        }

        private enum Outcome { Parsed, CacheHit, Unchanged, Failed, Skipped }

        private void Count(Counters c, Outcome outcome, int symbols)
        {
            switch (outcome)
            {
                case Outcome.Parsed: Interlocked.Increment(ref c.Parsed); break;
                case Outcome.CacheHit: Interlocked.Increment(ref c.CacheHits); break;
                case Outcome.Unchanged: Interlocked.Increment(ref c.Unchanged); break;
                case Outcome.Failed: Interlocked.Increment(ref c.Failed); break;
                default: Interlocked.Increment(ref c.Skipped); break;
            }
            Interlocked.Add(ref c.Symbols, symbols);
        }

        /// <summary>One bulk parse stage on the C++ worker threads; results go to <paramref name="sink"/> (segments).</summary>
        private async Task<Counters> ParseStageAsync(List<ListedFile> files, string stage, BulkSink sink, CancellationToken ct)
        {
            bool engine = stage == "engine";
            using var p = Phases.Begin(IndexPhase.Parsing, stage);
            _activeStage = "index.parse." + stage;
            _activeStart = Stopwatch.GetTimestamp();
            var c = new Counters();
            var rec = new ThroughputRecorder("parse." + stage, files.Count, () => CppThrottle.Effective, () => CppThrottle.Configured);
            AddThroughput(rec);
            sink.Recorder = rec;
            try
            {
                await ParsePipeline.RunAsync(files, CppThrottle, rec, (f, w, token) =>
                {
                    var outcome = ProcessFile(f, false, w, sink, rec, out int symbols, token);
                    Count(c, outcome, symbols);
                    if ((Interlocked.Increment(ref _filesParsed) & 63) == 0) RaiseStatus();
                }, "UEToVS index " + stage, ct).ConfigureAwait(false);
                using (Phases.Begin(IndexPhase.Merging, "segments." + stage))
                {
                    sink.Flush(engine);
                    _backend.FinishBulk(engine, ct);
                }
            }
            finally
            {
                rec.Complete();
                var t = rec.ToContract();
                p.Items = files.Count;
                p.Add("parsed", c.Parsed);
                p.Add("cacheHits", c.CacheHits + c.Unchanged);
                p.Add("parseFailed", c.Failed);
                p.Add("skipped", c.Skipped);
                p.Add("symbols", c.Symbols);
                p.Add("threads", t.ConfiguredThreads);
                p.Add("filesPerSec", t.FilesPerSecond);
                p.Add("mbPerSec", t.MegabytesPerSecond);
                p.Add("segments", t.SegmentsWritten);
                p.Timing.ItemsPerSecond = t.FilesPerSecond;
                p.Timing.MegabytesPerSecond = t.MegabytesPerSecond;
                if (ct.IsCancellationRequested) p.Complete(failed: true);
                RaiseStatus();
            }
            return c;
        }

        /// <summary>Incremental path (watcher / host changes, rescans): scheduler chunks, results through the delta.</summary>
        private async Task ProcessChunkAsync(List<ListedFile> chunk, string name, WorkPriority priority, Counters c, bool force, CancellationToken ct)
        {
            List<ListedFile> remaining = chunk;
            while (remaining.Count > 0)
            {
                var current = remaining;
                remaining = await _scheduler.RunAsync(priority, name, token =>
                {
                    for (int i = 0; i < current.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (i > 0 && priority == WorkPriority.Background && _scheduler.ShouldYield)
                        {
                            Interlocked.Increment(ref c.Yields);
                            return current.GetRange(i, current.Count - i);
                        }
                        var outcome = ProcessFile(current[i], force, null, null, null, out int symbols, token);
                        Count(c, outcome, symbols);
                    }
                    return new List<ListedFile>();
                }, ct).ConfigureAwait(false);
            }
            RaiseStatus();
        }

        private FileContext Context(ListedFile f) => new FileContext { Path = f.Path, Module = f.Module, Origin = f.Origin, Kind = f.Kind };

        private static bool IsEngineSide(ItemOrigin o) => !InMemorySymbolIndex.IsProject(o);

        /// <summary>
        /// One file: cache check (size + mtime), read (worker buffer when <paramref name="worker"/> is set), hash
        /// (unchanged content skips parsing), parse, then hand the result to <paramref name="sink"/> (bulk) or the
        /// backend's delta. Timings go to <paramref name="rec"/>.
        /// </summary>
        private Outcome ProcessFile(ListedFile f, bool force, WorkerState worker, BulkSink sink, ThroughputRecorder rec, out int symbolCount, CancellationToken ct)
        {
            symbolCount = 0;
            var entry = Files.TryGet(f.Path, out var e) ? e : Files.Upsert(f);
            var ctx = Context(f);
            if (!f.IsCpp || !_parser.CanParse(ctx))
            {
                rec?.RecordOther();
                return Outcome.Skipped;
            }
            if (f.Size > MaxParseBytes)
            {
                L.Debug("index.parse.too_large", null, ("path", f.Path), ("size", f.Size));
                rec?.RecordOther();
                return Outcome.Skipped;
            }

            IndexStateStore.FileState cached = null;
            _cachedState?.TryGetValue(f.Path, out cached);
            if (!force && cached != null && cached.Size == f.Size && cached.Ticks == f.LastWriteUtcTicks && _backend.ContainsFile(f.Path))
            {
                entry.Hash = cached.Hash;
                entry.HashKnown = true;
                rec?.RecordCacheHit();
                return Outcome.CacheHit;
            }

            long t0 = Stopwatch.GetTimestamp();
            ReadOnlyMemory<byte> content;
            try
            {
                if (worker != null)
                {
                    if (worker.ReadFile(f.Path, MaxParseBytes, out content) < 0)
                    {
                        rec?.RecordOther(Stopwatch.GetTimestamp() - t0);
                        return Outcome.Skipped;
                    }
                }
                else content = File.ReadAllBytes(f.Path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Debug("index.read_failed", ex.Message, ("path", f.Path));
                rec?.RecordOther(Stopwatch.GetTimestamp() - t0);
                return Outcome.Failed;
            }
            long t1 = Stopwatch.GetTimestamp();
            ulong hash = ContentHasher.Hash(content.Span);
            bool known = _backend.ContainsFile(f.Path);
            if (known && ((cached != null && cached.Hash == hash) || (entry.HashKnown && entry.Hash == hash)))
            {
                // Only the timestamp moved (branch switched back, touch): §9.4 skip.
                entry.Hash = hash;
                entry.HashKnown = true;
                rec?.RecordFile(f.Path, content.Length, t1 - t0, Stopwatch.GetTimestamp() - t1, 0, parsed: false);
                return force ? Outcome.Unchanged : Outcome.CacheHit;
            }

            try
            {
                var result = _parser.Parse(ctx, content, ct);
                long t2 = Stopwatch.GetTimestamp();
                var symbols = result?.Symbols ?? new List<SymbolInfo>();
                foreach (var s in symbols)
                {
                    s.Location ??= new SourceLocation();
                    s.Location.FilePath ??= f.Path;
                    s.Module ??= f.Module;
                    s.Origin = f.Origin;
                }
                if (result != null)
                {
                    result.MtimeTicks = f.LastWriteUtcTicks;
                    result.Length = content.Length;
                    result.ContentHash = hash;
                    if (sink != null) sink.Add(IsEngineSide(f.Origin), f.Path, result, content.Length);
                    else _backend.UpdateParsed(f.Path, result);
                }
                else _backend.UpdateFile(f.Path, symbols);
                entry.Hash = hash;
                entry.HashKnown = true;
                entry.SymbolCount = symbols.Count;
                symbolCount = symbols.Count;
                if (result?.Warning != null) L.Debug("index.parse.warning", result.Warning, ("path", f.Path));
                rec?.RecordFile(f.Path, content.Length, t1 - t0, t2 - t1, Stopwatch.GetTimestamp() - t2, parsed: true);
                return Outcome.Parsed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (SegmentWriteFailedException)
            {
                throw; // not this file's fault: the stage fails once (index.failed) instead of logging every remaining file
            }
            catch (Exception ex)
            {
                // §10.2: one bad file never stops the index; it stays file-only.
                L.Warn("index.parse.file_failed", "Parser failed on a file", ex, ("path", f.Path), ("size", f.Size));
                _backend.UpdateFile(f.Path, Array.Empty<SymbolInfo>());
                entry.Hash = hash;
                entry.HashKnown = true;
                rec?.RecordOther();
                return Outcome.Failed;
            }
        }


        // ---------------------------------------------------------------- incremental updates

        /// <summary>Applies coalesced file changes (watcher or host). Waits for the initial listing first.</summary>
        public async Task ApplyChangesAsync(ChangeBatch batch, CancellationToken ct)
        {
            if (!await _listed.Task.WaitAsync(ct).ConfigureAwait(false)) return;
            var ws = Workspace;
            if (ws == null) return;
            var sw = Stopwatch.StartNew();

            if (batch.NeedsRescan)
            {
                await RescanAsync(ct).ConfigureAwait(false);
                return;
            }

            var classifier = ws.CreateClassifier();
            var updates = new List<ListedFile>();
            int removed = 0, assetsChanged = 0;
            bool restart = false, modulesChanged = false;

            foreach (var change in ExpandDirectories(batch.Changes))
            {
                string path = change.FilePath;
                if (path.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase))
                {
                    if (Files.TryGet(path, out _) || change.Kind != FileChangeKind.Deleted) restart = true;
                }
                if (path.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase)) modulesChanged = true;

                if (change.Kind == FileChangeKind.Deleted || !File.Exists(path))
                {
                    if (Files.Remove(path)) { _backend.RemoveFile(path); removed++; }
                    else if (Assets.Remove(path)) assetsChanged++;
                    else if (Directory.Exists(path))
                    {
                        // A "deleted" event for a folder that still exists (rename back, atomic replace, watcher overflow
                        // replay): keep its contents; real changes below it arrive as their own events.
                        L.Debug("index.update.dir_still_exists", null, ("path", path), ("kind", change.Kind.ToString()));
                    }
                    else
                    {
                        // A deleted directory: drop everything below it.
                        int files = 0, assets = 0;
                        foreach (var p in Files.PathsUnder(path)) { Files.Remove(p); _backend.RemoveFile(p); removed++; files++; }
                        foreach (var p in Assets.PathsUnder(path)) { Assets.Remove(p); assetsChanged++; assets++; }
                        if (files + assets > 0)
                            L.Info("index.update.dir_removed", "A deleted folder took its files out of the index", ("path", path), ("files", files), ("assets", assets),
                                ("kind", change.Kind.ToString()), ("source", batch.Source));
                    }
                    continue;
                }

                if (AssetLister.IsIndexedAsset(path))
                {
                    var root = ws.ContentRoots.FirstOrDefault(r => Under(r.Path, path));
                    if (root != null) { Assets.Upsert(AssetLister.Create(path, root)); assetsChanged++; }
                    continue;
                }

                var listed = ClassifyNewFile(ws, classifier, path);
                if (listed != null)
                {
                    Files.Upsert(listed);
                    if (listed.IsCpp) updates.Add(listed); // .ini / .uplugin / .cs / shaders: file search only, never parsed
                }
            }

            if (restart)
            {
                L.Info("workspace.descriptor.changed", "Project or plugin descriptor changed: re-indexing");
                _ = Start(Options, CancellationToken.None);
                return;
            }
            if (modulesChanged) RefreshModules(ws);

            var c = new Counters();
            var priority = batch.IsBatch ? WorkPriority.Background : WorkPriority.Visible;
            var tasks = new List<Task>();
            for (int i = 0; i < updates.Count; i += ChunkSize)
                tasks.Add(ProcessChunkAsync(updates.GetRange(i, Math.Min(ChunkSize, updates.Count - i)), "index.update", priority, c, force: true, ct));
            await Task.WhenAll(tasks).ConfigureAwait(false);

            double ms = sw.Elapsed.TotalMilliseconds;
            if (!batch.IsBatch)
            {
                Log.Latency.Record("Index/index.update", ms);
                Log.LatencySinceStart.Record("Index/index.update", ms);
            }
            var level = !batch.IsBatch && ms > 500 ? LogLevel.Warn : LogLevel.Info; // §9.1: single save → 500 ms hard limit
            L.Write(level, "index.update", null, null, Math.Round(ms, 1), new (string, object)[]
            {
                ("changes", batch.Changes.Count), ("updated", updates.Count), ("parsed", c.Parsed), ("unchanged", c.Unchanged),
                ("failed", c.Failed), ("removed", removed), ("assets", assetsChanged), ("batch", batch.IsBatch), ("source", batch.Source),
            });
            if (updates.Count > 0 || removed > 0) RaiseStatus();
        }

        private IEnumerable<FileChange> ExpandDirectories(List<FileChange> changes)
        {
            foreach (var c in changes)
            {
                if (c.Kind != FileChangeKind.Deleted && Directory.Exists(c.FilePath))
                {
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(c.FilePath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
                    catch (Exception) { continue; }
                    foreach (var f in files) yield return new FileChange { FilePath = f, Kind = FileChangeKind.Created };
                    continue;
                }
                yield return c;
            }
        }

        private ListedFile ClassifyNewFile(Workspace ws, OriginClassifier classifier, string path)
        {
            if (!FileLister.TryGetKind(path, out var kind)) return null;
            var origin = classifier.Classify(path);
            if (origin == null) return null;
            bool isProject = origin == ItemOrigin.Project || origin == ItemOrigin.ProjectPlugin;
            if (!isProject && !Options.IncludeEngine) return null;
            var root = ws.AllListingRoots().FirstOrDefault(r => Under(r.Path, path));
            bool descriptor = ws.DescriptorFiles.Any(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
            if (root == null && !descriptor) return null;
            if (root != null && FileLister.IsInSkippedDirectory(root.Path, path)) return null;
            FileInfo fi;
            try { fi = new FileInfo(path); if (!fi.Exists) return null; }
            catch (Exception) { return null; }
            var module = Modules.FindForFile(path);
            return new ListedFile
            {
                Path = fi.FullName,
                Name = fi.Name,
                RelativePath = FileLister.Relative(ws, origin.Value, fi.FullName),
                Module = module?.Name,
                Plugin = root?.Plugin,
                Origin = origin.Value,
                Kind = kind,
                Size = fi.Length,
                LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
            };
        }

        private void RefreshModules(Workspace ws)
        {
            var found = new List<FoundBuildCs>();
            foreach (var e in Files.Snapshot())
            {
                if (!e.File.Name.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(new FoundBuildCs { Path = e.File.Path, Module = BuildCsModule(e.File.Name), Origin = e.File.Origin, Plugin = e.File.Plugin });
            }
            Modules = ModuleCatalog.Build(ws, found);
            L.Info("workspace.modules.refreshed", null, ("modules", Modules.Count));
        }

        private static string BuildCsModule(string fileName) => fileName.Substring(0, fileName.Length - ".Build.cs".Length);

        private static bool Under(string dir, string path) =>
            path.StartsWith(dir.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        /// <summary>Watcher overflow: re-list everything and apply the difference (size/mtime, then hash).</summary>
        private async Task RescanAsync(CancellationToken ct)
        {
            var ws = Workspace;
            using var p = Phases.Begin(IndexPhase.ListingFiles, "rescan");
            var roots = ws.AllListingRoots().Where(r => r.IsProject || Options.IncludeEngine).ToList();
            var listing = FileLister.List(ws, roots, ws.DescriptorFiles, _scheduler.MaxParallelism, ct);
            var changed = new List<ListedFile>();
            foreach (var f in listing.Files)
            {
                if (f.IsCpp && (!Files.TryGet(f.Path, out var e) || e.File.Size != f.Size || e.File.LastWriteUtcTicks != f.LastWriteUtcTicks)) changed.Add(f);
                Files.Upsert(f);
            }
            int before = Files.Count;
            RemoveStale(listing.Files);
            foreach (var a in ScanAssets(ws.ContentRoots.Where(r => r.IsProject || Options.IncludeEngine), "rescan", ct)) Assets.Upsert(a);
            var c = new Counters();
            var tasks = new List<Task>();
            for (int i = 0; i < changed.Count; i += ChunkSize)
                tasks.Add(ProcessChunkAsync(changed.GetRange(i, Math.Min(ChunkSize, changed.Count - i)), "index.rescan", WorkPriority.Background, c, force: true, ct));
            await Task.WhenAll(tasks).ConfigureAwait(false);
            p.Items = listing.Files.Count;
            p.Add("changed", changed.Count);
            p.Add("parsed", c.Parsed);
            p.Add("removed", before - Files.Count);
            RaiseStatus();
        }

        // ---------------------------------------------------------------- persistence / status

        /// <summary>Saves the index (graceful shutdown). Safe to call at any phase; never throws.</summary>
        public void SaveState()
        {
            if (Workspace == null || _phase < IndexPhase.Parsing) return;
            try
            {
                SaveStateCore();
            }
            catch (Exception ex)
            {
                L.Error("index.save.failed", "Saving the index failed", ex);
            }
        }

        private void SaveStateCore()
        {
            var sw = Stopwatch.StartNew();
            string key = CacheKey;
            _backend.Save(_indexDir, key);
            var files = Files.Snapshot().Where(e => e.HashKnown).Select(e => new IndexStateStore.FileState
            {
                Path = e.File.Path,
                Size = e.File.Size,
                Ticks = e.File.LastWriteUtcTicks,
                Hash = e.Hash,
            }).ToList();
            var stats = _backend.GetStats();
            IndexStateStore.Save(_indexDir, key, files, new
            {
                format = 1,
                key,
                engineBuildId = Workspace?.EngineBuildId,
                engineRoot = Workspace?.EngineRoot,
                projectFile = Workspace?.ProjectFile,
                parser = _parser.Name + ":" + _parser.Version,
                backend = _backend.Name + ":" + _backend.FormatVersion,
                savedUtc = DateTime.UtcNow,
                files = files.Count,
                symbols = stats.Symbols,
            });
            _cachedState = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            L.Info("index.saved", null, ("files", files.Count), ("symbols", stats.Symbols), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
        }

        public List<ActiveWork> ActiveWork()
        {
            var list = new List<ActiveWork>();
            string stage = _activeStage;
            var phase = _phase;
            if (stage != null && phase == IndexPhase.Parsing)
            {
                list.Add(new ActiveWork
                {
                    Name = stage, // stable code; the host localizes
                    Lane = WorkPriority.Background.ToString(),
                    Done = FilesParsed,
                    Total = FilesToParse,
                    ElapsedMs = (Stopwatch.GetTimestamp() - _activeStart) * 1000.0 / Stopwatch.Frequency,
                });
            }
            else if (phase != IndexPhase.Ready && phase != IndexPhase.NotStarted && phase != IndexPhase.Failed)
            {
                list.Add(new ActiveWork { Name = "index." + phase, Lane = WorkPriority.Background.ToString() });
            }
            return list;
        }

        /// <summary>Directories to watch: project roots always; engine roots only when <paramref name="includeEngine"/>.</summary>
        public static IEnumerable<string> WatchDirectories(Workspace ws, bool includeEngine)
        {
            var dirs = new List<string>();
            if (ws.ProjectRoot != null) dirs.Add(ws.ProjectRoot); // descriptors, Source, Config, Content, Plugins
            if (includeEngine)
            {
                dirs.AddRange(ws.SourceRoots.Where(r => !r.IsProject).Select(r => r.Path));
                dirs.AddRange(ws.ConfigRoots.Where(r => !r.IsProject).Select(r => r.Path));
            }
            return dirs;
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _runCts?.Cancel();
                _assetCts?.Cancel();
            }
        }

        public void Dispose()
        {
            Cancel();
            _backend.Dispose();
        }
    }
}
