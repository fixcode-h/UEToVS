using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Unreal;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Refactoring
{
    /// <summary>
    /// Implement Virtual / Override: the engine lists the virtual functions of the class's bases (engine classes included,
    /// BlueprintNativeEvent → Name_Implementation) that are not overridden yet; the user checks some in a dialog; the
    /// <c>override</c> declarations go into the class body (public section) and the implementations calling
    /// <c>Super::Name(args)</c> into the paired .cpp after the class's other definitions. One undo step for both files.
    /// </summary>
    internal static class ImplementVirtualFeature
    {
        private static readonly Logger L = Log.For("Refactor");

        public static async Task ExecuteAsync(ActionContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var cls = rc.Engine.Class;
            if (cls?.Symbol?.Location?.FilePath == null)
            {
                VsStatusBar.SetText(RefactorStrings.NoClassAtCaret);
                return;
            }
            string header = cls.Symbol.Location.FilePath;
            if (!RefactoringFeature.IsProjectPath(header))
            {
                VsStatusBar.SetText(RefactorStrings.NotProjectFile);
                return;
            }
            var engine = HostPackage.Instance?.Engine;
            if (engine == null || !engine.IsConnected) return;
            var caret = await Editor.EditorServices.CaretRequestAsync(view);
            var r = await engine.GetOverridableMethodsAsync(new OverridableMethodsRequest
            {
                Caret = string.Equals(rc.FilePath, header, StringComparison.OrdinalIgnoreCase) ? caret : null,
                TypeName = string.Equals(rc.FilePath, header, StringComparison.OrdinalIgnoreCase) ? null : cls.Symbol.Name,
                Container = cls.Symbol.Container,
            }, ctx.CancellationToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!r.Ok)
            {
                VsStatusBar.SetText(NavStrings.EngineUnavailable);
                return;
            }
            var methods = r.Value.Methods ?? new List<FunctionSignature>();
            string className = cls.Symbol.Name;
            if (methods.Count == 0)
            {
                VsStatusBar.SetText(RefactorStrings.NoOverridable(className));
                L.Info("refactor.implementVirtual", null, ("class", className), ("offered", 0), ("ms", sw.ElapsedMilliseconds));
                return;
            }

            // dialog: grouped by declaring base, nearest first
            var rows = new List<CheckRow<FunctionSignature>>();
            foreach (var g in methods.GroupBy(m => m.DeclaringType ?? string.Empty))
            {
                rows.Add(new CheckRow<FunctionSignature> { Header = g.Key, HeaderCount = g.Count() });
                rows.AddRange(g.Select(m => new CheckRow<FunctionSignature> { Item = m, Checked = false }));
            }
            var dialog = new CheckListDialog<FunctionSignature>(() => RefactorStrings.OverrideDialogTitle(className), () => RefactorStrings.OverrideButton,
                Present, m => m.Name, new System.Windows.Size(720, 560), "dialog.implementVirtual")
            {
                Description = RefactorStrings.OverrideDialogDescription,
            };
            var picked = dialog.ShowModal(rows, VsMainWindow.Handle);
            if (picked == null || picked.Count == 0)
            {
                L.Info("refactor.implementVirtual", null, ("class", className), ("offered", methods.Count), ("picked", 0), ("canceled", true), ("ms", sw.ElapsedMilliseconds));
                return;
            }

            string headerText = RefactorEdits.ReadText(header);
            if (headerText == null)
            {
                VsStatusBar.SetText(RefactorStrings.EditFailed);
                return;
            }
            string hnl = CppTextScan.NewlineOf(headerText);
            var decls = picked.Select(m => SignatureText.OverrideDeclaration(m, string.Empty, hnl)).ToList();
            var declPoint = CodePlacement.Declaration(headerText, className, cls.Symbol.Location.Line, null, AccessKind.Public, decls);
            if (declPoint == null)
            {
                VsStatusBar.SetText(RefactorStrings.ClassBodyNotFound(className));
                return;
            }
            var edits = new List<TextEdit> { TextEdit.Insert(header, declPoint.Line, declPoint.Text) };

            string cpp = null;
            if (RelatedNames.IsHeader(header))
            {
                var candidates = await RelatedNavigator.CounterpartFilesAsync(HostPackage.Instance, header, HostPackage.Instance.DisposalToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                cpp = candidates.FirstOrDefault(c => !RelatedNames.IsHeader(c));
            }
            else cpp = header;
            InsertPoint implPoint = null;
            if (cpp != null)
            {
                string cppText = RefactorEdits.ReadText(cpp);
                if (cppText != null)
                {
                    string nl = CppTextScan.NewlineOf(cppText);
                    string block = string.Join(nl, picked.Select(m => SignatureText.OverrideDefinition(m, className, nl)));
                    // both edits are planned on the original texts; in the same file the definitions land below the class
                    implPoint = CodePlacement.Implementation(cppText, className, null, block);
                    edits.Add(TextEdit.Insert(cpp, implPoint.Line, implPoint.Text));
                }
            }
            var result = RefactorEdits.Apply("UEToVS: " + RefactorStrings.CmdImplementVirtual, edits);
            bool ok = result.Ok && result.Applied > 0;
            L.Info("refactor.implementVirtual", null, ("class", className), ("offered", methods.Count), ("picked", picked.Count),
                ("names", string.Join(",", picked.Select(m => m.Name))), ("header", Path.GetFileName(header)), ("cpp", cpp == null ? null : Path.GetFileName(cpp)),
                ("declReason", declPoint.Reason), ("implReason", implPoint?.Reason), ("filesChanged", result.Files), ("ok", ok), ("ms", sw.ElapsedMilliseconds));
            if (!ok)
            {
                VsStatusBar.SetText(RefactorStrings.EditFailed);
                return;
            }
            if (cpp == null) VsStatusBar.SetText(RefactorStrings.NoCounterpartFile);
            else VsStatusBar.SetText(RefactorStrings.Overridden(picked.Count, className));
        }

        private static void Present(FunctionSignature m, UEToVS.UI.Molecules.RowData d)
        {
            d.Primary = SignatureText.Display(m);
            d.Secondary = m.DeclaringType;
            d.Right = m.Location == null ? null : Path.GetFileName(m.Location.FilePath) + ":" + (m.Location.Line + 1);
            if ((m.Modifiers & FunctionModifiers.Pure) != 0) d.AddBadge("= 0", BadgeKind.Neutral);
            if (m.Name.EndsWith("_Implementation", StringComparison.Ordinal)) d.AddBadge("BlueprintNativeEvent", BadgeKind.UFunction);
            if (!RenamePlanner.IsProjectOrigin(m.Origin)) d.AddBadge("Engine", BadgeKind.Neutral);
            d.ToolTip = m.Location?.FilePath;
        }
    }
}
