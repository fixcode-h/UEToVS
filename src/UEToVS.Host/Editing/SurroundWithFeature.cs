using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Snippets;
using UEToVS.Host.Shell;
using UEToVS.Host.Snippets;
using UEToVS.UI.Patterns;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// Surround With (v0.4.5): the selected lines (or the caret line) are wrapped in a template picked from a popup
    /// (<see cref="SurroundCatalog"/>); the template's fields (condition, stat name …) are then edited like a live template
    /// (<see cref="SnippetSession"/>: Tab / Shift+Tab, Enter / Esc). Indentation follows the first selected line. One undo step.
    /// </summary>
    internal static class SurroundWithFeature
    {
        private static readonly Logger L = Log.For("Editing");
        private static ContextMenuPopup _menu;

        public static async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorServices.ActiveView();
            if (view == null) return;
            if (IntelliSenseContext.Settings?.SurroundWithEnabled == false)
            {
                VsStatusBar.SetText(EditorFeatureStrings.SurroundDisabled);
                return;
            }
            if (view.Options.GetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId))
            {
                VsStatusBar.SetText(EditorFeatureStrings.SurroundReadOnly);
                return;
            }
            var snap = view.TextSnapshot;
            var sel = view.Selection.StreamSelectionSpan.SnapshotSpan;
            var startLine = sel.Start.GetContainingLine();
            var endLine = sel.End.GetContainingLine();
            var (first, last) = SurroundExpander.LineRange(startLine.LineNumber, endLine.LineNumber, sel.End.Position - endLine.Start.Position);
            var trackFirst = snap.CreateTrackingPoint(snap.GetLineFromLineNumber(first).Start, PointTrackingMode.Negative);
            var trackLast = snap.CreateTrackingPoint(snap.GetLineFromLineNumber(last).End, PointTrackingMode.Positive);

            var entries = SurroundCatalog.BuiltIn.Select(t => new MenuEntry(t.Key, () => t.Title)
            {
                Description = () => t.Preview,
                Tag = t,
            }).ToList();
            if (_menu == null) _menu = new ContextMenuPopup("uetovs.edit.surroundWith");
            _menu.OwnerHandle = VsMainWindow.Handle;
            var chosen = await _menu.ShowAsync(entries, EditorServices.AnchorAt(view, sel.Start));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(chosen?.Tag is SurroundTemplate template) || view.IsClosed) return;
            Safe.Execute(L, "editor.surround", () => Apply(view, template, trackFirst, trackLast));
        }

        private static void Apply(IWpfTextView view, SurroundTemplate template, ITrackingPoint firstPoint, ITrackingPoint lastPoint)
        {
            var sw = Stopwatch.StartNew();
            var snap = view.TextBuffer.CurrentSnapshot;
            var firstLine = firstPoint.GetPoint(snap).GetContainingLine();
            var lastLine = lastPoint.GetPoint(snap).GetContainingLine();
            if (lastLine.LineNumber < firstLine.LineNumber) lastLine = firstLine;
            string firstText = firstLine.GetText();
            string indent = SnippetExpander.LeadingWhitespace(firstText);
            var span = new SnapshotSpan(firstLine.Start + indent.Length, lastLine.End);
            var (unit, nl) = SnippetSession.Format(view, firstLine);
            var e = SurroundExpander.Expand(template, span.GetText(), indent, unit, nl, EditorServices.FilePath(view.TextBuffer));
            int lines = lastLine.LineNumber - firstLine.LineNumber + 1;
            SnippetSession.Insert(view, span, e, "surround:" + template.Key, template.Key);
            L.Info("editor.surround", null, ("template", template.Key), ("lines", lines), ("chars", span.Length), ("fields", e.TabStops().Count),
                ("indent", indent.Length), ("ms", sw.Elapsed.TotalMilliseconds), ("file", System.IO.Path.GetFileName(EditorServices.FilePath(view.TextBuffer) ?? string.Empty)));
            VsStatusBar.SetText(EditorFeatureStrings.SurroundDone(template.Title, lines));
        }
    }
}
