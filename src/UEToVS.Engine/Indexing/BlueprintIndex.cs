using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Indexing
{
    /// <summary>One asset as the Blueprint index knows it (from <see cref="UassetReader"/>).</summary>
    public sealed class BlueprintEntry
    {
        public string FilePath { get; set; }

        /// <summary>"/Game/Characters/B_Hero.B_Hero" (null when unknown).</summary>
        public string ObjectPath { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public long Size { get; set; }
        public string ParentClass { get; set; }
        public string NativeParentClass { get; set; }
        public string GeneratedClass { get; set; }

        /// <summary>Name table, sorted ordinal (binary search); empty for assets that are neither Blueprints nor maps.</summary>
        public string[] Names { get; set; } = Array.Empty<string>();

        public bool IsMap => FilePath != null && FilePath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);
        public bool IsBlueprint => ParentClass != null || NativeParentClass != null || GeneratedClass != null;

        /// <summary>Kept in the index (searched): Blueprints and maps (level Blueprints). Other assets are only remembered so they are not re-read.</summary>
        public bool IsSearchable => IsBlueprint || IsMap;

        public string Name => Path.GetFileNameWithoutExtension(FilePath ?? string.Empty);

        public bool HasName(string name) => name != null && Array.BinarySearch(Names, name, StringComparer.Ordinal) >= 0;
    }

    /// <summary>A Blueprint deriving from a class, with the Blueprints deriving from it.</summary>
    public sealed class DerivedBlueprint
    {
        public BlueprintEntry Entry { get; set; }
        public List<DerivedBlueprint> Children { get; } = new List<DerivedBlueprint>();
    }

    /// <summary>
    /// Offline Blueprint index: parent classes and name tables of the project's .uasset / .umap files, read straight from
    /// disk (no UE editor). Answers "which Blueprints derive from this C++ class" (asset registry tags, exact) and
    /// "which Blueprints / levels use this function or property" (name table match on the member name plus its class or
    /// module, so it is a name-based result, like Rider's offline one). Persisted as gzip JSON lines in
    /// <c>.vs\UEToVS\Blueprints\blueprints.jsonl.gz</c>, refreshed by file time and size; the MCP server reads the same file.
    /// </summary>
    public sealed class BlueprintIndex
    {
        public const string FileName = "blueprints.jsonl.gz";
        public const string FolderName = "Blueprints";
        public const int Format = 1;

        private readonly object _gate = new object();
        private Dictionary<string, BlueprintEntry> _entries = new Dictionary<string, BlueprintEntry>(StringComparer.OrdinalIgnoreCase);
        private BlueprintEntry[] _searchable = Array.Empty<BlueprintEntry>();

        public int Count { get { lock (_gate) return _entries.Count; } }
        public int SearchableCount => Volatile.Read(ref _searchable).Length;
        public BlueprintEntry[] Searchable => Volatile.Read(ref _searchable);

        /// <summary>Set once a refresh or a load completed.</summary>
        public bool IsReady { get; private set; }

        public static string DefaultPath(string uetovsRoot) => Path.Combine(uetovsRoot, FolderName, FileName);

        /// <summary>
        /// Brings the index in line with <paramref name="assets"/> (file path, object path): new or changed files (time / size)
        /// are read, removed ones dropped. Returns how many files were read.
        /// </summary>
        public int Refresh(IEnumerable<(string FilePath, string ObjectPath)> assets, CancellationToken ct, int maxParallelism = 2)
        {
            Dictionary<string, BlueprintEntry> old;
            lock (_gate) old = _entries;
            var next = new Dictionary<string, BlueprintEntry>(StringComparer.OrdinalIgnoreCase);
            var toRead = new List<(string FilePath, string ObjectPath, long Ticks, long Size)>();
            foreach (var (path, objectPath) in assets)
            {
                ct.ThrowIfCancellationRequested();
                FileInfo fi;
                try
                {
                    fi = new FileInfo(path);
                    if (!fi.Exists) continue;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    continue;
                }
                long ticks = fi.LastWriteTimeUtc.Ticks, size = fi.Length;
                if (old.TryGetValue(path, out var e) && e.LastWriteUtcTicks == ticks && e.Size == size)
                {
                    if (objectPath != null) e.ObjectPath = objectPath;
                    next[path] = e;
                }
                else toRead.Add((path, objectPath, ticks, size));
            }
            var read = new System.Collections.Concurrent.ConcurrentBag<BlueprintEntry>();
            System.Threading.Tasks.Parallel.ForEach(toRead,
                new System.Threading.Tasks.ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, maxParallelism) },
                t => read.Add(FromInfo(t.FilePath, t.ObjectPath, t.Ticks, t.Size, UassetReader.Read(t.FilePath))));
            foreach (var e in read) next[e.FilePath] = e;
            Publish(next);
            return toRead.Count;
        }

        public static BlueprintEntry FromInfo(string path, string objectPath, long ticks, long size, UassetInfo info)
        {
            var e = new BlueprintEntry
            {
                FilePath = path, ObjectPath = objectPath, LastWriteUtcTicks = ticks, Size = size,
                ParentClass = info.ParentClass, NativeParentClass = info.NativeParentClass, GeneratedClass = info.GeneratedClass,
            };
            if (e.IsSearchable) e.Names = SortedNames(info.Names);
            return e;
        }

        /// <summary>Identifier-like names only (paths, numbers and "None" never match a C++ member), sorted and distinct.</summary>
        public static string[] SortedNames(IEnumerable<string> names)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in names ?? Array.Empty<string>())
                if (IsIdentifier(n)) set.Add(string.Intern(n));
            var arr = set.ToArray();
            Array.Sort(arr, StringComparer.Ordinal);
            return arr;
        }

        private static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 128 || s == "None") return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char c in s)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        /// <summary>Replaces the whole index (tests, tools that build entries themselves).</summary>
        public void Replace(IEnumerable<BlueprintEntry> entries)
        {
            var next = new Dictionary<string, BlueprintEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) next[e.FilePath] = e;
            Publish(next);
        }

        private void Publish(Dictionary<string, BlueprintEntry> next)
        {
            var searchable = next.Values.Where(e => e.IsSearchable).OrderBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();
            lock (_gate) _entries = next;
            Volatile.Write(ref _searchable, searchable);
            IsReady = true;
        }

        // ---------------- queries ----------------

        /// <summary>
        /// UE's name of a C++ class: "ALyraCharacter" → "LyraCharacter" (A / U / F / S / I / E / T prefix dropped when an
        /// upper-case letter follows); other names stay as they are.
        /// </summary>
        public static string UeClassName(string cppName)
        {
            if (string.IsNullOrEmpty(cppName) || cppName.Length < 2) return cppName;
            int colon = cppName.LastIndexOf("::", StringComparison.Ordinal);
            if (colon >= 0) cppName = cppName.Substring(colon + 2);
            return "AUFSIET".IndexOf(cppName[0]) >= 0 && char.IsUpper(cppName[1]) ? cppName.Substring(1) : cppName;
        }

        /// <summary>
        /// Blueprints whose parent is one of <paramref name="classNames"/> (UE names without prefix, e.g. "LyraCharacter")
        /// — directly, or through a native subclass when that subclass is in the set too — each with its Blueprint
        /// children below it (at most <paramref name="maxDepth"/> levels).
        /// </summary>
        public List<DerivedBlueprint> Derived(ICollection<string> classNames, int maxDepth = 8)
        {
            var all = Searchable;
            var byParent = new Dictionary<string, List<BlueprintEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in all)
            {
                if (e.ParentClass == null) continue;
                string key = UassetReader.IsNative(e.ParentClass) ? "native:" + UassetReader.ClassName(e.ParentClass) : "bp:" + e.ParentClass;
                if (!byParent.TryGetValue(key, out var list)) byParent[key] = list = new List<BlueprintEntry>();
                list.Add(e);
            }
            var result = new List<DerivedBlueprint>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in classNames.Distinct(StringComparer.OrdinalIgnoreCase))
                if (byParent.TryGetValue("native:" + c, out var direct))
                    foreach (var e in direct)
                        if (visited.Add(e.FilePath)) result.Add(Expand(e, byParent, visited, maxDepth - 1));
            result.Sort((a, b) => string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static DerivedBlueprint Expand(BlueprintEntry e, Dictionary<string, List<BlueprintEntry>> byParent, HashSet<string> visited, int depth)
        {
            var node = new DerivedBlueprint { Entry = e };
            if (depth <= 0 || e.GeneratedClass == null) return node;
            if (byParent.TryGetValue("bp:" + e.GeneratedClass, out var children))
                foreach (var c in children.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    if (visited.Add(c.FilePath)) node.Children.Add(Expand(c, byParent, visited, depth - 1));
            return node;
        }

        /// <summary>Number of Blueprints in a <see cref="Derived"/> tree.</summary>
        public static int CountTree(IEnumerable<DerivedBlueprint> nodes) => nodes.Sum(n => 1 + CountTree(n.Children));

        /// <summary>
        /// Blueprints and maps whose name table has <paramref name="memberName"/> together with one of
        /// <paramref name="ownerClassNames"/> (UE names; the class or a subclass whose functions the graph calls) or that
        /// derive from one of them. Without owners every asset that has the name is returned.
        /// </summary>
        public List<BlueprintEntry> Usages(string memberName, ICollection<string> ownerClassNames, int max = 500)
        {
            var result = new List<BlueprintEntry>();
            if (string.IsNullOrEmpty(memberName)) return result;
            var owners = ownerClassNames?.Where(o => !string.IsNullOrEmpty(o)).ToList() ?? new List<string>();
            foreach (var e in Searchable)
            {
                if (!e.HasName(memberName)) continue;
                if (owners.Count > 0 && !owners.Any(o => e.HasName(o) || string.Equals(UassetReader.ClassName(e.NativeParentClass), o, StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Add(e);
                if (result.Count >= max) break;
            }
            return result;
        }

        // ---------------- persistence ----------------

        public void Save(string file)
        {
            Dictionary<string, BlueprintEntry> entries;
            lock (_gate) entries = _entries;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                w.WriteLine(new JsonObject { ["format"] = Format }.ToJsonString());
                foreach (var e in entries.Values)
                {
                    var o = new JsonObject { ["p"] = e.FilePath, ["t"] = e.LastWriteUtcTicks, ["s"] = e.Size };
                    if (e.ObjectPath != null) o["o"] = e.ObjectPath;
                    if (e.ParentClass != null) o["pc"] = e.ParentClass;
                    if (e.NativeParentClass != null) o["npc"] = e.NativeParentClass;
                    if (e.GeneratedClass != null) o["gc"] = e.GeneratedClass;
                    if (e.Names.Length > 0) o["n"] = new JsonArray(e.Names.Select(n => (JsonNode)JsonValue.Create(n)).ToArray());
                    w.WriteLine(o.ToJsonString());
                }
            }
            File.Move(tmp, file, true);
        }

        /// <summary>Loads a saved index; false (and an empty index) when the file is missing, from another format or unreadable.</summary>
        public bool Load(string file)
        {
            var next = new Dictionary<string, BlueprintEntry>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(file)) return false;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new StreamReader(gz, Encoding.UTF8))
                {
                    var head = JsonNode.Parse(r.ReadLine() ?? "{}");
                    if ((int?)head?["format"] != Format) return false;
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (!(JsonNode.Parse(line) is JsonObject o)) continue;
                        var e = new BlueprintEntry
                        {
                            FilePath = (string)o["p"], LastWriteUtcTicks = (long?)o["t"] ?? 0, Size = (long?)o["s"] ?? 0,
                            ObjectPath = (string)o["o"], ParentClass = (string)o["pc"], NativeParentClass = (string)o["npc"], GeneratedClass = (string)o["gc"],
                        };
                        if (o["n"] is JsonArray names) e.Names = names.Select(n => string.Intern((string)n)).ToArray(); // saved sorted
                        if (!string.IsNullOrEmpty(e.FilePath)) next[e.FilePath] = e;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                return false;
            }
            Publish(next);
            return true;
        }
    }
}
