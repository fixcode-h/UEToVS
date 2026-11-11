using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Matching;
using UEToVS.Core.Protocol;
using UEToVS.Indexer.Parsing;

namespace UEToVS.Indexer.Index
{
    /// <summary>Ranks index entries against a Search Everywhere request.</summary>
    internal static class Searcher
    {
        public static bool IsTypeKind(SymbolKind k) =>
            k == SymbolKind.Class || k == SymbolKind.Struct || k == SymbolKind.Enum || k == SymbolKind.Union || k == SymbolKind.Typedef || k == SymbolKind.Delegate;

        public static int OriginBonus(ItemOrigin o) => o switch
        {
            ItemOrigin.Project => 3000,
            ItemOrigin.ProjectPlugin => 2500,
            ItemOrigin.Engine => 0,
            ItemOrigin.EnginePlugin => -300,
            _ => -1500,
        };

        private static int KindBonus(SymbolKind k) => k switch
        {
            SymbolKind.Class => 600,
            SymbolKind.Struct => 600,
            SymbolKind.Enum => 500,
            SymbolKind.Delegate => 400,
            SymbolKind.Typedef => 300,
            SymbolKind.Method => 200,
            SymbolKind.Function => 200,
            SymbolKind.Field => 100,
            SymbolKind.Macro => 0,
            SymbolKind.EnumValue => 0,
            _ => 0,
        };

        public static SearchResponse Search(Snapshot snap, SearchRequest req, bool indexReady, CancellationToken ct)
        {
            var resp = new SearchResponse { IndexReady = indexReady };
            string pattern = req.Pattern ?? string.Empty;
            var matcher = new FuzzyMatcher(pattern);
            if (matcher.IsEmpty && string.IsNullOrEmpty(req.Container)) return resp;

            var recent = BuildRecentMap(req.RecentPaths);
            int max = Math.Max(1, req.MaxResultsPerCategory);
            string filterChars = Subsequence(pattern);
            var containerMatcher = string.IsNullOrEmpty(req.Container) ? null : new FuzzyMatcher(LastSegment(req.Container));
            int nonProject = 0;

            var tasks = new List<Task>();
            List<SearchHit> types = null, symbols = null, files = null, assets = null;

            bool pathQuery = req.PathFragments != null && req.PathFragments.Length > 0;
            if ((req.IncludeTypes || req.IncludeSymbols) && !matcher.IsEmpty && !pathQuery)
            {
                tasks.Add(Task.Run(() =>
                {
                    var r = SearchSymbols(snap, req, matcher, containerMatcher, filterChars, recent, max, ct, out int np);
                    types = r.types;
                    symbols = r.symbols;
                    Interlocked.Add(ref nonProject, np);
                }, ct));
            }
            if (req.IncludeFiles && !matcher.IsEmpty)
            {
                tasks.Add(Task.Run(() =>
                {
                    files = SearchFiles(snap, req, matcher, filterChars, recent, max, ct, out int np);
                    Interlocked.Add(ref nonProject, np);
                }, ct));
            }
            if (req.IncludeAssets && !matcher.IsEmpty)
            {
                tasks.Add(Task.Run(() =>
                {
                    assets = SearchAssets(snap, req, matcher, filterChars, max, ct, out int np);
                    Interlocked.Add(ref nonProject, np);
                }, ct));
            }
            Task.WaitAll(tasks.ToArray(), ct);

            if (types != null) resp.Hits.AddRange(types);
            if (symbols != null) resp.Hits.AddRange(symbols);
            if (files != null) resp.Hits.AddRange(files);
            if (assets != null) resp.Hits.AddRange(assets);
            resp.NonProjectHitCount = nonProject;
            return resp;
        }

