using System;
using System.Collections.Generic;

namespace UEToVS.Core.Matching
{
    /// <summary>
    /// Rider/IntelliJ style name matcher: exact, prefix, camel-hump ("PCU" -> PlayerCameraUpdate,
    /// "gAC" -> GetActorComponent), substring and '*'/space wildcards.
    /// A matcher instance is built once per query and reused for every candidate.
    /// A tolerant matcher (code completion) also accepts, below every other kind, letters in order after a word start
    /// ("umcmp" -> UMeshComponent) and typos in the leading part of the name ("Umehs" -> UMeshComponent: swapped,
    /// missing, extra or wrong letters, 1 edit from 5 letters, 2 from 8); the first letter must be right.
    /// </summary>
    public sealed class FuzzyMatcher
    {
        private const int ExactScore = 100000;
        private const int PrefixScore = 80000;
        private const int CamelScore = 60000;
        private const int SubstringScore = 30000;
        private const int WildcardScore = 20000;
        private const int SubsequenceScore = 15000;
        private const int TypoScore = 10000;

        /// <summary>Typo matching starts at this pattern length (shorter patterns would match almost everything).</summary>
        public const int TypoMinLength = 5;

        /// <summary>Two typos are allowed from this pattern length.</summary>
        public const int TypoTwoEditsLength = 8;

        [ThreadStatic] private static int[] _row0;
        [ThreadStatic] private static int[] _row1;
        [ThreadStatic] private static int[] _row2;

        private readonly string _pattern;
        private readonly string[] _fragments;
        private readonly bool _hasWildcard;
        private readonly bool _tolerant;

        public FuzzyMatcher(string pattern) : this(pattern, false)
        {
        }

