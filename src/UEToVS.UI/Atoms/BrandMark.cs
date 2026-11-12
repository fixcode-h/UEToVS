using System.Windows;
using System.Windows.Media;

namespace UEToVS.UI.Atoms
{
    /// <summary>
    /// The UEToVS brand mark, drawn as vectors (crisp at any DPI): a rounded indigo→blue tile with a white game
    /// controller (rounded body with two grips, d-pad on the left, four face buttons on the right). Same geometry as
    /// src/UEToVS.Host/Resources/UEToVS.svg and scripts/render_brand_icons.py (16-unit grid, pixel-aligned so the
    /// 16 px mark is crisp; at 16-20 px the face buttons are drawn as whole-pixel squares, larger as round dots).
    /// The brand colors are deliberately fixed (not theme tokens): the tile carries its own contrast, so it reads on
    /// light, dark and blue themes alike.
    /// </summary>
    public sealed class BrandMark : FrameworkElement
    {
        private static readonly Brush Tile = Freeze(new LinearGradientBrush(Color.FromRgb(0x6B, 0x4F, 0xE8), Color.FromRgb(0x1F, 0x7A, 0xE0), new Point(0, 0), new Point(1, 1)));
        private static readonly Brush Edge = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x10, 0x14, 0x40)));
        private static readonly Geometry PadSmall = BuildPad(true);
        private static readonly Geometry PadLarge = BuildPad(false);

        public BrandMark(double size = 16)
        {
            Width = size;
            Height = size;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = System.Math.Min(ActualWidth, ActualHeight);
            double s = size / 16.0;
            if (s <= 0) return;
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawRoundedRectangle(Tile, new Pen(Edge, 1 / s), new Rect(0.5, 0.5, 15, 15), 3.5, 3.5);
            dc.DrawGeometry(Brushes.White, null, size <= 20 ? PadSmall : PadLarge);
            dc.Pop();
        }

        /// <summary>Controller silhouette minus the d-pad and the face buttons.</summary>
        private static Geometry BuildPad(bool squareButtons)
        {
            var body = new GeometryGroup { FillRule = FillRule.Nonzero };
            body.Children.Add(new RectangleGeometry(new Rect(2, 4, 12, 6), 2, 2));
            body.Children.Add(new RectangleGeometry(new Rect(2, 8, 4, 4), 2, 2));
            body.Children.Add(new RectangleGeometry(new Rect(10, 8, 4, 4), 2, 2));

            var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
            double dr = squareButtons ? 0 : 0.18;
            holes.Children.Add(new RectangleGeometry(new Rect(3, 6, 3, 1), dr, dr));
            holes.Children.Add(new RectangleGeometry(new Rect(4, 5, 1, 3), dr, dr));
            foreach (var c in new[] { new Point(11.5, 5.5), new Point(10.5, 6.5), new Point(12.5, 6.5), new Point(11.5, 7.5) })
            {
                if (squareButtons) holes.Children.Add(new RectangleGeometry(new Rect(c.X - 0.5, c.Y - 0.5, 1, 1)));
                else holes.Children.Add(new EllipseGeometry(c, 0.58, 0.58));
            }
            var pad = Geometry.Combine(body, holes, GeometryCombineMode.Exclude, null);
            pad.Freeze();
            return pad;
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