        private static (List<SearchHit> types, List<SearchHit> symbols) SearchSymbols(
            Snapshot snap, SearchRequest req, FuzzyMatcher matcher, FuzzyMatcher containerMatcher, string filterChars,
            Dictionary<string, int> recent, int max, CancellationToken ct, out int nonProjectCount)
        {
            var syms = snap.Symbols;
            var names = snap.SymbolNamesLower;
            var fileOf = snap.SymbolFiles;
            int n = syms.Length;
            int npCount = 0;
            var typeBag = new List<(int idx, int score, MatchResult m)>();
            var symBag = new List<(int idx, int score, MatchResult m)>();
            object gate = new object();

            var partitions = System.Collections.Concurrent.Partitioner.Create(0, n, Math.Max(4096, n / (Environment.ProcessorCount * 4) + 1));
            Parallel.ForEach(partitions, new ParallelOptions { CancellationToken = ct }, range =>
            {
                var localTypes = new TopN(max * 3);
                var localSyms = new TopN(max * 3);
                int localNp = 0;
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (!ContainsSubsequence(names[i], filterChars)) continue;
                    var s = syms[i];
                    bool isType = IsTypeKind(s.Kind);
                    if (isType ? !req.IncludeTypes : !req.IncludeSymbols) continue;
                    if (req.RequireUe != UeFlags.None && (s.Ue & req.RequireUe) == 0) continue;
                    if (s.Kind == SymbolKind.Namespace) continue;

                    var m = isType ? matcher.MatchUnreal(s.Name) : matcher.Match(s.Name);
                    if (!m.IsMatch) continue;
                    int containerBonus = 0;
                    if (containerMatcher != null)
                    {
                        if (s.Container == null) continue;
                        var cm = containerMatcher.MatchUnreal(LastSegment(s.Container));
                        if (!cm.IsMatch) continue;
                        containerBonus = cm.Kind == MatchKind.Exact ? 1500 : cm.Kind == MatchKind.Prefix ? 800 : 0;
                    }

                    var f = fileOf[i];
                    if (!req.IncludeNonProject && !f.IsProject)
                    {
                        localNp++;
                        continue;
                    }

                    int score = m.Score + OriginBonus(f.Origin) + KindBonus(s.Kind) + containerBonus;
                    if (s.Ue != UeFlags.None) score += 150;
                    if (!s.IsDefinition && (s.Kind == SymbolKind.Method || s.Kind == SymbolKind.Function)) score += 50; // prefer declarations
                    if (recent.TryGetValue(f.Path, out int rank)) score += Math.Max(0, 2000 - rank * 40);
                    (isType ? localTypes : localSyms).Add(i, score, m);
                }
                lock (gate)
                {
                    typeBag.AddRange(localTypes.Items);
                    symBag.AddRange(localSyms.Items);
                    npCount += localNp;
                }
            });

            nonProjectCount = npCount;
            return (Finish(snap, typeBag, ResultCategory.Type, max), Finish(snap, symBag, ResultCategory.Symbol, max));
        }

        private static List<SearchHit> Finish(Snapshot snap, List<(int idx, int score, MatchResult m)> bag, ResultCategory cat, int max)
        {
            bag.Sort((a, b) =>
            {
                int c = b.score.CompareTo(a.score);
                if (c != 0) return c;
                c = snap.Symbols[a.idx].Name.Length.CompareTo(snap.Symbols[b.idx].Name.Length);
                return c != 0 ? c : string.CompareOrdinal(snap.Symbols[a.idx].Name, snap.Symbols[b.idx].Name);
            });
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var hits = new List<SearchHit>();
            foreach (var (idx, score, m) in bag)
            {
                var s = snap.Symbols[idx];
                // one entry per symbol: declaration and definition of the same method collapse
                string key = s.Kind + "|" + s.Container + "|" + s.Name + (s.Kind == SymbolKind.Class || s.Kind == SymbolKind.Struct ? "|" + snap.SymbolFiles[idx].Path : "");
                if (!seen.Add(key)) continue;
                hits.Add(new SearchHit
                {
                    Category = cat,
                    Score = score,
                    Highlights = Flatten(m),
                    Symbol = snap.SymbolFiles[idx].ToDto(s),
                });
                if (hits.Count >= max) break;
            }
            return hits;
        }

        private static List<SearchHit> SearchFiles(Snapshot snap, SearchRequest req, FuzzyMatcher matcher, string filterChars,
            Dictionary<string, int> recent, int max, CancellationToken ct, out int nonProjectCount)
        {
            var exts = req.FileExtensions == null ? null : new HashSet<string>(req.FileExtensions.Select(e => "." + e.TrimStart('.').ToLowerInvariant()));
            var frags = req.PathFragments?.Where(f => f.Length > 0).Select(f => f.ToLowerInvariant()).ToArray() ?? Array.Empty<string>();
            var top = new TopN(max);
            var tops = new List<(int, int, MatchResult)>();
            int np = 0;
            var files = snap.Files;
            for (int i = 0; i < files.Length; i++)
            {
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                var f = files[i];
                if (!ContainsSubsequence(f.NameLower, filterChars)) continue;
                if (exts != null && !exts.Contains(System.IO.Path.GetExtension(f.NameLower))) continue;
                var m = matcher.Match(f.Name);
                if (!m.IsMatch)
                {
                    // also match "MyActor" against "MyActor.cpp" without extension
                    continue;
                }
                if (frags.Length > 0 && !DirectoryMatches(f.DirectoryLower, frags)) continue;
                if (!req.IncludeNonProject && !f.IsProject)
                {
                    np++;
                    continue;
                }
                int score = m.Score + OriginBonus(f.Origin);
                if (recent.TryGetValue(f.Path, out int rank)) score += Math.Max(0, 2500 - rank * 50);
                // headers and sources before config/other files
                string ext = System.IO.Path.GetExtension(f.NameLower);
                if (ext == ".h" || ext == ".cpp") score += 200;
                top.Add(i, score, m);
            }
            nonProjectCount = np;
            var items = top.Items;
            items.Sort((a, b) => b.score.CompareTo(a.score) != 0 ? b.score.CompareTo(a.score) : files[a.idx].Name.Length.CompareTo(files[b.idx].Name.Length));
            return items.Select(t => new SearchHit
            {
                Category = ResultCategory.File,
                Score = t.score,
                Highlights = Flatten(t.m),
                File = files[t.idx].ToDto(),
            }).ToList();
        }

