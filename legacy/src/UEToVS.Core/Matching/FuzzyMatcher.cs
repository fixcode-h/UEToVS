using System;
using System.Collections.Generic;

namespace UEToVS.Core.Matching
{
    /// <summary>
    /// Rider/IntelliJ style name matcher: exact, prefix, camel-hump ("PCU" -> PlayerCameraUpdate,
    /// "gAC" -> GetActorComponent), substring and '*'/space wildcards.
    /// A matcher instance is built once per query and reused for every candidate.
    /// </summary>
    public sealed class FuzzyMatcher
    {
        private const int ExactScore = 100000;
        private const int PrefixScore = 80000;
        private const int CamelScore = 60000;
        private const int SubstringScore = 30000;
        private const int WildcardScore = 20000;

        private readonly string _pattern;
        private readonly string[] _fragments;
        private readonly bool _hasWildcard;

        public FuzzyMatcher(string pattern)
        {
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
            return _hasWildcard ? MatchWildcard(name) : MatchSingle(_pattern, name);
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
