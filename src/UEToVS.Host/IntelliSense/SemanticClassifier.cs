using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.IntelliSense
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType(EditorContentTypes.Cpp)]
    [TagType(typeof(IClassificationTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TextViewRole(Search.EditorCodePreview.Role)] // Search Everywhere / results code preview
    internal sealed class SemanticClassificationTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal IClassificationTypeRegistryService Registry { get; set; }

        [Import]
        internal IClassificationFormatMapService FormatMaps { get; set; }

        /// <summary>Set on first use so commands (reset colours, VA import) can reach the editor services.</summary>
        internal static SemanticClassificationTaggerProvider Instance { get; private set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            Instance = this;
            if (!(textView is IWpfTextView view) || buffer != textView.TextBuffer) return null;
            return view.Properties.GetOrCreateSingletonProperty(typeof(SemanticClassificationTagger),
                () => new SemanticClassificationTagger(view, Registry, FormatMaps?.GetClassificationFormatMap("text"))) as ITagger<T>;
        }

        public IClassificationFormatMap TextFormatMap => FormatMaps?.GetClassificationFormatMap("text");
    }

    /// <summary>
    /// VA-style semantic colouring (§7.7) for C++ editors of an Unreal solution. Only the viewport ± margin is classified
    /// (<see cref="VisibleRangePlanner"/>): after a scroll pause (60 ms) the missing lines, after an edit pause (250 ms) the
    /// window for the new version. A newer request cancels the older one (<see cref="LatestRequestGate"/>, overlapping
    /// ranges merged), answers whose TextVersion differs from the request are dropped, and until the new answer arrives the
    /// previous snapshot's spans are translated so colours do not flicker while typing. Results are bounded
    /// (<see cref="SemanticLineStore.Trim"/>). Everything off the UI thread except the cheap capture and the tag build.
    /// </summary>
    internal sealed class SemanticClassificationTagger : ITagger<IClassificationTag>
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private static readonly TimeSpan ScrollDelay = TimeSpan.FromMilliseconds(60);
        private static readonly TimeSpan EditDelay = TimeSpan.FromMilliseconds(250);
        private const int MaxStoredLines = 4000;

        private readonly IWpfTextView _view;
        private readonly IClassificationTypeRegistryService _registry;
        private readonly IClassificationFormatMap _formatMap;
        private readonly DispatcherTimer _timer;
        private readonly LatestRequestGate _gate = new LatestRequestGate();
        private readonly SemanticLineStore _store = new SemanticLineStore();
        private readonly Dictionary<SemanticKind, IClassificationTag> _tags = new Dictionary<SemanticKind, IClassificationTag>();
        private ITextSnapshot _storeSnapshot;
        private LineRange? _inFlight;
        private long _inFlightVersion = -1;
        private int _requests;

        public SemanticClassificationTagger(IWpfTextView view, IClassificationTypeRegistryService registry, IClassificationFormatMap formatMap)
        {
            _view = view;
            _registry = registry;
            _formatMap = formatMap;
            foreach (var info in SemanticClassifications.All)
            {
                var type = registry?.GetClassificationType(info.Name);
                if (type != null) _tags[info.Kind] = new ClassificationTag(type);
            }
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ScrollDelay };
            _timer.Tick += OnTimer;
            _view.LayoutChanged += OnLayoutChanged;
            _view.TextBuffer.Changed += OnBufferChanged;
            _view.BackgroundBrushChanged += OnBackgroundChanged;
            _view.Closed += OnClosed;
            NavigationContext.SettingsChanged += OnSettingsChanged;
            IntelliSenseContext.ColoringToggled += OnColoringToggled;
            Safe.Execute(L, "is.colors.init", ApplyColors);
            Schedule(ScrollDelay);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private static bool Enabled => IntelliSenseContext.ColoringActive;

        // ---------------------------------------------------------------- tags

        public IEnumerable<ITagSpan<IClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var source = _storeSnapshot;
            if (source == null || spans.Count == 0 || _store.SpanCount == 0 || !Enabled) yield break;
            var target = spans[0].Snapshot;
            if (target.TextBuffer != source.TextBuffer) yield break;
            foreach (var requested in spans)
            {
                var inSource = target == source ? requested : requested.TranslateTo(source, SpanTrackingMode.EdgeInclusive);
                int first = inSource.Start.GetContainingLine().LineNumber;
                int last = inSource.End.GetContainingLine().LineNumber;
                for (int l = first; l <= last; l++)
                {
                    var spansOnLine = _store.Line(l);
                    if (spansOnLine.Count == 0) continue;
                    var line = source.GetLineFromLineNumber(l);
                    foreach (var s in spansOnLine)
                    {
                        if (s.Column < 0 || s.Column + s.Length > line.Length) continue; // stale / bad span: skip, never throw
                        if (!_tags.TryGetValue(s.Kind, out var tag) || (s.Modifiers & SemanticModifiers.Unresolved) != 0) continue;
                        var span = new SnapshotSpan(source, line.Start.Position + s.Column, s.Length);
                        if (target != source)
                        {
                            span = span.TranslateTo(target, SpanTrackingMode.EdgeExclusive);
                            if (span.IsEmpty) continue;
                        }
                        if (!span.IntersectsWith(requested)) continue;
                        yield return new TagSpan<IClassificationTag>(span, tag);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- triggers

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (!Enabled) return;
            if (e.VerticalTranslation || e.NewSnapshot != e.OldSnapshot || e.NewViewState.ViewportHeight != e.OldViewState.ViewportHeight)
                Schedule(e.NewSnapshot != e.OldSnapshot ? EditDelay : ScrollDelay);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // A preview buffer only changes when it shows another file: colour it as soon as it is laid out.
            if (Enabled) Schedule(Search.EditorCodePreview.IsPreviewBuffer(_view.TextBuffer) ? ScrollDelay : EditDelay);
        }

        private void Schedule(TimeSpan delay)
        {
            // An edit delay must not be shortened by a scroll right after it (the edit is still settling).
            if (_timer.IsEnabled && _timer.Interval > delay) return;
            _timer.Stop();
            _timer.Interval = delay;
            _timer.Start();
        }

        private void OnTimer(object sender, EventArgs e)
        {
            _timer.Stop();
            Safe.Execute(L, "is.classify.plan", Request);
        }

        private void Request()
        {
            if (_view.IsClosed || !Enabled) return;
            var engine = IntelliSenseContext.Engine;
            if (engine == null || !engine.IsConnected) return;
            if (_view.InLayout || _view.TextViewLines == null)
            {
                Schedule(ScrollDelay);
                return;
            }
            var snapshot = _view.TextSnapshot;
            if (snapshot.TextBuffer != _view.TextBuffer) return; // projection: not supported
            int first = _view.TextViewLines.FirstVisibleLine.Start.TranslateTo(snapshot, PointTrackingMode.Negative).GetContainingLine().LineNumber;
            int last = _view.TextViewLines.LastVisibleLine.End.TranslateTo(snapshot, PointTrackingMode.Positive).GetContainingLine().LineNumber;
            long version = snapshot.Version.VersionNumber;
            int margin = Math.Max(0, IntelliSenseContext.Settings.SemanticColoringMarginLines);
            var plan = VisibleRangePlanner.Plan(first, last, snapshot.LineCount, version, _storeSnapshot == snapshot ? _store : null, margin);
            if (plan == null) return;
            var range = VisibleRangePlanner.Merge(_inFlight, _inFlightVersion, plan.Value, version);
            var capture = IntelliSenseEditor.CaptureAt(_view.TextBuffer, new SnapshotPoint(snapshot, 0));
            if (capture == null) return;
            var (gen, ct) = _gate.Begin(NavigationContext.Disposal);
            _inFlight = range;
            _inFlightVersion = version;
            int n = ++_requests;
            Safe.RunAsync(L, "is.classify.request", async () =>
            {
                var doc = await Task.Run(capture, ct).ConfigureAwait(false);
                var request = new ClassifyRequest { Document = doc, StartLine = range.Start, EndLine = range.End };
                var r = await engine.ClassifyAsync(request, ct).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_gate.IsCurrent(gen) || _view.IsClosed) return;
                _inFlight = null;
                if (!r.Ok) return;
                if (r.Value.TextVersion != version)
                {
                    L.Debug("is.classify.stale", null, ("sent", version), ("answer", r.Value.TextVersion), ("file", doc?.FilePath));
                    return;
                }
                Apply(snapshot, range, r.Value);
                if (n == 1) L.Info("is.classify.first", null, ("file", doc?.FilePath), ("lines", range.ToString()), ("spans", r.Value.Spans?.Count ?? 0),
                                   ("engineMs", r.Value.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            });
        }

        private void Apply(ITextSnapshot snapshot, LineRange range, ClassifyResponse response)
        {
            if (_storeSnapshot != snapshot && _storeSnapshot != null && _storeSnapshot.Version.VersionNumber > snapshot.Version.VersionNumber) return;
            var old = _storeSnapshot;
            _storeSnapshot = snapshot;
            _store.Apply(snapshot.Version.VersionNumber, range, response.Spans);
            if (_store.CoveredLines > MaxStoredLines)
                _store.Trim(VisibleRangePlanner.Desired(range.Start, range.End, snapshot.LineCount, MaxStoredLines / 4));
            // Repaint: the classified lines, or everything when the version changed (old spans were translated before).
            var current = _view.TextSnapshot;
            SnapshotSpan changed;
            if (old != snapshot)
            {
                changed = new SnapshotSpan(current, 0, current.Length);
            }
            else
            {
                var s = new SnapshotSpan(snapshot.GetLineFromLineNumber(range.Start).Start, snapshot.GetLineFromLineNumber(Math.Min(range.End, snapshot.LineCount - 1)).EndIncludingLineBreak);
                changed = current == snapshot ? s : s.TranslateTo(current, SpanTrackingMode.EdgeInclusive);
            }
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(changed));
        }

        private void ClearAll()
        {
            _gate.Cancel();
            _inFlight = null;
            bool had = _store.SpanCount > 0;
            _store.Clear();
            _storeSnapshot = null;
            if (had)
            {
                var s = _view.TextSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(s, 0, s.Length)));
            }
        }

        // ---------------------------------------------------------------- settings / theme

        private void OnSettingsChanged(object sender, Options.SettingsChangedEventArgs e) => Safe.Execute(L, "is.colors.settings", () =>
        {
            if (e.Old.SemanticColoring != e.New.SemanticColoring)
            {
                if (!e.New.SemanticColoring) ClearAll();
                else Schedule(ScrollDelay);
            }
            if (e.Old.SemanticColorOverrides != e.New.SemanticColorOverrides) ApplyColors();
        });

        private void OnBackgroundChanged(object sender, BackgroundBrushChangedEventArgs e) => Safe.Execute(L, "is.colors.theme", ApplyColors);

        private void ApplyColors() =>
            SemanticColors.Apply(_formatMap, _registry, SemanticColors.IsDark(_view.Background), IntelliSenseContext.Settings.SemanticColorOverrides);

        private void OnColoringToggled(object sender, EventArgs e) => Safe.Execute(L, "is.colors.toggled", Refresh);

        private void Refresh()
        {
            if (Enabled) Schedule(ScrollDelay);
            else ClearAll();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _timer.Stop();
            _gate.Dispose();
            _store.Clear();
            _storeSnapshot = null;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.BackgroundBrushChanged -= OnBackgroundChanged;
            _view.Closed -= OnClosed;
            NavigationContext.SettingsChanged -= OnSettingsChanged;
            IntelliSenseContext.ColoringToggled -= OnColoringToggled;
        }
    }
}
