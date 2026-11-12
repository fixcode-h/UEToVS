using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.IntelliSense;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>
    /// Inlay hints of one document, cached per text version (v0.4.5). Scrolling within lines already computed for the current
    /// version costs nothing; scrolling out asks only for the new visible lines (± a margin) and merges them in; an edit (new
    /// version) starts over. Bounded: beyond <see cref="MaxLines"/> covered lines the cache keeps only the newest range. Pure;
    /// one per view, UI thread.
    /// </summary>
    public sealed class InlayHintStore
    {
        public const int MarginLines = 30;
        public const int MaxLines = 4000;

        private readonly List<LineRange> _covered = new List<LineRange>();
        private readonly SortedDictionary<int, List<InlayHint>> _byLine = new SortedDictionary<int, List<InlayHint>>();

        /// <summary>Text version the cached hints belong to (-1 = nothing cached).</summary>
        public long Version { get; private set; } = -1;

        public int Count { get; private set; }

        public IReadOnlyList<LineRange> Covered => _covered;

        /// <summary>The lines to request for the visible range, or an empty range when they are all cached for <paramref name="version"/>.</summary>
        public LineRange Missing(long version, int firstVisible, int lastVisible, int lineCount)
        {
            if (lineCount <= 0) return new LineRange(0, -1);
            int first = Math.Max(0, Math.Min(firstVisible, lastVisible));
            int last = Math.Min(lineCount - 1, Math.Max(firstVisible, lastVisible));
            if (version == Version && _covered.Any(c => c.Contains(new LineRange(first, last)))) return new LineRange(0, -1);
            return CodeDiagnosticsPlan.Range(first, last, lineCount, MarginLines);
        }

        /// <summary>Stores the answer for <paramref name="range"/> of <paramref name="version"/> (an older version is ignored).</summary>
        public bool Add(long version, LineRange range, IEnumerable<InlayHint> items)
        {
            if (version < Version) return false;
            if (version != Version || CoveredLines() + range.Count > MaxLines) Clear(version);
            foreach (var line in _byLine.Keys.Where(range.Contains).ToList())
            {
                Count -= _byLine[line].Count;
                _byLine.Remove(line);
            }
            foreach (var h in items ?? Enumerable.Empty<InlayHint>())
            {
                if (h == null || !range.Contains(h.Line) || string.IsNullOrEmpty(h.Text)) continue;
                if (!_byLine.TryGetValue(h.Line, out var list)) _byLine[h.Line] = list = new List<InlayHint>();
                list.Add(h);
                Count++;
            }
            Merge(range);
            return true;
        }

        public void Clear(long version = -1)
        {
            Version = version;
            _covered.Clear();
            _byLine.Clear();
            Count = 0;
        }

        /// <summary>Hints on lines [<paramref name="first"/>, <paramref name="last"/>], in line / column order.</summary>
        public IEnumerable<InlayHint> Between(int first, int last)
        {
            foreach (var kv in _byLine)
            {
                if (kv.Key < first) continue;
                if (kv.Key > last) yield break;
                foreach (var h in kv.Value.OrderBy(x => x.Column)) yield return h;
            }
        }

        private int CoveredLines() => _covered.Sum(c => c.Count);

        private void Merge(LineRange r)
        {
            _covered.Add(r);
            _covered.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = _covered.Count - 1; i > 0; i--)
            {
                var a = _covered[i - 1];
                var b = _covered[i];
                if (b.Start > a.End + 1) continue;
                _covered[i - 1] = new LineRange(a.Start, Math.Max(a.End, b.End));
                _covered.RemoveAt(i);
            }
        }

        /// <summary>Text drawn in the editor: "Name:" before an argument, ": Type" after a variable.</summary>
        public static string Display(InlayHint h)
        {
            if (h == null) return string.Empty;
            return h.Kind == InlayHintKind.Type ? ": " + h.Text : h.Text + ":";
        }
    }
}
