using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
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
    /// Settings: 代码感知 > 内嵌提示; Ctrl+Shift+Alt+U, 2 hides / shows them for the session (<see cref="Hidden"/>). Log: editor.inlayHints.
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

        /// <summary>Some inlay hint kind is on in the settings (whether hidden or not).</summary>
        public static bool AnyEnabledInSettings => ParameterNames || AutoTypes;

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
                if (h.Line < 0 || h.Line >= source.LineCount) continue;
                var line = source.GetLineFromLineNumber(h.Line);
                if (h.Column < 0 || h.Column > line.Length) continue; // stale: skip, never throw
                var point = new SnapshotPoint(source, line.Start.Position + h.Column);
                var affinity = h.Kind == InlayHintKind.Type ? PositionAffinity.Predecessor : PositionAffinity.Successor;
                if (target != source) point = point.TranslateTo(target, h.Kind == InlayHintKind.Type ? PointTrackingMode.Negative : PointTrackingMode.Positive);
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
                Margin = h.Kind == InlayHintKind.Type ? new Thickness(1, 1, 2, 1) : new Thickness(0, 1, 3, 1),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = string.IsNullOrEmpty(h.Detail) ? null : h.Detail,
                IsHitTestVisible = !string.IsNullOrEmpty(h.Detail),
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
            bool names = ParameterNames, autos = AutoTypes;
            string path = _document?.FilePath;
            var (gen, ct) = _gate.Begin(NavigationContext.Disposal);
            Safe.RunAsync(L, "editor.inlayHints.request", async () =>
            {
                var total = Stopwatch.StartNew();
                var doc = await Task.Run(capture, ct).ConfigureAwait(false);
                var r = await engine.GetInlayHintsAsync(new InlayHintsRequest { Document = doc, StartLine = range.Start, EndLine = range.End, ParameterNames = names, AutoTypes = autos }, ct)
                    .ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_gate.IsCurrent(gen) || _view.IsClosed || !r.Ok) return;
                var v = r.Value;
                if (v.TextVersion != version)
                {
                    L.Debug("editor.inlayHints.stale", null, ("sent", version), ("answer", v.TextVersion), ("file", path));
                    return;
                }
                if (!_store.Add(version, new LineRange(v.StartLine, v.EndLine), v.Items)) return;
                if (_snapshot != snapshot)
                {
                    _snapshot = snapshot;
                    _elements.Clear();
                }
                var current = _view.TextSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(current, 0, current.Length)));
                L.Info("editor.inlayHints", null, ("file", path), ("version", version), ("lines", v.StartLine + ".." + v.EndLine), ("items", v.Items?.Count ?? 0),
                    ("cached", _store.Count), ("covered", _store.Covered.Count), ("parameterNames", names), ("autoTypes", autos), ("calls", v.Calls),
                    ("resolved", v.Resolved), ("autoVariables", v.AutoVariables), ("engineMs", v.ElapsedMs), ("roundTripMs", r.ElapsedMs),
                    ("totalMs", Math.Round(total.Elapsed.TotalMilliseconds, 2)));
            }, ("file", path));
        }

        private void ClearAll()
        {
            _gate.Cancel();
            bool had = _store.Count > 0;
            _store.Clear();
            _elements.Clear();
            _snapshot = null;
            if (had)
            {
                var s = _view.TextSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(s, 0, s.Length)));
            }
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
            if (e.Old.InlayParameterNames == e.New.InlayParameterNames && e.Old.InlayAutoTypes == e.New.InlayAutoTypes) return;
            Refresh();
        });

        private void OnHiddenChanged(object sender, EventArgs e) => Safe.Execute(L, "editor.inlayHints.toggle", Refresh);

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
        }
    }
}
