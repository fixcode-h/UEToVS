using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Refactoring.Batch2
{
    /// <summary>
    /// Introduce Variable (v0.4.5): the selected expression becomes a local declared right before its statement. Type: the
    /// engine's type of a member / call chain (ExpressionTypes), a literal's own type, else <c>auto</c>. Optionally every
    /// identical expression of the block (up to the first write to one of its names) is replaced too.
    /// </summary>
    internal static class IntroduceVariableFeature
    {
        private static readonly Logger L = Log.For("Refactor");

        public static async Task ExecuteAsync(ActionContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!rc.HasSelection)
            {
                VsStatusBar.SetText(Refactor2Strings.SelectFirst);
                return;
            }
            string text = rc.Text ?? view.TextBuffer.CurrentSnapshot.GetText();
            var a = rc.IsProjectFile ? IntroduceVariablePlanner.Analyze(text, rc.SelectionStart, rc.SelectionEnd) : null;
            if (a == null || !a.Ok)
            {
                string problem = a?.Problem ?? "engine-base";
                VsStatusBar.SetText(Refactor2Strings.Problem(problem));
                L.Info("refactor.introduceVariable", null, ("problem", problem), ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()));
                return;
            }
            string engineType = rc.Engine?.ExpressionType;
            string type = IntroduceVariablePlanner.ChooseType(a, engineType);
            string suggested = IntroduceVariablePlanner.SuggestName(a.Expression, type, a.Identifiers);

            var dialog = new NameDialog(() => Refactor2Strings.VariableTitle, () => Refactor2Strings.IntroduceButton, Refactor2Strings.VariableName, suggested)
            {
                Description = Refactor2Strings.VariableDescription(Shorten(a.Expression), type),
            };
            var replaceAll = a.Occurrences.Count > 1 ? dialog.AddCheckBox(Refactor2Strings.ReplaceAll(a.Occurrences.Count), true) : null;
            dialog.NameError = n => !RenamePlanner.IsValidIdentifier(n) ? Refactor2Strings.InvalidName : a.Identifiers.Contains(n) ? Refactor2Strings.NameTaken : null;
            dialog.Preview = n => type + " " + n + " = " + Shorten(a.Expression) + ";";
            if (!dialog.ShowModal(VsMainWindow.Handle))
            {
                L.Info("refactor.introduceVariable", null, ("canceled", true), ("type", type), ("engineType", engineType), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            string name = dialog.Name;
            bool all = replaceAll?.IsChecked == true;
            var edits = IntroduceVariablePlanner.Edits(rc.FilePath, text, a, type, name, all);
            var result = RefactorEdits.Apply("UEToVS: " + Refactor2Strings.VariableTitle, edits);
            int replaced = all ? a.Occurrences.Count : 1;
            L.Info("refactor.introduceVariable", null, ("type", type), ("engineType", engineType), ("chain", a.IsChain), ("literal", a.LiteralType),
                ("occurrences", a.Occurrences.Count), ("replaced", replaced), ("edits", result.Applied), ("skipped", result.Skipped), ("ok", result.Ok),
                ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()), ("file", Path.GetFileName(rc.FilePath)));
            VsStatusBar.SetText(!result.Ok ? RefactorStrings.EditFailed : result.Skipped > 0 ? Refactor2Strings.Skipped(result.Skipped)
                : Refactor2Strings.VariableIntroduced(name, replaced));
        }

        private static string Shorten(string s)
        {
            s = ChangeSignaturePlanner.NormalizeSpaces((s ?? string.Empty).Replace("\r", " ").Replace("\n", " "));
            return s.Length <= 120 ? s : s.Substring(0, 117) + "...";
        }
    }
}
