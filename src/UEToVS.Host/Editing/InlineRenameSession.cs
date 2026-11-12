using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// Inline rename (editor.renameMode = Inline, Ctrl+R, Ctrl+R): the occurrences of the symbol in the current file are edited
    /// together in place (typing in one mirrors to the others, marked with the reference-highlight write colour). Enter puts
    /// the text back and returns the typed name (the caller opens the existing project-wide Rename preview with it); Esc,
    /// an edit outside the occurrences, or closing the view cancels (text restored, null).
    /// </summary>
    internal sealed class InlineRenameSession
    {
        private static readonly Logger L = Log.For("Editing");
        private readonly IWpfTextView _view;
        private readonly string _oldName;
        private readonly List<ITrackingSpan> _spans = new List<ITrackingSpan>();
        private readonly TaskCompletionSource<string> _done = new TaskCompletionSource<string>();
        private ITrackingSpan _primary;
        private bool _mirroring;
        private bool _finished;

        private InlineRenameSession(IWpfTextView view, string oldName)
        {
            _view = view;
            _oldName = oldName;
        }

        /// <summary>The session running in <paramref name="view"/> (for the marker tagger), or null.</summary>
        public static InlineRenameSession Active(ITextView view) =>
            view != null && view.Properties.TryGetProperty(typeof(InlineRenameSession), out InlineRenameSession s) && !s._finished ? s : null;

        /// <summary>Runs a session; the typed name (valid, changed) after Enter, or null when canceled.</summary>
        public static async Task<string> RunAsync(IWpfTextView view, IReadOnlyList<InlineOccurrence> occurrences, string oldName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var previous = Active(view);
            if (previous != null) await previous.FinishAsync(null, "replaced", restore: true);
            var session = new InlineRenameSession(view, oldName);
            if (!session.Start(occurrences)) return null;
            return await session._done.Task;
        }

        /// <summary>A session started, mirrored or ended in this view (the marker tagger refreshes).</summary>
        public static event Action<ITextView> Changed;

        public IEnumerable<SnapshotSpan> Spans(ITextSnapshot snapshot) => _spans.Select(s => s.GetSpan(snapshot));

        private bool Start(IReadOnlyList<InlineOccurrence> occurrences)
        {
            var snapshot = _view.TextBuffer.CurrentSnapshot;
            int caret = _view.Caret.Position.BufferPosition.Position;
            foreach (var o in occurrences)
            {
                if (o.Line >= snapshot.LineCount) continue;
                var line = snapshot.GetLineFromLineNumber(o.Line);
                if (o.Column + o.Length > line.Length) continue;
                var span = new SnapshotSpan(snapshot, line.Start.Position + o.Column, o.Length);
                if (span.GetText() != _oldName || _view.TextBuffer.IsReadOnly(span)) continue;
                var tracking = snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeInclusive);
                _spans.Add(tracking);
                if (_primary == null && caret >= span.Start.Position && caret <= span.End.Position) _primary = tracking;
            }
            if (_spans.Count == 0) return false;
            if (_primary == null) _primary = _spans[0];
            _view.Properties[typeof(InlineRenameSession)] = this;
            _view.TextBuffer.Changed += OnBufferChanged;
            _view.VisualElement.PreviewKeyDown += OnPreviewKeyDown;
            _view.Closed += OnClosed;
            var primary = _primary.GetSpan(snapshot);
            _view.Selection.Select(primary, false);
            _view.Caret.MoveTo(primary.End);
            VsStatusBar.SetText(EditorFeatureStrings.InlineRenameHint);
            L.Info("editor.inlineRename.start", null, ("name", _oldName), ("occurrences", _spans.Count));
            Changed?.Invoke(_view);
            return true;
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_mirroring || _finished) return;
            var after = e.After;
            var primary = _primary.GetSpan(after);
            foreach (var c in e.Changes)
            {
                var changed = new Span(c.NewPosition, c.NewLength);
                bool inside = changed.Start >= primary.Start.Position && changed.End <= primary.End.Position;
                if (!inside)
                {
                    // undo, an edit elsewhere, a paste over the occurrences: stop mirroring and keep what is there
                    _ = UEToVS.Host.Diagnostics.Safe.RunAsync(L, "editor.inlineRename", () => FinishAsync(null, "edited-elsewhere", restore: false));
                    return;
                }
            }
            // mirror after this change completes (no edits from inside Changed)
            _ = UEToVS.Host.Diagnostics.Safe.RunAsync(L, "editor.inlineRename", MirrorAsync);
        }

        private async Task MirrorAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_finished) return;
            var snapshot = _view.TextBuffer.CurrentSnapshot;
            string text = _primary.GetText(snapshot);
            _mirroring = true;
            try
            {
                using (var edit = _view.TextBuffer.CreateEdit())
                {
                    foreach (var s in _spans)
                    {
                        if (s == _primary) continue;
                        var span = s.GetSpan(snapshot);
                        if (span.GetText() != text) edit.Replace(span, text);
                    }
                    if (edit.HasEffectiveChanges) edit.Apply();
                    else edit.Cancel();
                }
            }
            finally
            {
                _mirroring = false;
            }
            Changed?.Invoke(_view);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_finished) return;
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Finish(null, "escape");
            }
            else if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                string name = _primary.GetText(_view.TextBuffer.CurrentSnapshot).Trim();
                e.Handled = true;
                if (!InlineRenamePlan.IsAcceptable(_oldName, name))
                {
                    if (string.Equals(name, _oldName, StringComparison.Ordinal)) Finish(null, "unchanged");
                    else VsStatusBar.SetText(EditorFeatureStrings.InlineRenameInvalid(name));
                    return;
                }
                Finish(name, "enter");
            }
        }

        private void OnClosed(object sender, EventArgs e) => Finish(null, "closed", restore: false);

        private Task FinishAsync(string result, string reason, bool restore)
        {
            Finish(result, reason, restore);
            return Task.CompletedTask;
        }

        /// <summary>Ends the session; puts the old name back (the preview applies the change project-wide, one undo step).</summary>
        private void Finish(string result, string reason, bool restore = true)
        {
            if (_finished) return;
            _finished = true;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.VisualElement.PreviewKeyDown -= OnPreviewKeyDown;
            _view.Closed -= OnClosed;
            _view.Properties.RemoveProperty(typeof(InlineRenameSession));
            if (restore && !_view.IsClosed)
            {
                try
                {
                    var snapshot = _view.TextBuffer.CurrentSnapshot;
                    using (var edit = _view.TextBuffer.CreateEdit())
                    {
                        foreach (var s in _spans)
                        {
                            var span = s.GetSpan(snapshot);
                            if (span.GetText() != _oldName) edit.Replace(span, _oldName);
                        }
                        if (edit.HasEffectiveChanges) edit.Apply();
                        else edit.Cancel();
                    }
                }
                catch (Exception ex)
                {
                    L.Warn("editor.inlineRename.restore.failed", "Restoring the old name failed", ex, ("name", _oldName));
                }
            }
            L.Info("editor.inlineRename.end", null, ("name", _oldName), ("newName", result), ("reason", reason), ("occurrences", _spans.Count));
            Changed?.Invoke(_view);
            _done.TrySetResult(result);
        }

    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType(EditorContentTypes.Cpp)]
    [TagType(typeof(TextMarkerTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class InlineRenameTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (!(textView is IWpfTextView view) || buffer != textView.TextBuffer) return null;
            return view.Properties.GetOrCreateSingletonProperty(typeof(InlineRenameTagger), () => new InlineRenameTagger(view)) as ITagger<T>;
        }
    }

    /// <summary>Marks the occurrences of a running <see cref="InlineRenameSession"/> (reference-highlight write colour).</summary>
    internal sealed class InlineRenameTagger : ITagger<TextMarkerTag>
    {
        private static readonly TextMarkerTag Tag = new TextMarkerTag(ReferenceHighlightFormats.Write);
        private readonly IWpfTextView _view;

        public InlineRenameTagger(IWpfTextView view)
        {
            _view = view;
            InlineRenameSession.Changed += OnSessionChanged;
            _view.Closed += (s, e) => InlineRenameSession.Changed -= OnSessionChanged;
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private void OnSessionChanged(ITextView view)
        {
            if (view != _view || _view.IsClosed) return;
            var s = _view.TextSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(s, 0, s.Length)));
        }

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var session = InlineRenameSession.Active(_view);
            if (session == null || spans.Count == 0) yield break;
            foreach (var span in session.Spans(spans[0].Snapshot))
                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(span))) yield return new TagSpan<TextMarkerTag>(span, Tag);
        }
    }
}
