using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Scheduling;
using UEToVS.Engine.Unreal;
using UEToVS.Engine.Vfs;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// Index lifecycle: discover → list files (project, then engine) → load cache → parse (project, then engine, in
    /// background chunks that yield to interactive requests) → save → ready. Then applies file changes incrementally.
    /// Every phase is timed (wall, CPU, items) through <see cref="PhaseRecorder"/>.
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
        private CancellationTokenSource _runCts;
        private Task _runTask = Task.CompletedTask;
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

        public IndexOrchestrator(string indexDir, RequestScheduler scheduler, ISymbolIndexBackend backend, IFileParser parser)
        {
            _indexDir = indexDir;
            _scheduler = scheduler;
            _backend = backend;
            _parser = parser;
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

        /// <summary>Raised on phase changes and parse progress (the broadcaster throttles it).</summary>
        public event Action StatusChanged;

        /// <summary>Raised once per run when the workspace is known (the host starts file watchers).</summary>
        public event Action<Workspace> WorkspaceDiscovered;

        private static TaskCompletionSource<bool> NewTcs() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Starts (or restarts, cancelling the previous run) a full index run.</summary>
        public Task Start(WorkspaceOptions options, CancellationToken shutdown)
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
                    await RunAsync(options, run, ct).ConfigureAwait(false);
                });
                return _runTask;
            }
        }

        public Task<bool> WaitForReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

        public Task<bool> WaitForListingAsync(CancellationToken ct) => _listed.Task.WaitAsync(ct);

        public Task RunTask
        {
            get { lock (_gate) return _runTask; }
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

        private async Task RunAsync(WorkspaceOptions options, int run, CancellationToken ct)
        {
            Log.RequestId = "index-" + run;
            var total = Stopwatch.StartNew();
            LastError = null;
            Interlocked.Exchange(ref _filesParsed, 0);
            Interlocked.Exchange(ref _filesToParse, 0);
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
                    var assets = AssetLister.List(ws.ContentRoots.Where(r => r.IsProject == project), _scheduler.MaxParallelism, ct);
                    foreach (var a in assets) Assets.Upsert(a);
                    p.Items = listing.Files.Count;
                    p.Add("dirs", listing.Directories);
                    p.Add("assets", assets.Count);
                    p.Add("modules", listing.BuildCsFiles.Count);
                    RaiseStatus();
                }
                RemoveStale(listed);
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
                using (var p = Phases.Begin(IndexPhase.LoadingCache))
                {
                    bool backendLoaded = false;
                    try
                    {
                        _cachedState = IndexStateStore.TryLoad(_indexDir, key);
                        backendLoaded = _cachedState != null && _backend.TryLoad(_indexDir, key);
                        if (!backendLoaded) _cachedState = null;
                    }
                    catch (Exception ex)
                    {
                        // §10.2: a corrupt cache only costs a rebuild.
                        L.Warn("index.cache.corrupt", "Index cache unreadable; rebuilding", ex);
                        _cachedState = null;
                    }
                    p.Items = _cachedState?.Count ?? 0;
                    p.Add("loaded", backendLoaded);
                    p.Add("key", key);
                }

                // 4. Parse: project, then engine.
                SetPhase(IndexPhase.Parsing);
                var toParse = listed.Where(f => _parser.CanParse(Context(f))).ToList();
                Interlocked.Exchange(ref _filesToParse, toParse.Count);
                foreach (bool project in new[] { true, false })
                {
                    var part = toParse.Where(f => InMemorySymbolIndex.IsProject(f.Origin) == project).ToList();
                    if (part.Count == 0) continue;
                    await ParseFilesAsync(part, project ? "project" : "engine", ct).ConfigureAwait(false);
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

                SetPhase(IndexPhase.Ready);
                var stats = _backend.GetStats();
                L.Info("index.ready", "Index ready", ("files", Files.Count), ("symbols", stats.Symbols), ("assets", Assets.Count),
                    ("modules", Modules.Count), ("processed", FilesParsed), ("totalMs", Math.Round(total.Elapsed.TotalMilliseconds)));
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

        private sealed class Counters
        {
            public int Parsed, CacheHits, Unchanged, Failed, Skipped, Symbols, Yields;
        }

        private enum Outcome { Parsed, CacheHit, Unchanged, Failed, Skipped }

        private async Task ParseFilesAsync(List<ListedFile> files, string stage, CancellationToken ct)
        {
            using var p = Phases.Begin(IndexPhase.Parsing, stage);
            _activeStage = "index.parse." + stage;
            _activeStart = Stopwatch.GetTimestamp();
            var c = new Counters();
            var tasks = new List<Task>();
            for (int i = 0; i < files.Count; i += ChunkSize)
            {
                var chunk = files.GetRange(i, Math.Min(ChunkSize, files.Count - i));
                tasks.Add(ProcessChunkAsync(chunk, "index.parse." + stage, WorkPriority.Background, c, force: false, ct));
            }
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                p.Items = files.Count;
                p.Add("parsed", c.Parsed);
                p.Add("cacheHits", c.CacheHits + c.Unchanged);
                p.Add("parseFailed", c.Failed);
                p.Add("skipped", c.Skipped);
                p.Add("symbols", c.Symbols);
                p.Add("yields", c.Yields);
                if (ct.IsCancellationRequested) p.Complete(failed: true);
            }
        }

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
                        var outcome = ProcessFile(current[i], force, out int symbols, token);
                        switch (outcome)
                        {
                            case Outcome.Parsed: Interlocked.Increment(ref c.Parsed); break;
                            case Outcome.CacheHit: Interlocked.Increment(ref c.CacheHits); break;
                            case Outcome.Unchanged: Interlocked.Increment(ref c.Unchanged); break;
                            case Outcome.Failed: Interlocked.Increment(ref c.Failed); break;
                            default: Interlocked.Increment(ref c.Skipped); break;
                        }
                        Interlocked.Add(ref c.Symbols, symbols);
                        Interlocked.Increment(ref _filesParsed);
                    }
                    return new List<ListedFile>();
                }, ct).ConfigureAwait(false);
            }
            RaiseStatus();
        }

        private FileContext Context(ListedFile f) => new FileContext { Path = f.Path, Module = f.Module, Origin = f.Origin, Kind = f.Kind };

        private Outcome ProcessFile(ListedFile f, bool force, out int symbolCount, CancellationToken ct)
        {
            symbolCount = 0;
            var entry = Files.TryGet(f.Path, out var e) ? e : Files.Upsert(f);
            var ctx = Context(f);
            if (!_parser.CanParse(ctx)) return Outcome.Skipped;
            if (f.Size > MaxParseBytes)
            {
                L.Debug("index.parse.too_large", null, ("path", f.Path), ("size", f.Size));
                return Outcome.Skipped;
            }

            IndexStateStore.FileState cached = null;
            _cachedState?.TryGetValue(f.Path, out cached);
            if (!force && cached != null && cached.Size == f.Size && cached.Ticks == f.LastWriteUtcTicks && _backend.ContainsFile(f.Path))
            {
                entry.Hash = cached.Hash;
                entry.HashKnown = true;
                return Outcome.CacheHit;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(f.Path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Debug("index.read_failed", ex.Message, ("path", f.Path));
                return Outcome.Failed;
            }
            ulong hash = ContentHasher.Hash(bytes);
            bool known = _backend.ContainsFile(f.Path);
            if (known && ((cached != null && cached.Hash == hash) || (entry.HashKnown && entry.Hash == hash)))
            {
                // Only the timestamp moved (branch switched back, touch): §9.4 skip.
                entry.Hash = hash;
                entry.HashKnown = true;
                return force ? Outcome.Unchanged : Outcome.CacheHit;
            }

            try
            {
                var result = _parser.Parse(ctx, bytes, ct);
                var symbols = result?.Symbols ?? new List<SymbolInfo>();
                foreach (var s in symbols)
                {
                    s.Location ??= new SourceLocation();
                    s.Location.FilePath ??= f.Path;
                    s.Module ??= f.Module;
                    s.Origin = f.Origin;
                }
                _backend.UpdateFile(f.Path, symbols);
                entry.Hash = hash;
                entry.HashKnown = true;
                entry.SymbolCount = symbols.Count;
                symbolCount = symbols.Count;
                if (result?.Warning != null) L.Debug("index.parse.warning", result.Warning, ("path", f.Path));
                return Outcome.Parsed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // §10.2: one bad file never stops the index; it stays file-only.
                L.Warn("index.parse.file_failed", "Parser failed on a file", ex, ("path", f.Path), ("size", f.Size));
                _backend.UpdateFile(f.Path, Array.Empty<SymbolInfo>());
                entry.Hash = hash;
                entry.HashKnown = true;
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
                    else
                    {
                        // A deleted directory: drop everything below it.
                        foreach (var p in Files.PathsUnder(path)) { Files.Remove(p); _backend.RemoveFile(p); removed++; }
                        foreach (var p in Assets.PathsUnder(path)) { Assets.Remove(p); assetsChanged++; }
                    }
                    continue;
                }

                if (AssetLister.IsAsset(path))
                {
                    var root = ws.ContentRoots.FirstOrDefault(r => Under(r.Path, path));
                    if (root != null) { Assets.Upsert(AssetLister.Create(path, root)); assetsChanged++; }
                    continue;
                }

                var listed = ClassifyNewFile(ws, classifier, path);
                if (listed != null)
                {
                    Files.Upsert(listed);
                    updates.Add(listed);
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
                if (!Files.TryGet(f.Path, out var e) || e.File.Size != f.Size || e.File.LastWriteUtcTicks != f.LastWriteUtcTicks) changed.Add(f);
                Files.Upsert(f);
            }
            int before = Files.Count;
            RemoveStale(listing.Files);
            foreach (var a in AssetLister.List(ws.ContentRoots.Where(r => r.IsProject || Options.IncludeEngine), _scheduler.MaxParallelism, ct)) Assets.Upsert(a);
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
            lock (_gate) _runCts?.Cancel();
        }

        public void Dispose()
        {
            Cancel();
            _backend.Dispose();
        }
    }
}
