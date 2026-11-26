using System;
using System.Windows;
using Microsoft.VisualStudio.Text.Editor;

namespace UEToVS.Host.Vcs
{
    /// <summary>
    /// 行号旁的边距（改动色条、注解栏）和文字之间的纵向换算。不能假设边距的 y=0 就是视图的 ViewportTop：
    /// v0.11.1 用户那里色条整体比文字高了 2 行（弹窗写第 276–280 行，色条画在 274–278），说明编辑器文字区相对边距容器有偏移
    /// （VS 自己的行号边距会处理，自定义边距不会自动跟）。这里按两者在 WPF 里的实际位置换算：画的时候把视图坐标转到边距坐标，
    /// 点的时候把鼠标位置直接取在视图上，两边用同一个换算，画在哪里点到的就是哪一行。
    /// </summary>
    internal static class MarginGeometry
    {
        /// <summary>视图（文字区）左上角在边距坐标里的 y；拿不到（还没进可视树）时 0。</summary>
        public static double ViewOffsetY(IWpfTextView view, UIElement margin)
        {
            try
            {
                var element = view?.VisualElement;
                if (element == null || margin == null || PresentationSource.FromVisual(element) == null || PresentationSource.FromVisual(margin) == null) return 0;
                var p = element.TranslatePoint(new Point(0, 0), margin);
                return double.IsNaN(p.Y) || double.IsInfinity(p.Y) ? 0 : p.Y;
            }
            catch (InvalidOperationException)
            {
                return 0; // 不在同一棵可视树里
            }
        }

        /// <summary>某一行文字顶端在边距坐标里的 y。</summary>
        public static double TextTopInMargin(IWpfTextView view, Microsoft.VisualStudio.Text.Formatting.ITextViewLine line, double viewOffsetY) =>
            line.TextTop - view.ViewportTop + viewOffsetY;

        /// <summary>鼠标在视图坐标里的 y（加上 ViewportTop，可直接给 GetTextViewLineContainingYCoordinate）。</summary>
        public static double MouseViewY(IWpfTextView view, System.Windows.Input.MouseEventArgs e, UIElement margin)
        {
            var element = view.VisualElement;
            double y = element != null && PresentationSource.FromVisual(element) != null ? e.GetPosition(element).Y : e.GetPosition(margin).Y;
            return y + view.ViewportTop;
        }
    }
}
