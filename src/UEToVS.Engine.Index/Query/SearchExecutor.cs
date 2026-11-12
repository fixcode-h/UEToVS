using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Matching;
using UEToVS.Engine.Index.Segments;
using UEToVS.Engine.Index.Storage;

namespace UEToVS.Engine.Index.Query
{
    /// <summary>
    /// Search Everywhere over one snapshot. Per segment: candidate names come from the word-start initials postings
    /// (covers exact, prefix and camel-hump matches) united with the trigram intersection (covers substrings), are
    /// filtered by a lowercase subsequence test on the mapped UTF-8 bytes, and only then materialised and scored
    /// with <see cref="FuzzyMatcher"/>. A bounded TopN keeps the best results per category.
    /// </summary>
    internal sealed class SearchExecutor
    {
        private const int CancelCheckMask = 255;

        private readonly Snapshot _snap;
        private readonly SearchRequest _req;
        private readonly CancellationToken _ct;
        private readonly FuzzyMatcher _matcher;
        private readonly FuzzyMatcher _containerMatcher;
        private readonly bool _wildcard;
        private readonly string _filter;
        private readonly byte[] _filterUtf8;
        private readonly Dictionary<string, int> _recent;
        private readonly int _max;
        private readonly TopN _types;
        private readonly TopN _symbols;
        private readonly TopN _files;
        private int _nonProject;

        // per-segment state
        private Dictionary<uint, int> _segRecent;
        private Dictionary<uint, MatchResult> _segContainer;
        private readonly List<uint> _scratchA = new List<uint>();
        private readonly List<uint> _scratchB = new List<uint>();

        internal struct Candidate
        {
            public int Source;      // base index, or -1 for the delta
            public int Index;       // symbol index (base) / decl index (delta) / file index
            public DeltaFile Delta;
            public string Name;
            public int Score;
            public MatchResult Match;

            // deterministic tie-breakers (identical before and after compaction)
            public ulong PathHash;
            public int Line;
            public int Offset;
        }

        private SearchExecutor(Snapshot snap, SearchRequest req, string pattern, string container, CancellationToken ct)
        {
            _snap = snap;
            _req = req;
            _ct = ct;
            _matcher = new FuzzyMatcher(pattern);
            _containerMatcher = string.IsNullOrEmpty(container) ? null : new FuzzyMatcher(LastSegment(container));
            _wildcard = pattern.IndexOf('*') >= 0 || pattern.IndexOf(' ') >= 0;
            var sb = new StringBuilder();
            foreach (char c in pattern)
                if (c != '*' && c != ' ') sb.Append(char.ToLowerInvariant(c));
            _filter = sb.ToString();
            _filterUtf8 = Encoding.UTF8.GetBytes(_filter);
            _recent = BuildRecentMap(req.RecentPaths);
            _max = Math.Max(1, req.MaxResultsPerCategory);
            _types = new TopN(_max * 3);
            _symbols = new TopN(_max * 3);
            _files = new TopN(_max);
        }

        public static SearchResponse Run(Snapshot snap, SearchRequest req, CancellationToken ct)
        {
            var resp = new SearchResponse { IndexReady = snap.Bases.Length > 0 || snap.Delta.Count > 0 };
            if (req == null) return resp;
            string pattern = (req.Pattern ?? string.Empty).Trim();
            string container = req.Container;
            if (string.IsNullOrEmpty(container))
            {
                int sep = pattern.LastIndexOf("::", StringComparison.Ordinal);
                if (sep > 0)
                {
                    container = pattern.Substring(0, sep);
                    pattern = pattern.Substring(sep + 2);
                }
            }
            var ex = new SearchExecutor(snap, req, pattern, container, ct);
            if (ex._matcher.IsEmpty) return resp;
            ex.Execute(resp);
            return resp;
        }

