using System;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// v0.7.7: size of a list popup fitted to its content (Rider's small lists, e.g. the Code Vision Blueprint list): as wide
    /// as the widest row so paths are not cut, between a minimum and a maximum width; as tall as its rows, up to a row
    /// limit. Pure (DIPs); the popup measures the rows and the monitor clamp still applies afterwards.
    /// </summary>
    public static class ChooserFit
    {
        /// <summary>Room beside the rows: list padding, the scroll rail and the chrome border.</summary>
        public const double RowSlack = 20;

        public const int MaxRows = 14;

        public static double Width(double widestRow, double titleWidth, double minWidth, double maxWidth)
        {
            if (maxWidth <= 0) maxWidth = double.PositiveInfinity;
            if (minWidth > maxWidth) minWidth = maxWidth;
            double want = Math.Max(Clean(widestRow) + RowSlack, Clean(titleWidth));
            return Math.Ceiling(Math.Max(minWidth, Math.Min(maxWidth, want)));
        }

        public static double Height(int rows, double rowHeight, double chromeHeight, int maxRows = MaxRows) =>
            Math.Ceiling(Math.Min(Math.Max(rows, 1), Math.Max(1, maxRows)) * Math.Max(1, rowHeight) + Math.Max(0, chromeHeight));

        private static double Clean(double v) => double.IsNaN(v) || double.IsInfinity(v) || v < 0 ? 0 : v;
    }
}
