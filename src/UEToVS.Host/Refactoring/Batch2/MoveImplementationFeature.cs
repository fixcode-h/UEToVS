using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Refactoring.Batch2
{
    /// <summary>
    /// Move Implementation (v0.4.5): an inline member body in a header moves to the .cpp (placed by CodePlacement after the
    /// definition of the member declared before it; virtual / override / static / defaults stripped, class qualifier added);
    /// the reverse moves an out-of-line definition back into its declaration in the header. "Whole class" moves every inline
    /// body of the class at once. One undo step for both files.
    /// </summary>
    internal static class MoveImplementationFeature
    {
        private static readonly Logger L = Log.For("Refactor");

        public static async Task ExecuteAsync(ActionContext ctx, bool wholeClass)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string direction = rc.IsHeader ? "toSource" : "toHeader";
            MovePlan plan = null;
            string problem = null, target = null;
            if (!rc.IsProjectFile) problem = "engine-base";
            else if (rc.IsHeader)
            {
                string header = rc.Text ?? view.TextBuffer.CurrentSnapshot.GetText();
                var members = wholeClass ? ClassMembers(rc, header) : null;
                if (!wholeClass)
                {
                    var m = MoveImplementationPlanner.InlineMemberAt(header, rc.CaretOffset, out problem);
                    if (m != null) members = new[] { m }.ToList();
                }
                if (members == null || members.Count == 0) problem = problem ?? "no-member";
                else
                {
                    target = await RefactoringBatch2.CounterpartAsync(rc.FilePath, wantHeader: false);
                    string cppText = target == null ? null : RefactorEdits.ReadText(target);
                    if (cppText == null || !RefactoringBatch2.IsEditablePath(target)) problem = "no-counterpart";
                    else plan = MoveImplementationPlanner.ToSource(rc.FilePath, header, target, cppText, members);
                }
            }
            else
            {
                string cpp = rc.Text ?? view.TextBuffer.CurrentSnapshot.GetText();
                var def = MoveImplementationPlanner.DefinitionAt(cpp, rc.CaretOffset);
                if (def == null) problem = "no-definition";
                else
                {
                    target = await RefactoringBatch2.CounterpartAsync(rc.FilePath, wantHeader: true);
                    string headerText = target == null ? null : RefactorEdits.ReadText(target);
                    if (headerText == null || !RefactoringBatch2.IsEditablePath(target)) problem = "no-counterpart";
                    else plan = MoveImplementationPlanner.ToHeader(target, headerText, rc.FilePath, cpp, def);
                }
            }
            if (problem == null && plan != null && !plan.Ok) problem = plan.Problem ?? "unparsable";
            if (problem != null || plan == null)
            {
                VsStatusBar.SetText(Refactor2Strings.Problem(problem ?? "unparsable"));
                L.Info("refactor.moveImplementation", null, ("direction", direction), ("wholeClass", wholeClass), ("problem", problem), ("ms", sw.ElapsedMilliseconds),
                    ("source", ctx.Source.ToString()), ("file", Path.GetFileName(rc.FilePath)));
                return;
            }
            var result = RefactorEdits.Apply("UEToVS: " + Refactor2Strings.CmdMoveImplementation, plan.HeaderEdits.Concat(plan.SourceEdits));
            L.Info("refactor.moveImplementation", null, ("direction", direction), ("wholeClass", wholeClass), ("moved", plan.Moved.Count),
                ("names", string.Join(",", plan.Moved)), ("reason", plan.Reason), ("edits", result.Applied), ("skipped", result.Skipped), ("filesChanged", result.Files),
                ("ok", result.Ok), ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()), ("file", Path.GetFileName(rc.FilePath)),
                ("target", Path.GetFileName(target)));
            if (!result.Ok || result.Skipped > 0)
            {
                VsStatusBar.SetText(result.Ok ? Refactor2Strings.Skipped(result.Skipped) : RefactorStrings.EditFailed);
                return;
            }
            VsStatusBar.SetText(Refactor2Strings.Moved(plan.Moved.Count, Path.GetFileName(target)));
        }

        /// <summary>The class at the caret (or of the inline member at the caret) and all its inline member bodies.</summary>
        private static System.Collections.Generic.List<InlineMember> ClassMembers(RefactorContext rc, string header)
        {
            var at = MoveImplementationPlanner.InlineMemberAt(header, rc.CaretOffset, out _);
            string cls = at?.ClassName ?? rc.Engine?.Class?.Symbol?.Name;
            if (string.IsNullOrEmpty(cls)) return null;
            int hint = at != null ? at.Line : rc.Engine?.Class?.BodyStartLine ?? -1;
            return MoveImplementationPlanner.InlineMembersOf(header, cls, hint);
        }
    }
}
