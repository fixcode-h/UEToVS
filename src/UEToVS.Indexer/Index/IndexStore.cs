using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Protocol;
using UEToVS.Indexer.Parsing;
using UEToVS.Indexer.Unreal;

namespace UEToVS.Indexer.Index
{
    /// <summary>Immutable view used by queries; rebuilt after changes.</summary>
    internal sealed class Snapshot
    {
        public FileRecord[] Files = Array.Empty<FileRecord>();
        public FileRecord[] SymbolFiles = Array.Empty<FileRecord>(); // parallel to Symbols
        public ScannedSymbol[] Symbols = Array.Empty<ScannedSymbol>();
        public string[] SymbolNamesLower = Array.Empty<string>();
        public AssetDto[] Assets = Array.Empty<AssetDto>();
        public string[] AssetNamesLower = Array.Empty<string>();
        public Dictionary<string, List<int>> SymbolsByName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        public Dictionary<string, List<FileRecord>> FilesByName = new Dictionary<string, List<FileRecord>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> BuildCsByModule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ItemOrigin> ModuleOrigin = new Dictionary<string, ItemOrigin>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Owns all indexed files, keeps them fresh and serves queries.</summary>
    internal sealed class IndexStore : IDisposable
    {
        private readonly ConcurrentDictionary<string, FileRecord> _files = new ConcurrentDictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, AssetDto> _assets = new ConcurrentDictionary<string, AssetDto>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly ConcurrentDictionary<string, byte> _dirty = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly object _rebuildLock = new object();
        private Timer _debounce;
        private volatile Snapshot _snapshot = new Snapshot();
        private int _filesParsed;
        private int _filesToParse;

        public IndexStore(ProjectLayout layout, string cacheDir)
        {
            Layout = layout;
            CacheDir = cacheDir;
        }

        public ProjectLayout Layout { get; }
        public string CacheDir { get; }
        public Snapshot Current => _snapshot;
        public bool FilesReady { get; private set; }
        public bool SymbolsReady { get; private set; }
        public int FilesParsed => _filesParsed;
        public int FilesToParse => _filesToParse;

        public event Action SnapshotChanged;

        public async Task BuildAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            // 1) file lists (fast) -> file search usable immediately
            var roots = Layout.SourceRoots.Concat(Layout.ConfigRoots).ToList();
            Parallel.ForEach(roots, new ParallelOptions { CancellationToken = ct }, root =>
            {
                foreach (var rec in FileScanner.Scan(root)) _files[rec.Path] = rec;
            });
            Parallel.ForEach(Layout.ContentRoots, new ParallelOptions { CancellationToken = ct }, root =>
            {
                foreach (var a in FileScanner.ScanAssets(root)) AddAsset(root, a);
            });
            FilesReady = true;
            Rebuild();
            Log.Info($"Listed {_files.Count} files and {_assets.Count} assets in {sw.ElapsedMilliseconds} ms");

            // 2) cached symbols
            var engineCache = EngineCacheFile();
            var projectCache = ProjectCacheFile();
            var cached = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in new[] { engineCache, projectCache })
                foreach (var kv in IndexCache.Load(file)) cached[kv.Key] = kv.Value;

            var toParse = new List<FileRecord>();
            foreach (var rec in _files.Values)
            {
                if (!rec.Parseable) continue;
                if (cached.TryGetValue(rec.Path, out var c) && c.MtimeTicks == rec.MtimeTicks && c.Length == rec.Length)
                    rec.Symbols = c.Symbols;
                else
                    toParse.Add(rec);
            }
            Log.Info($"Cache hits: {_files.Values.Count(r => r.Parseable) - toParse.Count}, to parse: {toParse.Count}");
            if (toParse.Count < _files.Count) Rebuild();

            // 3) parse the rest, project files first
            _filesToParse = toParse.Count;
            toParse.Sort((a, b) => (b.IsProject ? 1 : 0).CompareTo(a.IsProject ? 1 : 0));
            int lastRebuild = Environment.TickCount;
            await Task.Run(() =>
            {
                Parallel.ForEach(toParse, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, rec =>
                {
                    rec.Symbols = ParseFile(rec.Path);
                    int done = Interlocked.Increment(ref _filesParsed);
                    if (Environment.TickCount - lastRebuild > 5000)
                    {
                        lastRebuild = Environment.TickCount;
                        Rebuild();
                    }
                });
            }, ct).ConfigureAwait(false);

