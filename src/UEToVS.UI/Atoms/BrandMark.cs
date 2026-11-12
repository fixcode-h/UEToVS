using System.Windows;
using System.Windows.Media;

namespace UEToVS.UI.Atoms
{
    /// <summary>
    /// The UEToVS brand mark, drawn as vectors (crisp at any DPI): a rounded indigo→blue tile with a white "U"
    /// whose right stem ends in a forward chevron ("Unreal → Visual Studio"). Same geometry as
    /// src/UEToVS.Host/Resources/UEToVS.svg (16-unit grid). The brand colors are deliberately fixed (not theme
    /// tokens): the tile carries its own contrast, so it reads on light, dark and blue themes alike.
    /// </summary>
    public sealed class BrandMark : FrameworkElement
    {
        private static readonly Brush Tile = Freeze(new LinearGradientBrush(Color.FromRgb(0x6B, 0x4F, 0xE8), Color.FromRgb(0x1F, 0x7A, 0xE0), new Point(0, 0), new Point(1, 1)));
        private static readonly Brush Edge = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x10, 0x14, 0x40)));
        private static readonly Pen Glyph = FreezePen(new Pen(Brushes.White, 1.9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        private static readonly Geometry U = FreezeGeo(Geometry.Parse("M 4.6,4 L 4.6,8.4 A 3,3 0 0 0 10.6,8.4 L 10.6,6.2"));
        private static readonly Geometry Chevron = FreezeGeo(Geometry.Parse("M 8.9,4.9 L 10.6,3.3 L 12.3,4.9"));

        public BrandMark(double size = 16)
        {
            Width = size;
            Height = size;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double s = System.Math.Min(ActualWidth, ActualHeight) / 16.0;
            if (s <= 0) return;
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawRoundedRectangle(Tile, new Pen(Edge, 1 / s), new Rect(0.5, 0.5, 15, 15), 3.5, 3.5);
            dc.DrawGeometry(null, Glyph, U);
            dc.DrawGeometry(null, Glyph, Chevron);
            dc.Pop();
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
        private static Pen FreezePen(Pen p) { p.Freeze(); return p; }
        private static Geometry FreezeGeo(Geometry g) { g.Freeze(); return g; }
    }
}
