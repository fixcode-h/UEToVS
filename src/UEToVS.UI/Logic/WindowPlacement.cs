using System;
using System.Collections.Generic;
using System.Globalization;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// Remembered position + size of a <c>UtilityWindow</c> (settings, performance, gallery) in DIPs of the virtual
    /// screen. Text form "left,top,width,height" (invariant culture) for the VS settings store.
    /// </summary>
    public sealed class WindowPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        public string Format() => string.Join(",", Array.ConvertAll(new[] { Left, Top, Width, Height }, d => Math.Round(d).ToString(CultureInfo.InvariantCulture)));

        public static bool TryParse(string text, out WindowPlacement placement)
        {
            placement = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(',');
            if (parts.Length != 4) return false;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || double.IsNaN(v[i]) || double.IsInfinity(v[i]))
                    return false;
            if (v[2] < 1 || v[3] < 1) return false;
            placement = new WindowPlacement { Left = v[0], Top = v[1], Width = v[2], Height = v[3] };
            return true;
        }

        /// <summary>
        /// Makes a stored placement usable on the current monitor layout: clamps the size to
        /// [min, work area] and moves the window so that at least its title strip (<paramref name="grip"/> DIPs) is on
        /// one of <paramref name="workAreas"/>; if no work area intersects any more (monitor unplugged) it is centered on
        /// the first one. Returns a new instance.
        /// </summary>
        public WindowPlacement EnsureVisible(IReadOnlyList<RectD> workAreas, double minWidth, double minHeight, double grip = 40)
        {
            var r = new WindowPlacement { Left = Left, Top = Top, Width = Math.Max(minWidth, Width), Height = Math.Max(minHeight, Height) };
            if (workAreas == null || workAreas.Count == 0) return r;
            RectD best = workAreas[0];
            double bestArea = -1;
            foreach (var a in workAreas)
            {
                double ix = Math.Max(0, Math.Min(r.Left + r.Width, a.X + a.Width) - Math.Max(r.Left, a.X));
                double iy = Math.Max(0, Math.Min(r.Top + grip, a.Y + a.Height) - Math.Max(r.Top, a.Y));
                if (ix * iy > bestArea) { bestArea = ix * iy; best = a; }
            }
            r.Width = Math.Min(r.Width, best.Width);
            r.Height = Math.Min(r.Height, best.Height);
            if (bestArea <= 0)
            {
                r.Left = best.X + (best.Width - r.Width) / 2;
                r.Top = best.Y + (best.Height - r.Height) / 2;
                return r;
            }
            r.Left = Math.Max(best.X, Math.Min(r.Left, best.X + best.Width - r.Width));
            r.Top = Math.Max(best.Y, Math.Min(r.Top, best.Y + best.Height - r.Height));
            return r;
        }
    }
}