            SymbolsReady = true;
            Rebuild();
            Log.Info($"Indexed {_snapshot.Symbols.Length} symbols in {_files.Count} files ({sw.Elapsed.TotalSeconds:F1} s)");

            if (toParse.Count > 0) SaveCaches();
            StartWatchers();
        }

        public void SaveCaches()
        {
            try
            {
                var all = _files.Values.ToList();
                var engine = all.Where(r => !r.IsProject);
                var project = all.Where(r => r.IsProject);
                var e = EngineCacheFile();
                if (e != null) IndexCache.Save(e, engine);
                var p = ProjectCacheFile();
                if (p != null) IndexCache.Save(p, project);
            }
            catch (Exception ex)
            {
                Log.Warn("Saving cache failed: " + ex.Message);
            }
        }

        private string EngineCacheFile() =>
            CacheDir == null || Layout.EngineRoot == null ? null
                : IndexCache.CacheFileFor(CacheDir, "engine", Layout.EngineRoot + "|" + (Layout.EngineVersion ?? ""));

        private string ProjectCacheFile() =>
            CacheDir == null || Layout.ProjectRoot == null ? null
                : IndexCache.CacheFileFor(CacheDir, "project", Layout.ProjectRoot);

        internal static ScannedSymbol[] ParseFile(string path)
        {
            try
            {
                string text = ReadText(path);
                return CppSymbolScanner.Scan(text).ToArray();
            }
            catch (Exception ex)
            {
                Log.Debug($"Parse failed {path}: {ex.Message}");
                return Array.Empty<ScannedSymbol>();
            }
        }

        internal static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // UE sources are UTF-8 (with or without BOM) or UTF-16 LE with BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }

