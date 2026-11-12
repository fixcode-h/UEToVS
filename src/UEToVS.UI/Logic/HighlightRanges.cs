using System;
using System.Collections.Generic;

namespace UEToVS.UI.Logic
{
    /// <summary>A half-open character range [Start, Start + Length).</summary>
    public readonly struct TextRange : IEquatable<TextRange>
    {
        public readonly int Start;
        public readonly int Length;

        public TextRange(int start, int length)
        {
            Start = start;
            Length = length;
        }

        public int End => Start + Length;

        public bool Equals(TextRange o) => Start == o.Start && Length == o.Length;
        public override bool Equals(object obj) => obj is TextRange o && Equals(o);
        public override int GetHashCode() => (Start * 397) ^ Length;
        public override string ToString() => "[" + Start + "," + End + ")";
    }

    /// <summary>One piece of text to render: either plain or matched.</summary>
    public readonly struct TextSegment
    {
        public readonly int Start;
        public readonly int Length;
        public readonly bool IsMatch;

        public TextSegment(int start, int length, bool isMatch)
        {
            Start = start;
            Length = length;
            IsMatch = isMatch;
        }

        public override string ToString() => (IsMatch ? "*" : "") + "[" + Start + "," + (Start + Length) + ")";
    }

    /// <summary>Normalizes match ranges (clamp, sort, merge overlapping/adjacent) and splits text into segments.</summary>
    public static class HighlightRanges
    {
        /// <summary>
        /// Clamps ranges to [0, textLength), drops empty ones, sorts and merges overlapping or touching ranges.
        /// Result is sorted, non-overlapping, non-adjacent.
        /// </summary>
        public static List<TextRange> Normalize(IEnumerable<TextRange> ranges, int textLength)
        {
            var list = new List<TextRange>();
            if (ranges == null || textLength <= 0) return list;
            foreach (var r in ranges)
            {
                int s = Math.Max(0, r.Start);
                int e = Math.Min(textLength, r.Length < 0 ? s : (int)Math.Min((long)r.Start + r.Length, int.MaxValue));
                if (e > s) list.Add(new TextRange(s, e - s));
            }
            if (list.Count <= 1) return list;
            list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
            var merged = new List<TextRange>(list.Count);
            var cur = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                var r = list[i];
                if (r.Start <= cur.End)
                {
                    int end = Math.Max(cur.End, r.End);
                    cur = new TextRange(cur.Start, end - cur.Start);
                }
                else
                {
                    merged.Add(cur);
                    cur = r;
                }
            }
            merged.Add(cur);
            return merged;
        }

        /// <summary>Converts the legacy flat [start, len, start, len, …] array form.</summary>
        public static List<TextRange> FromPairs(int[] pairs)
        {
            var list = new List<TextRange>();
            if (pairs == null) return list;
            for (int i = 0; i + 1 < pairs.Length; i += 2) list.Add(new TextRange(pairs[i], pairs[i + 1]));
            return list;
        }

        /// <summary>Splits [0, textLength) into alternating plain/match segments covering the whole text.</summary>
        public static List<TextSegment> Segments(IEnumerable<TextRange> ranges, int textLength)
        {
            var result = new List<TextSegment>();
            if (textLength <= 0) return result;
            int pos = 0;
            foreach (var r in Normalize(ranges, textLength))
            {
                if (r.Start > pos) result.Add(new TextSegment(pos, r.Start - pos, false));
                result.Add(new TextSegment(r.Start, r.Length, true));
                pos = r.End;
            }
            if (pos < textLength) result.Add(new TextSegment(pos, textLength - pos, false));
            return result;
        }

        /// <summary>
        /// Simple case-insensitive subsequence matcher used by the components when a feature supplies no ranges
        /// (e.g. chooser filtering). Prefers contiguous runs and word starts. Returns false when not all query chars match.
        /// </summary>
        public static bool TryMatchSubsequence(string text, string query, out List<TextRange> ranges)
        {
            ranges = new List<TextRange>();
            if (string.IsNullOrEmpty(query)) return true;
            if (string.IsNullOrEmpty(text)) return false;
            int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                ranges.Add(new TextRange(idx, query.Length));
                return true;
            }
            int ti = 0;
            int runStart = -1, runLen = 0;
            foreach (char qc in query)
            {
                if (char.IsWhiteSpace(qc)) continue;
                bool found = false;
                while (ti < text.Length)
                {
                    if (char.ToUpperInvariant(text[ti]) == char.ToUpperInvariant(qc))
                    {
                        if (runStart >= 0 && runStart + runLen == ti) runLen++;
                        else
                        {
                            if (runStart >= 0) ranges.Add(new TextRange(runStart, runLen));
                            runStart = ti;
                            runLen = 1;
                        }
                        ti++;
                        found = true;
                        break;
                    }
                    ti++;
                }
                if (!found) { ranges.Clear(); return false; }
            }
            if (runStart >= 0) ranges.Add(new TextRange(runStart, runLen));
            return true;
        }
    }
}
