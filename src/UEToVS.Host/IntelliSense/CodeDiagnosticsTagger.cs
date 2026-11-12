using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Unreal;
using UEToVS.Host.Navigation;
using UEToVS.Host.Unreal;

namespace UEToVS.Host.IntelliSense
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType(EditorContentTypes.Cpp)]
    [TagType(typeof(IErrorTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CodeDiagnosticsTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (!(textView is IWpfTextView view) || buffer != textView.TextBuffer) return null;
            if (EditorServices.Document(buffer) == null) return null; // previews / projections: nothing to check
            return view.Properties.GetOrCreateSingletonProperty(typeof(CodeDiagnosticsTagger), () => new CodeDiagnosticsTagger(view)) as ITagger<T>;
        }
    }

    /// <summary>
    /// Open C++ documents of this session (per buffer, ref-counted by view): the unsaved ones are sent with every diagnostics
    /// request (the engine only sees their saved text), and saving one re-checks the other editors (a member added to a header).
    /// UI thread only.
    /// </summary>
    internal static class OpenCppDocuments
    {
        private static readonly Dictionary<ITextDocument, int> Docs = new Dictionary<ITextDocument, int>();

        /// <summary>Raised on the UI thread with the path of a document that was saved.</summary>
        public static event EventHandler<string> Saved;

        public static void Track(ITextDocument doc)
        {
            if (doc == null) return;
            Docs.TryGetValue(doc, out int n);
            Docs[doc] = n + 1;
            if (n == 0) doc.FileActionOccurred += OnFileAction;
        }

        public static void Untrack(ITextDocument doc)
        {
            if (doc == null || !Docs.TryGetValue(doc, out int n)) return;
            if (n > 1)
            {
                Docs[doc] = n - 1;
                return;
            }
            Docs.Remove(doc);
            doc.FileActionOccurred -= OnFileAction;
        }

        public static List<string> UnsavedExcept(string path)
        {
            var list = new List<string>();
            foreach (var d in Docs.Keys)
            {
                if (!d.IsDirty || string.IsNullOrEmpty(d.FilePath) || string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(d.FilePath);
            }
            return list;
        }

        private static void OnFileAction(object sender, TextDocumentFileActionEventArgs e)
        {
            if (e.FileActionType == FileActionTypes.ContentSavedToDisk) Saved?.Invoke(null, e.FilePath);
        }
    }

    /// <summary>
    /// High-confidence squiggles (design §7.6 "波浪线"; the rules are in the engine's DiagnosticsProvider plus the Build.cs
    /// include check, UE0002, here). Active only once VS's C++ IntelliSense is taken over (<see cref="IntelliSenseContext.DiagnosticsActive"/>):
    /// then these are the user's only error marks. Requests the viewport ± 50 lines after an edit pause (500 ms) or a scroll out
    /// of the checked lines (200 ms); a newer request cancels the older one (<see cref="LatestRequestGate"/>); answers for another
    /// text version are dropped; until the new answer arrives the old squiggles are translated through the edits (no flicker).
    /// The include check runs again only when the file's #include lines change. Error List integration is out of scope.
    /// </summary>
    internal sealed class CodeDiagnosticsTagger : ITagger<IErrorTag>
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private static readonly TimeSpan EditDelay = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan ScrollDelay = TimeSpan.FromMilliseconds(200);

        private readonly IWpfTextView _view;
        private readonly ITextDocument _document;
        private readonly DispatcherTimer _timer;
        private readonly LatestRequestGate _gate = new LatestRequestGate();
        private ITextSnapshot _snapshot;
        private List<(CodeDiagnostic Item, IErrorTag Tag)> _items = new List<(CodeDiagnostic, IErrorTag)>();
        private LineRange _covered = new LineRange(0, -1);
        private long _coveredVersion = -1;
        private int _requests;

        // UE0002 cache: the include list it was computed for
        private string _includeKey;
        private List<CodeDiagnostic> _includeItems = new List<CodeDiagnostic>();
        private int _includeUncertain;

        public CodeDiagnosticsTagger(IWpfTextView view)
        {
            _view = view;
            _document = EditorServices.Document(view.TextBuffer);
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ScrollDelay };
            _timer.Tick += OnTimer;
            _view.LayoutChanged += OnLayoutChanged;
            _view.TextBuffer.Changed += OnBufferChanged;
            _view.Closed += OnClosed;
            NavigationContext.SettingsChanged += OnSettingsChanged;
            OpenCppDocuments.Track(_document);
            OpenCppDocuments.Saved += OnOtherSaved;
            Schedule(ScrollDelay);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private static bool Enabled => IntelliSenseContext.DiagnosticsActive;

        // ---------------------------------------------------------------- tags

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var source = _snapshot;
            var items = _items;
            if (source == null || spans.Count == 0 || items.Count == 0 || !Enabled) yield break;
            var target = spans[0].Snapshot;
            if (target.TextBuffer != source.TextBuffer) yield break;
            foreach (var (item, tag) in items)
            {
                if (item.Line < 0 || item.Line >= source.LineCount) continue;
                var line = source.GetLineFromLineNumber(item.Line);
                if (item.Column < 0 || item.Length <= 0 || item.Column + item.Length > line.Length) continue; // stale: skip, never throw
                var span = new SnapshotSpan(source, line.Start.Position + item.Column, item.Length);
                if (target != source)
                {
                    span = span.TranslateTo(target, SpanTrackingMode.EdgeExclusive);
                    if (span.IsEmpty) continue;
                }
                if (!spans.IntersectsWith(new NormalizedSnapshotSpanCollection(span))) continue;
                yield return new TagSpan<IErrorTag>(span, tag);
            }
        }

        // ---------------------------------------------------------------- triggers

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (!Enabled)
            {
                if (_items.Count > 0) ClearAll();
                return;
            }
            if (e.NewSnapshot != e.OldSnapshot) return; // the buffer change schedules the edit delay
            if (!e.VerticalTranslation && e.NewViewState.ViewportHeight == e.OldViewState.ViewportHeight && _coveredVersion >= 0) return;
            if (_view.TextViewLines == null) return;
            var snapshot = _view.TextSnapshot;
            int first = _view.TextViewLines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
            int last = _view.TextViewLines.LastVisibleLine.End.GetContainingLine().LineNumber;
            if (CodeDiagnosticsPlan.NeedsRequest(_covered, _coveredVersion, first, last, snapshot.Version.VersionNumber)) Schedule(ScrollDelay);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (Enabled) Schedule(EditDelay);
        }

        private void OnOtherSaved(object sender, string path)
        {
            if (!Enabled || _view.IsClosed) return;
            if (string.Equals(path, _document?.FilePath, StringComparison.OrdinalIgnoreCase)) return;
            Schedule(EditDelay);
        }

        private void Schedule(TimeSpan delay)
        {
            // An edit delay must not be shortened by a scroll right after it.
            if (_timer.IsEnabled && _timer.Interval > delay) return;
            _timer.Stop();
            _timer.Interval = delay;
            _timer.Start();
        }

        private void OnTimer(object sender, EventArgs e)
        {
            _timer.Stop();
            Safe.Execute(L, "intellisense.diagnostics.plan", Request);
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
            var range = CodeDiagnosticsPlan.Range(first, last, snapshot.LineCount);
            if (range.IsEmpty) return;
            var caret = _view.Caret.Position.BufferPosition.TranslateTo(snapshot, PointTrackingMode.Positive);
            var capture = IntelliSenseEditor.CaptureAt(_view.TextBuffer, caret);
            if (capture == null) return;
            string path = _document?.FilePath;
            var unsaved = OpenCppDocuments.UnsavedExcept(path);
            var package = HostPackage.Instance;
            bool checkIncludes = package?.Unreal?.IsUnreal == true && IncludeDependencyChecker.IsCppFile(path);
            var (gen, ct) = _gate.Begin(NavigationContext.Disposal);
            int n = ++_requests;
            Safe.RunAsync(L, "intellisense.diagnostics.request", async () =>
            {
                var total = Stopwatch.StartNew();
                var doc = await Task.Run(capture, ct).ConfigureAwait(false);
                var request = new CodeDiagnosticsRequest { Document = doc, StartLine = range.Start, EndLine = range.End, UnsavedFiles = unsaved };
                var engineTask = engine.GetDiagnosticsAsync(request, ct);
                var includes = checkIncludes ? await Task.Run(() => IncludeDependencyChecker.ParseIncludes(snapshot.GetText()), ct).ConfigureAwait(false) : null;
                var r = await engineTask.ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_gate.IsCurrent(gen) || _view.IsClosed) return;
                if (!r.Ok) return;
                if (r.Value.TextVersion != version)
                {
                    L.Debug("intellisense.diagnostics.stale", null, ("sent", version), ("answer", r.Value.TextVersion), ("file", path));
                    return;
                }
                bool includeRan = false;
                if (includes != null) includeRan = await UpdateIncludesAsync(package, path, snapshot, includes, ct);
                if (!_gate.IsCurrent(gen) || _view.IsClosed) return;
                var items = new List<CodeDiagnostic>(r.Value.Items ?? new List<CodeDiagnostic>());
                if (includes != null) items.AddRange(_includeItems);
                Apply(snapshot, range, items);
                Report(path, version, range, r, items, includeRan, total.Elapsed.TotalMilliseconds, n == 1);
            }, ("file", path));
        }

        /// <summary>UE0002: re-run the Build.cs include check when the include lines changed. Returns whether it ran.</summary>
        private async Task<bool> UpdateIncludesAsync(HostPackage package, string path, ITextSnapshot snapshot, List<IncludeRef> includes, CancellationToken ct)
        {
            string key = IncludeDiagnostics.Key(includes);
            if (key == _includeKey) return false;
            if (includes.Count == 0)
            {
                _includeKey = key;
                _includeItems = new List<CodeDiagnostic>();
                _includeUncertain = 0;
                return true;
            }
            var env = ModuleDependencyFixer.CreateEnvironment(package); // UI thread: reads open Build.cs buffers
            var missing = await Task.Run(() => IncludeDependencyChecker.CheckAsync(path, includes, env, ct), ct).ConfigureAwait(false);
            int uncertain = 0;
            var list = await Task.Run(() => IncludeDiagnostics.From(missing, line => line >= 0 && line < snapshot.LineCount ? snapshot.GetLineFromLineNumber(line).GetText() : null,
                                                                    env.ReadText, out uncertain), ct).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            _includeKey = key;
            _includeItems = list;
            _includeUncertain = uncertain;
            return true;
        }

        private void Apply(ITextSnapshot snapshot, LineRange range, List<CodeDiagnostic> items)
        {
            if (_snapshot != null && _snapshot != snapshot && _snapshot.Version.VersionNumber > snapshot.Version.VersionNumber) return;
            _snapshot = snapshot;
            _covered = range;
            _coveredVersion = snapshot.Version.VersionNumber;
            _items = items.Select(d => (d, (IErrorTag)new ErrorTag(d.Severity == CodeDiagnosticSeverity.Error ? PredefinedErrorTypeNames.SyntaxError
                                                                  : d.Severity == CodeDiagnosticSeverity.Warning ? PredefinedErrorTypeNames.Warning : PredefinedErrorTypeNames.HintedSuggestion,
                                                                  CodeDiagnosticText.ToolTip(d)))).ToList();
            var current = _view.TextSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(current, 0, current.Length)));
        }

        private void Report(string path, long version, LineRange range, EngineCallResult<CodeDiagnosticsResponse> r, List<CodeDiagnostic> items, bool includeRan, double totalMs, bool first)
        {
            var v = r.Value;
            var data = new (string, object)[]
            {
                ("file", path), ("version", version), ("lines", range.ToString()), ("items", items.Count),
                ("UE0001", items.Count(d => d.Code == CodeDiagnosticCodes.UnknownMember)),
                ("UE0002", items.Count(d => d.Code == CodeDiagnosticCodes.MissingModuleDependency)),
                ("UE0003", items.Count(d => d.Code == CodeDiagnosticCodes.MissingGeneratedBody)),
                ("checked", v.Checked), ("uncertain", v.Uncertain), ("includeUncertain", _includeUncertain), ("includeCheckRan", includeRan),
                ("engineMs", v.ElapsedMs), ("roundTripMs", r.ElapsedMs), ("totalMs", Math.Round(totalMs, 2)),
            };
            if (first || items.Count > 0) L.Info("intellisense.diagnostics", null, data);
            else L.Debug("intellisense.diagnostics", null, data);
        }

        private void ClearAll()
        {
            _gate.Cancel();
            bool had = _items.Count > 0;
            _items = new List<(CodeDiagnostic, IErrorTag)>();
            _snapshot = null;
            _covered = new LineRange(0, -1);
            _coveredVersion = -1;
            if (had)
            {
                var s = _view.TextSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(s, 0, s.Length)));
            }
        }

        private void OnSettingsChanged(object sender, Options.SettingsChangedEventArgs e) => Safe.Execute(L, "intellisense.diagnostics.settings", () =>
        {
            if (e.Old.DiagnosticsEnabled == e.New.DiagnosticsEnabled) return;
            if (!e.New.DiagnosticsEnabled) ClearAll();
            else Schedule(ScrollDelay);
        });

        private void OnClosed(object sender, EventArgs e)
        {
            _timer.Stop();
            _gate.Dispose();
            _items = new List<(CodeDiagnostic, IErrorTag)>();
            _snapshot = null;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.Closed -= OnClosed;
            NavigationContext.SettingsChanged -= OnSettingsChanged;
            OpenCppDocuments.Saved -= OnOtherSaved;
            OpenCppDocuments.Untrack(_document);
        }
    }
}