        private void Execute(SearchResponse resp)
        {
            bool symbolsWanted = _req.IncludeTypes || _req.IncludeSymbols;
            for (int si = 0; si < _snap.Bases.Length; si++)
            {
                _ct.ThrowIfCancellationRequested();
                var view = _snap.Bases[si];
                PrepareSegment(view.Segment);
                if (symbolsWanted) SearchSegmentSymbols(si, view);
                if (_req.IncludeFiles) SearchSegmentFiles(si, view);
            }
            _ct.ThrowIfCancellationRequested();
            if (_snap.Delta.Count > 0)
            {
                if (symbolsWanted) SearchDeltaSymbols();
                if (_req.IncludeFiles) SearchDeltaFiles();
            }

            if (_req.IncludeTypes) resp.Hits.AddRange(FinishSymbols(_types, ResultCategory.Type));
            if (_req.IncludeSymbols) resp.Hits.AddRange(FinishSymbols(_symbols, ResultCategory.Symbol));
            if (_req.IncludeFiles) resp.Hits.AddRange(FinishFiles());
            resp.NonProjectHitCount = _nonProject;
        }

        private void PrepareSegment(Segment seg)
        {
            _segContainer = _containerMatcher == null ? null : new Dictionary<uint, MatchResult>();
            _segRecent = null;
            if (_recent.Count == 0) return;
            _segRecent = new Dictionary<uint, int>();
            foreach (var kv in _recent)
            {
                int fi = seg.FindFile(kv.Key);
                if (fi >= 0) _segRecent[(uint)fi] = kv.Value;
            }
        }

        // ------------------------------------------------------------------ base segment symbols

        private void SearchSegmentSymbols(int si, SegmentView view)
        {
            var seg = view.Segment;
            if (_wildcard || _filter.Length == 0)
            {
                for (int n = 0; n < seg.NameCount; n++) ProcessName(si, view, n, n);
                return;
            }

            _scratchA.Clear();
            foreach (var n in seg.InitialPostings(_filter[0])) _scratchA.Add(n);

            if (_filter.Length >= 3)
            {
                IntersectTrigrams(seg, _scratchB);
                // union of two ascending lists
                int i = 0, j = 0, count = 0;
                while (i < _scratchA.Count || j < _scratchB.Count)
                {
                    uint n;
                    if (j >= _scratchB.Count || (i < _scratchA.Count && _scratchA[i] < _scratchB[j])) n = _scratchA[i++];
                    else if (i >= _scratchA.Count || _scratchB[j] < _scratchA[i]) n = _scratchB[j++];
                    else
                    {
                        n = _scratchA[i++];
                        j++;
                    }
                    ProcessName(si, view, (int)n, count++);
                }
                return;
            }

            // Short pattern: word-start candidates first; non word-start (substring-only) matches score below any
            // camel-hump match, so the full scan is needed only when there are not enough strong results yet.
            for (int k = 0; k < _scratchA.Count; k++) ProcessName(si, view, (int)_scratchA[k], k);
            if (HasEnoughStrongResults()) return;
            int a = 0;
            for (int n = 0; n < seg.NameCount; n++)
            {
                if (a < _scratchA.Count && _scratchA[a] == n)
                {
                    a++;
                    continue;
                }
                ProcessName(si, view, n, n);
            }
        }

        private bool HasEnoughStrongResults()
        {
            bool types = !_req.IncludeTypes || _types.StrongAdds >= _max;
            bool syms = !_req.IncludeSymbols || _symbols.StrongAdds >= _max;
            return types && syms;
        }

        private void IntersectTrigrams(Segment seg, List<uint> result)
        {
            result.Clear();
            var keys = new HashSet<ulong>();
            NameKeys.Trigrams(_filter, keys);
            var ordered = new List<(ulong key, int len)>(keys.Count);
            foreach (var k in keys)
            {
                int len = seg.TrigramPostings(k).Length;
                if (len == 0) return;
                ordered.Add((k, len));
            }
            ordered.Sort((x, y) => x.len.CompareTo(y.len));
            foreach (var n in seg.TrigramPostings(ordered[0].key)) result.Add(n);
            for (int t = 1; t < ordered.Count && result.Count > 0; t++)
            {
                var other = seg.TrigramPostings(ordered[t].key);
                int w = 0;
                for (int r = 0; r < result.Count; r++)
                    if (other.BinarySearch(result[r]) >= 0) result[w++] = result[r];
                result.RemoveRange(w, result.Count - w);
            }
        }

