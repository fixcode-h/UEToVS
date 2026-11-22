using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Vcs;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Vcs
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class InlineBlameListener : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(InlineBlameController.LayerName)]
        [Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Caret)]
        public AdornmentLayerDefinition Layer = null;

        public void TextViewCreated(IWpfTextView textView)
        {
            if (EditorServices.Document(textView.TextBuffer) == null) return; // 预览、投影缓冲区
            textView.Properties.GetOrCreateSingletonProperty(typeof(InlineBlameController), () => new InlineBlameController(textView));
        }
    }

    /// <summary>
    /// G1 行尾追溯（IDEA 2023 的 inline blame / GitLens）：光标停 300 ms 后，在所在行行尾画灰字“作者 · 多久前 · 提交说明”，
    /// 没提交的行是“未提交”；设置为“所有行”时屏幕上每段同一提交的第一行都画。画在独立的装饰层上（不像内嵌提示那样插进文本、不挤动正文），
    /// 字体和颜色跟内嵌提示 / Code Vision 一致。悬停 400 ms 或点击弹出 <see cref="CommitCard"/>。
    /// 追溯在后台算、按文件缓存；小文件打开时就预取，超过 5000 行的文件等光标第一次停下才算；引擎文件默认不算。
    /// UI 线程上每次只排版可见的几行，没有进程调用。
    /// </summary>
    internal sealed class InlineBlameController
    {
        public const string LayerName = "UEToVS.InlineBlame";
        private static readonly TimeSpan RestDelay = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(400);
        private static readonly Brush Foreground = Frozen(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));
        private static bool _hidden;

        private readonly IWpfTextView _view;
        private readonly VcsDocument _doc;
        private readonly IAdornmentLayer _layer;
        private readonly DispatcherTimer _rest;
        private readonly DispatcherTimer _hover;
        private bool _rested;
        private int _caretLine = -1;
        private FrameworkElement _hovered;

        public InlineBlameController(IWpfTextView view)
        {
            _view = view;
            _doc = VcsDocument.For(view.TextBuffer);
            _doc.Attach();
            _layer = view.GetAdornmentLayer(LayerName);
            _rest = new DispatcherTimer(DispatcherPriority.Background) { Interval = RestDelay };
            _rest.Tick += (s, e) =>
            {
                _rest.Stop();
                Safe.Execute(VcsService.L, "vcs.inline.rest", OnRest);
            };
            _hover = new DispatcherTimer(DispatcherPriority.Input) { Interval = HoverDelay };
            _hover.Tick += (s, e) =>
            {
                _hover.Stop();
                Safe.Execute(VcsService.L, "vcs.inline.hover", () => OpenCard(_hovered));
            };
            _view.Caret.PositionChanged += OnCaretMoved;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;
            _doc.Changed += OnDocChanged;
            HiddenChanged += OnHiddenChanged;
        }

        /// <summary>本次 VS 会话里隐藏（Ctrl+Shift+Alt+U, J），设置不变。</summary>
        public static bool Hidden
        {
            get => _hidden;
            set
            {
                if (_hidden == value) return;
                _hidden = value;
                HiddenChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        private static event EventHandler HiddenChanged;

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private bool Enabled
        {
            get
            {
                if (_hidden || !VcsService.Active || !_doc.Ready) return false;
                var s = VcsService.Settings;
                if (!s.VcsInlineBlame) return false;
                if (_doc.Revision == null && !s.VcsInlineBlameEngine && _doc.IsEngineFile) return false;
                return true;
            }
        }

        private bool AllLines => VcsService.Settings.VcsInlineBlameScope == InlineBlameScope.AllLines;

        private void OnClosed(object sender, EventArgs e)
        {
            _rest.Stop();
            _hover.Stop();
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
            _doc.Changed -= OnDocChanged;
            HiddenChanged -= OnHiddenChanged;
            _doc.Detach();
        }

        private void OnHiddenChanged(object sender, EventArgs e) => Safe.Execute(VcsService.L, "vcs.inline.hidden", Render);

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
        {
            int line = e.NewPosition.BufferPosition.GetContainingLine().LineNumber;
            if (line == _caretLine) return;
            _caretLine = line;
            _rested = false;
            if (!AllLines) _layer.RemoveAllAdornments();
            _rest.Stop();
            _rest.Start();
        }

        private void OnRest()
        {
            _rested = true;
            if (!Enabled) return;
            if (_doc.Blame == null && !_doc.BlameRunning && _doc.BlameError == null) _doc.RequestBlame(); // 结果经 Changed 回来
            Render();
        }

        private void OnDocChanged(object sender, EventArgs e) => Safe.Execute(VcsService.L, "vcs.inline.docChanged", () =>
        {
            // 小文件：打开时就在后台预取，第一次停下就能显示；大文件等光标第一次停下（OnRest）
            if (Enabled && _doc.Blame == null && !_doc.BlameRunning && _doc.BlameError == null && !_doc.IsLarge) _doc.RequestBlame();
            Render();
        });

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (!Enabled) return;
            if (AllLines || e.NewOrReformattedLines.Count > 0 || e.VerticalTranslation) Safe.Execute(VcsService.L, "vcs.inline.layout", Render);
        }

        private void Render()
        {
            if (_view.IsClosed || _view.InLayout) return;
            _layer.RemoveAllAdornments();
            if (!Enabled) return;
            var snapshot = _view.TextSnapshot;
            if (AllLines)
            {
                if (_doc.Blame == null && _doc.IsLarge && !_rested && _caretLine < 0) return;
                VcsCommit previous = null;
                bool firstVisible = true;
                foreach (var line in _view.TextViewLines)
                {
                    if (!line.IsFirstTextViewLineForSnapshotLine) continue;
                    int n = line.Start.GetContainingLine().LineNumber;
                    var c = _doc.CommitAt(snapshot, n, out int baseLine);
                    if (firstVisible && n > 0) previous = _doc.CommitAt(snapshot, n - 1, out _);
                    firstVisible = false;
                    bool same = c != null && previous != null && c.Id == previous.Id;
                    previous = c;
                    if (c == null || same || line.Length == 0 && !c.IsUncommitted && n != _caretLine) continue;
                    Add(line, c, baseLine);
                }
                return;
            }
            if (!_rested || _caretLine < 0 || _caretLine >= snapshot.LineCount) return;
            var caretLine = snapshot.GetLineFromLineNumber(_caretLine);
            var view = _view.TextViewLines.GetTextViewLineContainingBufferPosition(caretLine.End);
            if (view == null || view.VisibilityState == VisibilityState.Unattached) return;
            var commit = _doc.CommitAt(snapshot, _caretLine, out int bl);
            if (commit == null)
            {
                if (_doc.BlameRunning) AddText(view, Logic.Localization.VcsStrings.Blaming, null, -1);
                return;
            }
            Add(view, commit, bl);
        }

        private void Add(ITextViewLine line, VcsCommit c, int baseLine) =>
            AddText(line, BlameText.Inline(c, DateTimeOffset.Now, Loc.IsEnglish), c, baseLine);

        private void AddText(ITextViewLine line, string text, VcsCommit commit, int baseLine)
        {
            if (string.IsNullOrEmpty(text)) return;
            var props = _view.FormattedLineSource?.DefaultTextProperties;
            double size = Math.Max(7.0, (props?.FontRenderingEmSize ?? 13.0) * 0.9);
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = props?.Typeface?.FontFamily ?? new FontFamily("Consolas"),
                FontSize = size,
                FontStyle = FontStyles.Italic,
                Foreground = Foreground,
                Tag = commit == null ? null : new Hit { Commit = commit, BaseLine = baseLine },
                Cursor = commit == null ? null : System.Windows.Input.Cursors.Hand,
                IsHitTestVisible = commit != null,
            };
            double gap = (props?.FontRenderingEmSize ?? 13.0) * 3;
            Canvas.SetLeft(tb, line.TextRight + gap);
            Canvas.SetTop(tb, line.TextTop + Math.Max(0, (line.TextHeight - size * 1.3) / 2));
            if (commit != null)
            {
                tb.MouseEnter += (s, e) => { _hovered = tb; _hover.Stop(); _hover.Start(); };
                tb.MouseLeave += (s, e) => { if (ReferenceEquals(_hovered, tb)) { _hovered = null; _hover.Stop(); } };
                tb.MouseLeftButtonUp += (s, e) => Safe.Execute(VcsService.L, "vcs.inline.click", () => { e.Handled = true; _hover.Stop(); OpenCard(tb); });
            }
            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, line.Extent, null, tb, null);
        }

        private sealed class Hit
        {
            public VcsCommit Commit;
            public int BaseLine;
        }

        private void OpenCard(FrameworkElement element)
        {
            if (element == null || !(element.Tag is Hit hit) || !element.IsVisible) return;
            CommitCard.Show(_doc, hit.Commit, hit.BaseLine, PopupAnchor.Element(element));
        }

        /// <summary>给命令用：当前视图的行尾追溯（没创建过时 null）。</summary>
        public static InlineBlameController Of(IWpfTextView view) =>
            view != null && view.Properties.TryGetProperty(typeof(InlineBlameController), out InlineBlameController c) ? c : null;
    }
}
