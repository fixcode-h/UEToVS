using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Vcs;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Vcs
{
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(BlameGutterMargin.MarginName)]
    [MarginContainer(PredefinedMarginNames.Left)]
    [Order(Before = PredefinedMarginNames.Glyph)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class BlameGutterMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container)
        {
            if (EditorServices.Document(host.TextView.TextBuffer) == null) return null;
            return host.TextView.Properties.GetOrCreateSingletonProperty(typeof(BlameGutterMargin), () => new BlameGutterMargin(host.TextView));
        }
    }

    /// <summary>
    /// G4 追溯栏（命令 Ctrl+Shift+Alt+U, O 打开 / 关闭，每个编辑器窗口单独）：编辑器最左边列出作者和日期，同一提交同色相、越新越亮
    /// （<see cref="BlameColors"/>），同一提交连续的几行只在第一行写字；点一行弹出提交卡片，可“追溯上一版”（打开父提交的版本再追溯，一路往回看）。
    /// VS 自带的追溯（右键 > Git > 追溯）不动，两个可以同时用。只画可见的行。
    /// </summary>
    internal sealed class BlameGutterMargin : FrameworkElement, IWpfTextViewMargin
    {
        public const string MarginName = "UEToVS.VcsBlame";
        private const int AuthorChars = 12;
        private static readonly HashSet<string> OpenWithGutter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly IWpfTextView _view;
        private readonly VcsDocument _doc;
        private bool _shown;
        private bool _disposed;
        private Dictionary<string, double> _ranks;
        private BlameResult _ranksFor;
        private readonly Dictionary<string, Brush> _brushes = new Dictionary<string, Brush>(StringComparer.Ordinal);

        public BlameGutterMargin(IWpfTextView view)
        {
            _view = view;
            _doc = VcsDocument.For(view.TextBuffer);
            _doc.Attach();
            _doc.Changed += OnChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += (s, e) => Dispose();
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            Width = 0;
            string path = _doc.FilePath;
            if (path != null && OpenWithGutter.Remove(path)) SetShown(true);
        }

        /// <summary>“追溯上一版”打开的旧版本：打开时就显示追溯栏。</summary>
        public static void ShowForNextOpen(string path)
        {
            if (!string.IsNullOrEmpty(path)) OpenWithGutter.Add(path);
        }

        public static BlameGutterMargin Of(IWpfTextView view) =>
            view != null && view.Properties.TryGetProperty(typeof(BlameGutterMargin), out BlameGutterMargin m) ? m : null;

        public bool Shown => _shown;

        public void SetShown(bool shown)
        {
            _shown = shown;
            if (shown && _doc.Ready) _doc.RequestBlame();
            UpdateWidth();
            InvalidateVisual();
            VcsService.L.Info("vcs.gutter", null, ("shown", shown), ("file", System.IO.Path.GetFileName(_doc.FilePath)), ("revision", _doc.Revision?.Revision));
        }

        private double CharWidth => (_view.FormattedLineSource?.DefaultTextProperties?.FontRenderingEmSize ?? 13.0) * 0.92 * 0.6;

        private void UpdateWidth()
        {
            double w = _shown && VcsService.Active && _doc.Ready ? Math.Ceiling(CharWidth * (AuthorChars + 12) + 12) : 0;
            if (Width != w) Width = w;
        }

        private void OnChanged(object sender, EventArgs e) => Safe.Execute(VcsService.L, "vcs.gutter.changed", () =>
        {
            if (_shown && _doc.Ready && _doc.Blame == null && !_doc.BlameRunning && _doc.BlameError == null) _doc.RequestBlame();
            UpdateWidth();
            InvalidateVisual();
        });

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_shown) InvalidateVisual();
        }

        private Brush BrushFor(string commitId, bool uncommitted)
        {
            if (_brushes.TryGetValue(commitId, out var b)) return b;
            bool dark = ThemeManager.Instance.IsDark;
            if (uncommitted) b = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
            else
            {
                double rank = _ranks != null && _ranks.TryGetValue(commitId, out var r) ? r : 0;
                var (a, red, g, bl) = BlameColors.Background(commitId, rank, dark);
                b = new SolidColorBrush(Color.FromArgb(a, red, g, bl));
            }
            b.Freeze();
            _brushes[commitId] = b;
            return b;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_disposed || !_shown || ActualWidth <= 0 || _view.IsClosed || _view.InLayout) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            var props = _view.FormattedLineSource?.DefaultTextProperties;
            var typeface = props?.Typeface ?? new Typeface("Consolas");
            double size = Math.Max(7.0, (props?.FontRenderingEmSize ?? 13.0) * 0.92);
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var fg = Frozen(ThemeManager.Instance.IsDark ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x40, 0x40, 0x40));
            var blame = _doc.Blame;
            bool untracked = _doc.Revision == null && _doc.Base != null && !_doc.Base.Tracked; // 没进版本库：整篇“未提交”
            if (blame == null && !untracked)
            {
                string msg = _doc.BlameError ?? VcsStrings.GutterLoading;
                dc.DrawText(new FormattedText(msg, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, fg, dip) { MaxTextWidth = Math.Max(10, ActualWidth - 8) },
                            new Point(4, 4));
                return;
            }
            if (blame != null && !ReferenceEquals(_ranksFor, blame))
            {
                _ranksFor = blame;
                _ranks = BlameColors.AgeRanks(blame.Commits.Values);
                _brushes.Clear();
            }
            var snapshot = _view.TextSnapshot;
            double top = _view.ViewportTop;
            string prevId = null;
            bool first = true;
            bool english = Loc.IsEnglish;
            foreach (var line in _view.TextViewLines)
            {
                if (!line.IsFirstTextViewLineForSnapshotLine) continue;
                int n = line.Start.GetContainingLine().LineNumber;
                var c = _doc.CommitAt(snapshot, n, out _);
                if (first && n > 0) prevId = _doc.CommitAt(snapshot, n - 1, out _)?.Id;
                first = false;
                if (c == null) { prevId = null; continue; }
                double y = line.Top - top;
                dc.DrawRectangle(BrushFor(c.Id, c.IsUncommitted), null, new Rect(0, y, ActualWidth - 2, line.Height));
                if (c.Id != prevId)
                {
                    var ft = new FormattedText(BlameText.Gutter(c, english, AuthorChars), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, fg, dip)
                    {
                        MaxTextWidth = Math.Max(10, ActualWidth - 8), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                    };
                    dc.DrawText(ft, new Point(4, y + Math.Max(0, (line.Height - ft.Height) / 2)));
                }
                prevId = c.Id;
            }
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Safe.Execute(VcsService.L, "vcs.gutter.click", () =>
            {
                if (!_shown || !_doc.Ready) return;
                var line = _view.TextViewLines.GetTextViewLineContainingYCoordinate(e.GetPosition(this).Y + _view.ViewportTop);
                if (line == null) return;
                int n = line.Start.GetContainingLine().LineNumber;
                var c = _doc.CommitAt(_view.TextSnapshot, n, out int baseLine);
                if (c == null) return;
                e.Handled = true;
                CommitCard.Show(_doc, c, baseLine, PopupAnchor.Mouse());
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
