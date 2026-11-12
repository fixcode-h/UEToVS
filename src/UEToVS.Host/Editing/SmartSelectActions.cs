using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Shell;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// Smart Select: extend (<see cref="SmartSelection"/>) / shrink (back through the previous extensions) the selection of the
    /// active C++ editor. Works on the buffer text at the moment of the command; nothing runs in the background.
    /// </summary>
    internal static class SmartSelectActions
    {
        private static readonly Logger L = Log.For("Editing");
        private static readonly object StackKey = typeof(SmartSelectActions);

        /// <summary>Text this large is searched only in a window around the selection (keeps the command instant).</summary>
        private const int Window = 200_000;

        public static void Register(ActionRegistry registry)
        {
            registry.Register(new DelegateAction(ActionIds.ExpandSelection, Loc.T("扩大选区", "Extend Selection"), ActionCategory.Navigation,
                ctx => RunAsync(true), ctx => ctx.ActiveDocumentPath != null, null, "select", "selection", "选区", "扩选", "smart select"));
            registry.Register(new DelegateAction(ActionIds.ShrinkSelection, Loc.T("缩小选区", "Shrink Selection"), ActionCategory.Navigation,
                ctx => RunAsync(false), ctx => ctx.ActiveDocumentPath != null, null, "select", "selection", "选区", "缩选", "smart select"));
        }

        private static async Task RunAsync(bool expand)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorServices.ActiveView();
            if (view == null) return;
            var sw = Stopwatch.StartNew();
            var snap = view.TextSnapshot;
            var sel = view.Selection.StreamSelectionSpan.SnapshotSpan;
            var stack = view.Properties.GetOrCreateSingletonProperty(StackKey, () => new List<(ITrackingSpan Previous, ITrackingSpan Result)>());
            // a selection changed by hand since the last step starts a new history
            if (stack.Count > 0 && stack[stack.Count - 1].Result.GetSpan(snap) != sel.Span) stack.Clear();
            if (!expand)
            {
                if (stack.Count == 0) return;
                var prev = stack[stack.Count - 1].Previous.GetSpan(snap);
                stack.RemoveAt(stack.Count - 1);
                Select(view, prev);
                L.Debug("edit.smartSelect", null, ("direction", "shrink"), ("length", prev.Length), ("depth", stack.Count));
                return;
            }
            int offset = 0;
            string text;
            if (snap.Length > Window)
            {
                offset = System.Math.Max(0, sel.Start.Position - Window / 2);
                int end = System.Math.Min(snap.Length, System.Math.Max(sel.End.Position, offset + Window));
                text = snap.GetText(offset, end - offset);
            }
            else text = snap.GetText();
            var r = SmartSelection.Expand(text, sel.Start.Position - offset, sel.End.Position - offset);
            if (r == null) return;
            var next = new SnapshotSpan(snap, offset + r.Value.Start, r.Value.End - r.Value.Start);
            stack.Add((snap.CreateTrackingSpan(sel.Span, SpanTrackingMode.EdgeExclusive), snap.CreateTrackingSpan(next.Span, SpanTrackingMode.EdgeExclusive)));
            Select(view, next);
            L.Debug("edit.smartSelect", null, ("direction", "expand"), ("length", next.Length), ("depth", stack.Count), ("ms", sw.Elapsed.TotalMilliseconds));
        }

        private static void Select(ITextView view, SnapshotSpan span)
        {
            view.Selection.Select(span, false);
            view.Caret.MoveTo(span.End);
            view.ViewScroller.EnsureSpanVisible(span);
        }
    }
}
