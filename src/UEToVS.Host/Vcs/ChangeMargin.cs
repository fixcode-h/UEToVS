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

        public ChangeMargin(IWpfTextView view)
        {
            _view = view;
            _doc = VcsDocument.For(view.TextBuffer);
            _doc.Attach();
            _doc.Changed += OnChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += (s, e) => Dispose();
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            UpdateWidth();
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
            double w = Visible ? Width_ : 0;
            if (Width != w) Width = w;
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

        protected override void OnRender(DrawingContext dc)
        {
            if (_disposed || !Visible || _view.IsClosed || _view.InLayout) return;
            var hunks = _doc.Hunks;
            if (hunks == null || hunks.Count == 0) return;
            var snapshot = _view.TextSnapshot;
            double top = _view.ViewportTop;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // 整条可点击
            foreach (var line in _view.TextViewLines)
            {
                int n = line.Start.GetContainingLine().LineNumber;
                int dl = _doc.ToDiffLine(snapshot, n);
                var h = HunkMath.At(hunks, dl);
                if (h == null) continue;
                double y = line.Top - top;
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
                    continue;
                }
                dc.DrawRectangle(h.Kind == ChangeKind.Added ? AddedBrush : ModifiedBrush, null, new Rect(1, y, BarWidth, line.Height));
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Safe.Execute(VcsService.L, "vcs.margin.click", () =>
            {
                if (!Visible) return;
                double y = e.GetPosition(this).Y + _view.ViewportTop;
                var line = _view.TextViewLines.GetTextViewLineContainingYCoordinate(y);
                if (line == null) return;
                int n = line.Start.GetContainingLine().LineNumber;
                int dl = _doc.ToDiffLine(_view.TextSnapshot, n);
                var h = HunkMath.At(_doc.Hunks, dl);
                // 删除三角跨在两行之间：点在上一行底部也算
                if (h == null && y - line.Top < 4 || h == null && line.Bottom - y < 4) h = HunkMath.At(_doc.Hunks, y - line.Top < 4 ? dl : dl + 1);
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
            _doc.Detach();
        }
    }
}
