using System;
using System.Collections.Generic;
using UEToVS.Core.Matching;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// Most-recently committed completion labels (VA "recently used first"). Bounded LRU; thread-safe. The newest commit
    /// has the highest <see cref="Rank"/>; labels that were never committed rank 0.
    /// </summary>
    public sealed class RecentCompletions
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, long> _stamp = new Dictionary<string, long>(StringComparer.Ordinal);
        private long _clock;

        public RecentCompletions(int capacity = 200)
        {
            Capacity = Math.Max(1, capacity);
        }

        public int Capacity { get; }

        public int Count
        {
            get
            {
                lock (_gate) return _stamp.Count;
            }
        }

        public void Record(string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            lock (_gate)
            {
                _stamp[label] = ++_clock;
                if (_stamp.Count <= Capacity) return;
                string oldest = null;
                long min = long.MaxValue;
                foreach (var kv in _stamp)
                    if (kv.Value < min)
                    {
                        min = kv.Value;
                        oldest = kv.Key;
                    }
                if (oldest != null) _stamp.Remove(oldest);
            }
        }

        /// <summary>0 = never used; otherwise 1..Capacity, higher = more recent.</summary>
        public int Rank(string label)
        {
            if (string.IsNullOrEmpty(label)) return 0;
            lock (_gate)
            {
                if (!_stamp.TryGetValue(label, out long s)) return 0;
                long age = _clock - s; // 0 = newest
                return (int)Math.Max(1, Capacity - Math.Min(age, Capacity - 1));
            }
        }

        public void Clear()
        {
            lock (_gate) _stamp.Clear();
        }
    }

    /// <summary>What the ranker needs to know about one completion item (ours or another source's).</summary>
    public struct RankCandidate
    {
        public string FilterText;

        /// <summary>Engine ranking (higher first); 0 for items of other sources.</summary>
        public int EngineScore;

        /// <summary>Member of a base class (ranks after the type's own members).</summary>
        public bool Inherited;

        /// <summary>Keyword / snippet (ranks after symbols).</summary>
        public bool Keyword;

        /// <summary>Item from another completion source (VS's own C++ list while it coexists).</summary>
        public bool Foreign;
    }

    /// <summary>One entry of the ranked list: index into the candidates plus the matched character ranges (bold in the UI).</summary>
    public readonly struct RankedItem
    {
        public RankedItem(int index, MatchKind kind, int matchScore, IReadOnlyList<TextRange> ranges)
        {
            Index = index;
            Kind = kind;
            MatchScore = matchScore;
            Ranges = ranges;
        }

        public int Index { get; }
        public MatchKind Kind { get; }
        public int MatchScore { get; }
        public IReadOnlyList<TextRange> Ranges { get; }
    }

    /// <summary>
    /// Local re-ranking of the completion list on every keystroke (§7.6, VA style): exact &gt; prefix &gt; camel humps &gt;
    /// substring (UE prefixes optional: "Actor" finds AActor), then own members &gt; inherited &gt; keywords / other sources,
    /// then recently used, then the engine's score, then the finer match score, shorter, alphabetical. With an empty
    /// filter nothing is removed. Pure.
    /// </summary>
    public static class CompletionRanker
    {
        public static int Tier(MatchKind kind)
        {
            switch (kind)
            {
                case MatchKind.Exact: return 4;
                case MatchKind.Prefix: return 3;
                case MatchKind.CamelHump: return 2;
                case MatchKind.Substring:
                case MatchKind.Wildcard: return 1;
                default: return 0;
            }
        }

        /// <summary>0 = foreign / keyword, 1 = inherited, 2 = own member or symbol.</summary>
        public static int Origin(in RankCandidate c) => c.Foreign || c.Keyword ? 0 : c.Inherited ? 1 : 2;

        /// <summary>Filters (non-empty pattern) and orders <paramref name="items"/>; at most <paramref name="max"/> entries.</summary>
        public static List<RankedItem> Rank(IReadOnlyList<RankCandidate> items, string pattern, RecentCompletions recent, int max = int.MaxValue)
        {
            var result = new List<RankedItem>(items?.Count ?? 0);
            if (items == null || items.Count == 0) return result;
            var matcher = new FuzzyMatcher(pattern ?? string.Empty);
            bool empty = matcher.IsEmpty;
            var recency = new int[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                string text = items[i].FilterText ?? string.Empty;
                if (empty)
                {
                    result.Add(new RankedItem(i, MatchKind.None, 0, Array.Empty<TextRange>()));
                }
                else
                {
                    var m = matcher.MatchUnreal(text);
                    if (!m.IsMatch) continue;
                    result.Add(new RankedItem(i, m.Kind, m.Score, m.Ranges));
                }
                recency[i] = recent?.Rank(text) ?? 0;
            }
            result.Sort((a, b) =>
            {
                var ca = items[a.Index];
                var cb = items[b.Index];
                int c = Tier(b.Kind).CompareTo(Tier(a.Kind));
                if (c != 0) return c;
                c = Origin(cb).CompareTo(Origin(ca));
                if (c != 0) return c;
                c = recency[b.Index].CompareTo(recency[a.Index]);
                if (c != 0) return c;
                c = cb.EngineScore.CompareTo(ca.EngineScore);
                if (c != 0) return c;
                c = b.MatchScore.CompareTo(a.MatchScore);
                if (c != 0) return c;
                string ta = ca.FilterText ?? string.Empty, tb = cb.FilterText ?? string.Empty;
                c = ta.Length.CompareTo(tb.Length);
                if (c != 0) return c;
                c = string.Compare(ta, tb, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            });
            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }
    }
}
