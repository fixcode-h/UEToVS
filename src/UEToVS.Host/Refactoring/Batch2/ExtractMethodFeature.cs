using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// Extract Method (v0.4.5): whole statements of a member function in a .cpp become a private member (declared in the
    /// class, defined after the enclosing function). Locals of the enclosing function (engine, <c>IncludeLocals</c>) read
    /// in the selection become parameters; the one variable written and used afterwards becomes the return value. Anything
    /// the analysis is unsure about is refused (see <see cref="ExtractMethodPlanner"/>).
    /// </summary>
    internal static class ExtractMethodFeature
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
            string problem = null;
            ExtractMethodAnalysis a = null;
            if (!rc.IsProjectFile) problem = "engine-base";
            else if (rc.IsHeader) problem = "in-header";
            else if (rc.Engine?.EnclosingFunction == null) problem = "not-in-function";
            else
            {
                a = ExtractMethodPlanner.Analyze(text, rc.SelectionStart, rc.SelectionEnd, RefactorProviders2.LocalsOf(text, rc.Engine.Locals));
                problem = a.Problem;
            }
            string header = null, headerText = null;
            if (problem == null)
            {
                header = await HeaderOfAsync(rc, a.ClassName);
                headerText = header == null ? null : RefactorEdits.ReadText(header);
                if (headerText == null || !RefactoringBatch2.IsEditablePath(header)) problem = "no-header";
            }
            if (problem != null)
            {
                VsStatusBar.SetText(Refactor2Strings.Problem(problem));
                L.Info("refactor.extractMethod", null, ("problem", problem), ("detail", a?.ProblemDetail), ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()));
                return;
            }
            bool isStatic = ExtractMethodPlanner.IsStaticInHeader(headerText, a.ClassName, a.FunctionName) ?? false;
            string nl = CppTextScan.NewlineOf(text);
            var headerNames = new HashSet<string>(CppTextScan.Tokenize(headerText).Where(x => x.IsIdent).Select(x => x.Text), StringComparer.Ordinal);
            bool Free(string n) => ExtractMethodPlanner.IsFreeName(a, n) && !headerNames.Contains(n);
            string suggested = "ExtractedMethod";
            for (int i = 2; i < 100 && !Free(suggested); i++) suggested = "ExtractedMethod" + i;
            var dialog = new NameDialog(() => Refactor2Strings.ExtractTitle, () => Refactor2Strings.ExtractButton, Refactor2Strings.MethodName, suggested);
            dialog.NameError = n => !RenamePlanner.IsValidIdentifier(n) ? Refactor2Strings.InvalidName
                : !Free(n) ? Refactor2Strings.NameTaken : null;
            dialog.Preview = n => RenamePlanner.IsValidIdentifier(n) ? ExtractMethodPlanner.Generate(a, n, isStatic, nl).Declaration : null;
            dialog.Description = Refactor2Strings.ExtractDescription(a.ClassName);
            if (!dialog.ShowModal(VsMainWindow.Handle))
            {
                L.Info("refactor.extractMethod", null, ("canceled", true), ("parameters", a.Parameters.Count), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            string name = dialog.Name;
            var g = ExtractMethodPlanner.Generate(a, name, isStatic, nl);
            var point = CodePlacement.Declaration(headerText, a.ClassName, -1, null, AccessKind.Private, new[] { g.Declaration });
            if (point == null)
            {
                VsStatusBar.SetText(Refactor2Strings.Problem("no-class-body"));
                L.Info("refactor.extractMethod", null, ("problem", "no-class-body"), ("class", a.ClassName), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            var edits = new List<TextEdit>(ExtractMethodPlanner.SourceEdits(rc.FilePath, text, a, g)) { TextEdit.Insert(header, point.Line, point.Text) };
            var result = RefactorEdits.Apply("UEToVS: " + Refactor2Strings.ExtractTitle, edits);
            L.Info("refactor.extractMethod", null, ("class", a.ClassName), ("function", a.FunctionName), ("parameters", a.Parameters.Count),
                ("passing", string.Join(",", a.Parameters.Select(p => p.Passing.ToString()))), ("returns", a.ReturnVariable != null), ("static", isStatic),
                ("const", a.IsConst), ("lines", a.BodyLines.Count), ("placement", point.Reason), ("edits", result.Applied), ("skipped", result.Skipped),
                ("filesChanged", result.Files), ("ok", result.Ok), ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()), ("file", Path.GetFileName(rc.FilePath)));
            VsStatusBar.SetText(!result.Ok ? RefactorStrings.EditFailed : result.Skipped > 0 ? Refactor2Strings.Skipped(result.Skipped) : Refactor2Strings.MethodExtracted(name));
        }

        /// <summary>The header declaring <paramref name="className"/>: the engine's class location, else the counterpart header.</summary>
        private static async Task<string> HeaderOfAsync(RefactorContext rc, string className)
        {
            var cls = rc.Engine?.Class?.Symbol;
            string simple = className?.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (cls?.Location?.FilePath != null && string.Equals(cls.Name, simple, StringComparison.Ordinal) && Core.Unreal.RelatedNames.IsHeader(cls.Location.FilePath))
                return cls.Location.FilePath;
            return await RefactoringBatch2.CounterpartAsync(rc.FilePath, wantHeader: true);
        }
    }
}
