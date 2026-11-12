using System;
using System.Collections.Generic;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>Inclusive 0-based line range.</summary>
    public readonly struct LineRange : IEquatable<LineRange>
    {
        public LineRange(int start, int end)
        {
            Start = start;
            End = end;
        }

        public int Start { get; }
        public int End { get; }
        public int Count => End >= Start ? End - Start + 1 : 0;
        public bool IsEmpty => End < Start;

        public bool Contains(int line) => line >= Start && line <= End;

        public bool Contains(LineRange r) => r.Start >= Start && r.End <= End;

        public bool Equals(LineRange other) => Start == other.Start && End == other.End;

        public override bool Equals(object obj) => obj is LineRange r && Equals(r);

        public override int GetHashCode() => (Start * 397) ^ End;

        public override string ToString() => "[" + Start + ".." + End + "]";
    }

    /// <summary>
    /// Semantic spans of one document version, by line, for the line ranges that were classified (§7.7: only the viewport
    /// plus a margin is requested). Results for another version reset the store (the host translates the old snapshot's
    /// spans until then). Bounded: <see cref="Trim"/> drops lines far from the viewport. Not thread-safe (UI thread).
    /// </summary>
    public sealed class SemanticLineStore
    {
        private readonly Dictionary<int, List<SemanticSpan>> _lines = new Dictionary<int, List<SemanticSpan>>();
        private readonly List<LineRange> _covered = new List<LineRange>();

        /// <summary>TextVersion the spans belong to; -1 = empty.</summary>
        public long Version { get; private set; } = -1;

        public IReadOnlyList<LineRange> Covered => _covered;

        public int CoveredLines
        {
            get
            {
                int n = 0;
                foreach (var r in _covered) n += r.Count;
                return n;
            }
        }

        public int SpanCount { get; private set; }

        public void Clear()
        {
            _lines.Clear();
            _covered.Clear();
            SpanCount = 0;
            Version = -1;
        }

        /// <summary>Stores the answer for <paramref name="range"/> of <paramref name="version"/> (replacing what that range held).</summary>
        public void Apply(long version, LineRange range, IEnumerable<SemanticSpan> spans)
        {
            if (range.IsEmpty) return;
            if (version != Version)
            {
                Clear();
                Version = version;
            }
            for (int l = range.Start; l <= range.End; l++)
                if (_lines.TryGetValue(l, out var old))
                {
                    SpanCount -= old.Count;
                    _lines.Remove(l);
                }
            if (spans != null)
                foreach (var s in spans)
                {
                    if (s == null || s.Length <= 0 || s.Kind == SemanticKind.None || !range.Contains(s.Line)) continue;
                    if (!_lines.TryGetValue(s.Line, out var list)) _lines[s.Line] = list = new List<SemanticSpan>();
                    list.Add(s);
                    SpanCount++;
                }
            foreach (var list in _lines.Values) list.Sort((a, b) => a.Column.CompareTo(b.Column));
            AddCovered(range);
        }

        private void AddCovered(LineRange r)
        {
            _covered.Add(r);
            _covered.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<LineRange>(_covered.Count);
            foreach (var c in _covered)
            {
                if (merged.Count > 0 && c.Start <= merged[merged.Count - 1].End + 1)
                {
                    var last = merged[merged.Count - 1];
                    merged[merged.Count - 1] = new LineRange(last.Start, Math.Max(last.End, c.End));
                }
                else
                {
                    merged.Add(c);
                }
            }
            _covered.Clear();
            _covered.AddRange(merged);
        }

        /// <summary>Is every line of <paramref name="range"/> classified for <paramref name="version"/>?</summary>
        public bool Covers(long version, LineRange range)
        {
            if (version != Version || range.IsEmpty) return version == Version;
            foreach (var c in _covered)
                if (c.Contains(range)) return true;
            return false;
        }

        public IReadOnlyList<SemanticSpan> Line(int line) =>
            _lines.TryGetValue(line, out var list) ? (IReadOnlyList<SemanticSpan>)list : Array.Empty<SemanticSpan>();

        /// <summary>Forgets everything outside <paramref name="keep"/> (memory bound when the user scrolls through a big file).</summary>
        public void Trim(LineRange keep)
        {
            if (Version < 0) return;
            var drop = new List<int>();
            foreach (var l in _lines.Keys)
                if (!keep.Contains(l)) drop.Add(l);
            foreach (var l in drop)
            {
                SpanCount -= _lines[l].Count;
                _lines.Remove(l);
            }
            var next = new List<LineRange>();
            foreach (var c in _covered)
            {
                int s = Math.Max(c.Start, keep.Start), e = Math.Min(c.End, keep.End);
                if (s <= e) next.Add(new LineRange(s, e));
            }
            _covered.Clear();
            _covered.AddRange(next);
        }
    }

    /// <summary>
    /// Which lines to classify next (§7.7 "只请求可见区域 ± 50 行"): the viewport plus a margin, minus what the store
    /// already has for the current version, bounded to <c>maxLines</c> around the viewport; and merging with a request
    /// still in flight (it is about to be cancelled by the newer one, so the newer one takes over its lines). Pure.
    /// </summary>
    public static class VisibleRangePlanner
    {
        public const int DefaultMargin = 50;
        public const int DefaultMaxLines = 600;

        /// <summary>Viewport ± margin, clamped to the document.</summary>
        public static LineRange Desired(int firstVisible, int lastVisible, int lineCount, int margin)
        {
            if (lineCount <= 0) return new LineRange(0, -1);
            if (lastVisible < firstVisible) lastVisible = firstVisible;
            margin = Math.Max(0, margin);
            return new LineRange(Math.Max(0, firstVisible - margin), Math.Min(lineCount - 1, lastVisible + margin));
        }

        /// <summary>The range to request now, or null when the visible lines (and margin) are already classified.</summary>
        public static LineRange? Plan(int firstVisible, int lastVisible, int lineCount, long version, SemanticLineStore store,
                                      int margin = DefaultMargin, int maxLines = DefaultMaxLines)
        {
            var desired = Desired(firstVisible, lastVisible, lineCount, margin);
            if (desired.IsEmpty) return null;
            LineRange need = desired;
            if (store != null && store.Version == version)
            {
                // Only the part of the desired range that is not covered yet.
                int s = -1, e = -1;
                for (int l = desired.Start; l <= desired.End; l++)
                {
                    if (IsCovered(store, l)) continue;
                    if (s < 0) s = l;
                    e = l;
                }
                if (s < 0) return null;
                need = new LineRange(s, e);
            }
            return Clamp(need, firstVisible, lastVisible, maxLines);
        }

        private static bool IsCovered(SemanticLineStore store, int line)
        {
            foreach (var c in store.Covered)
                if (c.Contains(line)) return true;
            return false;
        }

        /// <summary>At most <paramref name="maxLines"/> lines, keeping the viewport centre inside.</summary>
        public static LineRange Clamp(LineRange r, int firstVisible, int lastVisible, int maxLines)
        {
            if (maxLines <= 0 || r.Count <= maxLines) return r;
            int centre = (Math.Max(r.Start, firstVisible) + Math.Min(r.End, Math.Max(firstVisible, lastVisible))) / 2;
            int start = Math.Max(r.Start, centre - maxLines / 2);
            int end = Math.Min(r.End, start + maxLines - 1);
            start = Math.Max(r.Start, end - maxLines + 1);
            return new LineRange(start, end);
        }

        /// <summary>
        /// Merges the next request with one still in flight for the same version (the gate cancels the old one): the union
        /// when they overlap or are close (≤ <paramref name="gap"/> lines apart) and the result stays within
        /// <paramref name="maxLines"/>; otherwise just the next one.
        /// </summary>
        public static LineRange Merge(LineRange? inFlight, long inFlightVersion, LineRange next, long nextVersion, int maxLines = DefaultMaxLines, int gap = 20)
        {
            if (inFlight == null || inFlightVersion != nextVersion || inFlight.Value.IsEmpty) return next;
            var a = inFlight.Value;
            if (a.Start > next.End + gap + 1 || next.Start > a.End + gap + 1) return next;
            var u = new LineRange(Math.Min(a.Start, next.Start), Math.Max(a.End, next.End));
            return u.Count <= maxLines ? u : next;
        }
    }
}
