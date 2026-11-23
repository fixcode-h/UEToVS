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
    [MarginContainer(PredefinedMarginNames.LeftSelection)]
    [Order(Before = PredefinedMarginNames.LineNumber)]
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
    /// G4 注解栏（v0.10.2 照用户给的 Rider Annotate 截图改）：紧贴行号左边，每行写“日期 作者”，越新底色越蓝（<see cref="BlameColors"/>）。
    /// 设置“行号旁显示注解”（vcs.annotate，默认开）决定打开文件时显不显示；引擎文件跟“引擎文件也自动追溯”。Ctrl+Shift+Alt+U, O
    /// 只切当前编辑器。点一行弹出提交卡片，可“追溯上一版”（打开父提交的版本再注解，一路往回看）。
    /// VS 自带的追溯（右键 > Git > 追溯）不动，两个可以同时用。只画可见的行。
    /// </summary>
    internal sealed class BlameGutterMargin : FrameworkElement, IWpfTextViewMargin
    {
        public const string MarginName = "UEToVS.VcsBlame";
        private const int AuthorChars = 12;
        private static readonly HashSet<string> OpenWithGutter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly IWpfTextView _view;
        private readonly VcsDocument _doc;
        /// <summary>Ctrl+Shift+Alt+U, O 或“追溯上一版”在这个编辑器里定下的显示 / 隐藏；null 时跟设置。</summary>
        private bool? _forced;
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
            if (path != null && OpenWithGutter.Remove(path)) _forced = true;
            Refresh();
        }

        /// <summary>“追溯上一版”打开的旧版本：打开时就显示追溯栏。</summary>
        public static void ShowForNextOpen(string path)
        {
            if (!string.IsNullOrEmpty(path)) OpenWithGutter.Add(path);
        }

        public static BlameGutterMargin Of(IWpfTextView view) =>
            view != null && view.Properties.TryGetProperty(typeof(BlameGutterMargin), out BlameGutterMargin m) ? m : null;

        /// <summary>现在是否显示（文档还没准备好时按将要显示算）。</summary>
        public bool Shown => _forced ?? BySettings;

        /// <summary>设置决定的显示：开着“行号旁显示注解”；引擎文件还要开“引擎文件也自动追溯”（旧版本临时文件不算引擎文件）。</summary>
        private bool BySettings
        {
            get
            {
                var s = VcsService.Settings;
                if (s == null || !s.VcsAnnotate) return false;
                return _doc.Revision != null || s.VcsInlineBlameEngine || !_doc.Ready || !_doc.IsEngineFile;
            }
        }

        public void SetShown(bool shown)
        {
            _forced = shown;
            Refresh();
            VcsService.L.Info("vcs.gutter", null, ("shown", shown), ("file", System.IO.Path.GetFileName(_doc.FilePath)), ("revision", _doc.Revision?.Revision));
        }

        private void Refresh()
        {
            _shown = Shown && VcsService.Active && _doc.Ready;
            if (_shown && _doc.Blame == null && !_doc.BlameRunning && _doc.BlameError == null) _doc.RequestBlame();
            UpdateWidth();
            InvalidateVisual();
        }

        private bool _shown;

        private double CharWidth => (_view.FormattedLineSource?.DefaultTextProperties?.FontRenderingEmSize ?? 13.0) * 0.92 * 0.6;

        private void UpdateWidth()
        {
            double w = _shown ? Math.Ceiling(CharWidth * (BlameText.DateWidth + 1 + AuthorChars) + 14) : 0;
            if (Width != w) Width = w;
        }

        // 设置改了（VcsService 会让文档重新载入）也走这里
        private void OnChanged(object sender, EventArgs e) => Safe.Execute(VcsService.L, "vcs.gutter.changed", Refresh);

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_shown) InvalidateVisual();
        }

        private Brush BrushFor(string commitId, bool uncommitted)
        {
            if (_brushes.TryGetValue(commitId, out var b)) return b;
            bool dark = ThemeManager.Instance.IsDark;
            // 未提交：中性灰，和蓝色的提交区分开
            if (uncommitted) b = new SolidColorBrush(dark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xE4, 0xE4, 0xE4));
            else
            {
                double rank = _ranks != null && _ranks.TryGetValue(commitId, out var r) ? r : 0;
                var (a, red, g, bl) = BlameColors.Background(rank, dark);
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
            bool english = Loc.IsEnglish;
            var texts = new Dictionary<string, FormattedText>(StringComparer.Ordinal); // 同一提交的字只排一次版
            foreach (var line in _view.TextViewLines)
            {
                // 自动换行的续行只铺底色，字写在第一段
                int n = line.Start.GetContainingLine().LineNumber;
                var c = _doc.CommitAt(snapshot, n, out _);
                if (c == null) continue;
                double y = line.Top - top;
                dc.DrawRectangle(BrushFor(c.Id, c.IsUncommitted), null, new Rect(0, y, ActualWidth - 2, line.Height));
                if (!line.IsFirstTextViewLineForSnapshotLine) continue;
                if (!texts.TryGetValue(c.Id, out var ft))
                {
                    ft = new FormattedText(BlameText.Gutter(c, english, AuthorChars), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, fg, dip)
                    {
                        MaxTextWidth = Math.Max(10, ActualWidth - 8), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                    };
                    texts[c.Id] = ft;
                }
                dc.DrawText(ft, new Point(4, y + Math.Max(0, (line.Height - ft.Height) / 2)));
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
                VcsCommitWindow.Show(_doc, c, baseLine); // v0.10.2：打开提交面板，列出这次提交改的所有文件
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
