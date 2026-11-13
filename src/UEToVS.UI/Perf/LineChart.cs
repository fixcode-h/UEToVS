using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Perf
{
    /// <summary>One line of a <see cref="LineChart"/>.</summary>
    public sealed class ChartSeries
    {
        public ChartSeries(Func<string> name, int capacity, object colorToken, Func<double, string> format)
        {
            Name = name;
            Data = new SampleBuffer(capacity);
            ColorToken = colorToken;
            Format = format ?? (v => v.ToString("0.#", CultureInfo.InvariantCulture));
        }

        public Func<string> Name { get; }
        public SampleBuffer Data { get; }
        public object ColorToken { get; set; }
        public Func<double, string> Format { get; }

        /// <summary>Fixed axis range (e.g. CPU 0..100); null = auto (nice axis from data).</summary>
        public double? FixedMin { get; set; }
        public double? FixedMax { get; set; }
    }

    /// <summary>
    /// Lightweight line chart / sparkline: up to two series (left and right Y axes), StreamGeometry per series,
    /// grid lines, axis labels, legend — all drawn in OnRender, no child elements. ~600 points per series; an update
    /// is one InvalidateVisual. <see cref="IsSparkline"/> hides axes, legend and grid.
    /// </summary>
    public class LineChart : FrameworkElement
    {
        private readonly List<ChartSeries> _series = new List<ChartSeries>();

        public LineChart()
        {
            MinHeight = 40;
            SnapsToDevicePixels = true;
            ClipToBounds = true;
            Ui.TrackTheme(this, InvalidateVisual);
            Strings.Track(this, InvalidateVisual);
        }

        public bool IsSparkline { get; set; }

        /// <summary>Seconds between samples (time axis labels).</summary>
        public double SampleIntervalSeconds { get; set; } = 1;

        public IReadOnlyList<ChartSeries> Series => _series;

        /// <summary>Adds a series; the first uses the left axis, the second the right axis.</summary>
        public ChartSeries AddSeries(ChartSeries s)
        {
            if (_series.Count >= 2) throw new InvalidOperationException("LineChart supports two series");
            _series.Add(s);
            InvalidateVisual();
            return s;
        }

        /// <summary>Replaces a series' samples (keeps the newest that fit) and redraws.</summary>
        public void SetData(int series, IReadOnlyList<double> values)
        {
            if (series < 0 || series >= _series.Count) return;
            _series[series].Data.SetAll(values);
            UpdateName();
            InvalidateVisual();
        }

        public void Append(int series, double value)
        {
            if (series < 0 || series >= _series.Count) return;
            _series[series].Data.Add(value);
            InvalidateVisual();
        }

        private void UpdateName()
        {
            var parts = new List<string>();
            foreach (var s in _series)
                if (s.Data.Count > 0) parts.Add(s.Name?.Invoke() + " " + s.Format(s.Data[s.Data.Count - 1]));
            AutomationProperties.SetName(this, string.Join(", ", parts));
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new Size(0, double.IsNaN(Height) ? (IsSparkline ? 32 : 160) : Height);

        private Brush Res(object key) => TryFindResource(key) as Brush;

        private FormattedText Text(string s, Brush brush, double size)
        {
            var family = TryFindResource(Tokens.FontFamilyUi) as FontFamily ?? SystemFonts.MessageFontFamily;
            return new FormattedText(s ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, TextWeights.Body, FontStretches.Normal), size, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 4 || h <= 4) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            var labelBrush = Res(Tokens.TextSecondary) ?? Brushes.Gray;
            var gridBrush = Res(Tokens.BorderSubtle) ?? Brushes.LightGray;
            double fontSize = TryFindResource(Tokens.FontSizeCaption) is double fs ? fs : 10;

            double left = 0, right = 0, top = 0, bottom = 0;
            if (!IsSparkline)
            {
                left = 44;
                right = _series.Count > 1 ? 56 : 8;
                top = fontSize + 10;
                bottom = fontSize + 6;
            }
            var plot = new Rect(left, top, Math.Max(1, w - left - right), Math.Max(1, h - top - bottom));
            int capacity = 1;
            foreach (var s in _series) capacity = Math.Max(capacity, s.Data.Capacity);

            var gridPen = new Pen(gridBrush, 1);
            gridPen.Freeze();

            for (int si = 0; si < _series.Count; si++)
            {
                var s = _series[si];
                var (dmin, dmax) = s.Data.Range();
                double min = s.FixedMin ?? Math.Min(0, dmin);
                double max = s.FixedMax ?? dmax;
                var axis = PerfMath.NiceAxis(min, max, 4);
                if (s.FixedMin.HasValue) axis.Min = s.FixedMin.Value;
                if (s.FixedMax.HasValue) axis.Max = s.FixedMax.Value;
                double range = Math.Max(1e-9, axis.Max - axis.Min);

                if (!IsSparkline)
                {
                    // Grid + labels (grid only for the first series).
                    for (double v = axis.Min; v <= axis.Max + axis.Step * 0.5; v += axis.Step)
                    {
                        double y = Math.Round(plot.Bottom - (v - axis.Min) / range * plot.Height) + 0.5;
                        if (si == 0) dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                        var ft = Text(s.Format(v), labelBrush, fontSize);
                        double ty = Math.Max(0, Math.Min(h - ft.Height, y - ft.Height / 2));
                        if (si == 0) dc.DrawText(ft, new Point(Math.Max(0, plot.Left - 6 - ft.Width), ty));
                        else dc.DrawText(ft, new Point(plot.Right + 6, ty));
                        if (axis.Step <= 0) break;
                    }
                }

                var brush = Res(s.ColorToken) ?? Brushes.SteelBlue;
                int n = s.Data.Count;
                if (n < 2) continue;
                double step = plot.Width / Math.Max(1, capacity - 1);
                double x0 = plot.Right - (n - 1) * step;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    bool started = false;
                    for (int i = 0; i < n; i++)
                    {
                        double v = s.Data[i];
                        if (double.IsNaN(v)) { started = false; continue; }
                        var p = new Point(x0 + i * step, plot.Bottom - (Math.Max(axis.Min, Math.Min(axis.Max, v)) - axis.Min) / range * plot.Height);
                        if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                        else ctx.LineTo(p, true, false);
                    }
                }
                geo.Freeze();
                var pen = new Pen(brush, IsSparkline ? 1.25 : 1.5) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }

            if (IsSparkline) return;

            // Time axis: "-10m … now".
            double seconds = (capacity - 1) * SampleIntervalSeconds;
            var startLabel = Text("-" + PerfMath.FormatDuration(seconds * 1000), labelBrush, fontSize);
            var nowLabel = Text(Strings.PerfNow, labelBrush, fontSize);
            dc.DrawText(startLabel, new Point(plot.Left, plot.Bottom + 3));
            dc.DrawText(nowLabel, new Point(plot.Right - nowLabel.Width, plot.Bottom + 3));

            // Legend.
            double lx = plot.Left;
            for (int si = 0; si < _series.Count; si++)
            {
                var s = _series[si];
                var brush = Res(s.ColorToken) ?? Brushes.SteelBlue;
                string last = s.Data.Count > 0 ? " " + s.Format(s.Data[s.Data.Count - 1]) : string.Empty;
                var ft = Text((s.Name?.Invoke() ?? string.Empty) + last, labelBrush, fontSize);
                dc.DrawRoundedRectangle(brush, null, new Rect(lx, 3 + ft.Height / 2 - 1.5, 10, 3), 1.5, 1.5);
                dc.DrawText(ft, new Point(lx + 14, 2));
                lx += 14 + ft.Width + 16;
            }
        }
    }
}
