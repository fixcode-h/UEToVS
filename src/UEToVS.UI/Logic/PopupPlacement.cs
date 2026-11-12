using System;

namespace UEToVS.UI.Logic
{
    /// <summary>WPF-free rectangle (any unit, typically device pixels).</summary>
    public readonly struct RectD
    {
        public readonly double X, Y, Width, Height;

        public RectD(double x, double y, double width, double height)
        {
            X = x; Y = y; Width = Math.Max(0, width); Height = Math.Max(0, height);
        }

        public double Right => X + Width;
        public double Bottom => Y + Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public override string ToString() => $"({X},{Y} {Width}x{Height})";
    }

    public enum AnchorMode
    {
        /// <summary>Below the anchor rect (caret / element), flipped above when it does not fit.</summary>
        Below,
        /// <summary>To the right of the anchor rect (submenus), flipped left when it does not fit.</summary>
        Right,
        /// <summary>Horizontally centered on the anchor rect, at <see cref="PopupPlacement.CenterTopRatio"/> of its height.</summary>
        CenterUpper,
        /// <summary>Exactly centered.</summary>
        Center,
    }

    /// <summary>Pure popup positioning: anchor + popup size + monitor work area → top-left, always on-screen.</summary>
    public static class PopupPlacement
    {
        /// <summary>Rider places Search Everywhere around the upper third of the IDE.</summary>
        public const double CenterTopRatio = 0.22;

        public static RectD Place(RectD anchor, double width, double height, RectD workArea, AnchorMode mode, double gap = 2)
        {
            width = Math.Min(width, workArea.Width);
            height = Math.Min(height, workArea.Height);
            double x, y;
            switch (mode)
            {
                case AnchorMode.Below:
                    x = anchor.X;
                    y = anchor.Bottom + gap;
                    if (y + height > workArea.Bottom && anchor.Y - gap - height >= workArea.Y) y = anchor.Y - gap - height;
                    break;
                case AnchorMode.Right:
                    x = anchor.Right + gap;
                    y = anchor.Y;
                    if (x + width > workArea.Right && anchor.X - gap - width >= workArea.X) x = anchor.X - gap - width;
                    break;
                case AnchorMode.CenterUpper:
                    x = anchor.X + (anchor.Width - width) / 2;
                    y = anchor.Y + Math.Max(0, anchor.Height * CenterTopRatio - 0);
                    if (y + height > anchor.Bottom) y = anchor.Y + (anchor.Height - height) / 2;
                    break;
                default:
                    x = anchor.X + (anchor.Width - width) / 2;
                    y = anchor.Y + (anchor.Height - height) / 2;
                    break;
            }
            x = Clamp(x, workArea.X, workArea.Right - width);
            y = Clamp(y, workArea.Y, workArea.Bottom - height);
            return new RectD(Math.Round(x), Math.Round(y), Math.Round(width), Math.Round(height));
        }

        private static double Clamp(double v, double min, double max) => max < min ? min : v < min ? min : v > max ? max : v;
    }
}
