using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Refactoring.Batch2
{
    /// <summary>
    /// Add Forward Declaration (v0.4.5): for the type at the caret in a header that uses it only through pointers, references
    /// and handle templates (TObjectPtr, TSubclassOf, …) without including it. <c>class</c> / <c>struct</c> follow the
    /// declaration; an enum must be <c>enum class E : T</c> (its underlying type is read from the declaring header). The line
    /// goes after the include group, before the first UCLASS / class; nothing when the header already declares the type.
    /// </summary>
    internal static class ForwardDeclarationFeature
    {
        private static readonly Logger L = Log.For("Refactor");

        public static async Task ExecuteAsync(ActionContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx, checkIncludes: true);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string problem = Check(rc, out string decl, out string name);
            if (problem == null && rc.Engine.Type.Symbol.Kind == SymbolKind.Enum)
            {
                // the exact underlying type, from the header that declares the enum
                string declaring = rc.Engine.Type.HeaderPath ?? rc.Engine.Type.Symbol.Location?.FilePath;
                string text = declaring == null ? null : RefactorEdits.ReadText(declaring);
                bool scoped = false;
                string underlying = text == null ? null : ForwardDeclarationPlanner.EnumUnderlyingType(text, name, out scoped);
                if (underlying == null) problem = "enum-underlying";
                else decl = ForwardDeclarationPlanner.Declaration(ForwardDeclarationPlanner.KeywordFor(SymbolKind.Enum, scoped), name, underlying);
            }
            if (problem != null)
            {
                VsStatusBar.SetText(Refactor2Strings.Problem(problem));
                L.Info("refactor.addForwardDeclaration", null, ("type", rc.Engine?.Type?.Symbol?.Name), ("problem", problem), ("ms", sw.ElapsedMilliseconds),
                    ("source", ctx.Source.ToString()));
                return;
            }
            string header = rc.Text ?? view.TextBuffer.CurrentSnapshot.GetText();
            var point = ForwardDeclarationPlanner.Plan(header, decl);
            var result = RefactorEdits.Apply("UEToVS: " + Refactor2Strings.CmdAddForwardDeclaration, new[] { TextEdit.Insert(rc.FilePath, point.Line, point.Text) });
            L.Info("refactor.addForwardDeclaration", null, ("type", name), ("declaration", decl), ("line", point.Line), ("reason", point.Reason),
                ("include", rc.Engine.Type.Include.ToString()), ("filesChanged", result.Files), ("ok", result.Ok && result.Applied > 0), ("ms", sw.ElapsedMilliseconds),
                ("source", ctx.Source.ToString()), ("file", Path.GetFileName(rc.FilePath)));
            VsStatusBar.SetText(result.Ok && result.Applied > 0 ? Refactor2Strings.ForwardDeclared(decl) : RefactorStrings.EditFailed);
        }

        /// <summary>Null when a forward declaration fits (an enum's underlying type is filled in by the caller); otherwise the problem code.</summary>
        private static string Check(RefactorContext rc, out string declaration, out string name)
        {
            declaration = null;
            var t = rc.Engine?.Type;
            name = t?.Symbol?.Name;
            if (!rc.IsHeader) return "not-header";
            if (!rc.IsProjectFile) return "engine-base";
            if (name == null) return "no-type";
            if (!string.IsNullOrEmpty(t.Symbol.Container)) return "nested";
            if (t.Include != IncludeState.Missing && !(t.Include == IncludeState.Unknown && t.FilesVisited > 0)) return "included";
            if (string.Equals(t.HeaderPath, rc.FilePath, StringComparison.OrdinalIgnoreCase)) return "declared";
            string keyword = t.Symbol.Kind == SymbolKind.Enum ? "enum class" : ForwardDeclarationPlanner.KeywordFor(t.Symbol.Kind, false);
            if (keyword == null) return "no-type";
            string text = rc.Text;
            if (text == null) return "unparsable";
            if (ForwardDeclarationPlanner.DeclaresType(text, name) || ForwardDeclarationPlanner.HasForwardDeclaration(text, name)) return "declared";
            if (!ForwardDeclarationPlanner.OnlyIndirectUses(text, name, out string reason)) return reason ?? "by-value";
            declaration = ForwardDeclarationPlanner.Declaration(keyword, name);
            return null;
        }
    }
}
