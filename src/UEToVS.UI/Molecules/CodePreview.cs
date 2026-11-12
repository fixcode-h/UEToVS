using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using UEToVS.UI.Atoms;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;
using TextRange = UEToVS.UI.Logic.TextRange;

namespace UEToVS.UI.Molecules
{
    /// <summary>What to preview: a file location, optionally with the text already in hand.</summary>
    public sealed class CodePreviewRequest
    {
        public string FilePath { get; set; }

        /// <summary>1-based line to focus.</summary>
        public int Line { get; set; } = 1;

        /// <summary>0-based column and length of the span to highlight on <see cref="Line"/> (Length 0 = none).</summary>
        public int Column { get; set; }
        public int Length { get; set; }

        /// <summary>Full text if the caller has it (unsaved buffer); otherwise the fallback reads the file.</summary>
        public string Text { get; set; }

        /// <summary>Header text; default = file name + line.</summary>
        public string Title { get; set; }
    }

    /// <summary>
    /// Host hook for a real editor view: the host plugs one shared read-only <c>IWpfTextView</c> (syntax colors,
    /// editor font) and swaps buffer/visible span per request (§8.5). The UI library never touches VS editor APIs.
    /// </summary>
    public interface ICodePreviewHost
    {
        /// <summary>The element to show (e.g. <c>IWpfTextViewHost.HostControl</c>).</summary>
        UIElement View { get; }

        void Show(CodePreviewRequest request);

        void Clear();
    }

    /// <summary>
    /// Preview region: header (file · line) + either the host's editor view or a built-in fallback
    /// (virtualized monospace lines with line numbers and the target line/span highlighted).
    /// </summary>
    public class CodePreview : Border
    {
        private const long MaxFallbackBytes = 8 * 1024 * 1024;
        private readonly UiText _header = new UiText((string)null, TextVariant.Small) { Margin = new Thickness(10, 4, 10, 4) };
        private readonly ContentControl _body = new ContentControl { Focusable = false };
        private readonly CodeLineList _fallback = new CodeLineList();
        private readonly UiText _message = new UiText((string)null, TextVariant.Secondary) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private ICodePreviewHost _host;
        private int _generation;
        private CodePreviewRequest _last;

        public CodePreview()
        {
            this.Bind(BackgroundProperty, Tokens.SurfaceSunken);
            var dock = new DockPanel { LastChildFill = true };
            var headerBar = new Border { Child = _header, BorderThickness = new Thickness(0, 0, 0, 1) };
            headerBar.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            DockPanel.SetDock(headerBar, Dock.Top);
            dock.Children.Add(headerBar);
            dock.Children.Add(_body);
            Child = dock;
            AutomationProperties.SetName(this, Strings.DialogPreview);
            Strings.Track(this, () =>
            {
                AutomationProperties.SetName(this, Strings.DialogPreview);
                if (_last == null && _body.Content == _message) _message.Text = Strings.PreviewNone;
            });
            ShowMessage(Strings.PreviewNone);
        }

        /// <summary>Plug a real editor view; null returns to the fallback.</summary>
        public ICodePreviewHost Host
        {
            get => _host;
            set
            {
                _host = value;
                if (_last != null) Show(_last);
            }
        }

        public void Clear()
        {
            _generation++;
            _last = null;
            try { _host?.Clear(); }
            catch (Exception ex) { Ui.Logger.Error("ui.preview.host_clear_failed", "Preview host Clear threw", ex); }
            _header.Text = string.Empty;
            ShowMessage(Strings.PreviewNone);
        }

        public void ShowMessage(string text)
        {
            _message.Text = text ?? string.Empty;
            _body.Content = _message;
        }