        private void AddAsset(IndexRoot root, string path)
        {
            string rel = Path.GetRelativePath(root.Path, path).Replace('\\', '/');
            string noExt = rel.Substring(0, rel.Length - Path.GetExtension(rel).Length);
            string name = Path.GetFileNameWithoutExtension(path);
            _assets[path] = new AssetDto
            {
                Name = name,
                FilePath = path,
                ObjectPath = root.ContentMount + "/" + noExt + "." + name,
                IsMap = path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase),
                Origin = root.Origin,
            };
        }

        /// <summary>Rebuilds the query snapshot from the current file set.</summary>
        public void Rebuild()
        {
            lock (_rebuildLock)
            {
                var files = _files.Values.ToArray();
                Array.Sort(files, (a, b) => string.CompareOrdinal(a.Path, b.Path));
                // Symbols may be replaced concurrently by the parser; read each array once.
                var symArrays = new ScannedSymbol[files.Length][];
                int total = 0;
                for (int k = 0; k < files.Length; k++)
                {
                    symArrays[k] = files[k].Symbols;
                    total += symArrays[k].Length;
                }

                var snap = new Snapshot
                {
                    Files = files,
                    Symbols = new ScannedSymbol[total],
                    SymbolFiles = new FileRecord[total],
                    SymbolNamesLower = new string[total],
                };
                int i = 0;
                for (int k = 0; k < files.Length; k++)
                {
                    var f = files[k];
                    foreach (var s in symArrays[k])
                    {
                        snap.Symbols[i] = s;
                        snap.SymbolFiles[i] = f;
                        snap.SymbolNamesLower[i] = s.Name.ToLowerInvariant();
                        if (!snap.SymbolsByName.TryGetValue(s.Name, out var list)) snap.SymbolsByName[s.Name] = list = new List<int>(1);
                        list.Add(i);
                        i++;
                    }
                    if (!snap.FilesByName.TryGetValue(f.Name, out var fl)) snap.FilesByName[f.Name] = fl = new List<FileRecord>(1);
                    fl.Add(f);
                    if (f.Name.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        string module = f.Name.Substring(0, f.Name.Length - ".Build.cs".Length);
                        if (!snap.BuildCsByModule.ContainsKey(module) || f.IsProject)
                        {
                            snap.BuildCsByModule[module] = f.Path;
                            snap.ModuleOrigin[module] = f.Origin;
                        }
                    }
                }
                snap.Assets = _assets.Values.ToArray();
                snap.AssetNamesLower = snap.Assets.Select(a => a.Name.ToLowerInvariant()).ToArray();
                _snapshot = snap;
            }
            SnapshotChanged?.Invoke();
        }

        // ---- incremental updates ----------------------------------------------------------------

        public void NotifyChanged(string path)
        {
            _dirty[Path.GetFullPath(path)] = 0;
            _debounce ??= new Timer(_ => FlushDirty(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(300, Timeout.Infinite);
        }

        private void FlushDirty()
        {
            var paths = _dirty.Keys.ToList();
            foreach (var p in paths) _dirty.TryRemove(p, out _);
            bool changed = false;
            foreach (var path in paths)
            {
                try
                {
                    changed |= Refresh(path);
                }
                catch (Exception ex)
                {
                    Log.Debug($"Refresh failed {path}: {ex.Message}");
                }
            }
            if (changed) Rebuild();
        }

        /// <summary>Re-reads one file or asset. Returns true if the index changed.</summary>
        internal bool Refresh(string path)
        {
            bool exists = File.Exists(path);
            string ext = Path.GetExtension(path);
            if (ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase) || ext.Equals(".umap", StringComparison.OrdinalIgnoreCase))
            {
                if (!exists) return _assets.TryRemove(path, out _);
                var root = Layout.ContentRoots.FirstOrDefault(r => IsUnder(path, r.Path));
                if (root == null) return false;
                AddAsset(root, path);
                return true;
            }

            if (!exists)
            {
                return _files.TryRemove(path, out _);
            }

            var info = new FileInfo(path);
            if (_files.TryGetValue(path, out var rec))
            {
                if (rec.MtimeTicks == info.LastWriteTimeUtc.Ticks && rec.Length == info.Length) return false;
                var updated = FileRecord.Create(path, RootOf(path)?.Path ?? Path.GetDirectoryName(path), rec.Module, rec.Origin, rec.Parseable, info);
                if (updated.Parseable) updated.Symbols = ParseFile(path);
                _files[path] = updated;
                return true;
            }

            var r = RootOf(path);
            if (r == null) return false;
            bool parse = FileScanner.ParsedExtensions.Contains(ext);
            if (!parse && !FileScanner.ListedExtensions.Contains(ext)) return false;
            var module = Core.Unreal.ModuleLocator.ForFileSystem().FindModuleName(path);
            var created = FileRecord.Create(path, r.Path, module, r.Origin, parse, info);
            if (parse) created.Symbols = ParseFile(path);
            _files[path] = created;
            return true;
        }

        private IndexRoot RootOf(string path) =>
            Layout.SourceRoots.Concat(Layout.ConfigRoots).Where(r => IsUnder(path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();

        private static bool IsUnder(string path, string root) =>
            path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private void StartWatchers()
        {
            // Engine trees are treated as read-only; only the project is watched.
            foreach (var root in Layout.SourceRoots.Concat(Layout.ConfigRoots).Concat(Layout.ContentRoots).Where(r => r.IsProject))
            {
                try
                {
                    var w = new FileSystemWatcher(root.Path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    w.Changed += (_, e) => NotifyChanged(e.FullPath);
                    w.Created += (_, e) => NotifyChanged(e.FullPath);
                    w.Deleted += (_, e) => NotifyChanged(e.FullPath);
                    w.Renamed += (_, e) => { NotifyChanged(e.OldFullPath); NotifyChanged(e.FullPath); };
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Cannot watch {root.Path}: {ex.Message}");
                }
            }
        }

        /// <summary>Adds a file outside the scanned roots (tests, ad-hoc files).</summary>
        internal void AddFileForTest(FileRecord rec) => _files[rec.Path] = rec;

        public void Dispose()
        {
            foreach (var w in _watchers) w.Dispose();
            _debounce?.Dispose();
        }
    }
}
