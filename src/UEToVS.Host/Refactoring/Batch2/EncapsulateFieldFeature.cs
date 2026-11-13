using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Shell;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Refactoring.Batch2
{
    /// <summary>
    /// Encapsulate Field / Generate Getter-Setter (v0.4.5): inline accessors with UE names (bool: IsX / SetX, <c>bX</c> →
    /// <c>SetX</c>) in the class's public section; optionally <c>UFUNCTION(BlueprintGetter / BlueprintSetter)</c> and the
    /// matching <c>BlueprintGetter=… / BlueprintSetter=…</c> on the UPROPERTY. Accessors the class already has are skipped.
    /// </summary>
    internal static class EncapsulateFieldFeature
    {
        private static readonly Logger L = Log.For("Refactor");

        public static async Task ExecuteAsync(ActionContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string text = rc.Text ?? view.TextBuffer.CurrentSnapshot.GetText();
            string problem = null;
            FieldDeclaration f = null;
            if (!rc.IsProjectFile) problem = "engine-base";
            else if (!rc.IsHeader) problem = "no-field";
            else f = EncapsulateFieldPlanner.FieldAt(text, rc.CaretOffset, out problem);
            if (f == null)
            {
                problem = problem ?? "no-field";
                VsStatusBar.SetText(Refactor2Strings.Problem(problem));
                L.Info("refactor.encapsulateField", null, ("problem", problem), ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()));
                return;
            }
            var options = Ask(f, text);
            if (options == null)
            {
                L.Info("refactor.encapsulateField", null, ("field", f.Name), ("canceled", true), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            var plan = EncapsulateFieldPlanner.Plan(f, options, n => EncapsulateFieldPlanner.ClassDeclares(text, f, n));
            var edits = plan.Ok ? EncapsulateFieldPlanner.Edits(rc.FilePath, text, f, plan, out _) : null;
            if (!plan.Ok || edits.Count == 0)
            {
                problem = plan.Problem ?? "no-class-body";
                VsStatusBar.SetText(problem == "exists" ? RefactorStrings.AlreadyExists(plan.GetterName + " / " + plan.SetterName) : Refactor2Strings.Problem(problem));
                L.Info("refactor.encapsulateField", null, ("field", f.Name), ("problem", problem), ("ms", sw.ElapsedMilliseconds));
                return;
            }
            var result = RefactorEdits.Apply("UEToVS: " + Refactor2Strings.EncapsulateTitle(f.Name), edits);
            L.Info("refactor.encapsulateField", null, ("field", f.Name), ("class", f.ClassName), ("generated", string.Join(",", plan.Generated)),
                ("blueprintGetter", options.BlueprintGetter), ("blueprintSetter", options.BlueprintSetter), ("uproperty", f.HasUProperty), ("edits", result.Applied),
                ("skipped", result.Skipped), ("ok", result.Ok), ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()),
                ("file", Path.GetFileName(rc.FilePath)));
            VsStatusBar.SetText(!result.Ok ? RefactorStrings.EditFailed : result.Skipped > 0 ? Refactor2Strings.Skipped(result.Skipped)
                : Refactor2Strings.AccessorsGenerated(string.Join(" / ", plan.Generated)));
        }

        /// <summary>Options dialog with a live preview of the generated lines; null when cancelled.</summary>
        private static EncapsulateOptions Ask(FieldDeclaration f, string text)
        {
            var (getter, setter) = EncapsulateFieldPlanner.Names(f);
            var shell = new DialogShell(() => Refactor2Strings.EncapsulateTitle(f.Name), () => Refactor2Strings.GenerateButton) { VisibleSize = new Size(620, 380), PlacementId = "dialog.encapsulateField" };
            shell.Description = Refactor2Strings.EncapsulateDescription;
            var form = new StackPanel();
            var getBox = new UiCheckBox(Refactor2Strings.GenerateGetter(getter), true) { Margin = new Thickness(0, 4, 0, 0) };
            var setBox = new UiCheckBox(Refactor2Strings.GenerateSetter(setter), !f.IsConst) { Margin = new Thickness(0, 6, 0, 0), IsEnabled = !f.IsConst };
            var bpGet = new UiCheckBox(Refactor2Strings.BlueprintGetter, false) { Margin = new Thickness(0, 10, 0, 0) };
            var bpSet = new UiCheckBox(Refactor2Strings.BlueprintSetter, false) { Margin = new Thickness(0, 6, 0, 0) };
            var preview = new UiText((string)null, TextVariant.Mono) { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
            form.Children.Add(getBox);
            form.Children.Add(setBox);
            if (f.HasUProperty)
            {
                form.Children.Add(bpGet);
                form.Children.Add(bpSet);
            }
            form.Children.Add(preview);
            shell.Form = form;

            EncapsulateOptions Current() => new EncapsulateOptions
            {
                Getter = getBox.IsChecked == true, Setter = setBox.IsChecked == true,
                BlueprintGetter = f.HasUProperty && bpGet.IsChecked == true, BlueprintSetter = f.HasUProperty && bpSet.IsChecked == true,
            };
            void Refresh()
            {
                bpGet.IsEnabled = getBox.IsChecked == true;
                bpSet.IsEnabled = setBox.IsChecked == true;
                var plan = EncapsulateFieldPlanner.Plan(f, Current(), n => EncapsulateFieldPlanner.ClassDeclares(text, f, n));
                preview.Text = plan.Ok ? string.Join(Environment.NewLine, plan.Lines) + (plan.NewSpecifiers != null ? Environment.NewLine + "UPROPERTY(" + plan.NewSpecifiers + ")" : string.Empty)
                    : RefactorStrings.AlreadyExists(getter + " / " + setter);
                shell.IsPrimaryEnabled = plan.Ok;
            }
            foreach (var box in new[] { getBox, setBox, bpGet, bpSet })
            {
                box.Checked += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.encapsulate.option", Refresh);
                box.Unchecked += (s, e) => UEToVS.UI.Theming.Ui.Safe("refactor.encapsulate.option", Refresh);
            }
            Refresh();
            return shell.ShowModal(VsMainWindow.Handle) ? Current() : null;
        }
    }
}
