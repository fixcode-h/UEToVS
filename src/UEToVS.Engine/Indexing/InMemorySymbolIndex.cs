using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MessagePack;
using MessagePack.Resolvers;
using UEToVS.Contracts;
using UEToVS.Core.Matching;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// Placeholder <see cref="ISymbolIndexBackend"/>: per-file symbol arrays in a concurrent dictionary, linear fuzzy
    /// search. Correct but not fast; replaced by UEToVS.Engine.Index (segments, trigram pruning, mmap).
    /// </summary>
    public sealed class InMemorySymbolIndex : ISymbolIndexBackend
    {
        private const string FileName = "placeholder-symbols.bin";
        private readonly ConcurrentDictionary<string, SymbolInfo[]> _files = new ConcurrentDictionary<string, SymbolInfo[]>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byName = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);
        private int _symbolCount;
        private long _diskBytes;

        public string Name => "in-memory-placeholder";
        public string FormatVersion => "mem1";

        public static bool IsTypeKind(SymbolKind k) =>
            k == SymbolKind.Class || k == SymbolKind.Struct || k == SymbolKind.Union || k == SymbolKind.Enum ||
            k == SymbolKind.Typedef || k == SymbolKind.Delegate || k == SymbolKind.Concept;

        public static bool IsProject(ItemOrigin o) => o == ItemOrigin.Project || o == ItemOrigin.ProjectPlugin;

        public SymbolSearchResult Search(SymbolQuery q, CancellationToken ct)
        {
            var result = new SymbolSearchResult();
            var matcher = new FuzzyMatcher(q.Pattern);
            if (matcher.IsEmpty) return result;
            var types = new TopN<SymbolMatch>(q.MaxResults, m => m.Score);
            var symbols = new TopN<SymbolMatch>(q.MaxResults, m => m.Score);
            int n = 0;
            foreach (var kv in _files)
            {
                foreach (var s in kv.Value)
                {
                    if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
                    bool isType = IsTypeKind(s.Kind);
                    if (isType ? !q.IncludeTypes : !q.IncludeSymbols) continue;
                    if (q.RequireUe != UeFlags.None && (s.Ue & q.RequireUe) == 0) continue;
                    if (!string.IsNullOrEmpty(q.Container) && !ContainerMatches(s.Container, q.Container)) continue;
                    var m = matcher.MatchUnreal(s.Name);
                    if (!m.IsMatch) continue;
                    if (!q.IncludeNonProject && !IsProject(s.Origin))
                    {
                        result.NonProjectMatchCount++;
                        continue;
                    }
                    int score = m.Score + (IsProject(s.Origin) ? 50 : 0);
                    var match = new SymbolMatch { Symbol = s, Category = isType ? ResultCategory.Type : ResultCategory.Symbol, Score = score, Highlights = Ranges(m) };
                    (isType ? types : symbols).Add(match);
                }
            }
            result.Matches.AddRange(types.ToSortedList());
            result.Matches.AddRange(symbols.ToSortedList());
            return result;
        }

        internal static int[] Ranges(MatchResult m)
        {
            var r = new int[m.Ranges.Count * 2];
            for (int i = 0; i < m.Ranges.Count; i++)
            {
                r[2 * i] = m.Ranges[i].Start;
                r[2 * i + 1] = m.Ranges[i].Length;
            }
            return r;
        }

        private static bool ContainerMatches(string symbolContainer, string wanted)
        {
            if (string.IsNullOrEmpty(symbolContainer)) return false;
            return string.Equals(LastSegment(symbolContainer), LastSegment(wanted), StringComparison.Ordinal);
        }

        private static string LastSegment(string s)
        {
            int i = s.LastIndexOf("::", StringComparison.Ordinal);
            return i < 0 ? s : s.Substring(i + 2);
        }

        public IReadOnlyList<SymbolInfo> GetFileSymbols(string filePath) =>
            filePath != null && _files.TryGetValue(filePath, out var s) ? s : Array.Empty<SymbolInfo>();

        public IReadOnlyList<SymbolInfo> FindSymbols(string name, string container)
        {
            if (string.IsNullOrEmpty(name) || !_byName.TryGetValue(name, out var files)) return Array.Empty<SymbolInfo>();
            var list = new List<SymbolInfo>();
            foreach (var f in files.Keys)
            {
                if (!_files.TryGetValue(f, out var syms)) continue;
                foreach (var s in syms)
                    if (s.Name == name && (string.IsNullOrEmpty(container) || ContainerMatches(s.Container, container))) list.Add(s);
            }
            return list;
        }

        public void UpdateFile(string filePath, IReadOnlyList<SymbolInfo> symbols)
        {
            var arr = symbols?.ToArray() ?? Array.Empty<SymbolInfo>();
            SymbolInfo[] old = null;
            _files.AddOrUpdate(filePath, arr, (_, prev) => { old = prev; return arr; });
            if (old != null) Unindex(filePath, old);
            foreach (var s in arr)
                if (s.Name != null) _byName.GetOrAdd(s.Name, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))[filePath] = 0;
            Interlocked.Add(ref _symbolCount, arr.Length - (old?.Length ?? 0));
        }

        public void RemoveFile(string filePath)
        {
            if (_files.TryRemove(filePath, out var old))
            {
                Unindex(filePath, old);
                Interlocked.Add(ref _symbolCount, -old.Length);
            }
        }

        private void Unindex(string filePath, SymbolInfo[] old)
        {
            foreach (var s in old)
                if (s.Name != null && _byName.TryGetValue(s.Name, out var set)) set.TryRemove(filePath, out _);
        }

        public bool ContainsFile(string filePath) => _files.ContainsKey(filePath);

        public void Reset(bool project, bool engine)
        {
            foreach (var kv in _files.ToList())
            {
                bool isProject = kv.Value.Length > 0 && IsProject(kv.Value[0].Origin);
                if (kv.Value.Length == 0 || (isProject ? project : engine)) RemoveFile(kv.Key);
            }
        }

        public IndexStats GetStats() => new IndexStats
        {
            Files = _files.Count,
            Symbols = Volatile.Read(ref _symbolCount),
            ManagedBytes = EstimateManagedBytes(),
            DiskBytes = Interlocked.Read(ref _diskBytes),
        };

        // ~200 bytes per SymbolInfo + location + strings (names are shared with the parser's strings).
        private long EstimateManagedBytes() => Volatile.Read(ref _symbolCount) * 200L + _files.Count * 120L + _byName.Count * 96L;

        public IReadOnlyList<MemoryPart> GetMemoryParts() => new[]
        {
            new MemoryPart { Name = "symbols", ManagedBytes = Volatile.Read(ref _symbolCount) * 200L + _files.Count * 120L, DiskBytes = Interlocked.Read(ref _diskBytes) },
            new MemoryPart { Name = "name-index", ManagedBytes = _byName.Count * 96L },
        };

        public sealed class Persisted
        {
            public string Key { get; set; }
            public Dictionary<string, SymbolInfo[]> Files { get; set; }
        }

        private static readonly MessagePackSerializerOptions Options =
            MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance).WithCompression(MessagePackCompression.Lz4BlockArray);

        public bool TryLoad(string indexDir, string key)
        {
            string path = Path.Combine(indexDir, FileName);
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            var p = MessagePackSerializer.Deserialize<Persisted>(fs, Options);
            if (p?.Files == null || p.Key != key) return false;
            foreach (var kv in p.Files) UpdateFile(kv.Key, kv.Value);
            Interlocked.Exchange(ref _diskBytes, fs.Length);
            return true;
        }

        public void Save(string indexDir, string key)
        {
            Directory.CreateDirectory(indexDir);
            string path = Path.Combine(indexDir, FileName);
            string tmp = path + ".tmp";
            var p = new Persisted { Key = key, Files = _files.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase) };
            using (var fs = File.Create(tmp)) MessagePackSerializer.Serialize(fs, p, Options);
            File.Move(tmp, path, true); // write temp + atomic replace (§10.2)
            Interlocked.Exchange(ref _diskBytes, new FileInfo(path).Length);
        }

        public void Dispose() { }
    }

    /// <summary>Keeps the N highest-scoring items.</summary>
    internal sealed class TopN<T>
    {
        private readonly int _n;
        private readonly Func<T, int> _score;
        private readonly PriorityQueue<T, int> _heap = new PriorityQueue<T, int>();

        public TopN(int n, Func<T, int> score)
        {
            _n = Math.Max(0, n);
            _score = score;
        }

        public void Add(T item)
        {
            if (_n == 0) return;
            int s = _score(item);
            if (_heap.Count < _n) _heap.Enqueue(item, s);
            else if (_heap.TryPeek(out _, out int min) && s > min) _heap.EnqueueDequeue(item, s);
        }

        public List<T> ToSortedList()
        {
            var list = _heap.UnorderedItems.Select(x => x.Element).ToList();
            list.Sort((a, b) => _score(b).CompareTo(_score(a)));
            return list;
        }
    }
}
