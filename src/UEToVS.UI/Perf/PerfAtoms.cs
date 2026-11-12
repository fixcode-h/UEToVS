using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using UEToVS.UI.Atoms;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Perf
{
    /// <summary>Big-number tile: label, value, sub line (delta / budget), state color, optional progress line.</summary>
    public class StatTile : Border
    {
        private readonly UiText _label = new UiText((string)null, TextVariant.Small);
        private readonly TextBlock _value = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly UiText _sub = new UiText((string)null, TextVariant.Caption);
        private readonly ProgressLine _progress = new ProgressLine { Height = 3, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Border _stateBar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 10, 0) };
        private readonly Func<string> _labelText;

        public StatTile(Func<string> label)
        {
            _labelText = label;
            Padding = new Thickness(12, 10, 12, 10);
            BorderThickness = new Thickness(1);
            this.Bind(BackgroundProperty, Tokens.SurfaceRaised);
            this.Bind(BorderBrushProperty, Tokens.BorderSubtle);
            this.Bind(CornerRadiusProperty, Tokens.CornerRadiusControl);
            _value.Bind(TextBlock.ForegroundProperty, Tokens.TextPrimary);
            var stack = new StackPanel();
            stack.Children.Add(_label);
            stack.Children.Add(_value);
            stack.Children.Add(_sub);
            stack.Children.Add(_progress);
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_stateBar, Dock.Left);
            dock.Children.Add(_stateBar);
            dock.Children.Add(stack);
            Child = dock;
            Ui.TrackTheme(this, ApplyFont);
            ApplyFont();
            Strings.Track(this, () => { _label.Text = _labelText?.Invoke() ?? string.Empty; UpdateName(); });
            SetState(null);
        }

        private void ApplyFont() => _value.FontSize = Math.Round((ThemeManager.Instance.Current?.Number(TokenNames.FontSizeTitle) ?? 14) * 1.45);

        /// <summary>Updates the tile (cheap: text + resource keys only).</summary>
        public void Set(string value, string sub = null, Severity? state = null, double? progress = null)
        {
            if (_value.Text != value) _value.Text = value ?? "—";
            if (_sub.Text != (sub ?? string.Empty)) _sub.Text = sub ?? string.Empty;
            _sub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
            SetState(state);
            if (progress.HasValue)
            {
                _progress.Visibility = Visibility.Visible;
                _progress.Fraction = progress.Value;
            }
            else _progress.Visibility = Visibility.Collapsed;
            UpdateName();
        }

        private Severity? _state = (Severity)(-1);

        private void SetState(Severity? state)
        {
            if (_state == state) return;
            _state = state;
            if (state.HasValue)
            {
                _stateBar.Visibility = Visibility.Visible;
                _stateBar.Bind(BackgroundProperty, Tokens.Severity(state.Value));
                _value.Bind(TextBlock.ForegroundProperty, state.Value >= Severity.Warning ? Tokens.Severity(state.Value) : Tokens.TextPrimary);
            }
            else
            {
                _stateBar.Visibility = Visibility.Collapsed;
                _value.Bind(TextBlock.ForegroundProperty, Tokens.TextPrimary);
            }
        }

        private void UpdateName() => AutomationProperties.SetName(this, _label.Text + ": " + _value.Text + (string.IsNullOrEmpty(_sub.Text) ? "" : ", " + _sub.Text));
    }

    /// <summary>Thin rounded progress bar drawn in OnRender. Fraction &lt; 0 = indeterminate (static partial fill).</summary>
    public class ProgressLine : FrameworkElement
    {
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(ProgressLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(ProgressLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private double _fraction;

        public ProgressLine()
        {
            this.Bind(FillProperty, Tokens.Accent);
            this.Bind(TrackProperty, Tokens.BorderSubtle);
            MinHeight = 3;
            SnapsToDevicePixels = true;
        }

        public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
        public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

        public double Fraction
        {
            get => _fraction;
            set
            {
                double v = double.IsNaN(value) ? -1 : Math.Min(1, value);
                if (Math.Abs(v - _fraction) < 0.001) return;
                _fraction = v;
                InvalidateVisual();
            }
        }

        public void SetSeverity(Severity? s) => this.Bind(FillProperty, s.HasValue && s.Value >= Severity.Warning ? Tokens.Severity(s.Value) : Tokens.Accent);

        protected override Size MeasureOverride(Size availableSize) => new Size(0, Math.Max(3, double.IsNaN(Height) ? 3 : Height));

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double r = h / 2;
            if (Track != null) dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), r, r);
            if (Fill == null) return;
            if (_fraction < 0)
            {
                dc.DrawRoundedRectangle(Fill, null, new Rect(w * 0.1, 0, w * 0.3, h), r, r);
                return;
            }
            double fw = Math.Max(_fraction > 0 ? h : 0, w * _fraction);
            if (fw > 0) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, fw, h), r, r);
        }
    }

    /// <summary>"正在进行" row: name (+ lane) · progress bar · done/total · elapsed.</summary>
    public class ProgressRow : Grid
    {
        private readonly UiText _name = new UiText((string)null);
        private readonly UiText _lane = new UiText((string)null, TextVariant.Caption) { Margin = new Thickness(6, 0, 0, 0) };
        private readonly ProgressLine _bar = new ProgressLine { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        private readonly UiText _count = new UiText((string)null, TextVariant.Small) { TextAlignment = TextAlignment.Right, MinWidth = 90 };
        private readonly UiText _elapsed = new UiText((string)null, TextVariant.Small) { TextAlignment = TextAlignment.Right, MinWidth = 60, Margin = new Thickness(8, 0, 0, 0) };

        public ProgressRow()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 80 });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 60 });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            this.Bind(HeightProperty, Tokens.RowHeight);
            var nameSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nameSp.Children.Add(_name);
            nameSp.Children.Add(_lane);
            SetColumn(nameSp, 0);
            SetColumn(_bar, 1);
            SetColumn(_count, 2);
            SetColumn(_elapsed, 3);
            Children.Add(nameSp);
            Children.Add(_bar);
            Children.Add(_count);
            Children.Add(_elapsed);
        }

        public void Update(string name, string lane, long done, long total, double elapsedMs)
        {
            _name.Text = name ?? string.Empty;
            _lane.Text = lane ?? string.Empty;
            _lane.Visibility = string.IsNullOrEmpty(lane) ? Visibility.Collapsed : Visibility.Visible;
            _bar.Fraction = total > 0 ? (double)done / total : -1;
            _count.Text = total > 0 ? PerfMath.FormatCount(done) + " / " + PerfMath.FormatCount(total) : PerfMath.FormatCount(done);
            _elapsed.Text = PerfMath.FormatDuration(elapsedMs);
            AutomationProperties.SetName(this, _name.Text + ", " + _count.Text + ", " + _elapsed.Text);
        }
    }

    /// <summary>One segment of a <see cref="StackedBar"/>.</summary>
    public readonly struct BarSegment
    {
        public readonly string Name;
        public readonly double Value;

        public BarSegment(string name, double value)
        {
            Name = name;
            Value = value;
        }
    }

    /// <summary>
    /// Horizontal stacked bar (memory breakdown). Segment colors cycle through badge solid tokens so they are
    /// themed and distinct; the same index → same color across bars (shared legend). Drawn in OnRender.
    /// </summary>
    public class StackedBar : FrameworkElement
    {
        public static readonly BadgeKind[] Palette =
        {
            BadgeKind.UClass, BadgeKind.UFunction, BadgeKind.UProperty, BadgeKind.UEnum, BadgeKind.UStruct,
            BadgeKind.Macro, BadgeKind.Asset, BadgeKind.Delegate, BadgeKind.Module, BadgeKind.File,
        };

        private IReadOnlyList<BarSegment> _segments = Array.Empty<BarSegment>();
        private double _scaleMax;

        public StackedBar()
        {
            Height = 12;
            SnapsToDevicePixels = true;
            Ui.TrackTheme(this, InvalidateVisual);
        }

        /// <summary>When &gt; 0, the bar length is relative to this total (compare several bars).</summary>
        public double ScaleMax
        {
            get => _scaleMax;
            set
            {
                _scaleMax = value;
                InvalidateVisual();
            }
        }

        public void SetSegments(IReadOnlyList<BarSegment> segments)
        {
            _segments = segments ?? Array.Empty<BarSegment>();
            double total = 0;
            var name = new System.Text.StringBuilder();
            foreach (var s in _segments)
            {
                total += Math.Max(0, s.Value);
                name.Append(s.Name).Append(' ').Append(PerfMath.FormatBytes(s.Value)).Append("; ");
            }
            AutomationProperties.SetName(this, name.ToString());
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) => new Size(0, double.IsNaN(Height) ? 12 : Height);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var track = TryFindResource(Tokens.BorderSubtle) as Brush;
            double r = Math.Min(3, h / 2);
            if (track != null) dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), r, r);
            double total = 0;
            foreach (var s in _segments) total += Math.Max(0, s.Value);
            double denom = _scaleMax > 0 ? Math.Max(_scaleMax, total) : total;
            if (denom <= 0) return;
            var clip = new RectangleGeometry(new Rect(0, 0, w * total / denom, h), r, r);
            clip.Freeze();
            dc.PushClip(clip);
            double x = 0;
            for (int i = 0; i < _segments.Count; i++)
            {
                double sw = w * Math.Max(0, _segments[i].Value) / denom;
                if (sw <= 0) continue;
                var b = TryFindResource(Tokens.BadgeSolid(Palette[i % Palette.Length])) as Brush;
                if (b != null) dc.DrawRectangle(b, null, new Rect(x, 0, Math.Max(0, sw - (i < _segments.Count - 1 ? 1 : 0)), h));
                x += sw;
            }
            dc.Pop();
        }
    }

    /// <summary>Legend for <see cref="StackedBar"/> segments (swatch + name), same color order.</summary>
    public class BarLegend : WrapPanel
    {
        private readonly List<(Border Swatch, UiText Text)> _items = new List<(Border, UiText)>();

        public void SetNames(IReadOnlyList<string> names)
        {
            names = names ?? Array.Empty<string>();
            while (_items.Count < names.Count)
            {
                var sw = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                var t = new UiText((string)null, TextVariant.Small);
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 14, 2) };
                sp.Children.Add(sw);
                sp.Children.Add(t);
                Children.Add(sp);
                _items.Add((sw, t));
            }
            for (int i = 0; i < _items.Count; i++)
            {
                var (sw, t) = _items[i];
                bool on = i < names.Count;
                ((FrameworkElement)sw.Parent).Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (!on) continue;
                t.Text = names[i];
                sw.Bind(Border.BackgroundProperty, Tokens.BadgeSolid(StackedBar.Palette[i % StackedBar.Palette.Length]));
            }
        }
    }
}
