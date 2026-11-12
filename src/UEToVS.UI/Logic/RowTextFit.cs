using System;
using System.Collections.Generic;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// Text shown after a middle trim: <c>source[0, HeadLength) + Middle + source[TailStart, end)</c>. When nothing was
    /// trimmed <see cref="Middle"/> is empty and the head is the whole source.
    /// </summary>
    public readonly struct MiddleTrimResult
    {
        public const string Ellipsis = "…";

        public readonly string Text;
        public readonly int HeadLength;
        public readonly int TailStart;
        public readonly string Middle;
        public readonly int SourceLength;

        public MiddleTrimResult(string source, int headLength, string middle, int tailStart)
        {
            source = source ?? string.Empty;
            SourceLength = source.Length;
            HeadLength = Math.Max(0, Math.Min(headLength, source.Length));
            TailStart = Math.Max(HeadLength, Math.Min(tailStart, source.Length));
            Middle = middle ?? string.Empty;
            Text = Middle.Length == 0 && TailStart == HeadLength
                ? (HeadLength == source.Length ? source : source.Substring(0, HeadLength))
                : source.Substring(0, HeadLength) + Middle + source.Substring(TailStart);
        }

        public static MiddleTrimResult Whole(string source) => new MiddleTrimResult(source, (source ?? string.Empty).Length, string.Empty, (source ?? string.Empty).Length);

        public bool IsTrimmed => Text.Length != SourceLength || Middle.Length > 0;

        /// <summary>Maps highlight ranges of the source onto <see cref="Text"/> (parts that were cut are dropped).</summary>
        public IReadOnlyList<TextRange> MapRanges(IReadOnlyList<TextRange> ranges)
        {
            if (ranges == null || ranges.Count == 0 || !IsTrimmed) return ranges;
            var list = new List<TextRange>(ranges.Count);
            int shift = HeadLength + Middle.Length - TailStart;
            foreach (var r in ranges)
            {
                int s = Math.Max(0, r.Start), e = Math.Min(SourceLength, r.End);
                if (e <= s) continue;
                // Head part.
                int hs = s, he = Math.Min(e, HeadLength);
                if (he > hs) list.Add(new TextRange(hs, he - hs));
                // Tail part.
                int ts = Math.Max(s, TailStart), te = e;
                if (te > ts) list.Add(new TextRange(ts + shift, te - ts));
            }
            return list.Count == 0 ? null : list;
        }

        public override string ToString() => Text;
    }

    /// <summary>Widths of the columns of a list row (see <see cref="RowTextFit.Allocate"/>).</summary>
    public readonly struct RowColumns
    {
        public readonly double Primary;
        public readonly double Secondary;
        public readonly double RightText;

        public RowColumns(double primary, double secondary, double rightText)
        {
            Primary = primary;
            Secondary = secondary;
            RightText = rightText;
        }

        public override string ToString() => "P=" + Primary + " S=" + Secondary + " R=" + RightText;
    }

    /// <summary>
    /// Pure layout rules of a list row (name · location ……… badges · right text): the name gets its full width first,
    /// the location takes what is left and is trimmed in the middle (path aware: the first and the last folders stay),
    /// the right-hand text keeps its natural width unless the name could not fit otherwise. WPF-free so it is tested on
    /// net8.0; the row measures text with the real typeface and passes a width function in.
    /// </summary>
    public static class RowTextFit
    {
        /// <summary>Below this the secondary column is hidden instead of showing a lone "…".</summary>
        public const double MinSecondary = 28;

        /// <summary>The right text may shrink to this share of the row when the name does not fit.</summary>
        public const double MinRightShare = 0.22;

        /// <summary>Slack added to measured text widths so layout rounding never triggers WPF's own ellipsis.</summary>
        public const double Slack = 1;

        /// <summary>
        /// Splits <paramref name="available"/> (row width minus the fixed left part, fixed right parts and the gaps)
        /// between primary, secondary and the right text. Wanted widths are natural (untrimmed) text widths.
        /// </summary>
        public static RowColumns Allocate(double available, double wantPrimary, double wantSecondary, double wantRight, double gap)
        {
            wantPrimary = Pos(wantPrimary);
            wantSecondary = Pos(wantSecondary);
            wantRight = Pos(wantRight);
            gap = Pos(gap);
            if (double.IsInfinity(available) || double.IsNaN(available)) return new RowColumns(wantPrimary, wantSecondary, wantRight);
            available = Pos(available);

            double right = wantRight;
            double rightGap = wantRight > 0 ? gap : 0;
            double forText = Math.Max(0, available - right - rightGap);
            if (wantPrimary > forText && wantRight > 0)
            {
                // The name does not fit: the right text gives back space down to its minimum share.
                double minRight = Math.Min(wantRight, available * MinRightShare);
                right = Math.Max(minRight, wantRight - (wantPrimary - forText));
                forText = Math.Max(0, available - right - rightGap);
            }
            double primary = Math.Min(wantPrimary, forText);
            double secondary = 0;
            if (wantSecondary > 0)
            {
                double left = forText - primary - (primary > 0 ? gap : 0);
                if (left >= Math.Min(MinSecondary, wantSecondary)) secondary = Math.Min(wantSecondary, left);
            }
            return new RowColumns(primary, secondary, right);
        }

        /// <summary>
        /// Trims <paramref name="text"/> in the middle so <paramref name="measure"/> of the result is at most
        /// <paramref name="maxWidth"/>. Paths keep whole leading and trailing segments ("Plugins/SPProject/…/Private/Foo");
        /// other text (or a path whose shortest segment form does not fit) keeps leading and trailing characters.
        /// Returns an empty text when not even the ellipsis fits.
        /// </summary>
        public static MiddleTrimResult TrimMiddle(string text, double maxWidth, Func<string, double> measure)
        {
            text = text ?? string.Empty;
            if (measure == null) throw new ArgumentNullException(nameof(measure));
            if (text.Length == 0 || double.IsInfinity(maxWidth) || measure(text) <= maxWidth) return MiddleTrimResult.Whole(text);
            if (maxWidth <= 0 || measure(MiddleTrimResult.Ellipsis) > maxWidth) return new MiddleTrimResult(text, 0, string.Empty, text.Length);

            var path = TrimPath(text, maxWidth, measure);
            if (path.HasValue) return path.Value;
            return TrimChars(text, maxWidth, measure);
        }

        /// <summary>Indexes of the path separators ('/' or '\\') of <paramref name="text"/>.</summary>
        public static List<int> Separators(string text)
        {
            var seps = new List<int>();
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '/' || text[i] == '\\') seps.Add(i);
            return seps;
        }

        private static MiddleTrimResult? TrimPath(string text, double maxWidth, Func<string, double> measure)
        {
            var seps = Separators(text);
            // Segments: [0, seps[0]), (seps[0], seps[1]), …, (seps[n-1], end). Need ≥ 3 segments to drop one in the middle.
            int segments = seps.Count + 1;
            if (segments < 3) return null;
            // A trailing component that is everything after the last separator; a leading "…/" prefix form is the last resort.
            MiddleTrimResult Make(int head, int tail)
            {
                // head: number of leading segments kept (≥ 0); tail: trailing segments kept (≥ 1).
                int headLen = head == 0 ? 0 : seps[head - 1] + 1;           // includes the separator after the head
                int tailStart = seps[seps.Count - tail] + 1;                 // just after the separator before the tail
                char sep = text[seps[seps.Count - tail]];
                string middle = MiddleTrimResult.Ellipsis + sep;
                return new MiddleTrimResult(text, headLen, middle, tailStart);
            }

            int h = 1, t = 1;
            var best = Make(1, 1);
            if (measure(best.Text) > maxWidth)
            {
                // "…/Last" only.
                var lastOnly = Make(0, 1);
                return measure(lastOnly.Text) <= maxWidth ? lastOnly : (MiddleTrimResult?)null;
            }
            // Grow greedily: tail first (the folders next to the file say the most), then head, alternating.
            bool grew = true;
            while (grew && h + t < segments - 1)
            {
                grew = false;
                if (h + t < segments - 1)
                {
                    var c = Make(h, t + 1);
                    if (measure(c.Text) <= maxWidth) { t++; best = c; grew = true; }
                }
                if (h + t < segments - 1)
                {
                    var c = Make(h + 1, t);
                    if (measure(c.Text) <= maxWidth) { h++; best = c; grew = true; }
                }
            }
            return best;
        }

        private static MiddleTrimResult TrimChars(string text, double maxWidth, Func<string, double> measure)
        {
            // Binary search the number of kept characters; the tail gets the larger half (file names end the text).
            int lo = 0, hi = text.Length - 1;
            var best = new MiddleTrimResult(text, 0, MiddleTrimResult.Ellipsis, text.Length);
            while (lo <= hi)
            {
                int keep = (lo + hi) / 2;
                int head = keep / 2;
                int tail = keep - head;
                var c = new MiddleTrimResult(text, head, MiddleTrimResult.Ellipsis, text.Length - tail);
                if (measure(c.Text) <= maxWidth) { best = c; lo = keep + 1; }
                else hi = keep - 1;
            }
            return best;
        }

        private static double Pos(double v) => double.IsNaN(v) || v < 0 ? 0 : v;
    }
}
