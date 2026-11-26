using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Vcs;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Vcs
{
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(ChangeMargin.MarginName)]
    [MarginContainer(PredefinedMarginNames.Left)]
    [Order(After = PredefinedMarginNames.LineNumber)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ChangeMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container)
        {
            if (EditorServices.Document(host.TextView.TextBuffer) == null) return null;
            return new ChangeMargin(host.TextView);
        }
    }

    /// <summary>
    /// G2 行号旁的改动色条（对比 HEAD / #have）：新增绿、修改蓝、删除红三角。只画可见的行（OnRender 里按 TextViewLines），
    /// 差异由 <see cref="VcsDocument"/> 在后台算好；点色条弹出这一块（<see cref="HunkPopup"/>）。设置 vcs.changeBars 关掉时宽度为 0。
    /// v0.11.1：色条显示时把这个编辑器里 VS 自带的“跟踪更改”边距（黄 / 绿条，记的是这次打开以来的编辑，不是和版本库比）关掉，
    /// 行号旁只剩一套和 Git / P4 一致的标记；只改这个视图的选项，不动 VS 的全局设置，色条隐藏（设置关掉、不在版本库里）时还原。
    /// 行号按每个 TextViewLine 自己的快照映射到 diff 快照（布局还没跟上最新编辑时也不会错行），色条画在文字那一段（TextTop），
    /// 不含 Code Vision 在上面加出来的空行。v0.11.3：位置按文字区到边距的实际变换算（编辑器缩放时只有文字区缩放），点击直接命中画出来的色条。
    /// </summary>
    internal sealed class ChangeMargin : FrameworkElement, IWpfTextViewMargin
    {
        public const string MarginName = "UEToVS.VcsChanges";
        private const double BarWidth = 4, Width_ = 6;

        private static readonly Brush AddedBrush = Frozen(Color.FromRgb(0x4C, 0xAF, 0x50));
        private static readonly Brush ModifiedBrush = Frozen(Color.FromRgb(0x3D, 0x8B, 0xE0));
        private static readonly Brush DeletedBrush = Frozen(Color.FromRgb(0xE0, 0x4A, 0x4A));

        private readonly IWpfTextView _view;
        private readonly VcsDocument _doc;
        private bool _disposed;
        private bool _hidVsTrackChanges;
        private string _loggedGeometry;
        private readonly System.Collections.Generic.List<(double Top, double Bottom, DiffHunk Hunk)> _hits = new System.Collections.Generic.List<(double, double, DiffHunk)>();

        public ChangeMargin(IWpfTextView view)
        {
            _view = view;
            _doc = VcsDocument.For(view.TextBuffer);
            _doc.Attach();
            _doc.Changed += OnChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.ZoomLevelChanged += OnZoomChanged;
            _view.Closed += (s, e) => Dispose();
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            UpdateWidth();
        }

        /// <summary>
        /// 我们的色条显示时关掉这个视图的 VS“跟踪更改”边距，不显示时还原成跟随 VS 的全局选项（ClearOptionValue，不写死 true）。
        /// </summary>
        private void SyncVsTrackChanges(bool oursVisible)
        {
            if (_view.IsClosed) return;
            var options = _view.Options;
            if (oursVisible)
            {
                if (!_hidVsTrackChanges || options.GetOptionValue(DefaultTextViewHostOptions.ChangeTrackingId))
                {
                    options.SetOptionValue(DefaultTextViewHostOptions.ChangeTrackingId, false);
                    _hidVsTrackChanges = true;
                }
            }
            else if (_hidVsTrackChanges)
            {
                options.ClearOptionValue(DefaultTextViewHostOptions.ChangeTrackingId);
                _hidVsTrackChanges = false;
            }
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private bool Visible => VcsService.Active && VcsService.Settings.VcsChangeBars && _doc.Revision == null && _doc.BaseLines != null;

        private void UpdateWidth()
        {
            bool visible = Visible;
            double w = visible ? Width_ : 0;
            if (Width != w) Width = w;
            SyncVsTrackChanges(visible);
        }

        private void OnChanged(object sender, EventArgs e) => Safe.Execute(VcsService.L, "vcs.margin.changed", () =>
        {
            UpdateWidth();
            InvalidateVisual();
        });

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (Visible) InvalidateVisual();
        }

        private void OnZoomChanged(object sender, ZoomLevelChangedEventArgs e)
        {
            if (Visible) InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            _hits.Clear();
            if (_disposed || !Visible || _view.IsClosed || _view.InLayout) return;
            var hunks = _doc.Hunks;
            if (hunks == null || hunks.Count == 0) return;
            var geo = MarginGeometry.For(_view, this);
            LogGeometry(geo);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // 整条可点击
            foreach (var line in _view.TextViewLines)
            {
                int dl = _doc.ToDiffLine(line.Start);
                var h = HunkMath.At(hunks, dl);
                if (h == null) continue;
                // 画在文字那一段：Code Vision 等行变换在行上面加出的空白不算（和行号对齐）
                var (y, bottom) = geo.TextSpan(line);
                if (h.Kind == ChangeKind.Deleted)
                {
                    if (!line.IsFirstTextViewLineForSnapshotLine) continue;
                    // 删除：在这一行顶上画朝右的小三角
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(0, y - 4), true, true);
                        c.LineTo(new Point(Width_, y), true, false);
                        c.LineTo(new Point(0, y + 4), true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(DeletedBrush, null, g);
                    _hits.Add((y - 5, y + 5, h));
                    continue;
                }
                dc.DrawRectangle(h.Kind == ChangeKind.Added ? AddedBrush : ModifiedBrush, null, new Rect(1, y, BarWidth, Math.Max(1, bottom - y)));
                _hits.Add((y, bottom, h));
            }
            _doc.EnsureDiffCurrent();
        }

        /// <summary>
        /// 记一次换算参数（变了再记）：缩放、文字区在边距里的位移、第一行的位置。色条位置再不对时，拿日志里的 vcs.margin.geometry 对照截图。
        /// </summary>
        private void LogGeometry(MarginGeometry geo)
        {
            double origin = geo.ToMargin(_view.ViewportTop);
            string key = Math.Round(geo.Scale, 3) + "|" + Math.Round(origin, 1) + "|" + geo.FromTransform;
            if (key == _loggedGeometry) return;
            _loggedGeometry = key;
            var first = _view.TextViewLines?.FirstVisibleLine;
            VcsService.L.Info("vcs.margin.geometry", null, ("scale", Math.Round(geo.Scale, 3)), ("originY", Math.Round(origin, 1)), ("transform", geo.FromTransform),
                              ("zoom", Math.Round(_view.ZoomLevel, 1)), ("lineHeight", Math.Round(_view.LineHeight, 1)), ("marginHeight", Math.Round(ActualHeight, 1)),
                              ("viewHeight", Math.Round(_view.VisualElement.ActualHeight, 1)), ("firstLine", first?.Start.GetContainingLine().LineNumber ?? -1),
                              ("firstTextTop", first == null ? 0 : Math.Round(first.TextTop - _view.ViewportTop, 1)));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Safe.Execute(VcsService.L, "vcs.margin.click", () =>
            {
                if (!Visible) return;
                // 直接命中上次画出来的色条：画在哪里就点得到哪里，不再从鼠标位置反算行号
                double y = e.GetPosition(this).Y;
                DiffHunk h = null;
                foreach (var (top, bottom, hunk) in _hits)
                    if (y >= top && y < bottom) { h = hunk; break; }
                if (h == null)
                    foreach (var (top, bottom, hunk) in _hits)
                        if (y >= top - 3 && y < bottom + 3) { h = hunk; break; }
                VcsService.L.Debug("vcs.margin.click", null, ("y", Math.Round(y, 1)), ("bars", _hits.Count), ("hit", h?.ToString() ?? "none"));
                if (h == null) return;
                e.Handled = true;
                HunkPopup.Show(_view, _doc, h, PopupAnchor.Mouse());
            });
        }

        // ---------------------------------------------------------------- IWpfTextViewMargin

        public FrameworkElement VisualElement => this;
        public double MarginSize => ActualWidth;
        public bool Enabled => !_disposed;

        public ITextViewMargin GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _doc.Changed -= OnChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.ZoomLevelChanged -= OnZoomChanged;
            if (_hidVsTrackChanges && !_view.IsClosed) _view.Options.ClearOptionValue(DefaultTextViewHostOptions.ChangeTrackingId);
            _hidVsTrackChanges = false;
            _doc.Detach();
        }
    }
}
