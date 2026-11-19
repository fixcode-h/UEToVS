using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Patterns
{
    /// <summary>
    /// 编辑器顶部的通知条（IDEA 的 Editor Notification）：通栏、底部一条细线，左边图标 + 一句说明（可带一句灰字补充），右边是操作按钮。
    /// 第一处用法是 v0.9.4 引擎代码只读（“此文件属于引擎代码，只读” + “启用编辑”）。<see cref="Flash"/> 在用户想改只读文件时闪一下，
    /// 提示该点哪里。文字都用 Func&lt;string&gt;，切换语言时跟着变。
    /// </summary>
    public class EditorNoticeBar : Border
    {
        private readonly Path _glyph;
        private readonly UiText _message = new UiText((string)null, TextVariant.Body) { Margin = new Thickness(0, 0, 8, 0) };
        private readonly UiText _detail = new UiText((string)null, TextVariant.Secondary) { Margin = new Thickness(0, 0, 8, 0) };
        private readonly StackPanel _actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _flash = new Border { Opacity = 0, IsHitTestVisible = false };
        private Func<string> _messageText;
        private Func<string> _detailText;

        public EditorNoticeBar()
        {
            BorderThickness = new Thickness(0, 0, 0, 1);
            this.Bind(BorderBrushProperty, Tokens.AccentBorder);
            this.Bind(BackgroundProperty, Tokens.AccentSubtle);
            ThemeManager.Instance.Attach(this);

            _glyph = new Path
            {
                Data = Ui.Lock,
                StrokeThickness = 1.3,
                Stretch = Stretch.Uniform,
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            _glyph.Bind(Shape.StrokeProperty, Tokens.Accent);
            _flash.Bind(BackgroundProperty, Tokens.Accent);

            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 3, 6, 3), MinHeight = 24 };
            DockPanel.SetDock(_glyph, Dock.Left);
            DockPanel.SetDock(_actions, Dock.Right);
            dock.Children.Add(_glyph);
            dock.Children.Add(_actions);
            var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            text.Children.Add(_message);
            text.Children.Add(_detail);
            dock.Children.Add(text);

            // 闪烁层盖在内容下面（Grid 里先加的在下层），不挡点击
            var grid = new Grid();
            grid.Children.Add(_flash);
            grid.Children.Add(dock);
            Child = grid;

            AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
            Strings.Track(this, Refresh);
        }

        /// <summary>图标（默认是锁）。</summary>
        public Geometry Glyph
        {
            get => _glyph.Data;
            set => _glyph.Data = value ?? Ui.Lock;
        }

        /// <summary>说明文字和可选的灰字补充（空 = 不显示）。</summary>
        public void Set(Func<string> message, Func<string> detail = null)
        {
            _messageText = message;
            _detailText = detail;
            Refresh();
        }

        public void ClearActions() => _actions.Children.Clear();

        /// <summary>右侧加一个按钮；<paramref name="shortcut"/> 显示在悬停提示里。返回按钮，调用方可以再改它。</summary>
        public UiButton AddAction(Func<string> text, Action onClick, ButtonKind kind = ButtonKind.Primary, Func<string> tooltip = null, string shortcut = null)
        {
            var b = new UiButton(text, kind)
            {
                MinHeight = 0,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (tooltip != null || shortcut != null) Tip.Set(b, tooltip ?? text, shortcut);
            b.Click += (s, e) => Ui.Safe("ui.editorNotice.action", () => onClick?.Invoke());
            _actions.Children.Add(b);
            return b;
        }

        /// <summary>闪一下（用户在只读文件里打字时），关掉动画时不闪。</summary>
        public void Flash()
        {
            if (Visibility != Visibility.Visible) return;
            _flash.BeginAnimation(OpacityProperty, null);
            _flash.Opacity = 0.35;
            Ui.Fade(_flash, 0);
        }

        private void Refresh()
        {
            string m = _messageText?.Invoke() ?? string.Empty;
            string d = _detailText?.Invoke() ?? string.Empty;
            _message.Text = m;
            _detail.Text = d;
            _detail.Visibility = d.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(this, d.Length == 0 ? m : m + " " + d);
        }
    }
}
