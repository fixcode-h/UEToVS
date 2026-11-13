using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Search;
using UEToVS.UI.Molecules;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Search
{
    /// <summary>
    /// Code preview with a real, read-only VS editor view (<see cref="ICodePreviewHost"/>): one <see cref="IWpfTextView"/>
    /// per preview pane, created on first use and reused for every selection. Its buffer has the C/C++ content type, so
    /// VS's own C++ colouring applies where VS offers it for a plain buffer; the view role <see cref="Role"/> pulls in
    /// UEToVS's semantic colouring (<c>SemanticClassificationTagger</c>) and a lexical C++ colouring
    /// (<see cref="PreviewLexicalTaggerProvider"/>), all drawn with the editor's own Fonts and Colors. No margins.
    /// Files are read off the UI thread (size capped, cached in a small LRU), stale loads are cancelled, and every
    /// failure falls back to the built-in plain-text preview. Timings are logged as <c>ui.search.preview</c>.
    /// </summary>
    internal sealed class EditorCodePreview : ICodePreviewHost
    {
        /// <summary>Text view role of preview views (our classifiers attach to it; the Document-only features do not).</summary>
        public const string Role = "UEToVS.Preview";

        /// <summary>Largest file shown in the editor view; larger files use the built-in (virtualized) preview.</summary>
        public const long MaxBytes = 4 * 1024 * 1024;

        private static readonly Logger L = Log.For("Search");
        private static readonly object PathKey = typeof(EditorCodePreview).FullName + ".Path";
        private static readonly PreviewTextCache Cache = new PreviewTextCache();
        private static int _created;

        private readonly CodePreview _owner;
        private IWpfTextView _view;
        private ITextBuffer _buffer;
        private IAdornmentLayer _targetLayer;
        private CancellationTokenSource _cts;
        private int _generation;
        private PreviewText _shown;
        private int _targetLine = -1;
        private bool _centerPending;
        private SnapshotSpan? _targetSpan;

        private EditorCodePreview(CodePreview owner) => _owner = owner;

        /// <summary>Plugs an editor-backed preview into <paramref name="preview"/> (no-op outside VS / when the editor is unavailable).</summary>
        public static void Attach(CodePreview preview)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (preview == null || preview.Host != null) return;
            preview.Host = new EditorCodePreview(preview);
        }

        /// <summary>The file a preview buffer shows (for the semantic classifier's engine request), or null.</summary>
        public static string PathOf(ITextBuffer buffer) =>
            buffer != null && buffer.Properties.TryGetProperty(PathKey, out string p) ? p : null;

        /// <summary>The loaded text of a preview buffer (lexer states for <see cref="PreviewLexicalTagger"/>), or null.</summary>
        public static PreviewText TextOf(ITextBuffer buffer) =>
            buffer != null && buffer.Properties.TryGetProperty(typeof(PreviewText), out PreviewText t) ? t : null;

        public static bool IsPreviewBuffer(ITextBuffer buffer) => buffer != null && buffer.Properties.ContainsProperty(PathKey);

        public UIElement View
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return EnsureView().VisualElement;
            }
        }

        private IWpfTextView EnsureView()
        {
            if (_view != null) return _view;
            var sw = Stopwatch.StartNew();
            if (!(Package.GetGlobalService(typeof(SComponentModel)) is IComponentModel cm))
                throw new InvalidOperationException("SComponentModel unavailable");
            var editors = cm.GetService<ITextEditorFactoryService>();
            var buffers = cm.GetService<ITextBufferFactoryService>();
            var contentTypes = cm.GetService<IContentTypeRegistryService>();
            var contentType = contentTypes.GetContentType(EditorContentTypes.Cpp) ?? contentTypes.GetContentType("code") ?? buffers.TextContentType;
            _buffer = buffers.CreateTextBuffer(string.Empty, contentType);
            _buffer.Properties[PathKey] = string.Empty;
            var roles = editors.CreateTextViewRoleSet(Role, PredefinedTextViewRoles.Interactive);
            var view = editors.CreateTextView(_buffer, roles);
            var o = view.Options;
            o.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
            o.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
            o.SetOptionValue(DefaultTextViewOptions.DisplayUrlsAsHyperlinksId, false);
            o.SetOptionValue(DefaultWpfViewOptions.EnableHighlightCurrentLineId, false);
            o.SetOptionValue(DefaultWpfViewOptions.EnableMouseWheelZoomId, false);
            o.SetOptionValue(DefaultTextViewOptions.ShowBlockStructureId, false);
            o.SetOptionValue(DefaultTextViewOptions.UseVisibleWhitespaceId, false);
            view.Caret.IsHidden = true;
            // Never take keyboard focus: a focused VS text view turns keys into editor commands that VS routes to the last
            // code editor (typing in Search Everywhere ended up in the code, v0.4.2). Keys stay in the search box.
            view.VisualElement.Focusable = false;
            _targetLayer = view.GetAdornmentLayer(PreviewAdornments.TargetLineLayer);
            view.LayoutChanged += OnLayoutChanged;
            view.VisualElement.SizeChanged += (s, e) => Safe.Execute(L, "ui.search.preview.resize", CenterIfPending);
            // A bare IWpfTextView (no IWpfTextViewHost, input prohibited, not focusable) gets no wheel scrolling from the
            // editor's mouse processors, so the preview could not be scrolled at all (v0.5.3). Scroll it ourselves.
            view.VisualElement.PreviewMouseWheel += (s, e) => Safe.Execute(L, "ui.search.preview.wheel", () => e.Handled = OnWheel(e.Delta));
            _view = view;
            L.Info("ui.search.preview.view_created", null, ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)), ("contentType", contentType.TypeName),
                   ("count", Interlocked.Increment(ref _created)));
            return view;
        }

        public void Show(CodePreviewRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureView();
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            int gen = ++_generation;
            string path = request.FilePath;
            if (request.Text == null && string.IsNullOrEmpty(path))
            {
                _owner.ShowFallback(request);
                return;
            }
            var sw = Stopwatch.StartNew();
            var token = cts.Token;
            Safe.RunAsync(L, "ui.search.preview.load", async () =>
            {
                string source;
                PreviewText text;
                try
                {
                    (text, source) = await Task.Run(() => Load(request, token), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (gen != _generation) return;
                    L.Warn("ui.search.preview.read_failed", "Preview read failed; using the plain preview", ex, ("path", path));
                    _owner.ShowFallback(request);
                    return;
                }
                double readMs = sw.Elapsed.TotalMilliseconds;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (gen != _generation || token.IsCancellationRequested) return;
                if (text == null)
                {
                    L.Info("ui.search.preview", null, ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)), ("source", "tooLarge"), ("path", path));
                    _owner.ShowFallback(request);
                    return;
                }
                var apply = Stopwatch.StartNew();
                if (ReferenceEquals(text, _shown)) source = "shown"; // same file, unchanged: only the target moves
                try
                {
                    SetText(text);
                    MoveTarget(request);
                }
                catch (Exception ex)
                {
                    L.Error("ui.search.preview.apply_failed", "Editor preview failed; using the plain preview", ex, ("path", path), ("bytes", text.Bytes));
                    _shown = null;
                    _owner.ShowFallback(request);
                    return;
                }
                L.Info("ui.search.preview", null, ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)), ("readMs", Math.Round(readMs, 1)),
                       ("applyMs", Math.Round(apply.Elapsed.TotalMilliseconds, 1)), ("bytes", text.Bytes), ("lines", text.LineCount), ("source", source));
            }, ("path", path));
        }

        /// <summary>Background: the text to show (request text, cache or disk) or null when the file is too large.</summary>
        private static (PreviewText, string) Load(CodePreviewRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string path = request.FilePath ?? string.Empty;
            if (request.Text != null)
                return (request.Text.Length > MaxBytes ? null : new PreviewText(path, request.Text, DateTime.MinValue, request.Text.Length), "request");
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException("File not found", path);
            if (fi.Length > MaxBytes) return (null, "tooLarge");
            var cached = Cache.TryGet(path, fi.LastWriteTimeUtc);
            if (cached != null) return (cached, "cache");
            string text = File.ReadAllText(path);
            ct.ThrowIfCancellationRequested();
            var t = new PreviewText(path, text, fi.LastWriteTimeUtc, fi.Length);
            Cache.Add(t);
            return (t, "disk");
        }

        /// <summary>UI thread: swaps the reused buffer's text (one edit; the colouring follows the buffer change).</summary>
        private void SetText(PreviewText text)
        {
            if (ReferenceEquals(text, _shown)) return;
            _targetSpan = null;
            _targetLine = -1;
            _targetLayer?.RemoveAllAdornments();
            _buffer.Properties[PathKey] = text.Path ?? string.Empty;
            _buffer.Properties[typeof(PreviewText)] = text;
            using (var edit = _buffer.CreateEdit())
            {
                edit.Replace(new Span(0, _buffer.CurrentSnapshot.Length), text.Text);
                edit.Apply();
            }
            _shown = text;
            PreviewLexicalTagger.Refresh(_buffer);
        }

        private void MoveTarget(CodePreviewRequest request)
        {
            var snapshot = _buffer.CurrentSnapshot;
            int line = Math.Max(0, Math.Min(request.Line - 1, snapshot.LineCount - 1));
            var l = snapshot.GetLineFromLineNumber(line);
            int col = Math.Max(0, Math.Min(request.Column, l.Length));
            int len = Math.Max(0, Math.Min(request.Length, l.Length - col));
            _targetLine = line;
            _targetSpan = new SnapshotSpan(snapshot, l.Start.Position + col, len);
            if (len > 0) _view.Selection.Select(_targetSpan.Value, false);
            else _view.Selection.Clear();
            _view.Caret.MoveTo(new SnapshotPoint(snapshot, l.Start.Position + col));
            _centerPending = true;
            CenterIfPending();
        }

        /// <summary>Wheel: vertical by the system's wheel lines (Shift: horizontal); Ctrl does nothing (zoom is off).</summary>
        private bool OnWheel(int delta)
        {
            if (_view == null || _view.IsClosed || delta == 0) return false;
            var mods = System.Windows.Input.Keyboard.Modifiers;
            if ((mods & System.Windows.Input.ModifierKeys.Control) != 0) return true;
            _centerPending = false;
            double lineHeight = _view.LineHeight > 0 ? _view.LineHeight : 16;
            int lines = SystemParameters.WheelScrollLines;
            double notches = delta / 120.0;
            if ((mods & System.Windows.Input.ModifierKeys.Shift) != 0)
            {
                double charWidth = _view.FormattedLineSource?.ColumnWidth ?? 8;
                double left = Math.Max(0, _view.ViewportLeft - notches * Math.Max(1, lines) * 3 * charWidth);
                _view.ViewScroller.ScrollViewportHorizontallyByPixels(left - _view.ViewportLeft);
                return true;
            }
            // WheelScrollLines -1 = "one screen at a time"; positive pixels move the viewport up (towards line 1).
            double pixels = lines < 0 ? notches * Math.Max(lineHeight, _view.ViewportHeight - lineHeight) : notches * Math.Max(1, lines) * lineHeight;
            _view.ViewScroller.ScrollViewportVerticallyByPixels(pixels);
            return true;
        }

        private void CenterIfPending()
        {
            if (!_centerPending || _view == null || _view.IsClosed || _targetSpan == null) return;
            if (_view.InLayout || _view.ViewportHeight <= 1 || !_view.VisualElement.IsLoaded) return; // retried on SizeChanged / LayoutChanged
            _centerPending = false;
            var point = _targetSpan.Value.Start;
            if (point.Snapshot != _view.TextSnapshot) return;
            double lineHeight = _view.LineHeight > 0 ? _view.LineHeight : 16;
            _view.DisplayTextLineContainingBufferPosition(point, Math.Max(0, (_view.ViewportHeight - lineHeight) / 2), ViewRelativePosition.Top);
            _view.ViewScroller.ScrollViewportHorizontallyByPixels(-_view.ViewportLeft);
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Safe.Execute(L, "ui.search.preview.layout", () =>
        {
            if (_centerPending)
            {
                // Called inside layout: centre on the next dispatcher turn.
                Safe.RunAsync(L, "ui.search.preview.center", async () =>
                {
                    await Task.Yield();
                    CenterIfPending();
                });
            }
            DrawTarget();
        });

        /// <summary>Background band behind the target line (theme token, like the plain preview).</summary>
        private void DrawTarget()
        {
            if (_targetLayer == null) return;
            _targetLayer.RemoveAllAdornments();
            if (_targetLine < 0 || _view.TextViewLines == null) return;
            var snapshot = _view.TextSnapshot;
            if (_targetLine >= snapshot.LineCount) return;
            var line = snapshot.GetLineFromLineNumber(_targetLine);
            var vl = _view.TextViewLines.GetTextViewLineContainingBufferPosition(line.Start);
            if (vl == null || vl.VisibilityState == VisibilityState.Unattached) return;
            var brush = _owner.TryFindResource(Tokens.CodeLineHighlight) as Brush;
            if (brush == null) return;
            var rect = new Rectangle { Width = Math.Max(_view.ViewportRight, vl.Right), Height = vl.Height, Fill = brush, IsHitTestVisible = false };
            Canvas.SetLeft(rect, 0);
            Canvas.SetTop(rect, vl.Top);
            _targetLayer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, rect, null);
        }

        public void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _cts?.Cancel();
            _generation++;
        }
    }

    /// <summary>Adornment layer for the preview's target line (drawn below the selection and the text).</summary>
    internal static class PreviewAdornments
    {
        public const string TargetLineLayer = "UEToVS.PreviewTargetLine";

#pragma warning disable 169, 649 // MEF export
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(TargetLineLayer)]
        [Order(Before = PredefinedAdornmentLayers.Selection)]
        [TextViewRole(EditorCodePreview.Role)]
        internal static AdornmentLayerDefinition TargetLine;
