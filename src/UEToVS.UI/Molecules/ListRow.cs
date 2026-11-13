using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Molecules
{
    /// <summary>A recyclable row of a virtualized list.</summary>
    public interface IVirtualRow
    {
        /// <summary>Data index currently shown (-1 = none).</summary>
        int BoundIndex { get; set; }

        void SetState(bool selected, bool hover, bool active);
    }

    /// <summary>
    /// List row molecule: [indent/chevron] [icon] primary · secondary ……… [badges] [right text] [kbd] [›].
    /// A single custom panel (no Grid). Column widths come from <see cref="RowTextFit.Allocate"/>: the primary gets its
    /// full width first, the secondary (location) takes what is left and is trimmed in the middle, path aware
    /// ("Plugins/Foo/…/Private/Bar"); the right group keeps its natural width and stays right-aligned. Re-fitted on every
    /// width change. Selection/hover is a rounded rect drawn in OnRender (no extra elements, no effects).
    /// </summary>
    public class ListRow : Panel, IVirtualRow
    {
        public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
            nameof(HighlightBrush), typeof(Brush), typeof(ListRow), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Accent pill drawn at the left of the selected row (VS 2026 / Fluent selection indicator).</summary>
        public static readonly DependencyProperty IndicatorBrushProperty = DependencyProperty.Register(
            nameof(IndicatorBrush), typeof(Brush), typeof(ListRow), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PadXProperty = DependencyProperty.Register(
            nameof(PadX), typeof(double), typeof(ListRow), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(ListRow), new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        private const double IndentStep = 16;
        private const double Inset = 4;

        private readonly Path _chevron = new Path { Data = Ui.ChevronRight, StrokeThickness = 1.3, Width = 5, Height = 9, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        private readonly UiIcon _icon = new UiIcon();
        private readonly TypeGlyph _glyph = new TypeGlyph();
        private readonly HighlightedText _primary = new HighlightedText();
        private readonly HighlightedText _secondary = new HighlightedText { IsSecondary = true };
        private readonly StackPanel _badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly List<Badge> _badgePool = new List<Badge>();
        private readonly TextBlock _right = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Kbd _kbd = new Kbd();
        private readonly Path _submenu = new Path { Data = Ui.ChevronRight, StrokeThickness = 1.3, Width = 5, Height = 9, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };

        private bool _selected, _hover, _active, _header, _dimmed;
        private int _indent;
        private bool? _expanded;
        private string _tooltip;
        private double _wantP, _wantS, _wantR;
        private double _leftW, _rightFixedW, _fitWidth = double.NaN, _rowHeight = 24, _pixelsPerDip;
        private RowColumns _cols;
        private bool _arranging;
        private string _secSource, _secShown;
        private IReadOnlyList<TextRange> _secRanges;

        public ListRow()
        {
            SnapsToDevicePixels = true;
            ClipToBounds = true;
            Background = Brushes.Transparent; // hit-testable
            this.Bind(PadXProperty, Tokens.PadRowX);
            this.Bind(IndicatorBrushProperty, Tokens.SelectionIndicator);
            this.Bind(GapProperty, Tokens.GapInline);
            _chevron.Bind(Shape.StrokeProperty, Tokens.TextSecondary);
            _submenu.Bind(Shape.StrokeProperty, Tokens.TextSecondary);
            _right.Bind(TextBlock.ForegroundProperty, Tokens.TextSecondary);
            _right.Bind(TextBlock.FontSizeProperty, Tokens.FontSizeSmall);
            foreach (UIElement c in new UIElement[] { _chevron, _icon, _glyph, _primary, _secondary, _badges, _right, _kbd, _submenu })
            {
                c.Visibility = Visibility.Collapsed;
                Children.Add(c);
            }
            Focusable = false;
        }

        public int BoundIndex { get; set; } = -1;

        public Brush HighlightBrush
        {
            get => (Brush)GetValue(HighlightBrushProperty);
            set => SetValue(HighlightBrushProperty, value);
        }

        public Brush IndicatorBrush
        {
            get => (Brush)GetValue(IndicatorBrushProperty);
            set => SetValue(IndicatorBrushProperty, value);
        }

        public double PadX
        {
            get => (double)GetValue(PadXProperty);
            set => SetValue(PadXProperty, value);
        }

        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        /// <summary>True when the pointer is over the expander chevron (TreeList toggles on click there).</summary>
        public bool IsOverChevron(Point p) =>
            _chevron.Visibility == Visibility.Visible && p.X <= PadX + _indent * IndentStep + 14;

        public void Bind(RowData d, int index, int count)
        {
            _header = d.IsGroupHeader;
            _dimmed = d.IsDimmed || !d.IsEnabled;
            _indent = Math.Max(0, d.Indent);
            _expanded = d.Expanded;

            _chevron.Visibility = d.Expanded.HasValue ? Visibility.Visible : Visibility.Collapsed;
            if (d.Expanded.HasValue)
                _chevron.RenderTransform = d.Expanded.Value ? new RotateTransform(90, 2.5, 4.5) : Transform.Identity;

            bool hasMoniker = !UiIcon.IsEmpty(d.Moniker);
            _icon.Visibility = hasMoniker && !_header ? Visibility.Visible : Visibility.Collapsed;
            if (hasMoniker) _icon.Moniker = d.Moniker;
            _glyph.Visibility = !hasMoniker && d.Glyph.HasValue && !_header ? Visibility.Visible : Visibility.Collapsed;
            if (d.Glyph.HasValue) _glyph.Kind = d.Glyph.Value;

            _primary.Set(d.Primary, d.PrimaryRanges);
            _primary.Visibility = string.IsNullOrEmpty(d.Primary) ? Visibility.Collapsed : Visibility.Visible;
            if (_header)
            {
                _primary.IsSecondary = true;
                _primary.FontWeight = FontWeights.SemiBold;
                _primary.Bind(TextBlock.FontSizeProperty, Tokens.FontSizeSmall);
            }
            else
            {
                _primary.IsSecondary = false;
                _primary.FontWeight = TextWeights.Body;
                _primary.ClearValue(TextBlock.FontSizeProperty);
            }

            _secSource = d.Secondary ?? string.Empty;
            _secRanges = d.SecondaryRanges;
            _secShown = _secSource;
            _secondary.Set(d.Secondary, d.SecondaryRanges);
            _fitWidth = double.NaN;
            _secondary.Visibility = string.IsNullOrEmpty(d.Secondary) ? Visibility.Collapsed : Visibility.Visible;

            int n = d.Badges.Count;
            while (_badgePool.Count < n)
            {
                var b = new Badge { Margin = new Thickness(4, 0, 0, 0) };
                _badgePool.Add(b);
                _badges.Children.Add(b);
            }
            for (int i = 0; i < _badgePool.Count; i++)
            {
                var b = _badgePool[i];
                if (i < n)
                {
                    var spec = d.Badges[i];
                    b.Text = spec.Text;
                    b.Kind = spec.Kind;
                    b.Solid = spec.Solid;
                    b.Visibility = Visibility.Visible;
                }
                else b.Visibility = Visibility.Collapsed;
            }
            _badges.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;

            string right = d.Right;
            if (_header && d.GroupCount.HasValue && string.IsNullOrEmpty(right)) right = PerfMath.FormatCount(d.GroupCount.Value);
            _right.Text = right ?? string.Empty;
            _right.Visibility = string.IsNullOrEmpty(right) ? Visibility.Collapsed : Visibility.Visible;

            _kbd.Shortcut = d.Shortcut;
            _kbd.Visibility = string.IsNullOrEmpty(d.Shortcut) ? Visibility.Collapsed : Visibility.Visible;
            _submenu.Visibility = d.HasSubmenu ? Visibility.Visible : Visibility.Collapsed;

            Opacity = _dimmed ? 0.6 : 1.0;
            if (!string.Equals(_tooltip, d.ToolTip, StringComparison.Ordinal))
            {
                _tooltip = d.ToolTip;
                if (string.IsNullOrEmpty(d.ToolTip)) ToolTip = null;
                else Tip.Set(this, d.ToolTip);
            }

            string name = d.AutomationName;
            if (name == null)
            {
                name = d.Primary ?? string.Empty;
                if (!string.IsNullOrEmpty(d.Secondary)) name += ", " + d.Secondary;
                if (!string.IsNullOrEmpty(d.Right)) name += ", " + d.Right;
                for (int i = 0; i < d.Badges.Count; i++) name += ", " + d.Badges[i].Text;
                if (_header && d.GroupCount.HasValue) name = Strings.ListGroup(d.Primary, d.GroupCount.Value);
                else if (count > 0) name += ", " + Strings.ListPosition(index + 1, count);
            }
            AutomationProperties.SetName(this, name);
            ApplyState();
            InvalidateMeasure();
        }

        public void SetState(bool selected, bool hover, bool active)
        {
            if (_selected == selected && _hover == hover && _active == active) return;
            _selected = selected;
            _hover = hover;
            _active = active;
            ApplyState();
        }

        private void ApplyState()
        {
            bool sel = _selected && !_header;
            if (sel) SetResourceReference(HighlightBrushProperty, _active ? Tokens.SelectionBackground : Tokens.SelectionInactiveBackground);
            else if (_hover && !_header) SetResourceReference(HighlightBrushProperty, Tokens.SurfaceHover);
            else HighlightBrush = null;
            bool selFg = sel && _active;
            _primary.IsSelectedRow = selFg && !_header;
            _secondary.IsSelectedRow = selFg;
            _right.Bind(TextBlock.ForegroundProperty, selFg ? Tokens.SelectionSecondary : Tokens.TextSecondary);
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            var b = HighlightBrush;
            if (b == null) return;
            double r = Math.Min(4, ActualHeight / 2);
            dc.DrawRoundedRectangle(b, null, new Rect(Inset, 1, Math.Max(0, ActualWidth - 2 * Inset), Math.Max(0, ActualHeight - 2)), r, r);
            // Fluent selection indicator: 3px accent pill, vertically centered, only on the active selection.
            var ind = IndicatorBrush;
            if (_selected && _active && !_header && ind != null && ActualHeight > 10)
            {
                double h = Math.Min(16, ActualHeight - 8);
                dc.DrawRoundedRectangle(ind, null, new Rect(Inset, (ActualHeight - h) / 2, 3, h), 1.5, 1.5);
            }
        }

        protected override Size MeasureOverride(Size available)
        {
            var inf = new Size(double.PositiveInfinity, available.Height);
            double h = double.IsInfinity(available.Height) ? 24 : available.Height;
            _rowHeight = h;
            foreach (UIElement c in Children)
                if (c.Visibility != Visibility.Collapsed && c != _primary && c != _secondary && c != _right) c.Measure(inf);
            MeasureNatural();
            Fit(available.Width);
            if (!double.IsInfinity(available.Width)) return new Size(available.Width, h);
            double gaps = (_cols.Secondary > 0 ? Gap : 0) + (_cols.RightText > 0 ? Gap : 0);
            return new Size(_leftW + _cols.Primary + _cols.Secondary + _rightFixedW + _cols.RightText + gaps, h);
        }

        /// <summary>
        /// Natural (untrimmed) widths of the three text columns, measured with the real typeface. The primary and right
        /// texts are measured as TextBlocks (exact, highlight runs included); the secondary is measured from its source
        /// text with FormattedText because the TextBlock may currently hold a middle-trimmed copy.
        /// </summary>
        private void MeasureNatural()
        {
            var inf = new Size(double.PositiveInfinity, _rowHeight);
            _wantP = 0;
            _wantS = 0;
            _wantR = 0;
            if (_primary.Visibility != Visibility.Collapsed)
            {
                _primary.Measure(inf);
                _wantP = Math.Ceiling(_primary.DesiredSize.Width) + RowTextFit.Slack;
            }
            if (_right.Visibility != Visibility.Collapsed)
            {
                _right.Measure(inf);
                _wantR = Math.Ceiling(_right.DesiredSize.Width) + RowTextFit.Slack;
            }
            if (_secondary.Visibility != Visibility.Collapsed)
            {
                double w = MeasureSecondary(_secSource, _secRanges);
                _wantS = w > 0 ? Math.Ceiling(w) + RowTextFit.Slack : 0;
            }
        }

        /// <summary>Width of <paramref name="text"/> in the secondary column's font (matches in the match font weight).</summary>
        private double MeasureSecondary(string text, IReadOnlyList<TextRange> ranges)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            try
            {
                if (_pixelsPerDip <= 0) _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var tf = new Typeface(_secondary.FontFamily, _secondary.FontStyle, _secondary.FontWeight, _secondary.FontStretch);
                var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, _secondary.FontSize, Brushes.Black, _pixelsPerDip);
                if (ranges != null && ranges.Count > 0 && TryFindResource(Tokens.MatchFontWeight) is FontWeight mw && mw != _secondary.FontWeight)
                {
                    foreach (var r in ranges)
                    {
                        int st = Math.Max(0, r.Start), en = Math.Min(text.Length, r.End);
                        if (en > st) ft.SetFontWeight(mw, st, en - st);
                    }
                }
                return ft.WidthIncludingTrailingWhitespace;
            }
            catch (Exception ex)
            {
                Ui.Logger.Warn("ui.row.measure_failed", "Measuring the secondary text failed", ex);
                return 0;
            }
        }

        /// <summary>
        /// Allocates the columns for <paramref name="width"/> (<see cref="RowTextFit.Allocate"/>: the name first, then the
        /// location, trimmed in the middle) and measures the text children at their final widths. Re-run on every width
        /// change (resize) so trimming always matches the space actually available.
        /// </summary>
        private void Fit(double width)
        {
            double pad = PadX + Inset, gap = Gap;
            _leftW = pad + _indent * IndentStep;
            if (_chevron.Visibility != Visibility.Collapsed) _leftW += 10;
            if (_icon.Visibility != Visibility.Collapsed) _leftW += _icon.DesiredSize.Width + gap;
            if (_glyph.Visibility != Visibility.Collapsed) _leftW += _glyph.DesiredSize.Width + gap;
            _rightFixedW = pad;
            foreach (var e in new FrameworkElement[] { _badges, _kbd, _submenu })
                if (e.Visibility != Visibility.Collapsed) _rightFixedW += e.DesiredSize.Width + gap;

            double avail = double.IsInfinity(width) ? double.PositiveInfinity : width - _leftW - _rightFixedW;
            _cols = RowTextFit.Allocate(avail, _wantP, _wantS, _wantR, gap);
            _fitWidth = width;

            double h = _rowHeight;
            if (_primary.Visibility != Visibility.Collapsed) _primary.Measure(new Size(_cols.Primary, h));
            if (_right.Visibility != Visibility.Collapsed) _right.Measure(new Size(_cols.RightText, h));
            if (_secondary.Visibility != Visibility.Collapsed)
            {
                double cap = _cols.Secondary;
                // Matches drawn bolder can make the shown text a little wider than measured here: the TextBlock's own
                // end ellipsis remains the last guard.
                var trim = cap >= _wantS
                    ? MiddleTrimResult.Whole(_secSource)
                    : RowTextFit.TrimMiddle(_secSource, Math.Max(0, cap - RowTextFit.Slack), t => MeasureSecondary(t, null));
                if (!string.Equals(trim.Text, _secShown, StringComparison.Ordinal))
                {
                    _secShown = trim.Text;
                    _secondary.Set(trim.Text, trim.IsTrimmed ? trim.MapRanges(_secRanges) : _secRanges);
                }
                _secondary.Measure(new Size(cap, h));
            }
        }

        protected override void OnChildDesiredSizeChanged(UIElement child)
        {
            // Our size never depends on the children's widths; while arranging we re-measure them ourselves.
            if (_arranging) return;
            base.OnChildDesiredSizeChanged(child);
        }

        protected override Size ArrangeOverride(Size final)
        {
            _arranging = true;
            try
            {
                // The list may arrange us at a different width than it measured (resize): re-fit for the real width.
                if (double.IsNaN(_fitWidth) || Math.Abs(final.Width - _fitWidth) > 0.5) Fit(final.Width);
            }
            finally
            {
                _arranging = false;
            }
            double h = final.Height, gap = Gap;
            double x = PadX + Inset + _indent * IndentStep;
            void Place(UIElement e, double w)
            {
                double eh = Math.Min(h, e.DesiredSize.Height);
                e.Arrange(new Rect(x, (h - eh) / 2, w, eh));
            }
            if (_chevron.Visibility != Visibility.Collapsed) { Place(_chevron, 5); x += 10; }
            if (_icon.Visibility != Visibility.Collapsed) { Place(_icon, _icon.DesiredSize.Width); x += _icon.DesiredSize.Width + gap; }
            if (_glyph.Visibility != Visibility.Collapsed) { Place(_glyph, _glyph.DesiredSize.Width); x += _glyph.DesiredSize.Width + gap; }
            if (_primary.Visibility != Visibility.Collapsed) { Place(_primary, _cols.Primary); x += _cols.Primary + gap; }
            if (_secondary.Visibility != Visibility.Collapsed) Place(_secondary, _cols.Secondary);

            double rx = final.Width - PadX - Inset;
            foreach (var e in new FrameworkElement[] { _submenu, _kbd, _right, _badges })
            {
                if (e.Visibility == Visibility.Collapsed) continue;
                double w = e == _right ? _cols.RightText : e.DesiredSize.Width;
                rx -= w;
                double eh = Math.Min(h, e.DesiredSize.Height);
                e.Arrange(new Rect(rx, (h - eh) / 2, w, eh));
                rx -= gap;
            }
            return final;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            _pixelsPerDip = newDpi.PixelsPerDip;
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateMeasure();
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ListRowAutomationPeer(this);

        private sealed class ListRowAutomationPeer : FrameworkElementAutomationPeer
        {
            public ListRowAutomationPeer(ListRow owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

            protected override string GetClassNameCore() => "ListRow";

            protected override bool IsContentElementCore() => true;

            protected override List<AutomationPeer> GetChildrenCore() => null;
        }
    }
}
