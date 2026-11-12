using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Matching;
using UEToVS.Core.Storage;
using UEToVS.Engine.Index.Lua;

namespace UEToVS.Mcp.Data
{
    /// <summary>A Lua symbol with the file it belongs to.</summary>
    public sealed class LuaHit
    {
        public LuaSymbol Symbol { get; set; }
        public LuaFileEntry File { get; set; }
        public int Score { get; set; }
    }

    /// <summary>
    /// Read side of the Lua script index: &lt;solution&gt;\.vs\UEToVS\Lua\lua-index.json written by the engine
    /// (<c>LuaIndexService</c>). Reloaded when the file changes (checked at most once per second), so answers follow
    /// the engine's incremental updates without a connection to it. Thread-safe.
    /// </summary>
    public sealed class LuaIndexReader
    {
        private static readonly Logger L = Log.For("Mcp.Lua");
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

        private readonly object _gate = new object();
        private LuaIndexData _data;
        private DateTime _fileTimeUtc = DateTime.MinValue;
        private long _fileLength = -1;
        private DateTime _lastCheckUtc = DateTime.MinValue;

        public LuaIndexReader(string solutionDir)
        {
            IndexFile = LuaIndexStore.PathIn(StoragePaths.ForSolution(solutionDir).Lua);
        }

        public string IndexFile { get; }

        /// <summary>Last load error (unreadable file), for index_status.</summary>
        public string LoadError { get; private set; }

