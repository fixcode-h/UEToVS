using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Unreal;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Refactoring
{
    /// <summary>
    /// Create Implementation (header declaration → body in the paired .cpp, after the neighbouring member's definition;
    /// UE RPC / BlueprintNativeEvent → _Implementation, Server + WithValidation → also _Validate returning true) and
    /// Create Declaration (.cpp definition → declaration in the class body next to its neighbour). Signatures come from the
    /// engine's parse; one undo step; the target file is opened at the generated code.
    /// </summary>
    internal static class CreateMemberFeature
    {
        private static readonly Logger L = Log.For("Refactor");

        public static async Task CreateImplementationAsync(ActionContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var f = rc.Function;
            if (f == null || f.IsDefinition)
            {
                VsStatusBar.SetText(RefactorStrings.NoFunctionAtCaret);
                return;
            }
            if (!RefactorProviders.NeedsImplementation(rc, out _))
            {
                VsStatusBar.SetText(RefactorStrings.AlreadyExists(f.Name));
                return;
            }
            var package = HostPackage.Instance;
            // the source file: the paired .cpp of the header (an existing definition of another member of the class wins)
            string header = f.Location?.FilePath ?? rc.FilePath;
            string cpp = null;
            if (RelatedNames.IsHeader(header))
            {
                var candidates = await RelatedNavigator.CounterpartFilesAsync(package, header, package.DisposalToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                cpp = candidates.FirstOrDefault(c => !RelatedNames.IsHeader(c));
            }
            else cpp = header; // a declaration in a .cpp (local class / free function): define it in the same file
            if (cpp == null)
            {
                VsStatusBar.SetText(RefactorStrings.NoCounterpartFile);
                L.Info("refactor.createImplementation", null, ("name", f.Name), ("result", "no-source"), ("header", header));
                return;
            }
            string cppText = RefactorEdits.ReadText(cpp);
            string headerText = RefactorEdits.ReadText(header);
            if (cppText == null || headerText == null)
            {
                VsStatusBar.SetText(RefactorStrings.EditFailed);
                return;
            }
            string cls = SignatureText.ClassName(f.Container);
            var existing = cls == null ? new List<MemberDefinition>() : CppTextScan.MemberDefinitions(cppText, cls);
            var bodies = SignatureText.Implementations(f, CppTextScan.NewlineOf(cppText))
                                      .Where(b => !existing.Any(d => d.Name == b.Name)).ToList();
            if (bodies.Count == 0)
            {
                VsStatusBar.SetText(RefactorStrings.AlreadyExists(f.Name));
                return;
            }
            var before = cls == null ? new List<string>() : CodePlacement.DeclaredBefore(headerText, cls, rc.Engine.Class?.Symbol?.Location?.Line ?? -1, f.Location?.Line ?? rc.Line);
            string nl = CppTextScan.NewlineOf(cppText);
            string block = string.Join(nl, bodies.Select(b => b.Text));
            var point = cls == null
                ? CodePlacement.Implementation(cppText, "\0", null, block)
                : CodePlacement.Implementation(cppText, cls, before, block);
            var result = RefactorEdits.Apply("UEToVS: " + RefactorStrings.CmdCreateImplementation, new[] { TextEdit.Insert(cpp, point.Line, point.Text) });
            bool ok = result.Ok && result.Applied > 0;
            L.Info("refactor.createImplementation", null, ("name", f.Name), ("class", cls), ("generated", string.Join(",", bodies.Select(b => b.Name))),
                ("file", Path.GetFileName(cpp)), ("line", point.Line), ("reason", point.Reason), ("filesChanged", result.Files), ("ok", ok),
                ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()));
            if (!ok)
            {
                VsStatusBar.SetText(RefactorStrings.EditFailed);
                return;
            }
            // caret into the first generated body
            int blockStart = point.Line + CountLeadingNewlines(point.Text);
            int bodyLine = blockStart + bodies[0].Text.Replace("\r\n", "\n").Split('\n').TakeWhile(l => l != "{").Count() + 1;
            NavigationService.Instance.JumpTo(cpp, bodyLine, 1, JumpReasons.Related);
            VsStatusBar.SetText(RefactorStrings.Created(string.Join(", ", bodies.Select(b => b.Name)), Path.GetFileName(cpp)));
        }

        private static int CountLeadingNewlines(string s)
        {
            int n = 0, i = 0;
            while (i < s.Length && (s[i] == '\r' || s[i] == '\n')) { if (s[i] == '\n') n++; i++; }
            return n;
        }

        public static async Task CreateDeclarationAsync(ActionContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var f = rc.Function;
            if (f == null || !f.IsDefinition)
            {
                VsStatusBar.SetText(RefactorStrings.NoFunctionAtCaret);
                return;
            }
            if (!RefactorProviders.NeedsDeclaration(rc))
            {
                VsStatusBar.SetText(rc.Engine.Declarations.Count > 0 ? RefactorStrings.AlreadyExists(f.Name) : RefactorStrings.NoClassAtCaret);
                return;
            }
            var cls = rc.Engine.Class;
            string header = cls.Symbol.Location.FilePath;
            if (!RefactoringFeature.IsProjectPath(header))
            {
                VsStatusBar.SetText(RefactorStrings.NotProjectFile);
                return;
            }
            string headerText = RefactorEdits.ReadText(header);
            string cppText = view.TextBuffer.CurrentSnapshot.GetText();
            if (headerText == null)
            {
                VsStatusBar.SetText(RefactorStrings.EditFailed);
                return;
            }
            string className = cls.Symbol.Name;
            var definedBefore = CodePlacement.DefinedBefore(cppText, SignatureText.ClassName(f.Container) ?? className, f.Location?.Line ?? rc.Line);
            string decl = SignatureText.Declaration(f, string.Empty, CppTextScan.NewlineOf(headerText));
            var point = CodePlacement.Declaration(headerText, className, cls.Symbol.Location.Line, definedBefore, AccessKind.Public, new[] { decl });
            if (point == null)
            {
                VsStatusBar.SetText(RefactorStrings.ClassBodyNotFound(className));
                L.Info("refactor.createDeclaration", null, ("name", f.Name), ("class", className), ("result", "no-class-body"), ("header", header));
                return;
            }
            var result = RefactorEdits.Apply("UEToVS: " + RefactorStrings.CmdCreateDeclaration, new[] { TextEdit.Insert(header, point.Line, point.Text) });
            bool ok = result.Ok && result.Applied > 0;
            L.Info("refactor.createDeclaration", null, ("name", f.Name), ("class", className), ("file", Path.GetFileName(header)), ("line", point.Line),
                ("reason", point.Reason), ("filesChanged", result.Files), ("ok", ok), ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()));
            if (!ok)
            {
                VsStatusBar.SetText(RefactorStrings.EditFailed);
                return;
            }
            int declLine = point.Line + point.Text.Replace("\r\n", "\n").Split('\n').Count(l => l.TrimEnd().EndsWith(":", StringComparison.Ordinal));
            NavigationService.Instance.JumpTo(header, declLine, 1, JumpReasons.Related);
            VsStatusBar.SetText(RefactorStrings.Created(f.Name, Path.GetFileName(header)));
        }
    }
}
