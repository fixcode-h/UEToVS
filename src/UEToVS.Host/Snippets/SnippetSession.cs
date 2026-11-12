using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Snippets;

namespace UEToVS.Host.Snippets
{
    /// <summary>
    /// Field navigation after a template was inserted: the first field is selected, Tab / Shift+Tab move between fields, typing
    /// replaces the selected field and linked copies (same field name) follow, Enter / Esc / an edit outside the template end the
    /// session (Enter moves the caret to $END$). One session per view; UI thread only.
    /// </summary>
    internal sealed class SnippetSession
    {
        private static readonly Logger L = Log.For("Snippets");

        private readonly ITextView _view;
        private readonly List<(string Name, ITrackingSpan Span, bool Primary)> _fields;
        private readonly List<int> _stops;
        private readonly ITrackingSpan _whole;
        private readonly ITrackingPoint _end;
        private readonly string _shortcut;
        private int _current;
        private bool _syncing;

        private SnippetSession(ITextView view, ITextSnapshot snapshot, int start, SnippetExpansion e, string shortcut)
        {
            _view = view;
            _shortcut = shortcut;
            _fields = e.Fields.Select(f => (f.Name, snapshot.CreateTrackingSpan(start + f.Start, f.Length, SpanTrackingMode.EdgeInclusive), f.IsPrimary)).ToList();
            _stops = new List<int>();
            for (int i = 0; i < _fields.Count; i++)
                if (_fields[i].Primary) _stops.Add(i);
            _whole = snapshot.CreateTrackingSpan(start, e.Text.Length, SpanTrackingMode.EdgeInclusive);
            _end = snapshot.CreateTrackingPoint(start + e.EndOffset, PointTrackingMode.Positive);
        }

        public static SnippetSession Current(ITextView view) =>
            view != null && view.Properties.TryGetProperty(typeof(SnippetSession), out SnippetSession s) ? s : null;

        /// <summary>Replaces <paramref name="replace"/> with the expansion and starts field navigation (when it has fields).</summary>
        public static void Insert(ITextView view, SnapshotSpan replace, SnippetExpansion e, string kind, string shortcut)
        {
            Current(view)?.Finish(false, "replaced");
            var buffer = view.TextBuffer;
            var target = replace.TranslateTo(buffer.CurrentSnapshot, SpanTrackingMode.EdgeInclusive);
            int start = target.Start.Position;
            ITextSnapshot after;
            using (var edit = buffer.CreateEdit())
            {
                edit.Replace(target.Span, e.Text);
                after = edit.Apply();
            }
            L.Info("snippets.expand", null, ("kind", kind), ("shortcut", shortcut), ("fields", e.TabStops().Count), ("chars", e.Text.Length),
                ("file", System.IO.Path.GetFileName(EditorServices.FilePath(buffer) ?? string.Empty)));
            if (e.TabStops().Count == 0)
            {
                view.Caret.MoveTo(new SnapshotPoint(after, Math.Min(after.Length, start + e.EndOffset)));
                return;
            }
            var session = new SnippetSession(view, after, start, e, shortcut);
            view.Properties[typeof(SnippetSession)] = session;
            buffer.PostChanged += session.OnPostChanged;
            view.Closed += session.OnClosed;
            session.Select(0);
        }

        public bool Next() => Move(+1);

        public bool Previous() => Move(-1);

        private bool Move(int delta)
        {
            Sync();
            if (_stops.Count == 0) return false;
            int next = _current + delta;
            if (next >= _stops.Count)
            {
                Finish(true, "last-field");
                return true;
            }
            if (next < 0) next = _stops.Count - 1;
            Select(next);
            return true;
        }

        private void Select(int stop)
        {
            _current = stop;
            var span = _fields[_stops[stop]].Span.GetSpan(_view.TextSnapshot);
            _view.Selection.Select(span, false);
            _view.Caret.MoveTo(span.End);
            _view.ViewScroller.EnsureSpanVisible(span);
        }

        /// <summary>Ends the session; <paramref name="toEnd"/> moves the caret to $END$.</summary>
        public void Finish(bool toEnd, string reason)
        {
            Sync();
            _view.TextBuffer.PostChanged -= OnPostChanged;
            _view.Closed -= OnClosed;
            _view.Properties.RemoveProperty(typeof(SnippetSession));
            if (toEnd)
            {
                _view.Selection.Clear();
                _view.Caret.MoveTo(_end.GetPoint(_view.TextSnapshot));
            }
            L.Debug("snippets.session.end", null, ("shortcut", _shortcut), ("reason", reason), ("field", _current));
        }

        private void OnClosed(object sender, EventArgs e) => Finish(false, "view-closed");

        /// <summary>Copies primary fields into their linked copies; an edit outside the template ends the session.</summary>
        private void OnPostChanged(object sender, EventArgs e)
        {
            if (_syncing) return;
            Safe.Execute(L, "snippets.session.changed", () =>
            {
                var snap = _view.TextSnapshot;
                var whole = _whole.GetSpan(snap);
                var caret = _view.Caret.Position.BufferPosition;
                if (caret.Position < whole.Start.Position || caret.Position > whole.End.Position)
                {
                    Finish(false, "edit-outside");
                    return;
                }
                Sync();
            });
        }

        private void Sync()
        {
            if (_syncing) return;
            var snap = _view.TextBuffer.CurrentSnapshot;
            var spans = _fields.Select(f => (f.Name, Span: f.Span.GetSpan(snap), f.Primary)).ToList();
            var edits = SnippetExpander.SyncLinked(spans.Select(s => (s.Name, s.Span.Start.Position, s.Span.Length, s.Primary)).ToList(),
                                                   (s, l) => snap.GetText(s, l));
            if (edits.Count == 0) return;
            _syncing = true;
            try
            {
                using (var edit = _view.TextBuffer.CreateEdit())
                {
                    foreach (var x in edits) edit.Replace(x.Start, x.Length, x.Text);
                    edit.Apply();
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        /// <summary>Indent unit and line break for templates inserted into <paramref name="view"/>.</summary>
        public static (string Indent, string NewLine) Format(ITextView view, ITextSnapshotLine line)
        {
            bool spaces = view.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId);
            int size = Math.Max(1, view.Options.GetOptionValue(DefaultOptions.IndentSizeOptionId));
            string nl = line.LineBreakLength > 0 ? line.GetLineBreakText() : view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId);
            return (spaces ? new string(' ', size) : "\t", string.IsNullOrEmpty(nl) ? "\r\n" : nl);
        }
    }
}