        private static List<SearchHit> SearchAssets(Snapshot snap, SearchRequest req, FuzzyMatcher matcher, string filterChars, int max, CancellationToken ct, out int nonProjectCount)
        {
            var top = new TopN(max);
            int np = 0;
            for (int i = 0; i < snap.Assets.Length; i++)
            {
                if (!ContainsSubsequence(snap.AssetNamesLower[i], filterChars)) continue;
                var a = snap.Assets[i];
                var m = matcher.Match(a.Name);
                if (!m.IsMatch) continue;
                bool isProject = a.Origin == ItemOrigin.Project || a.Origin == ItemOrigin.ProjectPlugin;
                if (!req.IncludeNonProject && !isProject)
                {
                    np++;
                    continue;
                }
                top.Add(i, m.Score + OriginBonus(a.Origin), m);
            }
            nonProjectCount = np;
            var items = top.Items;
            items.Sort((x, y) => y.score.CompareTo(x.score));
            return items.Select(t => new SearchHit
            {
                Category = ResultCategory.Asset,
                Score = t.score,
                Highlights = Flatten(t.m),
                Asset = snap.Assets[t.idx],
            }).ToList();
        }

        internal static bool DirectoryMatches(string dirLower, string[] fragments)
        {
            int pos = 0;
            foreach (var frag in fragments)
            {
                int idx = dirLower.IndexOf(frag, pos, StringComparison.Ordinal);
                if (idx < 0) return false;
                pos = idx + frag.Length;
            }
            return true;
        }

        public static string LastSegment(string container)
        {
            int i = container.LastIndexOf("::", StringComparison.Ordinal);
            return i < 0 ? container : container.Substring(i + 2);
        }

        /// <summary>Lowercased letters/digits of the pattern, used as a cheap subsequence pre-filter.</summary>
        internal static string Subsequence(string pattern)
        {
            var chars = new List<char>();
            foreach (char c in pattern)
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.') chars.Add(char.ToLowerInvariant(c));
            return new string(chars.ToArray());
        }

        internal static bool ContainsSubsequence(string text, string sub)
        {
            int j = 0;
            for (int i = 0; i < text.Length && j < sub.Length; i++)
                if (text[i] == sub[j]) j++;
            return j == sub.Length;
        }

        private static Dictionary<string, int> BuildRecentMap(string[] recent)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (recent == null) return map;
            for (int i = 0; i < recent.Length; i++)
                if (!map.ContainsKey(recent[i])) map[recent[i]] = i;
            return map;
        }

        private static int[] Flatten(MatchResult m)
        {
            var arr = new int[m.Ranges.Count * 2];
            for (int i = 0; i < m.Ranges.Count; i++)
            {
                arr[i * 2] = m.Ranges[i].Start;
                arr[i * 2 + 1] = m.Ranges[i].Length;
            }
            return arr;
        }

        /// <summary>Keeps the N best (index, score) pairs.</summary>
        private sealed class TopN
        {
            private readonly int _n;
            private readonly List<(int idx, int score, MatchResult m)> _items = new List<(int, int, MatchResult)>();
            private int _minScore = int.MinValue;

            public TopN(int n) => _n = n;

            public List<(int idx, int score, MatchResult m)> Items => _items;

            public void Add(int idx, int score, MatchResult m)
            {
                if (_items.Count >= _n && score <= _minScore) return;
                _items.Add((idx, score, m));
                if (_items.Count > _n * 2)
                {
                    _items.Sort((a, b) => b.score.CompareTo(a.score));
                    _items.RemoveRange(_n, _items.Count - _n);
                    _minScore = _items[_items.Count - 1].score;
                }
            }
        }
    }
}
