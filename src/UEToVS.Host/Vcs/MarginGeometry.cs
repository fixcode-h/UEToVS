using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace UEToVS.Host.Vcs
{
    /// <summary>
    /// 行号旁的边距（改动色条、注解栏）和文字之间的纵向换算。
    /// 编辑器缩放（Ctrl+滚轮 / 左下角百分比）时 VS 只缩放文字区，自定义边距不跟着缩放：边距里的 1 个单位不等于文字区的 1 个单位。
    /// v0.11.0–0.11.2 直接用 line.Top - ViewportTop 当边距坐标，在 125% 缩放下离视口顶端越远偏得越多（用户截图里第 276 行的色条画在 274），
    /// 色条也比行矮。这里每次绘制取一次“文字区 → 边距”的 WPF 变换（含缩放和位移），行的上下沿都经它换算；
    /// 拿不到变换时退回按 ZoomLevel 缩放。点击不再反算，直接命中绘制时记下的色条（见 ChangeMargin），画在哪里就点得到哪里。
    /// </summary>
    internal readonly struct MarginGeometry
    {
        private readonly GeneralTransform _toMargin;
        private readonly double _scale;
        private readonly double _viewportTop;

        private MarginGeometry(GeneralTransform toMargin, double scale, double viewportTop)
        {
            _toMargin = toMargin;
            _scale = scale;
            _viewportTop = viewportTop;
        }

        /// <summary>是不是用的 WPF 变换（false = 退回按缩放比例算）。</summary>
        public bool FromTransform => _toMargin != null;

        /// <summary>文字区 1 个单位在边距里的高度（缩放比例）。</summary>
        public double Scale => _scale;

        public static MarginGeometry For(IWpfTextView view, Visual margin)
        {
            double zoom = view.ZoomLevel > 0 ? view.ZoomLevel / 100.0 : 1.0;
            try
            {
                var element = view.VisualElement;
                if (element != null && margin != null && PresentationSource.FromVisual(element) != null && PresentationSource.FromVisual(margin) != null)
                {
                    var t = element.TransformToVisual(margin);
                    var a = t.Transform(new Point(0, 0));
                    var b = t.Transform(new Point(0, 100));
                    double scale = (b.Y - a.Y) / 100.0;
                    if (!double.IsNaN(scale) && !double.IsInfinity(scale) && scale > 0.05) return new MarginGeometry(t, scale, view.ViewportTop);
                }
            }
            catch (InvalidOperationException)
            {
                // 不在同一棵可视树里：按缩放比例
            }
            return new MarginGeometry(null, zoom, view.ViewportTop);
        }

        /// <summary>视图坐标的 y（和 ITextViewLine.Top 同一坐标系）在边距里的 y。</summary>
        public double ToMargin(double viewY)
        {
            double local = viewY - _viewportTop;
            return _toMargin != null ? _toMargin.Transform(new Point(0, local)).Y : local * _scale;
        }

        /// <summary>一行文字（TextTop..TextBottom，不含 Code Vision 等加在上面的空白）在边距里的上下沿。</summary>
        public (double Top, double Bottom) TextSpan(ITextViewLine line) => (ToMargin(line.TextTop), ToMargin(line.TextBottom));

        /// <summary>整行（含行变换加的空白）在边距里的上下沿。</summary>
        public (double Top, double Bottom) LineSpan(ITextViewLine line) => (ToMargin(line.Top), ToMargin(line.Bottom));

        /// <summary>边距里的点 → 视图坐标的 y（给 GetTextViewLineContainingYCoordinate）。</summary>
        public static double MouseViewY(IWpfTextView view, System.Windows.Input.MouseEventArgs e, IInputElement margin)
        {
            var element = view.VisualElement;
            double y = element != null && PresentationSource.FromVisual(element) != null
                ? e.GetPosition(element).Y // 鼠标直接取在文字区上，缩放和位移由 WPF 处理
                : e.GetPosition(margin).Y / (view.ZoomLevel > 0 ? view.ZoomLevel / 100.0 : 1.0);
            return y + view.ViewportTop;
        }
    }
}
