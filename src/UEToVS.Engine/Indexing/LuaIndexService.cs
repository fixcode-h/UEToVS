using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index.Lua;
using UEToVS.Engine.Vfs;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// The Lua script index of one solution: folders from <see cref="IndexingSettings.LuaScriptPaths"/>, parsed by
    /// <see cref="LuaParser"/>, persisted to &lt;solution&gt;\.vs\UEToVS\Lua\lua-index.json (the MCP server reads that file).
    /// Kept up to date like the C++ index: the first scan reuses the saved index for files whose size and timestamp did
    /// not change (and skips re-parsing files whose content hash is unchanged); afterwards a file watcher on the Lua
    /// folders plus host save notifications update only the touched files. <see cref="Rebuild"/> (settings window
    /// "索引 Lua 脚本") drops everything and re-parses. All work runs on one background lane, off the RPC threads.
    /// </summary>
    public sealed class LuaIndexService : IDisposable
    {
        private static readonly Logger L = Log.For("Index.Lua");

        private readonly string _indexFile;
        private readonly string _solutionDir;
        private readonly bool _watch;
        private readonly CancellationToken _shutdown;
        private readonly SemaphoreSlim _lane = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object();
        private readonly FileChangeTracker _hostChanges;
        private FileChangeTracker _watcher;
        private CancellationTokenSource _scanCts;
        private Task _scanTask = Task.CompletedTask;
        private List<string> _configured;
        private string _projectRoot;
        private List<string> _roots = new List<string>();
        private bool _configuredOnce;
        private LuaIndexData _data;
        private int _disposed;

        public LuaIndexService(string luaDirectory, string solutionDir, bool watch, CancellationToken shutdown)
        {
            _indexFile = LuaIndexStore.PathIn(luaDirectory);
            _solutionDir = solutionDir;
            _watch = watch;
            _shutdown = shutdown;
            _hostChanges = new FileChangeTracker(ApplyBatchAsync, LuaIndexer.IsLuaFile);
        }

        public event Action StatusChanged;

        public string IndexFile => _indexFile;
        public LuaIndexState State { get; private set; }
        public string LastError { get; private set; }
        public LuaScanStats LastScan { get; private set; }
        public LuaIndexData Data => Volatile.Read(ref _data);
        public IReadOnlyList<string> Roots { get { lock (_gate) return _roots.ToList(); } }

        /// <summary>The current scan (tests wait on it).</summary>
        public Task ScanTask { get { lock (_gate) return _scanTask; } }

        public static int Parallelism => Math.Max(1, Math.Min(4, Environment.ProcessorCount / 4));

        /// <summary>Settings (initial command line or the host's push): (re)scan when the resolved folders changed.</summary>
        public void Configure(IEnumerable<string> luaScriptPaths, string reason)
        {
            lock (_gate)
            {
                _configured = luaScriptPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
                _configuredOnce = true;
            }
            Resolve(reason, force: false);
        }

        /// <summary>Workspace discovery found the .uproject folder: relative Lua paths resolve against it.</summary>
        public void SetProjectRoot(string projectRoot)
        {
            lock (_gate) _projectRoot = projectRoot;
            Resolve("workspace", force: false);
        }

        /// <summary>Drop the Lua index and re-parse every file. Returns at once; progress via the status.</summary>
        public void Rebuild()
        {
            L.Info("lua.index.rebuild.requested", null, ("roots", Roots.Count));
            Resolve("rebuild", force: true);
        }

        /// <summary>Host notifications (document saved, files renamed in Solution Explorer); non-Lua paths are ignored.</summary>
        public void NotifyChanges(IEnumerable<FileChange> changes)
        {
            if (changes == null) return;
            var lua = changes.Where(c => c != null && LuaIndexer.IsLuaFile(c.FilePath)).ToList();
            if (lua.Count > 0) _hostChanges.Enqueue(lua, "host");
        }

        private void Resolve(string reason, bool force)
        {
            List<string> roots;
            lock (_gate)
            {
                if (!_configuredOnce || Volatile.Read(ref _disposed) == 1) return;
                roots = LuaIndexer.ResolveRoots(_configured, _solutionDir, _projectRoot);
                bool same = roots.SequenceEqual(_roots, StringComparer.OrdinalIgnoreCase);
                if (same && !force && (_data != null || State == LuaIndexState.Indexing)) return;
                _roots = roots;
            }
            if (roots.Count == 0)
            {
                Disable(reason);
                return;
            }
            StartScan(roots, rebuild: force, reason);
        }

        private void Disable(string reason)
        {
            lock (_gate)
            {
                _scanCts?.Cancel();
                _watcher?.Dispose();
                _watcher = null;
            }
            bool had = false;
            try
            {
                had = File.Exists(_indexFile);
                LuaIndexStore.Delete(_indexFile);
            }
            catch (Exception ex)
            {
                L.Warn("lua.index.delete_failed", "Could not delete the Lua index file", ex, ("file", _indexFile));
            }
            Volatile.Write(ref _data, new LuaIndexData());
            State = LuaIndexState.Disabled;
            LastError = null;
            L.Info("lua.index.disabled", "No Lua script path configured", ("reason", reason), ("deletedFile", had));
            Signal();
        }

        private void StartScan(List<string> roots, bool rebuild, string reason)
        {
            lock (_gate)
            {
                _scanCts?.Cancel();
                _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
                var ct = _scanCts.Token;
                var previous = _scanTask;
                State = LuaIndexState.Indexing;
                _scanTask = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    await RunScanAsync(roots, rebuild, reason, ct).ConfigureAwait(false);
                });
            }
            Signal();
        }

        private async Task RunScanAsync(List<string> roots, bool rebuild, string reason, CancellationToken ct)
        {
            try
            {
                await _lane.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            try
            {
                LuaIndexData previous = null;
                if (!rebuild)
                {
                    previous = Data;
                    if (previous == null || previous.Files.Count == 0) previous = LoadSaved();
                }
                var stats = new LuaScanStats();
                L.Info("lua.index.scan.start", null, ("reason", reason), ("rebuild", rebuild), ("roots", string.Join(";", roots)),
                    ("cached", previous?.Files.Count ?? 0));
                var data = LuaIndexer.Scan(roots, previous, stats, Parallelism, ct);
                ct.ThrowIfCancellationRequested();
                Publish(data);
                LastScan = stats;
                LastError = stats.FirstError;
                State = LuaIndexState.Ready;
                foreach (var r in roots.Where(r => !Directory.Exists(r)))
                    L.Warn("lua.index.root_missing", "Lua script folder does not exist (it is watched once created on the next scan)", ("root", r));
                L.Info("lua.index.scan.done", null, ("files", stats.Files), ("symbols", stats.Symbols), ("parsed", stats.Parsed), ("reused", stats.Reused),
                    ("unchanged", stats.Unchanged), ("removed", stats.Removed), ("failed", stats.Failed), ("ms", Math.Round(stats.ElapsedMs, 1)), ("rebuild", rebuild));
                ReplaceWatcher(roots);
            }
            catch (OperationCanceledException)
            {
                L.Debug("lua.index.scan.cancelled", null, ("reason", reason));
            }
            catch (Exception ex)
            {
                State = LuaIndexState.Failed;
                LastError = ex.GetType().Name + ": " + ex.Message;
                L.Error("lua.index.scan.failed", "Lua index scan failed", ex, ("reason", reason));
            }
            finally
            {
                _lane.Release();
                Signal();
            }
        }

        private LuaIndexData LoadSaved()
        {
            try
            {
                var saved = LuaIndexStore.Load(_indexFile);
                if (saved != null) L.Debug("lua.index.loaded", null, ("files", saved.Files.Count), ("writtenUtc", saved.WrittenUtc.ToString("o")));
                return saved;
            }
            catch (Exception ex)
            {
                L.Warn("lua.index.load_failed", "Saved Lua index is unreadable; re-parsing everything", ex, ("file", _indexFile));
                return null;
            }
        }

        private void Publish(LuaIndexData data)
        {
            Volatile.Write(ref _data, data);
            try
            {
                LuaIndexStore.Save(_indexFile, data);
            }
            catch (Exception ex)
            {
                L.Warn("lua.index.save_failed", "Could not write the Lua index file (MCP sees the previous one)", ex, ("file", _indexFile));
            }
        }

        private void ReplaceWatcher(List<string> roots)
        {
            if (!_watch) return;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 1) return;
                _watcher?.Dispose();
                _watcher = new FileChangeTracker(ApplyBatchAsync, path => LuaIndexer.IsLuaFile(path) || string.IsNullOrEmpty(Path.GetExtension(path)));
                _watcher.Watch(roots);
                L.Info("lua.fs.watching", null, ("dirs", roots.Count), ("watchers", _watcher.WatcherCount));
            }
        }

        /// <summary>Watcher / host batch: update just those files (a lost-events overflow re-scans incrementally).</summary>
        private async Task ApplyBatchAsync(ChangeBatch batch)
        {
            if (State == LuaIndexState.Disabled || Data == null) return;
            if (batch.NeedsRescan)
            {
                L.Info("lua.index.rescan", "Watcher overflow: incremental re-scan", ("source", batch.Source));
                StartScan(Roots.ToList(), rebuild: false, "overflow");
                return;
            }
            try
            {
                await _lane.WaitAsync(_shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            try
            {
                var current = Data;
                if (current == null || State == LuaIndexState.Disabled) return;
                var stats = new LuaScanStats();
                var data = LuaIndexer.Apply(current, batch.Changes.Select(c => c.FilePath), stats, _shutdown);
                if (stats.Parsed + stats.Removed + stats.Unchanged == 0)
                {
                    L.Trace("lua.index.update.noop", null, ("changes", batch.Changes.Count), ("source", batch.Source));
                    return;
                }
                Publish(data);
                L.Info("lua.index.update", null, ("changes", batch.Changes.Count), ("parsed", stats.Parsed), ("unchanged", stats.Unchanged),
                    ("removed", stats.Removed), ("files", stats.Files), ("symbols", stats.Symbols), ("ms", Math.Round(stats.ElapsedMs, 1)), ("source", batch.Source));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                L.Error("lua.index.update.failed", "Applying Lua file changes failed", ex, ("changes", batch.Changes.Count));
            }
            finally
            {
                _lane.Release();
                Signal();
            }
        }

        /// <summary>Flushes pending host/watcher changes now (tests).</summary>
        public async Task FlushAsync()
        {
            await _hostChanges.FlushAsync().ConfigureAwait(false);
            FileChangeTracker w;
            lock (_gate) w = _watcher;
            if (w != null) await w.FlushAsync().ConfigureAwait(false);
        }

        public void FillStatus(EngineStatus status)
        {
            var data = Data;
            status.LuaState = State;
            status.LuaFileCount = data?.Files.Count ?? 0;
            status.LuaSymbolCount = data?.SymbolCount ?? 0;
            var roots = Roots;
            status.LuaRoots = roots.Count == 0 ? null : roots.ToList();
            status.LuaLastError = LastError;
        }

        private void Signal()
        {
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { L.Warn("lua.index.status_handler_failed", null, ex); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            lock (_gate)
            {
                _scanCts?.Cancel();
                _watcher?.Dispose();
                _watcher = null;
            }
            _hostChanges.Dispose();
        }
    }
}