        private void ProcessName(int si, SegmentView view, int n, int counter)
        {
            if ((counter & CancelCheckMask) == 0) _ct.ThrowIfCancellationRequested();
            var seg = view.Segment;
            var entry = seg.Names[n];
            if (entry.SymCount == 0) return;
            if (!Subsequence(seg.NameLower(n), _filterUtf8)) return;

            string name = null;
            MatchResult plain = null, unreal = null;
            var syms = seg.SymbolsOfName(n);
            var recs = seg.Symbols;
            var files = seg.Files;
            for (int k = 0; k < syms.Length; k++)
            {
                ref readonly var r = ref recs[(int)syms[k]];
                var kind = r.SymbolKind;
                if (kind == SymbolKind.Namespace) continue;
                bool isType = IsTypeKind(kind);
                if (isType ? !_req.IncludeTypes : !_req.IncludeSymbols) continue;
                if (_req.RequireUe != UeFlags.None && (r.UeFlags & _req.RequireUe) == 0) continue;
                if (view.IsDead(r.FileId)) continue;

                name ??= seg.GetString(r.NameId);
                var m = isType ? (unreal ??= _matcher.MatchUnreal(name)) : (plain ??= _matcher.Match(name));
                if (!m.IsMatch) continue;

                int containerBonus = 0;
                if (_containerMatcher != null)
                {
                    if (r.ContainerId == 0) continue;
                    if (!_segContainer.TryGetValue(r.ContainerId, out var cm))
                    {
                        cm = _containerMatcher.MatchUnreal(LastSegment(seg.GetString(r.ContainerId)));
                        _segContainer[r.ContainerId] = cm;
                    }
                    if (!cm.IsMatch) continue;
                    containerBonus = ContainerBonus(cm);
                }

                ref readonly var f = ref files[(int)r.FileId];
                if (!_req.IncludeNonProject && !f.IsProject)
                {
                    _nonProject++;
                    continue;
                }

                int score = m.Score + OriginBonus(f.ItemOrigin) + KindBonus(kind) + containerBonus;
                if (r.Ue != 0) score += 150;
                if (!r.IsDefinition && (kind == SymbolKind.Method || kind == SymbolKind.Function)) score += 50;
                if (_segRecent != null && _segRecent.TryGetValue(r.FileId, out int rank)) score += RecentBoost(rank, 2000, 40);

                (isType ? _types : _symbols).Add(new Candidate
                {
                    Source = si, Index = (int)syms[k], Name = name, Score = score, Match = m,
                    PathHash = StringPoolBuilder.Hash(seg.Strings.GetBytes(f.PathId)), Line = r.Line, Offset = (int)r.Offset,
                });
            }
        }

        // ------------------------------------------------------------------ delta symbols

