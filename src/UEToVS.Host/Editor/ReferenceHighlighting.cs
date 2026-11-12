using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows.Media;
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
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Editor
{
    /// <summary>Marker formats (Tools &gt; Options &gt; Fonts and Colors: "UEToVS Reference Highlight (Read/Write)").</summary>
    internal static class ReferenceHighlightFormats
    {
        public const string Read = "UEToVS.ReferenceHighlight.Read";
        public const string Write = "UEToVS.ReferenceHighlight.Write";
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name(ReferenceHighlightFormats.Read)]
    [UserVisible(true)]
    internal sealed class ReadHighlightFormatDefinition : MarkerFormatDefinition
    {
        public ReadHighlightFormatDefinition()
        {
            DisplayName = "UEToVS Reference Highlight (Read)";
            Fill = Frozen(HighlightColors.DarkRead);
            Border = null;
            ZOrder = 5;
        }

        internal static SolidColorBrush Frozen(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name(ReferenceHighlightFormats.Write)]
    [UserVisible(true)]
    internal sealed class WriteHighlightFormatDefinition : MarkerFormatDefinition
    {
        public WriteHighlightFormatDefinition()
        {
            DisplayName = "UEToVS Reference Highlight (Write)";
            Fill = ReadHighlightFormatDefinition.Frozen(HighlightColors.DarkWrite);
            Border = null;
            ZOrder = 5;
        }
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType(EditorContentTypes.Cpp)]
    [TagType(typeof(TextMarkerTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ReferenceHighlightTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal IEditorFormatMapService FormatMaps { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (!(textView is IWpfTextView view) || buffer != textView.TextBuffer) return null;
            return view.Properties.GetOrCreateSingletonProperty(typeof(ReferenceHighlightTagger),
                () => new ReferenceHighlightTagger(view, FormatMaps?.GetEditorFormatMap(view))) as ITagger<T>;
        }
    }

    /// <summary>
    /// Highlights the other occurrences (read / write colors) of the symbol at the caret in C++ documents. The engine is
    /// asked only after the caret rested ~150 ms on an identifier (lexical pre-check, no RPC on whitespace / keywords /
    /// comments), not again while the caret stays inside an already highlighted occurrence, and a newer caret cancels an
    /// in-flight request (<see cref="LatestRequestGate"/>). Results for an older snapshot are translated to the current one.
    /// Off with the "reference highlighting" setting or outside Unreal solutions.
    /// </summary>
    internal sealed class ReferenceHighlightTagger : ITagger<TextMarkerTag>
    {
        private static readonly Logger L = Log.For("Highlight");
        private static readonly TextMarkerTag ReadTag = new TextMarkerTag(ReferenceHighlightFormats.Read);
        private static readonly TextMarkerTag WriteTag = new TextMarkerTag(ReferenceHighlightFormats.Write);
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

        private readonly IWpfTextView _view;
        private readonly IEditorFormatMap _formatMap;
        private readonly DispatcherTimer _timer;
        private readonly LatestRequestGate _gate = new LatestRequestGate();
        private List<(SnapshotSpan Span, bool Write)> _spans = new List<(SnapshotSpan, bool)>();
        private (bool Dark, uint Read, uint Write)? _appliedColors;

        public ReferenceHighlightTagger(IWpfTextView view, IEditorFormatMap formatMap)
        {
            _view = view;
            _formatMap = formatMap;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Delay };
            _timer.Tick += OnTimer;
            _view.Caret.PositionChanged += OnCaretMoved;
            _view.TextBuffer.Changed += OnBufferChanged;
            _view.BackgroundBrushChanged += OnBackgroundChanged;
            _view.Closed += OnClosed;
            NavigationContext.SettingsChanged += OnSettingsChanged;
            Safe.Execute(L, "highlight.colors", ApplyColors);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private static bool Enabled => NavigationContext.IsUnrealSolution && NavigationContext.Settings.ReferenceHighlighting;

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var current = _spans;
            if (current.Count == 0 || spans.Count == 0) yield break;
            var snapshot = spans[0].Snapshot;
            foreach (var (span, write) in current)
            {
                var s = span.Snapshot == snapshot ? span : span.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
                if (s.IsEmpty || !spans.IntersectsWith(new NormalizedSnapshotSpanCollection(s))) continue;
                yield return new TagSpan<TextMarkerTag>(s, write ? WriteTag : ReadTag);
            }
        }

        // ---------------------------------------------------------------- triggers

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
        {
            // Hot path (every caret move): keep it allocation-free when the caret stays inside a highlighted occurrence.
            if (_spans.Count > 0 && InsideHighlight(e.NewPosition.BufferPosition)) return;
            _timer.Stop();
            _gate.Cancel();
            if (!Enabled)
            {
                Clear();
                return;
            }
            _timer.Start();
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // Typing: drop the highlights; they come back once the caret rests again.
            _gate.Cancel();
            Clear();
            if (Enabled)
            {
                _timer.Stop();
                _timer.Start();
            }
        }

        private void OnTimer(object sender, EventArgs e)
        {
            _timer.Stop();
            if (_view.IsClosed || !Enabled) return;
            var caret = _view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            if (line.Length > 4000 || !IdentifierFinder.TryFind(line.GetText(), caret.Position - line.Start.Position, true, out _))
            {
                Clear();
                return;
            }
            var snapshot = caret.Snapshot;
            var capture = EditorServices.CaptureCaret(_view);
            if (capture == null) return;
            var (gen, ct) = _gate.Begin(NavigationContext.Disposal);
            Safe.RunAsync(L, "highlight.request", async () =>
            {
                var engine = NavigationContext.Engine;
                if (engine == null) return;
                var request = await Task.Run(capture).ConfigureAwait(false);
                var r = await engine.GetHighlightsAsync(request, ct).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_gate.IsCurrent(gen) || _view.IsClosed) return;
                if (!r.Ok)
                {
                    if (r.Status != EngineCallStatus.Canceled) Clear();
                    return;
                }
                SetSpans(ToSpans(snapshot, r.Value));
            });
        }

        private static List<(SnapshotSpan, bool)> ToSpans(ITextSnapshot snapshot, HighlightResponse r)
        {
            var list = new List<(SnapshotSpan, bool)>(r.Spans?.Count ?? 0);
            if (r.Spans == null) return list;
            foreach (var h in r.Spans)
            {
                if (h == null || h.Line < 0 || h.Line >= snapshot.LineCount || h.Length <= 0) continue;
                var line = snapshot.GetLineFromLineNumber(h.Line);
                if (h.Column < 0 || h.Column + h.Length > line.Length) continue; // stale / bad span: skip, never throw
                list.Add((new SnapshotSpan(snapshot, line.Start.Position + h.Column, h.Length), (h.Kind & ReferenceKind.Write) != 0));
            }
            return list;
        }

        private bool InsideHighlight(SnapshotPoint p)
        {
            foreach (var (span, _) in _spans)
            {
                var s = span.Snapshot == p.Snapshot ? span : span.TranslateTo(p.Snapshot, SpanTrackingMode.EdgeExclusive);
                if (p.Position >= s.Start.Position && p.Position <= s.End.Position) return true;
            }
            return false;
        }

        private void SetSpans(List<(SnapshotSpan, bool)> spans)
        {
            var old = _spans;
            _spans = spans;
            RaiseChanged(old, spans);
        }

        private void Clear()
        {
            if (_spans.Count == 0) return;
            var old = _spans;
            _spans = new List<(SnapshotSpan, bool)>();
            RaiseChanged(old, _spans);
        }

        private void RaiseChanged(List<(SnapshotSpan Span, bool Write)> a, List<(SnapshotSpan Span, bool Write)> b)
        {
            var handler = TagsChanged;
            if (handler == null) return;
            var snapshot = _view.TextSnapshot;
            int start = int.MaxValue, end = -1;
            foreach (var list in new[] { a, b })
                foreach (var (span, _) in list)
                {
                    var s = span.Snapshot == snapshot ? span : span.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
                    start = Math.Min(start, s.Start.Position);
                    end = Math.Max(end, s.End.Position);
                }
            if (end < start) return;
            handler(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, Span.FromBounds(start, end))));
        }

        // ---------------------------------------------------------------- settings / colors

        private void OnSettingsChanged(object sender, Options.SettingsChangedEventArgs e) => Safe.Execute(L, "highlight.settings", () =>
        {
            if (!e.New.ReferenceHighlighting)
            {
                _timer.Stop();
                _gate.Cancel();
                Clear();
            }
            ApplyColors();
        });

        private void OnBackgroundChanged(object sender, BackgroundBrushChangedEventArgs e) => Safe.Execute(L, "highlight.theme", ApplyColors);

        /// <summary>Settings colors (or the dark/light defaults of §7.7, chosen from the editor background) → format map.</summary>
        private void ApplyColors()
        {
            if (_formatMap == null) return;
            var s = NavigationContext.Settings;
            bool dark = true;
            if (_view.Background is SolidColorBrush bg) dark = HighlightColors.IsDark(bg.Color.R, bg.Color.G, bg.Color.B);
            uint read = HighlightColors.Resolve(s.ReferenceHighlightReadColor, false, dark);
            uint write = HighlightColors.Resolve(s.ReferenceHighlightWriteColor, true, dark);
            if (_appliedColors == (dark, read, write)) return;
            _appliedColors = (dark, read, write);
            _formatMap.BeginBatchUpdate();
            try
            {
                Set(ReferenceHighlightFormats.Read, read);
                Set(ReferenceHighlightFormats.Write, write);
            }
            finally
            {
                _formatMap.EndBatchUpdate();
            }
            L.Debug("highlight.colors", null, ("dark", dark), ("read", HighlightColors.Format(read)), ("write", HighlightColors.Format(write)));
        }

        private void Set(string format, uint argb)
        {
            var props = _formatMap.GetProperties(format);
            props[MarkerFormatDefinition.FillId] = ReadHighlightFormatDefinition.Frozen(argb);
            _formatMap.SetProperties(format, props);
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _timer.Stop();
            _gate.Dispose();
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.BackgroundBrushChanged -= OnBackgroundChanged;
            _view.Closed -= OnClosed;
            NavigationContext.SettingsChanged -= OnSettingsChanged;
        }
    }
}
