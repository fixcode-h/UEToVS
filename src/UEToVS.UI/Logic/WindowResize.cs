using System;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// Resize math for the borderless chrome windows' edge / corner grips (pure, unit-tested). <c>sx</c> / <c>sy</c> say
    /// which edge moves: -1 = left / top (the window origin moves with it so the opposite edge stays put), 1 = right /
    /// bottom, 0 = that axis is not resized. The size never goes below the minimum; when it hits the minimum on a
    /// left / top grip the origin stops too, so the window does not "walk" while the mouse keeps dragging.
    /// </summary>
    public static class WindowResize
    {
        public static RectD Apply(RectD bounds, int sx, int sy, double dx, double dy, double minWidth, double minHeight)
        {
            double w = bounds.Width, h = bounds.Height, x = bounds.X, y = bounds.Y;
            if (sx != 0 && !double.IsNaN(dx) && !double.IsInfinity(dx))
            {
                double nw = Math.Max(Math.Min(minWidth, w), w + Math.Sign(sx) * dx);
                if (sx < 0) x -= nw - w;
                w = nw;
            }
            if (sy != 0 && !double.IsNaN(dy) && !double.IsInfinity(dy))
            {
                double nh = Math.Max(Math.Min(minHeight, h), h + Math.Sign(sy) * dy);
                if (sy < 0) y -= nh - h;
                h = nh;
            }
            return new RectD(x, y, w, h);
        }
    }
}