        /// <param name="tolerant">Also accept subsequence and typo matches (ranked below every other kind).</param>
        public FuzzyMatcher(string pattern, bool tolerant)
        {
            _tolerant = tolerant;
            pattern = pattern ?? string.Empty;
            _pattern = pattern.Trim();
            _hasWildcard = _pattern.IndexOf('*') >= 0 || _pattern.IndexOf(' ') >= 0;
            _fragments = _pattern.Split(new[] { '*', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public string Pattern => _pattern;

        public bool IsEmpty => _fragments.Length == 0;

        public MatchResult Match(string name)
        {
            if (string.IsNullOrEmpty(name) || IsEmpty) return MatchResult.NoMatch;
            if (_hasWildcard) return MatchWildcard(name);
            var m = MatchSingle(_pattern, name);
            if (m.IsMatch || !_tolerant) return m;
            return MatchTolerant(_pattern, name);
        }

        /// <summary>Allowed typos for a pattern length (0 = typo matching off for it).</summary>
        public static int MaxTypos(int patternLength) =>
            patternLength >= TypoTwoEditsLength ? 2 : patternLength >= TypoMinLength ? 1 : 0;

        private static MatchResult MatchTolerant(string pattern, string name)
        {
            if (pattern.Length >= 2)
            {
                var sub = MatchSubsequence(pattern, name);
                if (sub != null)
                {
                    int score = SubsequenceScore - sub.Count * 200 - Math.Min(name.Length, 500) - sub[0].Start * 10;
                    return new MatchResult(MatchKind.Subsequence, score, sub);
                }
            }
            int max = MaxTypos(pattern.Length);
            if (max == 0) return MatchResult.NoMatch;
            int d = TypoDistance(pattern, name, max, out int end);
            if (d < 0) return MatchResult.NoMatch;
            return new MatchResult(MatchKind.Typo, TypoScore - d * 1000 - Math.Min(name.Length, 500), TypoRanges(pattern, name, end));
        }

        /// <summary>
        /// Case-insensitive letters in order; the first one at a word start. Prefers later letters at word starts
        /// (fewer, longer runs) by trying every word start of the first letter and keeping the fewest runs.
        /// </summary>
        public static List<TextRange> MatchSubsequence(string pattern, string name)
        {
            if (pattern.Length == 0 || pattern.Length > name.Length) return null;
            List<TextRange> best = null;
            var positions = new int[pattern.Length];
            for (int start = 0; start <= name.Length - pattern.Length; start++)
            {
                if (!IsWordStart(name, start) || !EqIgnoreCase(pattern[0], name[start])) continue;
                positions[0] = start;
                int pi = 1;
                for (int ni = start + 1; ni < name.Length && pi < pattern.Length; ni++)
                    if (EqIgnoreCase(pattern[pi], name[ni])) positions[pi++] = ni;
                if (pi < pattern.Length) break; // a later start only has fewer letters left
                var ranges = ToRanges(positions);
                if (best == null || ranges.Count < best.Count) best = ranges;
                if (best.Count == 1) break;
            }
            return best;
        }

        /// <summary>
        /// Smallest optimal-string-alignment distance (insert / delete / substitute / swap adjacent) between the pattern
        /// and any leading part of <paramref name="name"/>, case-insensitive, with the first letter fixed; -1 when above
        /// <paramref name="max"/>. <paramref name="end"/> = length of the best leading part.
        /// </summary>
        public static int TypoDistance(string pattern, string name, int max, out int end)
        {
            end = 0;
            int m = pattern.Length;
            if (m == 0 || name.Length == 0 || !EqIgnoreCase(pattern[0], name[0])) return -1;
            int n = Math.Min(name.Length, m + max);
            if (n < m - max) return -1;
            var prev2 = Row(ref _row0, m + 1);
            var prev = Row(ref _row1, m + 1);
            var cur = Row(ref _row2, m + 1);
            // Rows run over the name (j), columns over the pattern (i): D[j][i].
            for (int i = 0; i <= m; i++) prev[i] = i;
            int best = int.MaxValue;
            if (m <= max) { best = m; end = 0; }
            for (int j = 1; j <= n; j++)
            {
                cur[0] = j;
                int rowMin = cur[0];
                char nc = char.ToLowerInvariant(name[j - 1]);
                for (int i = 1; i <= m; i++)
                {
                    char pc = char.ToLowerInvariant(pattern[i - 1]);
                    int cost = pc == nc ? 0 : 1;
                    int v = Math.Min(Math.Min(prev[i] + 1, cur[i - 1] + 1), prev[i - 1] + cost);
                    if (i > 1 && j > 1 && pc == char.ToLowerInvariant(name[j - 2]) && char.ToLowerInvariant(pattern[i - 2]) == nc)
                        v = Math.Min(v, prev2[i - 2] + 1);
                    cur[i] = v;
                    if (v < rowMin) rowMin = v;
                }
                if (cur[m] < best || cur[m] == best && j <= m)
                {
                    best = cur[m];
                    end = j;
                }
                if (rowMin > max) break;
                var t = prev2;
                prev2 = prev;
                prev = cur;
                cur = t;
            }
            return best <= max ? best : -1;
        }

        private static int[] Row(ref int[] row, int length)
        {
            if (row == null || row.Length < length) row = new int[Math.Max(length, 64)];
            return row;
        }

        /// <summary>Highlight for a typo match: the letters of the matched leading part that occur in the pattern.</summary>
        private static List<TextRange> TypoRanges(string pattern, string name, int end)
        {
            var list = new List<TextRange>();
            string lower = pattern.ToLowerInvariant();
            for (int i = 0; i < end && i < name.Length; i++)
            {
                if (lower.IndexOf(char.ToLowerInvariant(name[i])) < 0) continue;
                if (list.Count > 0 && list[list.Count - 1].End == i)
                {
                    var last = list[list.Count - 1];
                    list[list.Count - 1] = new TextRange(last.Start, last.Length + 1);
                }
                else list.Add(new TextRange(i, 1));
            }
            if (list.Count == 0) list.Add(new TextRange(0, 1));
            return list;
        }

        private static bool EqIgnoreCase(char a, char b) => a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

        /// <summary>
        /// Letters of the pattern whose case differs from the name letter they matched (wildcards and spaces skipped);
        /// -1 when the highlight does not pair one name letter with every pattern letter (typo matches).
        /// <paramref name="firstDiffers"/>: the first pattern letter's case differs.
        /// </summary>
        public static int CaseMismatches(string pattern, string name, IReadOnlyList<TextRange> ranges, out bool firstDiffers)
        {
            firstDiffers = false;
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(name) || ranges == null || ranges.Count == 0) return -1;
            int total = 0;
            foreach (var r in ranges) total += r.Length;
            int letters = 0;
            foreach (char ch in pattern)
                if (ch != '*' && ch != ' ') letters++;
            if (total != letters) return -1;
            int pi = 0, mismatches = 0;
            foreach (var r in ranges)
                for (int k = r.Start; k < r.End; k++)
                {
                    while (pattern[pi] == '*' || pattern[pi] == ' ') pi++;
                    if (k < 0 || k >= name.Length) return -1;
                    if (pattern[pi] != name[k])
                    {
                        mismatches++;
                        if (pi == 0 || IsFirstLetter(pattern, pi)) firstDiffers = true;
                    }
                    pi++;
                }
            return mismatches;
        }

        private static bool IsFirstLetter(string pattern, int index)
        {
            for (int i = 0; i < index; i++)
                if (pattern[i] != '*' && pattern[i] != ' ') return false;
            return true;
        }

        /// <summary>Matches a name that may carry a UE type prefix (AActor, UObject, FVector...).</summary>
        public MatchResult MatchUnreal(string name)
        {
            var direct = Match(name);
            var stripped = UnrealNaming.StripTypePrefix(name);
            if (stripped == null) return direct;
            var alt = Match(stripped);
            if (!alt.IsMatch) return direct;
            alt = alt.ShiftRanges(1).WithPenalty(5);
            return direct.IsMatch && direct.Score >= alt.Score ? direct : alt;
        }

        private static MatchResult MatchSingle(string pattern, string name)
        {
            int len = name.Length;
            if (string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase))
            {
                int bonus = string.Equals(pattern, name, StringComparison.Ordinal) ? 100 : 0;
                return new MatchResult(MatchKind.Exact, ExactScore + bonus, new[] { new TextRange(0, len) });
            }

            if (pattern.Length < len && RegionMatches(pattern, name, 0))
            {
                int bonus = name.StartsWith(pattern, StringComparison.Ordinal) ? 100 : 0;
                return new MatchResult(MatchKind.Prefix, PrefixScore + bonus - Math.Min(len, 500), new[] { new TextRange(0, pattern.Length) });
            }

            var camel = MatchCamelHumps(pattern, name);
            if (camel != null)
            {
                // fewer fragments = more consecutive characters = better
                int score = CamelScore - camel.Count * 200 - Math.Min(len, 500) - camel[0].Start * 10;
                return new MatchResult(MatchKind.CamelHump, score, camel);
            }

            int idx = IndexOfPattern(pattern, name, 0);
            if (idx >= 0)
            {
                int score = SubstringScore - idx * 10 - Math.Min(len, 500);
                if (IsWordStart(name, idx)) score += 2000;
                return new MatchResult(MatchKind.Substring, score, new[] { new TextRange(idx, pattern.Length) });
            }

            return MatchResult.NoMatch;
        }

        private MatchResult MatchWildcard(string name)
        {
            var ranges = new List<TextRange>();
            int pos = 0;
            for (int f = 0; f < _fragments.Length; f++)
            {
                string frag = _fragments[f];
                if (f == 0 && _pattern[0] != '*' && _pattern[0] != ' ')
                {
                    // first fragment anchors at a word start (camel hump allowed)
                    var head = MatchCamelHumpsFrom(frag, name, 0);
                    if (head == null)
                    {
                        int i = IndexOfPattern(frag, name, 0);
                        if (i < 0) return MatchResult.NoMatch;
                        head = new List<TextRange> { new TextRange(i, frag.Length) };
                    }
                    ranges.AddRange(head);
                    pos = head[head.Count - 1].End;
                    continue;
                }

                var part = MatchCamelHumpsFrom(frag, name, pos);
                if (part != null)
                {
                    ranges.AddRange(part);
                    pos = part[part.Count - 1].End;
                    continue;
                }

                int idx = IndexOfPattern(frag, name, pos);
                if (idx < 0) return MatchResult.NoMatch;
                ranges.Add(new TextRange(idx, frag.Length));
                pos = idx + frag.Length;
            }

            int score = WildcardScore - ranges.Count * 100 - Math.Min(name.Length, 500);
            return new MatchResult(MatchKind.Wildcard, score, Merge(ranges));
        }

        public static List<TextRange> MatchCamelHumps(string pattern, string name) => MatchCamelHumpsFrom(pattern, name, 0);

        /// <summary>
        /// Every pattern character must either continue the previous matched run or start a word
        /// in <paramref name="name"/>. The first pattern character must start a word.
        /// </summary>
        public static List<TextRange> MatchCamelHumpsFrom(string pattern, string name, int from)
        {
            if (pattern.Length == 0 || pattern.Length > name.Length - from) return null;
            for (int start = from; start < name.Length; start++)
            {
                if (!IsWordStart(name, start) || !CharEq(pattern[0], name[start])) continue;
                var positions = new int[pattern.Length];
                positions[0] = start;
                if (Extend(pattern, name, 1, start, positions))
                    return ToRanges(positions);
            }
            return null;
        }

        private static bool Extend(string pattern, string name, int pi, int lastNi, int[] positions)
        {
            if (pi == pattern.Length) return true;
            char pc = pattern[pi];

            // Prefer continuing the current run.
            int next = lastNi + 1;
            if (next < name.Length && CharEq(pc, name[next]))
            {
                positions[pi] = next;
                if (Extend(pattern, name, pi + 1, next, positions)) return true;
            }

            // Otherwise jump to a later word start.
            for (int ni = lastNi + 2; ni < name.Length; ni++)
            {
                if (!IsWordStart(name, ni) || !CharEq(pc, name[ni])) continue;
                positions[pi] = ni;
                if (Extend(pattern, name, pi + 1, ni, positions)) return true;
            }
            return false;
        }

        private static bool RegionMatches(string pattern, string name, int at)
        {
            if (at + pattern.Length > name.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
                if (!CharEq(pattern[i], name[at + i])) return false;
            return true;
        }

        private static int IndexOfPattern(string pattern, string name, int from)
        {
            for (int i = from; i + pattern.Length <= name.Length; i++)
                if (RegionMatches(pattern, name, i)) return i;
            return -1;
        }

        private static bool CharEq(char p, char n)
        {
            if (p == n) return true;
            // An uppercase pattern char only matches uppercase/word starts; lowercase matches either case.
            return (char.ToLowerInvariant(p) == char.ToLowerInvariant(n) && !char.IsUpper(p)) || char.ToUpperInvariant(p) == n;
        }

        public static bool IsWordStart(string s, int i)
        {
            if (i == 0) return true;
            char c = s[i];
            char prev = s[i - 1];
            if (!char.IsLetterOrDigit(c)) return false;
            if (!char.IsLetterOrDigit(prev)) return true; // after _ . : / etc.
            if (char.IsUpper(c) && char.IsLower(prev)) return true;
            if (char.IsDigit(c) && !char.IsDigit(prev)) return true;
            if (char.IsLetter(c) && char.IsDigit(prev)) return true;
            // HTTPServer: 'S' starts a word because next char is lower and prev is upper
            if (char.IsUpper(c) && char.IsUpper(prev) && i + 1 < s.Length && char.IsLower(s[i + 1])) return true;
            return false;
        }

        private static List<TextRange> ToRanges(int[] positions)
        {
            var list = new List<TextRange>();
            int start = positions[0], len = 1;
            for (int i = 1; i < positions.Length; i++)
            {
                if (positions[i] == positions[i - 1] + 1) { len++; continue; }
                list.Add(new TextRange(start, len));
                start = positions[i];
                len = 1;
            }
            list.Add(new TextRange(start, len));
            return list;
        }

        private static List<TextRange> Merge(List<TextRange> ranges)
        {
            var result = new List<TextRange>();
            foreach (var r in ranges)
            {
                if (result.Count > 0 && result[result.Count - 1].End == r.Start)
                {
                    var last = result[result.Count - 1];
                    result[result.Count - 1] = new TextRange(last.Start, last.Length + r.Length);
                }
                else result.Add(r);
            }
            return result;
        }
    }
}
