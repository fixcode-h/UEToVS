using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MessagePack;
using MessagePack.Resolvers;
using UEToVS.Contracts;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Indexing
{
    public sealed class FileEntry
    {
        public FileEntry(ListedFile file) => File = file;

        public ListedFile File { get; set; }
        public ulong Hash { get; set; }
        public bool HashKnown { get; set; }
        public int SymbolCount { get; set; }
    }

    /// <summary>All listed files (path → entry) with a cached array snapshot for search.</summary>
    public sealed class FileTable
    {
        private readonly ConcurrentDictionary<string, FileEntry> _files = new ConcurrentDictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        private FileEntry[] _snapshot = Array.Empty<FileEntry>();
        private int _version;
        private int _snapshotVersion = -1;
        private readonly object _snapGate = new object();

        public int Count => _files.Count;

        public bool TryGet(string path, out FileEntry e) => _files.TryGetValue(path, out e);

        public FileEntry Upsert(ListedFile f)
        {
            var e = _files.AddOrUpdate(f.Path, _ => new FileEntry(f), (_, old) => { old.File = f; return old; });
            Interlocked.Increment(ref _version);
            return e;
        }

        public bool Remove(string path)
        {
            bool removed = _files.TryRemove(path, out _);
            if (removed) Interlocked.Increment(ref _version);
            return removed;
        }

        /// <summary>Paths below a directory (for directory deletes).</summary>
        public List<string> PathsUnder(string dir)
        {
            string prefix = dir.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            return _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public FileEntry[] Snapshot()
        {
            int v = Volatile.Read(ref _version);
            var snap = _snapshot;
            if (_snapshotVersion == v) return snap;
            lock (_snapGate)
            {
                v = Volatile.Read(ref _version);
                if (_snapshotVersion != v)
                {
                    _snapshot = _files.Values.ToArray();
                    _snapshotVersion = v;
                }
                return _snapshot;
            }
        }

        public int CountWhere(Func<FileEntry, bool> predicate) => _files.Values.Count(predicate);
    }

    /// <summary>Assets by file path.</summary>
    /// <summary>
    /// One asset in the in-memory table. Big UE projects list close to a million assets, so the entry keeps only what search
    /// needs per item (name, path, origin) plus the package folder, interned and shared by every asset in that folder;
    /// the object path and class are rebuilt for the few hits that are returned (<see cref="ToItem"/>).
    /// </summary>
    public sealed class AssetEntry
    {
        public AssetEntry(string filePath, string name, string packageDir, ItemOrigin origin, bool isMap)
        {
            FilePath = filePath;
            Name = name;
            PackageDir = packageDir;
            Origin = origin;
            IsMap = isMap;
        }

        public string FilePath { get; }
        public string Name { get; }

        /// <summary>"/Game/Characters" for /Game/Characters/BP_Hero.BP_Hero; null when the object path was unknown.</summary>
        public string PackageDir { get; }

        public ItemOrigin Origin { get; }
        public bool IsMap { get; }

        public string ObjectPath => PackageDir == null ? null : PackageDir + "/" + Name + "." + Name;

        public AssetItem ToItem() => new AssetItem
        {
            Name = Name,
            FilePath = FilePath,
            ObjectPath = ObjectPath,
            ClassName = IsMap ? "World" : null,
            Origin = Origin,
        };
    }

    public sealed class AssetTable
    {
        private readonly ConcurrentDictionary<string, AssetEntry> _assets = new ConcurrentDictionary<string, AssetEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _dirs = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private AssetEntry[] _snapshot = Array.Empty<AssetEntry>();
        private int _version;
        private int _snapshotVersion = -1;

        public int Count => _assets.Count;

        /// <summary>Distinct package folders (interned strings) — for memory diagnostics.</summary>
        public int PackageDirCount => _dirs.Count;

        public void Upsert(AssetItem a)
        {
            if (a == null || string.IsNullOrEmpty(a.FilePath)) return;
            string dir = null;
            if (!string.IsNullOrEmpty(a.ObjectPath))
            {
                int slash = a.ObjectPath.LastIndexOf('/');
                if (slash >= 0)
                {
                    string d = a.ObjectPath.Substring(0, slash);
                    dir = _dirs.GetOrAdd(d, d);
                }
            }
            bool isMap = string.Equals(a.ClassName, "World", StringComparison.Ordinal);
            _assets[a.FilePath] = new AssetEntry(a.FilePath, a.Name ?? Path.GetFileNameWithoutExtension(a.FilePath), dir, a.Origin, isMap);
            Interlocked.Increment(ref _version);
        }

        public bool Remove(string path)
        {
            bool r = _assets.TryRemove(path, out _);
            if (r) Interlocked.Increment(ref _version);
            return r;
        }

        public List<string> PathsUnder(string dir)
        {
            string prefix = dir.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            return _assets.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public AssetEntry[] Snapshot()
        {
            int v = Volatile.Read(ref _version);
            if (_snapshotVersion == v) return _snapshot;
            var arr = _assets.Values.ToArray();
            _snapshot = arr;
            _snapshotVersion = v;
            return arr;
        }
    }

    public sealed class IndexStateStore
    {
        public sealed class FileState
        {
            public string Path { get; set; }
            public long Size { get; set; }
            public long Ticks { get; set; }
            public ulong Hash { get; set; }
        }

        public sealed class State
        {
            public int Format { get; set; } = 1;
            public string Key { get; set; }
            public List<FileState> Files { get; set; } = new List<FileState>();
        }

        private const string StateFile = "files-state.bin";
        private const string ManifestFile = "manifest.json";

        private static readonly MessagePackSerializerOptions Options =
            MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance).WithCompression(MessagePackCompression.Lz4BlockArray);

        public static Dictionary<string, FileState> TryLoad(string indexDir, string key)
        {
            string path = Path.Combine(indexDir, StateFile);
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var s = MessagePackSerializer.Deserialize<State>(fs, Options);
            if (s == null || s.Key != key || s.Files == null) return null;
            var d = new Dictionary<string, FileState>(s.Files.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var f in s.Files) d[f.Path] = f;
            return d;
        }

        public static void Save(string indexDir, string key, IEnumerable<FileState> files, object manifest)
        {
            Directory.CreateDirectory(indexDir);
            var state = new State { Key = key, Files = files.ToList() };
            string path = Path.Combine(indexDir, StateFile);
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp)) MessagePackSerializer.Serialize(fs, state, Options);
            File.Move(tmp, path, true);
            string m = Path.Combine(indexDir, ManifestFile);
            File.WriteAllText(m + ".tmp", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(m + ".tmp", m, true);
        }

        public static long DirectoryBytes(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return 0;
                return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