        /// <summary>The current index, or null when the engine has not written one (no Lua script path configured).</summary>
        public LuaIndexData Data
        {
            get
            {
                lock (_gate)
                {
                    var now = DateTime.UtcNow;
                    if (now - _lastCheckUtc < CheckInterval) return _data;
                    _lastCheckUtc = now;
                    try
                    {
                        var info = new FileInfo(IndexFile);
                        if (!info.Exists)
                        {
                            _data = null;
                            _fileTimeUtc = DateTime.MinValue;
                            _fileLength = -1;
                            return null;
                        }
                        if (info.LastWriteTimeUtc == _fileTimeUtc && info.Length == _fileLength && _data != null) return _data;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        _data = LuaIndexStore.Load(IndexFile);
                        _fileTimeUtc = info.LastWriteTimeUtc;
                        _fileLength = info.Length;
                        LoadError = null;
                        L.Info("mcp.lua.loaded", null, ("files", _data?.Files.Count ?? 0), ("symbols", _data?.SymbolCount ?? 0),
                            ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                    }
                    catch (Exception ex)
                    {
                        // Keep the previous data: the engine may be replacing the file right now.
                        LoadError = ex.GetType().Name + ": " + ex.Message;
                        L.Warn("mcp.lua.load_failed", "Lua index unreadable", ex, ("file", IndexFile));
                    }
                    return _data;
                }
            }
        }

        /// <summary>Forces the next <see cref="Data"/> read to check the file (tests).</summary>
        public void Invalidate()
        {
            lock (_gate) _lastCheckUtc = DateTime.MinValue;
        }

        /// <summary>Splits <c>a.b:c</c> / <c>a.b.c</c> into (container, name).</summary>
        public static (string Container, string Name) Split(string query)
        {
            query = (query ?? string.Empty).Trim();
            int cut = query.LastIndexOfAny(new[] { '.', ':' });
            if (cut <= 0 || cut == query.Length - 1) return (null, query.Trim('.', ':'));
            return (query.Substring(0, cut), query.Substring(cut + 1));
        }

        private static bool ContainerMatches(string actual, string wanted, bool exact)
        {
            if (string.IsNullOrEmpty(wanted)) return true;
            if (string.IsNullOrEmpty(actual)) return false;
            wanted = wanted.Replace(':', '.');
            if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            if (actual.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return !exact && actual.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int KindRank(LuaSymbolKind k) => k switch
        {
            LuaSymbolKind.Class => 0,
            LuaSymbolKind.Table => 1,
            LuaSymbolKind.Method => 2,
            LuaSymbolKind.Function => 2,
            LuaSymbolKind.Field => 3,
            _ => 4,
        };

        /// <summary>Symbols matching <paramref name="name"/> (fuzzy: prefix, CamelHumps, substring; or exact), best first.</summary>
        public List<LuaHit> Search(string name, string container, bool exact, HashSet<LuaSymbolKind> kinds, bool includeLocals)
        {
            var data = Data;
            var hits = new List<LuaHit>();
            if (data == null || string.IsNullOrEmpty(name)) return hits;
            var matcher = exact ? null : new FuzzyMatcher(name);
            foreach (var f in data.Files)
                foreach (var s in f.Symbols)
                {
                    if (kinds != null && !kinds.Contains(s.Kind)) continue;
                    if (!includeLocals && s.IsLocal) continue;
                    if (!ContainerMatches(s.Container, container, exact)) continue;
                    int score;
                    if (exact)
                    {
                        if (!string.Equals(s.Name, name, StringComparison.Ordinal)) continue;
                        score = 0;
                    }
                    else
                    {
                        var m = matcher.Match(s.Name);
                        if (!m.IsMatch) continue;
                        score = m.Score - (s.IsLocal ? 50 : 0) - KindRank(s.Kind) * 10;
                    }
                    hits.Add(new LuaHit { Symbol = s, File = f, Score = score });
                }
            return hits.OrderByDescending(h => h.Score).ThenBy(h => KindRank(h.Symbol.Kind))
                .ThenBy(h => h.File.Path, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Symbol.Line).ToList();
        }

        private Tuple<LuaIndexData, Dictionary<string, List<LuaHit>>> _functionsByName; // one reference: swapped atomically

        /// <summary>
        /// Lua functions and methods named exactly <paramref name="name"/> (case-sensitive, like Lua and UnLua's override
        /// lookup). Backed by a name map built once per loaded index (only names of functions/methods; the hits reference the
        /// loaded data), so linking many C++ UFUNCTIONs to Lua costs one dictionary lookup each.
        /// </summary>
        public IReadOnlyList<LuaHit> FunctionsNamed(string name)
        {
            var data = Data;
            if (data == null || string.IsNullOrEmpty(name)) return Array.Empty<LuaHit>();
            var cached = Volatile.Read(ref _functionsByName);
            if (cached == null || !ReferenceEquals(cached.Item1, data))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var map = new Dictionary<string, List<LuaHit>>(StringComparer.Ordinal);
                foreach (var f in data.Files)
                    foreach (var s in f.Symbols)
                    {
                        if (s.Kind != LuaSymbolKind.Function && s.Kind != LuaSymbolKind.Method) continue;
                        if (!map.TryGetValue(s.Name, out var list)) map[s.Name] = list = new List<LuaHit>(1);
                        list.Add(new LuaHit { Symbol = s, File = f });
                    }
                cached = Tuple.Create(data, map);
                Volatile.Write(ref _functionsByName, cached);
                L.Info("mcp.lua.byname.built", null, ("names", map.Count), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            }
            return cached.Item2.TryGetValue(name, out var hits) ? hits : (IReadOnlyList<LuaHit>)Array.Empty<LuaHit>();
        }

        /// <summary>Files by name or module: 'MainPanel', 'UI.MainPanel', 'UI/MainPanel.lua'.</summary>
        public List<(LuaFileEntry File, int Score)> SearchFiles(string query)
        {
            var data = Data;
            var list = new List<(LuaFileEntry, int)>();
            if (data == null || string.IsNullOrWhiteSpace(query)) return list;
            string q = query.Trim().Replace('\\', '/');
            if (q.EndsWith(LuaIndexer.Extension, StringComparison.OrdinalIgnoreCase)) q = q.Substring(0, q.Length - LuaIndexer.Extension.Length);
            bool dotted = q.IndexOf('.') >= 0 || q.IndexOf('/') >= 0;
            string moduleQuery = q.Replace('/', '.');
            var matcher = new FuzzyMatcher(dotted ? moduleQuery.Substring(moduleQuery.LastIndexOf('.') + 1) : q);
            foreach (var f in data.Files)
            {
                string module = f.Module ?? Path.GetFileNameWithoutExtension(f.Path);
                if (dotted)
                {
                    if (string.Equals(module, moduleQuery, StringComparison.OrdinalIgnoreCase)) { list.Add((f, 200000)); continue; }
                    if (module.EndsWith("." + moduleQuery, StringComparison.OrdinalIgnoreCase)) { list.Add((f, 150000)); continue; }
                    if (module.IndexOf(moduleQuery, StringComparison.OrdinalIgnoreCase) >= 0) { list.Add((f, 90000)); continue; }
                }
                var m = matcher.Match(Path.GetFileNameWithoutExtension(f.Path));
                if (!m.IsMatch) continue;
                if (dotted)
                {
                    // The folder part must still appear in the module path.
                    string folder = moduleQuery.Contains('.') ? moduleQuery.Substring(0, moduleQuery.LastIndexOf('.')) : null;
                    if (folder != null && module.IndexOf(folder, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                list.Add((f, m.Score));
            }
            return list.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1.Path.Length).ThenBy(x => x.Item1.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The file for an absolute path, a path relative to a Lua root, or a module name ('UI.MainPanel').</summary>
        public LuaFileEntry FindFile(string pathOrModule, IEnumerable<string> extraBases)
        {
            var data = Data;
            if (data == null || string.IsNullOrWhiteSpace(pathOrModule)) return null;
            string q = pathOrModule.Trim().Trim('"');
            var candidates = new List<string>();
            try
            {
                if (Path.IsPathRooted(q)) candidates.Add(Path.GetFullPath(q));
                else
                {
                    foreach (var b in data.Roots.Concat(extraBases ?? Enumerable.Empty<string>()).Where(b => !string.IsNullOrEmpty(b)))
                    {
                        candidates.Add(Path.GetFullPath(Path.Combine(b, q)));
                        if (!LuaIndexer.IsLuaFile(q)) candidates.Add(Path.GetFullPath(Path.Combine(b, q + LuaIndexer.Extension)));
                    }
                }
            }
            catch (Exception) { /* not a path: try it as a module name */ }
            foreach (var c in candidates)
            {
                var f = data.Files.FirstOrDefault(x => string.Equals(x.Path, c, StringComparison.OrdinalIgnoreCase));
                if (f != null) return f;
            }
            string module = q.EndsWith(LuaIndexer.Extension, StringComparison.OrdinalIgnoreCase) ? q.Substring(0, q.Length - LuaIndexer.Extension.Length) : q;
            module = module.Replace('\\', '.').Replace('/', '.');
            return data.Files.FirstOrDefault(x => string.Equals(x.Module, module, StringComparison.OrdinalIgnoreCase));
        }
    }
}
