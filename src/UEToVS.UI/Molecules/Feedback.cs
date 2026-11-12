using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Molecules
{
    /// <summary>Centered "nothing here" message with optional description and action link.</summary>
    public class EmptyState : StackPanel
    {
        private readonly UiText _title = new UiText((string)null, TextVariant.Body) { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        private readonly UiText _desc = new UiText((string)null, TextVariant.Small) { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 360 };
        private readonly LinkText _action = new LinkText((string)null) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Path _glyph;
        private Action _onAction;

        public EmptyState()
        {
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            Margin = new Thickness(16);
            _glyph = new Path { Data = Ui.SearchGlass, StrokeThickness = 1.2, Width = 28, Height = 28, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.6 };
            _glyph.Bind(Shape.StrokeProperty, Tokens.TextSecondary);
            _title.Bind(TextBlock.ForegroundProperty, Tokens.TextSecondary);
            _title.TextTrimming = TextTrimming.None;
            _title.TextWrapping = TextWrapping.Wrap;
            Children.Add(_glyph);
            Children.Add(_title);
            Children.Add(_desc);
            Children.Add(_action);
            _action.Click += (s, e) => Ui.Safe("ui.empty.action", () => _onAction?.Invoke());
            Set(Strings.ListEmpty);
        }

        public void Set(string title, string description = null, string actionText = null, Action onAction = null, bool showGlyph = true)
        {
            _title.Text = title ?? string.Empty;
            _desc.Text = description ?? string.Empty;
            _desc.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
            _action.Text = actionText ?? string.Empty;
            _action.Visibility = string.IsNullOrEmpty(actionText) ? Visibility.Collapsed : Visibility.Visible;
            _onAction = onAction;
            _glyph.Visibility = showGlyph ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(this, title ?? string.Empty);
        }
    }

    /// <summary>A keyboard hint in the status footer: [keycap] label.</summary>
    public sealed class KeyHint
    {
        public KeyHint(string shortcut, Func<string> label)
        {
            Shortcut = shortcut;
            Label = label;
        }

        public string Shortcut { get; }
        public Func<string> Label { get; }
    }

    /// <summary>Bottom status bar: busy spinner + status text on the left, keycap hints + count on the right.</summary>
    public class StatusFooter : Border
    {
        private readonly Spinner _spinner = new Spinner(12) { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
        private readonly UiText _status = new UiText((string)null, TextVariant.Small);
        private readonly StackPanel _hints = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly UiText _count = new UiText((string)null, TextVariant.Small) { Margin = new Thickness(10, 0, 0, 0) };
        private readonly List<(KeyHint Hint, UiText Label)> _hintLabels = new List<(KeyHint, UiText)>();

        public StatusFooter()
        {
            BorderThickness = new Thickness(0, 1, 0, 0);
            Padding = new Thickness(10, 0, 10, 0);
            MinHeight = 26;
            this.Bind(BorderBrushProperty, Tokens.BorderSubtle);
            this.Bind(BackgroundProperty, Tokens.SurfaceRaised);
            var dock = new DockPanel { LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(_hints);
            right.Children.Add(_count);
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(_spinner);
            left.Children.Add(_status);
            dock.Children.Add(left);
            Child = dock;
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
            Strings.Track(this, () => { foreach (var (h, l) in _hintLabels) l.Text = h.Label?.Invoke() ?? string.Empty; });
        }

        public string Status
        {
            get => _status.Text;
            set
            {
                _status.Text = value ?? string.Empty;
                AutomationProperties.SetName(_status, value ?? string.Empty);
            }
        }

        public string Count
        {
            get => _count.Text;
            set => _count.Text = value ?? string.Empty;
        }

        public bool IsBusy
        {
            get => _spinner.Visibility == Visibility.Visible;
            set => _spinner.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Shows/hides the keycap hints (they are hidden automatically when the footer gets narrow).</summary>
        public bool ShowHints
        {
            get => _hints.Visibility == Visibility.Visible;
            set => _hints.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetHints(params KeyHint[] hints)
        {
            _hints.Children.Clear();
            _hintLabels.Clear();
            _hints.Visibility = Visibility.Visible;
            if (hints == null) return;
            foreach (var h in hints)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
                sp.Children.Add(new Kbd(h.Shortcut));
                var label = new UiText(h.Label?.Invoke(), TextVariant.Small) { Margin = new Thickness(3, 0, 0, 0) };
                sp.Children.Add(label);
                _hintLabels.Add((h, label));
                _hints.Children.Add(sp);
            }
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            // English hints are wider: drop them before they collide with the status text.
            if (_hints.Children.Count > 0)
            {
                // Natural width is only measurable while visible (collapsed measures as 0): cache it.
                if (_hints.Visibility == Visibility.Visible)
                {
                    _hints.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    _hintsNatural = _hints.DesiredSize.Width;
                }
                _status.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                bool fits = _hintsNatural + _status.DesiredSize.Width + _count.DesiredSize.Width + 60 < arrangeSize.Width;
                var want = fits ? Visibility.Visible : Visibility.Collapsed;
                if (_hints.Visibility != want && _hintsAuto) _hints.Visibility = want;
            }
            return base.ArrangeOverride(arrangeSize);
        }

        private bool _hintsAuto = true;
        private double _hintsNatural;

        /// <summary>When false, <see cref="ShowHints"/> is not changed automatically by width.</summary>
        public bool AutoHideHints
        {
            get => _hintsAuto;
            set => _hintsAuto = value;
        }
    }

    /// <summary>InfoBar-like banner: severity icon, message, action links, close button.</summary>
    public class Banner : Border
    {
        private readonly Border _dot = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBlock _dotText = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly UiText _message = new UiText((string)null) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        private readonly WrapPanel _actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        private readonly IconButton _close;
        private Severity _severity;

        public Banner(Severity severity = Severity.Info, string message = null)
        {
            BorderThickness = new Thickness(1);
            Padding = new Thickness(10, 6, 6, 6);
            this.Bind(CornerRadiusProperty, Tokens.CornerRadiusControl);
            _dot.Child = _dotText;
            _close = new IconButton(Ui.CrossMark, () => Strings.BannerDismiss, null, 8);
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_dot, Dock.Left);
            DockPanel.SetDock(_close, Dock.Right);
            DockPanel.SetDock(_actions, Dock.Right);
            dock.Children.Add(_dot);
            dock.Children.Add(_close);
            dock.Children.Add(_actions);
            dock.Children.Add(_message);
            Child = dock;
            _close.Click += (s, e) => Ui.Safe("ui.banner.close", () =>
            {
                Visibility = Visibility.Collapsed;
                Closed?.Invoke(this, EventArgs.Empty);
            });
            AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
            Severity = severity;
            Message = message;
        }

        public event EventHandler Closed;

        public bool CanClose
        {
            get => _close.Visibility == Visibility.Visible;
            set => _close.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public string Message
        {
            get => _message.Text;
            set
            {
                _message.Text = value ?? string.Empty;
                AutomationProperties.SetName(this, value ?? string.Empty);
            }
        }

        public Severity Severity
        {
            get => _severity;
            set
            {
                _severity = value;
                this.Bind(BackgroundProperty, Tokens.SeverityBackground(value));
                this.Bind(BorderBrushProperty, Tokens.SeverityBackground(value));
                _dot.Bind(BackgroundProperty, Tokens.Severity(value));
                _dotText.Bind(TextBlock.ForegroundProperty, Tokens.SurfacePopup);
                _dotText.Text = value == Severity.Error ? "×" : value == Severity.Warning ? "!" : value == Severity.Success ? "✓" : "i";
            }
        }

        public void ClearActions() => _actions.Children.Clear();

        /// <summary>Adds an action link (localized text function).</summary>
        public void AddAction(Func<string> text, Action onClick)
        {
            var link = new LinkText(text) { Margin = new Thickness(12, 0, 0, 0) };
            link.Click += (s, e) => Ui.Safe("ui.banner.action", () => onClick?.Invoke());
            _actions.Children.Add(link);
        }
    }

    /// <summary>Transient notification near the bottom of an element; fades in/out (animation settings).</summary>
    public static class Toast
    {
        public static void Show(FrameworkElement anchor, string message, Severity severity = Severity.Info, TimeSpan? duration = null)
        {
            if (anchor == null || string.IsNullOrEmpty(message)) return;
            Ui.Safe("ui.toast.show", () =>
            {
                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                dot.Bind(Border.BackgroundProperty, Tokens.Severity(severity));
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(dot);
                sp.Children.Add(new UiText(message) { TextTrimming = TextTrimming.None });
                var chrome = new Border
                {
                    Child = sp,
                    Padding = new Thickness(12, 7, 12, 7),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(8),
                };
                chrome.Bind(Border.BackgroundProperty, Tokens.SurfaceRaised);
                chrome.Bind(Border.BorderBrushProperty, Tokens.BorderStrong);
                chrome.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
                ThemeManager.Instance.Attach(chrome);
                AutomationProperties.SetLiveSetting(chrome, AutomationLiveSetting.Assertive);
                AutomationProperties.SetName(chrome, message);

                var popup = new Popup
                {
                    Child = chrome,
                    AllowsTransparency = true,
                    PlacementTarget = anchor,
                    Placement = PlacementMode.Custom,
                    StaysOpen = true,
                    IsHitTestVisible = true,
                };
                popup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) => new[]
                {
                    new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height - popupSize.Height - 16), PopupPrimaryAxis.Horizontal),
                };
                chrome.Opacity = 0;
                popup.IsOpen = true;
                Ui.Fade(chrome, 1);
                var timer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(severity >= Severity.Warning ? 5 : 3) };
                void Close()
                {
                    timer.Stop();
                    Ui.Fade(chrome, 0, () => popup.IsOpen = false);
                }
                timer.Tick += (s, e) => Ui.Safe("ui.toast.tick", Close);
                chrome.MouseLeftButtonUp += (s, e) => Ui.Safe("ui.toast.click", Close);
                timer.Start();
            });
        }
    }
}