#pragma warning restore 169, 649
    }

    /// <summary>Lexical C++ colouring for preview views only (comments, strings, numbers, keywords, directives).</summary>
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("text")]
    [TagType(typeof(IClassificationTag))]
    [TextViewRole(EditorCodePreview.Role)]
    internal sealed class PreviewLexicalTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal IClassificationTypeRegistryService Registry { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (buffer != textView.TextBuffer || !EditorCodePreview.IsPreviewBuffer(buffer)) return null;
            return buffer.Properties.GetOrCreateSingletonProperty(typeof(PreviewLexicalTagger), () => new PreviewLexicalTagger(buffer, Registry)) as ITagger<T>;
        }
    }

    internal sealed class PreviewLexicalTagger : ITagger<IClassificationTag>
    {
        private static readonly Logger L = Log.For("Search");
        private readonly ITextBuffer _buffer;
        private readonly IClassificationTag[] _tags;
        private readonly List<CppToken> _scratch = new List<CppToken>();

        public PreviewLexicalTagger(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            _buffer = buffer;
            IClassificationTag Tag(string name)
            {
                var t = registry.GetClassificationType(name);
                return t == null ? null : new ClassificationTag(t);
            }
            _tags = new IClassificationTag[5];
            _tags[(int)CppTokenKind.Comment] = Tag("comment");
            _tags[(int)CppTokenKind.String] = Tag("string");
            _tags[(int)CppTokenKind.Number] = Tag("number");
            _tags[(int)CppTokenKind.Keyword] = Tag("keyword");
            _tags[(int)CppTokenKind.Preprocessor] = Tag("preprocessor keyword");
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        /// <summary>The buffer got new text: repaint everything.</summary>
        public static void Refresh(ITextBuffer buffer)
        {
            if (buffer.Properties.TryGetProperty(typeof(PreviewLexicalTagger), out PreviewLexicalTagger t))
            {
                var s = buffer.CurrentSnapshot;
                t.TagsChanged?.Invoke(t, new SnapshotSpanEventArgs(new SnapshotSpan(s, 0, s.Length)));
            }
        }

        public IEnumerable<ITagSpan<IClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) return Array.Empty<ITagSpan<IClassificationTag>>();
            var text = EditorCodePreview.TextOf(_buffer);
            var snapshot = spans[0].Snapshot;
            // Only the snapshot that holds exactly the loaded text has matching lexer line states.
            if (text == null || snapshot.Length != text.Text.Length || snapshot.LineCount != text.LineCount) return Array.Empty<ITagSpan<IClassificationTag>>();
            var result = new List<ITagSpan<IClassificationTag>>();
            try
            {
                foreach (var span in spans)
                {
                    int first = span.Start.GetContainingLine().LineNumber;
                    int last = span.End.GetContainingLine().LineNumber;
                    for (int n = first; n <= last; n++)
                    {
                        var line = snapshot.GetLineFromLineNumber(n);
                        _scratch.Clear();
                        CppLexer.LexLine(line.GetText(), text.StateAt(n), _scratch);
                        foreach (var tok in _scratch)
                        {
                            var tag = _tags[(int)tok.Kind];
                            if (tag == null) continue;
                            var ts = new SnapshotSpan(snapshot, line.Start.Position + tok.Start, tok.Length);
                            if (ts.IntersectsWith(span)) result.Add(new TagSpan<IClassificationTag>(ts, tag));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                L.Warn("ui.search.preview.lex_failed", "Preview lexical colouring failed", ex);
            }
            return result;
        }
    }
}
