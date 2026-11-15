using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Editor
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType(EditorContentTypes.Cpp)]
    [TagType(typeof(IntraTextAdornmentTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class InlayHintsTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (!(textView is IWpfTextView view) || buffer != textView.TextBuffer) return null;
            if (EditorServices.Document(buffer) == null) return null; // previews / projections
            return view.Properties.GetOrCreateSingletonProperty(typeof(InlayHintsTagger), () => new InlayHintsTagger(view)) as ITagger<T>;
        }
    }

    /// <summary>
    /// Inlay hints (v0.4.5, Rider style): "Name:" before literal arguments and ": Type" after auto variables, drawn as
    /// IntraTextAdornment tags. Only the visible lines (± <see cref="InlayHintStore.MarginLines"/>) are requested, after a
    /// scroll (debounced 150 ms) or an edit pause (600 ms); answers are cached per text version (<see cref="InlayHintStore"/>),
    /// so scrolling back costs nothing; until a new version is answered the old hints are translated through the edits.
    /// Settings: 补全与提示 > 内嵌提示; Ctrl+Shift+Alt+U, 2 hides / shows them for the session (<see cref="Hidden"/>). Log: editor.inlayHints.
    /// </summary>
    internal sealed class InlayHintsTagger : ITagger<IntraTextAdornmentTag>
    {
        private static readonly Logger L = Log.For("Editing");
        private static readonly TimeSpan ScrollDelay = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan EditDelay = TimeSpan.FromMilliseconds(600);
        private static bool _hidden;

        private readonly IWpfTextView _view;
        private readonly ITextDocument _document;
        private readonly DispatcherTimer _timer;
        private readonly LatestRequestGate _gate = new LatestRequestGate();
        private readonly InlayHintStore _store = new InlayHintStore();
        private readonly Dictionary<InlayHint, UIElement> _elements = new Dictionary<InlayHint, UIElement>();
        private ITextSnapshot _snapshot;
        private bool _wasEnabled;

        public InlayHintsTagger(IWpfTextView view)
        {
            _view = view;
            _document = EditorServices.Document(view.TextBuffer);
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ScrollDelay };
            _timer.Tick += OnTimer;
            _view.LayoutChanged += OnLayoutChanged;
            _view.TextBuffer.Changed += OnBufferChanged;
            _view.Closed += OnClosed;
            NavigationContext.SettingsChanged += OnSettingsChanged;
            HiddenChanged += OnHiddenChanged;
            IntelliSenseContext.EngineReady += OnEngineReady;
            _wasEnabled = Enabled;
            Schedule(ScrollDelay);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        /// <summary>Session-wide hide (Ctrl+Shift+Alt+U, 2); the settings stay as they are.</summary>
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

        private static bool ParameterNames => IntelliSenseContext.Settings?.InlayParameterNames == true;
        private static bool AutoTypes => IntelliSenseContext.Settings?.InlayAutoTypes == true;
        private static bool CodeVision => IntelliSenseContext.Settings?.InlayCodeVision == true;

        /// <summary>Some inlay hint kind is on in the settings (whether hidden or not).</summary>
        public static bool AnyEnabledInSettings => ParameterNames || AutoTypes || CodeVision;

        private static bool Enabled => IntelliSenseContext.IsUnrealSolution && AnyEnabledInSettings && !_hidden;

        // ---------------------------------------------------------------- tags

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var source = _snapshot;
            if (source == null || spans.Count == 0 || _store.Count == 0 || !Enabled) yield break;
            var target = spans[0].Snapshot;
            if (target.TextBuffer != source.TextBuffer) yield break;
            // lines of the requested spans, mapped back to the snapshot the hints belong to
            var first = spans[0].Start.TranslateTo(source, PointTrackingMode.Negative).GetContainingLine().LineNumber;
            var last = spans[spans.Count - 1].End.TranslateTo(source, PointTrackingMode.Positive).GetContainingLine().LineNumber;
            foreach (var h in _store.Between(first, last))
            {
                if (h.Kind == InlayHintKind.Usages) continue; // Code Vision: own row above the line (CodeVisionLayout)
                if (h.Line < 0 || h.Line >= source.LineCount) continue;
                var line = source.GetLineFromLineNumber(h.Line);
                if (h.Column < 0 || h.Column > line.Length) continue; // stale: skip, never throw
                var point = new SnapshotPoint(source, line.Start.Position + h.Column);
                bool after = h.Kind != InlayHintKind.ParameterName; // types and Code Vision hang off the text before them
                var affinity = after ? PositionAffinity.Predecessor : PositionAffinity.Successor;
                if (target != source) point = point.TranslateTo(target, after ? PointTrackingMode.Negative : PointTrackingMode.Positive);
                var span = new SnapshotSpan(point, 0);
                if (!Intersects(spans, point)) continue;
                yield return new TagSpan<IntraTextAdornmentTag>(span, new IntraTextAdornmentTag(Element(h), null, affinity));
            }
        }

        private static bool Intersects(NormalizedSnapshotSpanCollection spans, SnapshotPoint p)
        {
            foreach (var s in spans)
                if (p.Position >= s.Start.Position && p.Position <= s.End.Position) return true;
            return false;
        }

        private UIElement Element(InlayHint h)
        {
            if (_elements.TryGetValue(h, out var e)) return e;
            var props = _view.FormattedLineSource?.DefaultTextProperties;
            double size = Math.Max(7.0, (props?.FontRenderingEmSize ?? 13.0) * 0.86);
            var text = new TextBlock
            {
                Text = InlayHintStore.Display(h),
                FontFamily = props?.Typeface?.FontFamily ?? new FontFamily("Consolas"),
                FontSize = size,
                Foreground = HintForeground,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var border = new Border
            {
                Child = text,
                Background = HintBackground,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = h.Kind == InlayHintKind.Usages ? new Thickness(12, 1, 2, 1) : h.Kind == InlayHintKind.Type ? new Thickness(1, 1, 2, 1) : new Thickness(0, 1, 3, 1),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = h.Kind == InlayHintKind.Usages ? Logic.Localization.NavStrings.CodeVisionTip(h.SymbolName) : string.IsNullOrEmpty(h.Detail) ? null : h.Detail,
                IsHitTestVisible = h.Kind == InlayHintKind.Usages || !string.IsNullOrEmpty(h.Detail),
            };
            _elements[h] = border;
            return border;
        }

        private static readonly Brush HintForeground = Frozen(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));
        private static readonly Brush HintBackground = Frozen(Color.FromArgb(0x26, 0x80, 0x80, 0x80));

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // ---------------------------------------------------------------- Code Vision rows (v0.7.3, read by CodeVisionLayout)

        private Dictionary<int, InlayHint> _visionLines;
        private ITextSnapshot _visionFor;
        private ITextSnapshot _visionSource;
        private int _visionGeneration;
        private int _visionBuiltAt = -1;

        /// <summary>Raised when the Code Vision rows changed (the layout re-formats the visible lines).</summary>
        public event EventHandler CodeVisionChanged;

        private void RaiseCodeVisionChanged()
        {
            _visionGeneration++;
            CodeVisionChanged?.Invoke(this, EventArgs.Empty);
        }

        private string CodeVisionSignature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var h in _store.Between(0, int.MaxValue))
                if (h.Kind == InlayHintKind.Usages) sb.Append(h.Line).Append(':').Append(h.DerivedTypes).Append('/').Append(h.Blueprints).Append(';');
            return sb.ToString();
        }

        /// <summary>The Code Vision hint drawn above line <paramref name="lineNumber"/> of <paramref name="target"/>, or null.</summary>
        public InlayHint CodeVisionAt(ITextSnapshot target, int lineNumber)
        {
            if (!Enabled || !CodeVision || target == null || _snapshot == null || target.TextBuffer != _snapshot.TextBuffer) return null;
            if (_visionLines == null || _visionFor != target || _visionSource != _snapshot || _visionBuiltAt != _visionGeneration)
            {
                var map = new Dictionary<int, InlayHint>();
                var source = _snapshot;
                foreach (var h in _store.Between(0, int.MaxValue))
                {
                    if (h.Kind != InlayHintKind.Usages || h.Line < 0 || h.Line >= source.LineCount) continue;
                    var start = source.GetLineFromLineNumber(h.Line).Start;
                    int line = target == source ? h.Line : start.TranslateTo(target, PointTrackingMode.Negative).GetContainingLine().LineNumber;
                    if (!map.ContainsKey(line)) map[line] = h;
                }
                _visionLines = map;
                _visionFor = target;
                _visionSource = source;
                _visionBuiltAt = _visionGeneration;
            }
            return _visionLines.TryGetValue(lineNumber, out var hint) ? hint : null;
        }

        // ---------------------------------------------------------------- triggers

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (!Enabled) return;
            if (e.NewSnapshot != e.OldSnapshot) return; // the buffer change schedules the edit delay
            if (!e.VerticalTranslation && e.NewViewState.ViewportHeight == e.OldViewState.ViewportHeight && _store.Version >= 0) return;
            Schedule(ScrollDelay);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (Enabled) Schedule(EditDelay);
        }

        private void Schedule(TimeSpan delay)
        {
            if (_timer.IsEnabled && _timer.Interval > delay) return; // an edit pause is not shortened by a scroll
            _timer.Stop();
            _timer.Interval = delay;
            _timer.Start();
        }

        private void OnTimer(object sender, EventArgs e)
        {
            _timer.Stop();
            Safe.Execute(L, "editor.inlayHints.plan", Request);
        }

        private void Request()
        {
            if (_view.IsClosed) return;
            if (!Enabled)
            {
                ClearAll();
                return;
            }
            var engine = IntelliSenseContext.Engine;
            if (engine == null || !engine.IsConnected) return;
            if (_view.InLayout || _view.TextViewLines == null)
            {
                Schedule(ScrollDelay);
                return;
            }
            var snapshot = _view.TextSnapshot;
            if (snapshot.TextBuffer != _view.TextBuffer) return;
            int first = _view.TextViewLines.FirstVisibleLine.Start.TranslateTo(snapshot, PointTrackingMode.Negative).GetContainingLine().LineNumber;
            int last = _view.TextViewLines.LastVisibleLine.End.TranslateTo(snapshot, PointTrackingMode.Positive).GetContainingLine().LineNumber;
            long version = snapshot.Version.VersionNumber;
            var range = _store.Missing(version, first, last, snapshot.LineCount);
            if (range.IsEmpty) return; // cached for this version
            var capture = IntelliSenseEditor.CaptureAt(_view.TextBuffer, new SnapshotPoint(snapshot, 0));
            if (capture == null) return;
            bool names = ParameterNames, autos = AutoTypes, vision = CodeVision;
            string path = _document?.FilePath;
            var (gen, ct) = _gate.Begin(NavigationContext.Disposal);
            Safe.RunAsync(L, "editor.inlayHints.request", async () =>
            {
                var total = Stopwatch.StartNew();
                var doc = await Task.Run(capture, ct).ConfigureAwait(false);
                var r = await engine.GetInlayHintsAsync(new InlayHintsRequest { Document = doc, StartLine = range.Start, EndLine = range.End, ParameterNames = names, AutoTypes = autos, CodeVision = vision }, ct)
                    .ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_gate.IsCurrent(gen) || _view.IsClosed || !r.Ok) return;
                var v = r.Value;
                if (v.TextVersion != version)
                {
                    L.Debug("editor.inlayHints.stale", null, ("sent", version), ("answer", v.TextVersion), ("file", path));
                    return;
                }
                string visionBefore = CodeVisionSignature();
                if (!_store.Add(version, new LineRange(v.StartLine, v.EndLine), v.Items)) return;
                if (_snapshot != snapshot)
                {
                    _snapshot = snapshot;
                    _elements.Clear();
                }
                var current = _view.TextSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(current, 0, current.Length)));
                string visionAfter = CodeVisionSignature();
                if (visionAfter != visionBefore) RaiseCodeVisionChanged();
                L.Info("editor.inlayHints", null, ("file", path), ("version", version), ("lines", v.StartLine + ".." + v.EndLine), ("items", v.Items?.Count ?? 0),
                    ("cached", _store.Count), ("covered", _store.Covered.Count), ("parameterNames", names), ("autoTypes", autos), ("codeVision", vision ? v.Items?.Count(i => i.Kind == InlayHintKind.Usages) ?? 0 : -1), ("calls", v.Calls),
                    ("resolved", v.Resolved), ("autoVariables", v.AutoVariables), ("engineMs", v.ElapsedMs), ("roundTripMs", r.ElapsedMs),
                    ("totalMs", Math.Round(total.Elapsed.TotalMilliseconds, 2)));
            }, ("file", path));
        }

        private void ClearAll()
        {
            _gate.Cancel();
            bool had = _store.Count > 0;
            bool hadVision = CodeVisionSignature().Length > 0;
            _store.Clear();
            _elements.Clear();
            _snapshot = null;
            if (had)
            {
                var s = _view.TextSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(s, 0, s.Length)));
            }
            if (hadVision) RaiseCodeVisionChanged();
        }

        private void Refresh()
        {
            bool now = Enabled;
            if (!now) ClearAll();
            else
            {
                if (_wasEnabled) ClearAll(); // a kind was switched: recompute
                Schedule(ScrollDelay);
            }
            _wasEnabled = now;
        }

        private void OnSettingsChanged(object sender, Options.SettingsChangedEventArgs e) => Safe.Execute(L, "editor.inlayHints.settings", () =>
        {
            if (e.Old.InlayParameterNames == e.New.InlayParameterNames && e.Old.InlayAutoTypes == e.New.InlayAutoTypes &&
                e.Old.InlayCodeVision == e.New.InlayCodeVision) return;
            Refresh();
        });

        private void OnHiddenChanged(object sender, EventArgs e) => Safe.Execute(L, "editor.inlayHints.toggle", Refresh);

        // Documents restored at VS startup asked before the engine was up (dropped), or got answers from a partial index.
        private void OnEngineReady(object sender, string reason) => Safe.Execute(L, "editor.inlayHints.engineReady", () =>
        {
            if (_view.IsClosed || !Enabled) return;
            ClearAll();
            Schedule(ScrollDelay);
        });

        private void OnClosed(object sender, EventArgs e)
        {
            _timer.Stop();
            _gate.Dispose();
            _store.Clear();
            _elements.Clear();
            _snapshot = null;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.Closed -= OnClosed;
            NavigationContext.SettingsChanged -= OnSettingsChanged;
            HiddenChanged -= OnHiddenChanged;
            IntelliSenseContext.EngineReady -= OnEngineReady;
        }
    }
}
