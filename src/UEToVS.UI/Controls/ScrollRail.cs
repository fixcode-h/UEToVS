using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Controls
{
    /// <summary>
    /// Slim Rider-like scrollbar drawn in OnRender (one element, no template). Thumb widens on hover; click on the
    /// track jumps there; drag scrolls. Units are arbitrary (pixels or rows) — the owner sets Value/Maximum/Viewport.
    /// </summary>
    public class ScrollRail : FrameworkElement
    {
        public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
            nameof(Orientation), typeof(Orientation), typeof(ScrollRail),
            new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ThumbBrushProperty = DependencyProperty.Register(
            nameof(ThumbBrush), typeof(Brush), typeof(ScrollRail), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThumbHoverBrushProperty = DependencyProperty.Register(
            nameof(ThumbHoverBrush), typeof(Brush), typeof(ScrollRail), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private const double Thickness = 10;
        private double _value, _maximum, _viewport = 1;
        private bool _dragging;
        private double _dragStartPos, _dragStartValue;

        public ScrollRail()
        {
            this.Bind(ThumbBrushProperty, Tokens.ScrollThumb);
            this.Bind(ThumbHoverBrushProperty, Tokens.ScrollThumbHover);
            Focusable = false;
            SnapsToDevicePixels = true;
        }

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public Brush ThumbBrush
        {
            get => (Brush)GetValue(ThumbBrushProperty);
            set => SetValue(ThumbBrushProperty, value);
        }

        public Brush ThumbHoverBrush
        {
            get => (Brush)GetValue(ThumbHoverBrushProperty);
            set => SetValue(ThumbHoverBrushProperty, value);
        }

        /// <summary>Raised when the user scrolls with the rail (new value in owner units).</summary>
        public event Action<double> UserScrolled;

        public double Value => _value;
        public double Maximum => _maximum;
        public double Viewport => _viewport;
        public bool IsScrollable => _maximum > 1e-6;

        public void Update(double value, double maximum, double viewport)
        {
            maximum = Math.Max(0, maximum);
            value = Math.Max(0, Math.Min(maximum, value));
            viewport = Math.Max(1e-6, viewport);
            if (Math.Abs(value - _value) < 1e-6 && Math.Abs(maximum - _maximum) < 1e-6 && Math.Abs(viewport - _viewport) < 1e-6) return;
            _value = value;
            _maximum = maximum;
            _viewport = viewport;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) =>
            Orientation == Orientation.Vertical ? new Size(Thickness, 0) : new Size(0, Thickness);

        private double TrackLength => Orientation == Orientation.Vertical ? ActualHeight : ActualWidth;

        private void ThumbGeometry(out double start, out double length)
        {
            double track = TrackLength;
            double total = _maximum + _viewport;
            length = Math.Max(24, Math.Min(track, track * _viewport / total));
            double free = Math.Max(0, track - length);
            start = _maximum > 0 ? free * _value / _maximum : 0;
        }

        protected override void OnRender(DrawingContext dc)
        {
            // Transparent background so the whole rail is hit-testable.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (!IsScrollable || TrackLength <= 0) return;
            ThumbGeometry(out double start, out double length);
            bool hot = IsMouseOver || _dragging;
            double w = hot ? 8 : 5;
            var brush = hot ? (ThumbHoverBrush ?? ThumbBrush) : ThumbBrush;
            if (brush == null) return;
            Rect r = Orientation == Orientation.Vertical
                ? new Rect(ActualWidth - w - 1, start + 1, w, Math.Max(0, length - 2))
                : new Rect(start + 1, ActualHeight - w - 1, Math.Max(0, length - 2), w);
            dc.DrawRoundedRectangle(brush, null, r, w / 2, w / 2);
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            InvalidateVisual();
        }

        private double Pos(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            return Orientation == Orientation.Vertical ? p.Y : p.X;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Ui.Safe("ui.scrollrail.down", () =>
            {
                if (!IsScrollable) return;
                ThumbGeometry(out double start, out double length);
                double pos = Pos(e);
                if (pos < start || pos > start + length)
                {
                    // Jump so the thumb centers on the click.
                    double free = Math.Max(1, TrackLength - length);
                    double v = (pos - length / 2) / free * _maximum;
                    Set(v);
                    ThumbGeometry(out start, out length);
                }
                _dragging = true;
                _dragStartPos = pos;
                _dragStartValue = _value;
                CaptureMouse();
                e.Handled = true;
            });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            Ui.Safe("ui.scrollrail.move", () =>
            {
                ThumbGeometry(out _, out double length);
                double free = Math.Max(1, TrackLength - length);
                Set(_dragStartValue + (Pos(e) - _dragStartPos) / free * _maximum);
            });
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _dragging = false;
            InvalidateVisual();
        }

        private void Set(double v)
        {
            v = Math.Max(0, Math.Min(_maximum, v));
            if (Math.Abs(v - _value) < 1e-9) return;
            _value = v;
            InvalidateVisual();
            UserScrolled?.Invoke(v);
        }
    }

    /// <summary>ScrollViewer using <see cref="ScrollRail"/>s (consistent look in and outside VS).</summary>
    public class UiScrollViewer : ScrollViewer
    {
        private ScrollRail _v, _h;

        public UiScrollViewer()
        {
            Style = Styles.ScrollViewer;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Focusable = false;
            ScrollChanged += (s, e) => Ui.Safe("ui.scroll.changed", SyncRails);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _v = GetTemplateChild("PART_VRail") as ScrollRail;
            _h = GetTemplateChild("PART_HRail") as ScrollRail;
            if (_v != null)
            {
                _v.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(ComputedVerticalScrollBarVisibility)) { Source = this });
                _v.UserScrolled += v => Ui.Safe("ui.scroll.v", () => ScrollToVerticalOffset(v));
            }
            if (_h != null)
            {
                _h.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(ComputedHorizontalScrollBarVisibility)) { Source = this });
                _h.UserScrolled += v => Ui.Safe("ui.scroll.h", () => ScrollToHorizontalOffset(v));
            }
            SyncRails();
        }

        private void SyncRails()
        {
            _v?.Update(VerticalOffset, ScrollableHeight, ViewportHeight);
            _h?.Update(HorizontalOffset, ScrollableWidth, ViewportWidth);
        }
    }
}