        public void Show(CodePreviewRequest request)
        {
            if (request == null) { Clear(); return; }
            _last = request;
            int gen = ++_generation;
            _header.Text = request.Title ?? FormatTitle(request);
            if (_host != null)
            {
                try
                {
                    _body.Content = _host.View;
                    _host.Show(request);
                    return;
                }
                catch (Exception ex)
                {
                    Ui.Logger.Error("ui.preview.host_failed", "Preview host threw; using fallback", ex);
                }
            }
            if (request.Text != null)
            {
                ShowLines(SplitLines(request.Text), request);
                return;
            }
            if (string.IsNullOrEmpty(request.FilePath))
            {
                ShowMessage(Strings.PreviewNone);
                return;
            }
            ShowMessage(Strings.PreviewLoading);
            var path = request.FilePath;
            Task.Run(() =>
            {
                var fi = new FileInfo(path);
                if (fi.Length > MaxFallbackBytes) throw new IOException("file too large for preview");
                return SplitLines(File.ReadAllText(path));
            }).ContinueWith(t =>
            {
                Ui.Safe("ui.preview.loaded", () =>
                {
                    if (gen != _generation) return;
                    if (t.IsFaulted)
                    {
                        var ex = t.Exception?.GetBaseException();
                        Ui.Logger.Warn("ui.preview.read_failed", "Preview read failed", ex, ("path", path));
                        ShowMessage(Strings.PreviewFailed(ex?.Message));
                        return;
                    }
                    ShowLines(t.Result, request);
                });
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowLines(IReadOnlyList<string> lines, CodePreviewRequest r)
        {
            _body.Content = _fallback;
            _fallback.SetLines(lines, r.Line - 1, r.Column, r.Length);
        }

        private static string FormatTitle(CodePreviewRequest r)
        {
            string name = string.IsNullOrEmpty(r.FilePath) ? string.Empty : Path.GetFileName(r.FilePath);
            return r.Line > 0 ? name + ":" + r.Line.ToString(CultureInfo.InvariantCulture) : name;
        }

        private static IReadOnlyList<string> SplitLines(string text)
        {
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].IndexOf('\t') >= 0) lines[i] = lines[i].Replace("\t", "    ");
            return lines;
        }

        /// <summary>Virtualized read-only line list (fallback preview).</summary>
        private sealed class CodeLineList : VirtualListCore
        {
            private IReadOnlyList<string> _lines = Array.Empty<string>();
            private int _focus = -1;
            private TextRange[] _focusRange;

            public CodeLineList()
            {
                FocusOnClick = false;
                Focusable = false;
                WrapAround = false;
            }

            public void SetLines(IReadOnlyList<string> lines, int focusLine, int column, int length)
            {
                _lines = lines ?? Array.Empty<string>();
                _focus = Math.Max(0, Math.Min(focusLine, _lines.Count - 1));
                _focusRange = length > 0 ? new[] { new TextRange(column, length) } : null;
                ResetCount(_lines.Count, false);
                // Center the focus line once the viewport is known.
                Dispatcher.BeginInvoke(new Action(() => Ui.Safe("ui.preview.center", () =>
                {
                    Navigator.ScrollTo(_focus - Navigator.ViewportRows / 2 + 0.5);
                    InvalidateMeasure();
                })), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            protected override FrameworkElement CreateRow() => new CodeLineRow();

            protected override void BindRow(FrameworkElement row, int index) =>
                ((CodeLineRow)row).Bind(index + 1, _lines[index], index == _focus, index == _focus ? _focusRange : null);

            protected override bool IsSelectableCore(int index) => false;

            protected override void OnInvoke(int index, InvokeKind kind) { }
        }

        private sealed class CodeLineRow : Panel, IVirtualRow
        {
            private readonly TextBlock _number = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            private readonly HighlightedText _text = new HighlightedText { TextTrimming = TextTrimming.None };
            private bool _focus;

            public CodeLineRow()
            {
                _number.Bind(TextBlock.ForegroundProperty, Tokens.CodeLineNumber);
                _number.Bind(TextBlock.FontFamilyProperty, Tokens.FontFamilyCode);
                _number.Bind(TextBlock.FontSizeProperty, Tokens.FontSizeCode);
                _text.Bind(TextBlock.FontFamilyProperty, Tokens.FontFamilyCode);
                _text.Bind(TextBlock.FontSizeProperty, Tokens.FontSizeCode);
                Children.Add(_number);
                Children.Add(_text);
                ClipToBounds = true;
            }

            public int BoundIndex { get; set; } = -1;

            public void SetState(bool selected, bool hover, bool active) { }

            public void Bind(int number, string text, bool focus, IReadOnlyList<TextRange> ranges)
            {
                _number.Text = number.ToString(CultureInfo.InvariantCulture);
                _text.Set(text, ranges);
                if (_focus != focus)
                {
                    _focus = focus;
                    if (focus) SetResourceReference(BackgroundProperty, Tokens.CodeLineHighlight);
                    else Background = null;
                }
            }

            protected override Size MeasureOverride(Size a)
            {
                _number.Measure(new Size(48, a.Height));
                _text.Measure(new Size(double.PositiveInfinity, a.Height));
                return new Size(double.IsInfinity(a.Width) ? 400 : a.Width, a.Height);
            }

            protected override Size ArrangeOverride(Size f)
            {
                _number.Arrange(new Rect(0, 0, 44, f.Height));
                _text.Arrange(new Rect(56, 0, Math.Max(0, Math.Max(f.Width - 56, _text.DesiredSize.Width)), f.Height));
                return f;
            }
        }
    }
}
