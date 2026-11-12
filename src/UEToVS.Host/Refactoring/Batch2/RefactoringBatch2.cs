using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Refactoring.Batch2
{
    /// <summary>
    /// v0.4.5 refactorings (batch 2): registers the actions (Find Action), the Alt+Enter / light bulb providers
    /// (<see cref="RefactorProviders2"/>) and the Ctrl+R chord commands (0x0710–0x073F). The keys replace VS's own
    /// Refactor.* chords; outside UE C++ (or before UEToVS IntelliSense takes over) a command runs VS's command instead, so the
    /// keys keep working in C# and in plain C++ solutions.
    /// </summary>
    internal static class RefactoringBatch2
    {
        private static readonly Logger L = Log.For("Refactor");

        public static IReadOnlyList<(int CommandId, string ActionId, Func<string> Text)> Commands { get; } = new (int, string, Func<string>)[]
        {
            (CommandCatalog.ChangeSignature, RefactorActionIds.ChangeSignature, () => Refactor2Strings.CmdChangeSignature),
            (CommandCatalog.MoveImplementation, RefactorActionIds.MoveImplementation, () => Refactor2Strings.CmdMoveImplementation),
            (CommandCatalog.AddForwardDeclaration, RefactorActionIds.AddForwardDeclaration, () => Refactor2Strings.CmdAddForwardDeclaration),
            (CommandCatalog.IntroduceVariable, RefactorActionIds.IntroduceVariable, () => Refactor2Strings.CmdIntroduceVariable),
            (CommandCatalog.ExtractMethod, RefactorActionIds.ExtractMethod, () => Refactor2Strings.CmdExtractMethod),
            (CommandCatalog.EncapsulateField, RefactorActionIds.EncapsulateField, () => Refactor2Strings.CmdEncapsulateField),
            (CommandCatalog.GenerateGetterSetter, RefactorActionIds.GenerateGetterSetter, () => Refactor2Strings.CmdGenerateGetterSetter),
        };

        public static void Initialize(ActionRegistry registry, ContextActionHub hub, CommandRouter router, OleMenuCommandService mcs)
        {
            foreach (var p in RefactorProviders2.All) hub.Register(p);
            // VS command each key fell on before (Ctrl+R, Ctrl+O / M / E / V / I); null: nothing to fall back to
            Register(registry, RefactorActionIds.ChangeSignature, Refactor2Strings.CmdChangeSignature, ActionCategory.Refactoring,
                ChangeSignatureFeature.ExecuteAsync, "Refactor.ReorderParameters", Refactor2Strings.KwChangeSignature);
            Register(registry, RefactorActionIds.MoveImplementation, Refactor2Strings.CmdMoveImplementation, ActionCategory.Refactoring,
                ctx => MoveImplementationFeature.ExecuteAsync(ctx, false), "Refactor.ExtractInterface", Refactor2Strings.KwMoveImplementation);
            Register(registry, RefactorActionIds.MoveImplementationClass, Refactor2Strings.CmdMoveClassImplementations, ActionCategory.Refactoring,
                ctx => MoveImplementationFeature.ExecuteAsync(ctx, true), null, Refactor2Strings.KwMoveImplementation);
            Register(registry, RefactorActionIds.AddForwardDeclaration, Refactor2Strings.CmdAddForwardDeclaration, ActionCategory.Fix,
                ForwardDeclarationFeature.ExecuteAsync, null, Refactor2Strings.KwAddForwardDeclaration);
            Register(registry, RefactorActionIds.IntroduceVariable, Refactor2Strings.CmdIntroduceVariable, ActionCategory.Refactoring,
                IntroduceVariableFeature.ExecuteAsync, "Refactor.RemoveParameters", Refactor2Strings.KwIntroduceVariable);
            Register(registry, RefactorActionIds.ExtractMethod, Refactor2Strings.CmdExtractMethod, ActionCategory.Refactoring,
                ExtractMethodFeature.ExecuteAsync, "Refactor.ExtractMethod", Refactor2Strings.KwExtractMethod);
            Register(registry, RefactorActionIds.EncapsulateField, Refactor2Strings.CmdEncapsulateField, ActionCategory.Refactoring,
                EncapsulateFieldFeature.ExecuteAsync, "Refactor.EncapsulateField", Refactor2Strings.KwEncapsulateField);
            Register(registry, RefactorActionIds.GenerateGetterSetter, Refactor2Strings.CmdGenerateGetterSetter, ActionCategory.Generate,
                EncapsulateFieldFeature.ExecuteAsync, null, Refactor2Strings.KwEncapsulateField);
            if (mcs != null && router != null) router.RegisterMap(mcs, Commands);
            L.Info("refactor.batch2.registered", null, ("actions", 8), ("providers", hub.Count), ("commands", Commands.Count));
        }

        private static void Register(ActionRegistry registry, string id, string title, ActionCategory category, Func<ActionContext, Task> run, string vsFallback,
                                     string[] keywords)
        {
            // no availability predicate: the command must stay enabled so the key can fall back to VS outside UE C++
            registry.Register(new DelegateAction(id, title, category, ctx => RunAsync(id, ctx, run, vsFallback), null, null, keywords));
        }

        private static async Task RunAsync(string id, ActionContext ctx, Func<ActionContext, Task> run, string vsFallback)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bool fromHub = ctx?.Get<RefactorContext>(RefactorKeys.Context) != null;
            if (!fromHub)
            {
                var view = EditorServices.ActiveView();
                bool cpp = view != null && EditorServices.IsCpp(view.TextBuffer);
                if (!cpp || !RefactoringFeature.IsActive)
                {
                    bool ok = vsFallback != null && VsEditor.TryExecute(vsFallback);
                    L.Info("refactor.batch2.fallback", null, ("action", id), ("cpp", cpp), ("active", RefactoringFeature.IsActive), ("vsCommand", vsFallback), ("ok", ok));
                    if (!ok) VsStatusBar.SetText(cpp ? RefactorStrings.NotActive : NavStrings.NoActiveCppEditor);
                    return;
                }
            }
            await run(ctx);
        }

        // ---------------------------------------------------------------- context helpers (used by RefactoringFeature.BuildContextAsync)

        /// <summary>The single-span stream selection of <paramref name="view"/> in <paramref name="snapshot"/>, or (-1, -1).</summary>
        public static (int Start, int End) Selection(ITextView view, ITextSnapshot snapshot)
        {
            var sel = view.Selection;
            if (sel == null || sel.IsEmpty || sel.Mode != TextSelectionMode.Stream || sel.SelectedSpans.Count != 1) return (-1, -1);
            var span = sel.SelectedSpans[0];
            if (span.Snapshot != snapshot || span.Length == 0) return (-1, -1);
            return (span.Start.Position, span.End.Position);
        }

        /// <summary>Adds the selection (and the locals Extract Method needs) to the engine request.</summary>
        public static void Prepare(RefactoringContextRequest request, ITextSnapshot snapshot, (int Start, int End) selection)
        {
            if (selection.Start < 0) return;
            var s = snapshot.GetLineFromPosition(selection.Start);
            var e = snapshot.GetLineFromPosition(selection.End);
            request.SelectionStartLine = s.LineNumber;
            request.SelectionStartColumn = selection.Start - s.Start.Position;
            request.SelectionEndLine = e.LineNumber;
            request.SelectionEndColumn = selection.End - e.Start.Position;
            request.IncludeLocals = true;
        }

        /// <summary>Caret, selection and lazy text of the snapshot the context belongs to.</summary>
        public static void Attach(RefactorContext rc, ITextView view, ITextSnapshot snapshot, (int Start, int End) selection)
        {
            if (rc == null) return;
            var caret = view.Caret.Position.BufferPosition;
            rc.CaretOffset = caret.Snapshot == snapshot ? caret.Position : -1;
            rc.SelectionStart = selection.Start;
            rc.SelectionEnd = selection.End;
            rc.TextSource = snapshot.GetText;
        }

        /// <summary>
        /// The view and context an action runs on (<see cref="RefactoringFeature.ResolveAsync"/>), asked again with extra
        /// engine data when <paramref name="configure"/> is given (override chain, locals).
        /// </summary>
        public static async Task<(IWpfTextView View, RefactorContext Context)> ResolveAsync(ActionContext ctx, Action<RefactoringContextRequest> configure = null)
        {
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx);
            if (rc == null || configure == null) return (view, rc);
            var again = await RefactoringFeature.BuildContextAsync(view, false, ctx?.CancellationToken ?? CancellationToken.None, configure);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return (view, again?.Engine == null ? rc : again);
        }

        /// <summary>Project file that may be edited: not engine / third party, not generated (Intermediate, *.generated.h).</summary>
        public static bool IsEditablePath(string path)
        {
            if (string.IsNullOrEmpty(path) || !RefactoringFeature.IsProjectPath(path)) return false;
            if (path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gen.cpp", StringComparison.OrdinalIgnoreCase)) return false;
            return path.IndexOf("\\Intermediate\\", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf("/Intermediate/", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>The header / source counterpart of <paramref name="path"/> (first match), or null.</summary>
        public static async Task<string> CounterpartAsync(string path, bool wantHeader)
        {
            var package = HostPackage.Instance;
            if (package == null) return null;
            var candidates = await RelatedNavigator.CounterpartFilesAsync(package, path, package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            foreach (var c in candidates)
                if (Core.Unreal.RelatedNames.IsHeader(c) == wantHeader && System.IO.File.Exists(c)) return c;
            return null;
        }
    }
}
