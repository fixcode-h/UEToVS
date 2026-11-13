using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.UI.Controls;
using UEToVS.UI.Theming;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>
    /// 悬停提示里的滚动框。VS 的提示弹窗本身不限高，内容超出屏幕后下半截看不到也滚不动；这里用插件的细滚动条
    /// （<see cref="UiScrollViewer"/>）限高。提示弹窗里没有接主题字典，滚动条颜色直接放在滚动框自己的资源里，
    /// 用半透明灰，深浅色提示背景上都看得清。
    /// </summary>
    internal static class QuickInfoScroll
    {
        private static readonly Brush Thumb = Frozen(Color.FromArgb(0x66, 0x80, 0x80, 0x80));
        private static readonly Brush ThumbHover = Frozen(Color.FromArgb(0xA0, 0x80, 0x80, 0x80));

        /// <summary>
        /// 把 <paramref name="content"/> 放进最高 <paramref name="maxHeight"/> 的竖向滚动框。<paramref name="chainWheel"/>：
        /// 内层滚动框（代码块）滚到头后把滚轮交给外层，鼠标停在代码块上也能继续滚动整个提示。
        /// </summary>
        public static UiScrollViewer Wrap(UIElement content, double maxHeight, bool chainWheel, Brush foreground = null)
        {
            var sv = new UiScrollViewer
            {
                Content = content,
                MaxHeight = maxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent, // 透明背景才能在空白处接到滚轮
            };
            ApplyForeground(sv, foreground);
            sv.Resources[Tokens.ScrollThumb] = Thumb;
            sv.Resources[Tokens.ScrollThumbHover] = ThumbHover;
            if (chainWheel) sv.PreviewMouseWheel += OnInnerWheel;
            return sv;
        }

        /// <summary>
        /// VS 编辑器工具提示的默认文字颜色（字体和颜色 › 编辑器工具提示 › 纯文本）。标点、运算符和普通文本的分类没有自己的
        /// 前景色，靠继承显示；进了滚动框后继承到的是 WPF 默认的黑色（v0.5.13 深色主题下括号、逗号看不清），所以显式给出。
        /// </summary>
        public static Brush TooltipForeground(IClassificationFormatMapService maps)
        {
            try
            {
                var props = maps?.GetClassificationFormatMap("tooltip")?.DefaultTextProperties;
                if (props != null && !props.ForegroundBrushEmpty) return props.ForegroundBrush;
            }
            catch (Exception)
            {
                // 拿不到时用 VS 主题的工具提示文字色
            }
            return null;
        }

        /// <summary>给 <paramref name="e"/> 及其内部的文字设前景色；没有格式映射时跟随 VS 主题的工具提示文字色。</summary>
        public static void ApplyForeground(FrameworkElement e, Brush foreground)
        {
            if (e == null) return;
            if (foreground != null) System.Windows.Documents.TextElement.SetForeground(e, foreground);
            else e.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolTipTextBrushKey);
        }

        /// <summary>工作区高度（DIP）；拿不到时返回 0，由 <see cref="QuickInfoLayout"/> 按 1080p 估算。</summary>
        public static double WorkAreaHeight()
        {
            try
            {
                return SystemParameters.WorkArea.Height;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void OnInnerWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || !(sender is ScrollViewer inner)) return;
            if (!QuickInfoLayout.ShouldChainWheel(e.Delta, inner.VerticalOffset, inner.ScrollableHeight)) return;
            e.Handled = true;
            var outer = FindParentScrollViewer(inner);
            if (outer == null) return;
            outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = inner });
        }

        private static ScrollViewer FindParentScrollViewer(DependencyObject d)
        {
            for (var p = Parent(d); p != null; p = Parent(p))
                if (p is ScrollViewer sv) return sv;
            return null;
        }

        private static DependencyObject Parent(DependencyObject d) =>
            d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    /// <summary>整个悬停提示的外层：内容超过 <see cref="QuickInfoLayout.PopupMaxHeight"/> 时在框内滚动。</summary>
    internal sealed class ScrollableQuickInfoElement
    {
        public ScrollableQuickInfoElement(object content) => Content = content;

        public object Content { get; }
    }

    [Export(typeof(IViewElementFactory))]
    [Name("UEToVS Scrollable Quick Info Element Factory")]
    [TypeConversion(from: typeof(ScrollableQuickInfoElement), to: typeof(UIElement))]
    [Order(Before = "default")]
    internal sealed class ScrollableQuickInfoViewElementFactory : IViewElementFactory
    {
        private static readonly Logger L = Log.For("IntelliSense");

        [Import]
        internal IViewElementFactoryService Factory { get; set; }

        [Import]
        internal IClassificationFormatMapService FormatMaps { get; set; }

        public TView CreateViewElement<TView>(ITextView textView, object model) where TView : class
        {
            if (!(model is ScrollableQuickInfoElement m) || typeof(TView) != typeof(UIElement)) return null;
            try
            {
                var inner = Factory.CreateViewElement<UIElement>(textView, m.Content);
                if (inner == null) return null;
                return QuickInfoScroll.Wrap(inner, QuickInfoLayout.PopupMaxHeight(QuickInfoScroll.WorkAreaHeight()), chainWheel: false,
                    QuickInfoScroll.TooltipForeground(FormatMaps)) as TView;
            }
            catch (Exception ex)
            {
                L.Error("is.quickInfo.scrollElement.failed", "Rendering the scrollable Quick Info failed", ex);
                return null;
            }
        }
    }
}