        private void SearchDeltaSymbols()
        {
            int counter = 0;
            foreach (var df in _snap.DeltaSorted)
            {
                int rank = -1;
                if (_recent.Count > 0 && _recent.TryGetValue(df.Key, out int rr)) rank = rr;
                for (int i = 0; i < df.Decls.Length; i++)
                {
                    if ((counter++ & CancelCheckMask) == 0) _ct.ThrowIfCancellationRequested();
                    ref readonly var d = ref df.Decls[i];
                    if (d.Kind == SymbolKind.Namespace) continue;
                    bool isType = IsTypeKind(d.Kind);
                    if (isType ? !_req.IncludeTypes : !_req.IncludeSymbols) continue;
                    if (_req.RequireUe != UeFlags.None && (d.Ue & _req.RequireUe) == 0) continue;
                    if (!Subsequence(df.NameLower[i], _filter)) continue;
                    var m = isType ? _matcher.MatchUnreal(d.Name) : _matcher.Match(d.Name);
                    if (!m.IsMatch) continue;
                    int containerBonus = 0;
                    if (_containerMatcher != null)
                    {
                        if (string.IsNullOrEmpty(d.Container)) continue;
                        var cm = _containerMatcher.MatchUnreal(LastSegment(d.Container));
                        if (!cm.IsMatch) continue;
                        containerBonus = ContainerBonus(cm);
                    }
                    if (!_req.IncludeNonProject && !df.IsProject)
                    {
                        _nonProject++;
                        continue;
                    }
                    int score = m.Score + OriginBonus(df.Source.Origin) + KindBonus(d.Kind) + containerBonus;
                    if (d.Ue != UeFlags.None) score += 150;
                    if (!d.IsDefinition && (d.Kind == SymbolKind.Method || d.Kind == SymbolKind.Function)) score += 50;
                    if (rank >= 0) score += RecentBoost(rank, 2000, 40);
                    (isType ? _types : _symbols).Add(new Candidate
                    {
                        Source = -1, Index = i, Delta = df, Name = d.Name, Score = score, Match = m,
                        PathHash = df.PathHash, Line = Math.Clamp(d.Line, 0, SymbolRecord.MaxLine), Offset = Math.Max(0, d.Offset),
                    });
                }
            }
        }

        // ------------------------------------------------------------------ files

        private void SearchSegmentFiles(int si, SegmentView view)
        {
            var seg = view.Segment;
            var files = seg.Files;
            for (int fi = 0; fi < files.Length; fi++)
            {
                if ((fi & 1023) == 0) _ct.ThrowIfCancellationRequested();
                if (view.IsDead((uint)fi)) continue;
                if (!Subsequence(seg.FileNameLower(fi), _filterUtf8)) continue;
                ref readonly var f = ref files[fi];
                string name = seg.GetString(f.FileNameId);
                var m = _matcher.Match(name);
                if (!m.IsMatch) continue;
                if (!_req.IncludeNonProject && !f.IsProject)
                {
                    _nonProject++;
                    continue;
                }
                int score = m.Score + OriginBonus(f.ItemOrigin) + ExtensionBonus(name);
                if (_segRecent != null && _segRecent.TryGetValue((uint)fi, out int rank)) score += RecentBoost(rank, 2500, 50);
                _files.Add(new Candidate { Source = si, Index = fi, Name = name, Score = score, Match = m, PathHash = StringPoolBuilder.Hash(seg.Strings.GetBytes(f.PathId)) });
            }
        }

        private void SearchDeltaFiles()
        {
            foreach (var df in _snap.DeltaSorted)
            {
                if (!Subsequence(df.FileNameLower, _filter)) continue;
                string name = System.IO.Path.GetFileName(df.Source.Path);
                var m = _matcher.Match(name);
                if (!m.IsMatch) continue;
                if (!_req.IncludeNonProject && !df.IsProject)
                {
                    _nonProject++;
                    continue;
                }
                int score = m.Score + OriginBonus(df.Source.Origin) + ExtensionBonus(name);
                if (_recent.TryGetValue(df.Key, out int rank)) score += RecentBoost(rank, 2500, 50);
                _files.Add(new Candidate { Source = -1, Delta = df, Name = name, Score = score, Match = m, PathHash = df.PathHash });
            }
        }

        // ------------------------------------------------------------------ results

        private List<SearchHit> FinishSymbols(TopN top, ResultCategory cat)
        {
            var bag = top.Items;
            bag.Sort(CompareCandidates);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var hits = new List<SearchHit>();
            foreach (var c in bag)
            {
                var info = c.Source >= 0 ? Dto.Symbol(_snap.Bases[c.Source].Segment, (uint)c.Index) : Dto.Symbol(c.Delta, c.Index);
                // declaration and definition of the same function collapse into one entry
                string key = info.Kind + "|" + info.Container + "|" + info.Name +
                             (info.Kind == SymbolKind.Class || info.Kind == SymbolKind.Struct ? "|" + info.Location.FilePath : "");
                if (!seen.Add(key)) continue;
                hits.Add(new SearchHit { Category = cat, Score = c.Score, Highlights = Flatten(c.Match), Symbol = info });
                if (hits.Count >= _max) break;
            }
            return hits;
        }

