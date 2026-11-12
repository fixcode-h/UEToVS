using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using UEToVS.Engine.Index;
using UEToVS.Engine.Unreal;

namespace UEToVS.Mcp.Data
{
    /// <summary>
    /// The persisted index of a solution, opened read-only (<see cref="ReadOnlyIndex"/>): symbols, files and
    /// occurrences straight from the memory-mapped segments, plus an asset listing of the project's Content folders.
    /// Used when the engine is not running, and for occurrence-based answers the engine does not expose over RPC.
    /// The snapshot is reopened when manifest.json changes (checked at most once per second).
    /// </summary>
    public sealed class OfflineIndex : IDisposable
    {
        private static readonly Logger L = Log.For("Mcp.Index");
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan AssetTtl = TimeSpan.FromSeconds(60);

        private readonly object _gate = new object();
        private readonly string _symbolsDir;
        private readonly string _solutionDir;
        private Holder _current;
        private DateTime _lastCheckUtc = DateTime.MinValue;
        private DateTime _manifestTimeUtc = DateTime.MinValue;
        private Workspace _workspace;
        private DateTime _workspaceTimeUtc;
        private List<AssetItem> _assets;
        private bool _assetsIncludeEngine;
        private DateTime _assetsTimeUtc;

        public OfflineIndex(string solutionDir)
        {
            _solutionDir = solutionDir;
            // Index layout: manifest + project segments in Index/project (engine segments in Index/engine or a shared
            // cache, referenced from the manifest). Older builds used Index/symbols.
            string indexRoot = StoragePaths.ForSolution(solutionDir).Index;
            string project = Path.Combine(indexRoot, "project");
            string legacy = Path.Combine(indexRoot, "symbols");
            _symbolsDir = !Directory.Exists(project) && Directory.Exists(legacy) ? legacy : project;
        }

        public string SymbolsDirectory => _symbolsDir;

        public ReadOnlyIndexInfo Info { get; private set; }

        public DateTime ManifestTimeUtc => _manifestTimeUtc;

        /// <summary>True when an index exists on disk.</summary>
        public bool IsAvailable
        {
            get
            {
                lock (_gate)
                {
                    Refresh();
                    return _current != null;
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="query"/> on the current snapshot (kept mapped for the duration even if a newer manifest
        /// replaces it meanwhile). Returns <paramref name="fallback"/> when there is no index on disk. Materialise lazy
        /// results inside the callback.
        /// </summary>
        public T Use<T>(Func<IndexSnapshot, T> query, T fallback = default)
        {
            Holder h;
            lock (_gate)
            {
                Refresh();
                h = _current;
                if (h == null) return fallback;
                h.AddRef();
            }
            try
            {
                return query(h.Snapshot);
            }
            finally
            {
                h.Release();
            }
        }

        private void Refresh()
        {
            var now = DateTime.UtcNow;
            if (_current != null && now - _lastCheckUtc < CheckInterval) return;
            _lastCheckUtc = now;
            DateTime mtime;
            try
            {
                string manifest = ReadOnlyIndex.ManifestPath(_symbolsDir);
                mtime = File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest) : DateTime.MinValue;
            }
            catch (Exception)
            {
                mtime = DateTime.MinValue;
            }
            if (_current != null && mtime == _manifestTimeUtc) return;
            var old = _current;
            _current = null;
            _manifestTimeUtc = mtime;
            if (mtime == DateTime.MinValue)
            {
                old?.Release();
                Info = new ReadOnlyIndexInfo();
                return;
            }
            using (var scope = L.Time("mcp.index.open", 500, ("dir", _symbolsDir)))
            {
                var snap = ReadOnlyIndex.TryOpen(_symbolsDir, out var info);
                Info = info;
                if (snap != null) _current = new Holder(snap);
                L.Info("mcp.index.opened", null, ("dir", _symbolsDir), ("segments", info.SegmentsOpened), ("rejected", info.Rejected.ToArray()),
                    ("kb", info.KnowledgeBaseVersion), ("written", info.ManifestWrittenUtc), ("ms", Math.Round(scope.ElapsedMs, 1)));
            }
            old?.Release();
        }

        /// <summary>Project/engine layout (cached 60 s). Never throws; null when discovery failed.</summary>
        public Workspace Workspace(bool full = false)
        {
            lock (_gate)
            {
                if (_workspace != null && DateTime.UtcNow - _workspaceTimeUtc < AssetTtl && (!full || _workspace.ContentRoots.Count > 0 || _workspace.SourceRoots.Count > 0))
                    return _workspace;
            }
            try
            {
                var opts = new WorkspaceOptions { SolutionDir = _solutionDir };
                var ws = full ? WorkspaceDiscovery.Discover(opts) : WorkspaceDiscovery.Locate(opts);
                lock (_gate)
                {
                    _workspace = ws;
                    _workspaceTimeUtc = DateTime.UtcNow;
                }
                return ws;
            }
            catch (Exception ex)
            {
                L.Warn("mcp.workspace.failed", "Workspace discovery failed", ex, ("solutionDir", _solutionDir));
                return null;
            }
        }

        /// <summary>.uasset / .umap files under the project's content roots (and the engine's when asked). Cached 60 s.</summary>
        public IReadOnlyList<AssetItem> Assets(bool includeEngine, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_assets != null && (_assetsIncludeEngine || !includeEngine) && DateTime.UtcNow - _assetsTimeUtc < AssetTtl) return _assets;
            }
            var ws = Workspace(full: true);
            if (ws == null) return Array.Empty<AssetItem>();
            using (var scope = L.Time("mcp.assets.list", 2000, ("includeEngine", includeEngine)))
            {
                var roots = ws.ContentRoots.Where(r => r.IsProject || includeEngine).ToList();
                var list = AssetLister.List(roots, Math.Max(1, Environment.ProcessorCount / 4), ct);
                L.Info("mcp.assets.listed", null, ("roots", roots.Count), ("assets", list.Count), ("includeEngine", includeEngine), ("ms", Math.Round(scope.ElapsedMs, 1)));
                lock (_gate)
                {
                    _assets = list;
                    _assetsIncludeEngine = includeEngine;
                    _assetsTimeUtc = DateTime.UtcNow;
                }
                return list;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _current?.Release();
                _current = null;
            }
        }

        /// <summary>Reference-counted owner of one opened snapshot; the last release unmaps it.</summary>
        private sealed class Holder
        {
            private int _refs = 1;

            public Holder(IndexSnapshot snap) => Snapshot = snap;

            public IndexSnapshot Snapshot { get; }

            public void AddRef() => Interlocked.Increment(ref _refs);

            public void Release()
            {
                if (Interlocked.Decrement(ref _refs) == 0) Snapshot.Dispose();
            }
        }
    }
}