        private List<SearchHit> FinishFiles()
        {
            var bag = _files.Items;
            bag.Sort(CompareCandidates);
            var hits = new List<SearchHit>();
            foreach (var c in bag)
            {
                var item = c.Source >= 0 ? Dto.File(_snap.Bases[c.Source].Segment, c.Index).ToFileItem() : c.Delta.ToIndexedFile().ToFileItem();
                hits.Add(new SearchHit { Category = ResultCategory.File, Score = c.Score, Highlights = Flatten(c.Match), File = item });
                if (hits.Count >= _max) break;
            }
            return hits;
        }

        private static int CompareCandidates(Candidate a, Candidate b)
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = a.Name.Length.CompareTo(b.Name.Length);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Name, b.Name);
            if (c != 0) return c;
            c = a.PathHash.CompareTo(b.PathHash);
            if (c != 0) return c;
            c = a.Line.CompareTo(b.Line);
            return c != 0 ? c : a.Offset.CompareTo(b.Offset);
        }

        // ------------------------------------------------------------------ helpers

        internal static bool IsTypeKind(SymbolKind k) =>
            k == SymbolKind.Class || k == SymbolKind.Struct || k == SymbolKind.Enum || k == SymbolKind.Union || k == SymbolKind.Typedef ||
            k == SymbolKind.Delegate;

        internal static int OriginBonus(ItemOrigin o) => o switch
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
            _ => 0,
        };

        private static int ContainerBonus(MatchResult cm) => cm.Kind == MatchKind.Exact ? 1500 : cm.Kind == MatchKind.Prefix ? 800 : 0;

        private static int RecentBoost(int rank, int max, int step) => Math.Max(0, max - rank * step);

        private static int ExtensionBonus(string fileName)
        {
            return fileName.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ? 200 : 0;
        }

        internal static string LastSegment(string container)
        {
            if (container == null) return null;
            int i = container.LastIndexOf("::", StringComparison.Ordinal);
            return i < 0 ? container : container.Substring(i + 2);
        }

        internal static bool Subsequence(ReadOnlySpan<byte> text, ReadOnlySpan<byte> sub)
        {
            if (sub.Length == 0) return true;
            if (text.Length < sub.Length) return false;
            int j = 0;
            byte want = sub[0];
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != want) continue;
                if (++j == sub.Length) return true;
                want = sub[j];
            }
            return false;
        }

        internal static bool Subsequence(string text, string sub)
        {
            if (sub.Length == 0) return true;
            int j = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == sub[j] && ++j == sub.Length) return true;
            return false;
        }

        private static Dictionary<string, int> BuildRecentMap(string[] recent)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (recent == null) return map;
            for (int i = 0; i < recent.Length; i++)
            {
                if (string.IsNullOrEmpty(recent[i])) continue;
                string key = PathKey.Normalize(recent[i]);
                if (!map.ContainsKey(key)) map[key] = i;
            }
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

        /// <summary>Keeps (roughly) the N best candidates; trims to N whenever it grows past 2N.</summary>
        internal sealed class TopN
        {
            private readonly int _n;
            private readonly List<Candidate> _items = new List<Candidate>();
            private int _minScore = int.MinValue;

            public TopN(int n) => _n = n;

            /// <summary>Number of exact/prefix/camel-hump candidates offered so far.</summary>
            public int StrongAdds { get; private set; }

            public List<Candidate> Items
            {
                get
                {
                    Trim();
                    return _items;
                }
            }

            public void Add(in Candidate c)
            {
                if (c.Match.Kind >= MatchKind.CamelHump) StrongAdds++;
                if (_items.Count >= _n && c.Score <= _minScore) return;
                _items.Add(c);
                if (_items.Count > _n * 2) Trim();
            }

            private void Trim()
            {
                if (_items.Count <= _n) return;
                _items.Sort(CompareCandidates);
                _items.RemoveRange(_n, _items.Count - _n);
                _minScore = _items[_items.Count - 1].Score;
            }
        }
    }
}
