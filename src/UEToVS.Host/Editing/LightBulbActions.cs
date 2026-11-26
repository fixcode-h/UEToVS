using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.IntelliSense;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Unreal;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Navigation;
using UEToVS.Host.Refactoring;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;
using UEToVS.Host.Unreal;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// v0.5.12 灯泡菜单：UE 规则波浪线的修复（UE0002–UE0006）、跳到 _Implementation、打开 X.generated.h，以及把生成 / 翻译 /
    /// 包围这些原本只能走命令的操作按光标上下文放进灯泡和 Alt+Enter（provider 在 Logic/Editing，纯逻辑）。
    /// </summary>
    internal static class LightBulbActions
    {
        private static readonly Logger L = Log.For("Editing");

        public static void Register(ActionRegistry registry)
        {
            // 这几个只在灯泡 / Alt+Enter 里带着参数出现，Find Action 里不列（没有参数时不可用）
            registry.Register(new DelegateAction(UeRuleFixIds.Fix, EditorFeatureStrings.GroupFix, ActionCategory.Fix,
                FixAsync, ctx => ctx.Get<UeRuleFix>(RefactorKeys.Argument) != null, null));
            registry.Register(new DelegateAction(LightBulbActionIds.GoToLocation, LightBulbStrings.GroupNavigate, ActionCategory.Navigation,
                GoToAsync, ctx => ctx.Get<SourceLocation>(RefactorKeys.Argument) != null, null));
            registry.Register(new DelegateAction(LightBulbActionIds.OpenGeneratedHeader, LightBulbStrings.GroupNavigate, ActionCategory.Navigation,
                OpenGeneratedHeaderAsync, ctx => ctx.Get<string>(RefactorKeys.Argument) != null, null));

            RefactoringFeature.Hub.Register(new UeRuleFixProvider());
            RefactoringFeature.Hub.Register(new UhtTypeFixProvider()); // v0.9.12 UE0009 / UE0010
            RefactoringFeature.Hub.Register(new EngineDiagnosticFixProvider()); // v0.11.0 UE0017–UE0023
            SpecifierEditorFeature.Register(registry); // v0.9.12 说明符编辑器
            foreach (var p in LightBulbProviders.Create(surroundEnabled: () => IntelliSenseContext.Settings?.SurroundWithEnabled != false))
                RefactoringFeature.Hub.Register(p);
            L.Debug("editor.lightbulb.registered", null, ("actions", 3), ("providers", RefactoringFeature.Hub.Count));
        }

        // ---------------------------------------------------------------- UE 规则修复

        private static async Task FixAsync(ActionContext ctx)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var fix = ctx.Get<UeRuleFix>(RefactorKeys.Argument);
            var view = ctx.Get<IWpfTextView>(RefactorKeys.View) ?? EditorServices.ActiveView();
            if (fix == null || view == null || view.IsClosed) return;
            string file = EditorServices.FilePath(view.TextBuffer);
            string problem = null;
            List<TextEdit> edits = null;
            string title = null;
            try
            {
                string text = view.TextBuffer.CurrentSnapshot.GetText();
                // v0.11.0 引擎算好的修复（UE0017–UE0023）
                if (EngineDiagnosticFixProvider.Codes.Contains(fix.Code ?? string.Empty) && fix.Variant != null)
                {
                    title = EngineDiagnosticFixPlanner.Title(fix.Variant, fix.Target);
                    if (fix.Variant == EngineDiagnosticFixPlanner.AddInclude)
                    {
                        if (IncludePlanner.AlreadyIncluded(text, fix.Target)) { VsStatusBar.SetText(RefactorStrings.IncludeAlreadyThere(fix.Target)); return; }
                        var point = IncludePlanner.Plan(text, fix.Target);
                        int applied = RefactorEdits.ApplyToBuffer(view.TextBuffer, new[] { TextEdit.Insert(file, point.Line, point.Text) }, out _);
                        edits = new List<TextEdit>();
                        if (applied == 0) { problem = "stale"; VsStatusBar.SetText(LightBulbStrings.FixProblem(problem)); return; }
                        VsStatusBar.SetText(RefactorStrings.IncludeAdded(fix.Target));
                        // 头文件所在模块还不是依赖时，和“添加 include”一样提示加 Build.cs 依赖
                        ModuleDependencyFixer.OnIncludeCommitted(HostPackage.Instance, file, fix.Target, point.Line);
                        return;
                    }
                    edits = EngineDiagnosticFixPlanner.Plan(file, text, fix, out problem);
                }
                else switch (fix.Code)
                {
                    case CodeDiagnosticCodes.MissingModuleDependency:
                        title = LightBulbStrings.AddModuleDependency(fix.Arg(1), fix.Arg(2), fix.IsPublic);
                        ModuleDependencyFixer.Apply(new List<MissingDependency>
                        {
                            new MissingDependency { Include = fix.Arg(0), Line = fix.Line, Module = fix.Arg(1), OwnerModule = fix.Arg(2), OwnerBuildCsPath = fix.Arg(3) },
                        }, fix.IsPublic);
                        return; // Apply 自己写状态栏和日志
                    case CodeDiagnosticCodes.MissingGeneratedBody:
                        title = LightBulbStrings.InsertGeneratedBody(fix.Arg(1) ?? fix.Arg(0));
                        edits = UeRuleFixPlanner.GeneratedBody(file, text, fix.Line, fix.Column, out problem);
                        break;
                    case CodeDiagnosticCodes.GeneratedFunctionBody:
                        title = LightBulbStrings.RenameToImplementation(fix.Arg(0));
                        edits = UeRuleFixPlanner.RenameToImplementation(file, text, fix.Line, fix.Column, fix.Arg(0), fix.Arg(1), out problem);
                        break;
                    case CodeDiagnosticCodes.UnregisteredReplicatedProperty:
                        title = LightBulbStrings.RegisterReplicated(fix.Properties.Count > 0 ? fix.Properties : new List<string> { fix.Arg(0) });
                        edits = UeRuleFixPlanner.RegisterReplicated(file, text, fix.Line, fix.Column, fix.Arg(1),
                            fix.Properties.Count > 0 ? fix.Properties : new List<string> { fix.Arg(0) }, out problem);
                        break;
                    case CodeDiagnosticCodes.MissingRepNotify:
                        title = LightBulbStrings.CreateRepNotify(fix.Arg(1));
                        // 实现放到对应的 .cpp（打开着的用未保存的内容）；找不到就只加声明
                        var package = HostPackage.Instance;
                        var counterparts = package == null ? new List<string>() : await RelatedNavigator.CounterpartFilesAsync(package, file, package.DisposalToken);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        string cpp = counterparts.FirstOrDefault(p => p.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase));
                        string cppText = cpp == null ? null : RefactorEdits.ReadText(cpp);
                        if (view.TextBuffer.CurrentSnapshot.GetText() != text) { problem = "stale"; break; }
                        edits = UeRuleFixPlanner.RepNotify(file, text, fix.Line, fix.Column, fix.Arg(1), fix.Arg(2), cppText == null ? null : cpp, cppText, out problem);
                        if (edits != null && cppText == null) title = null; // 状态栏改为提示只加了声明
                        break;
                    case CodeDiagnosticCodes.UnsupportedPropertyType: // v0.9.12 UE0009 / UE0010
                    case CodeDiagnosticCodes.UnsupportedParameterType:
                        title = UhtTypeFixTitle(fix);
                        edits = UhtTypeFixPlanner.Plan(file, text, fix, out problem);
                        break;
                    default:
                        problem = "unknown-rule";
                        break;
                }
                if (edits == null || edits.Count == 0)
                {
                    VsStatusBar.SetText(LightBulbStrings.FixProblem(problem));
                    return;
                }
                if (view.TextBuffer.IsReadOnly(0) || EngineReadOnlyState.For(view.TextBuffer).Locked) { problem = "readonly"; VsStatusBar.SetText(LightBulbStrings.FixProblem(problem)); return; }
                var result = RefactorEdits.Apply(title ?? LightBulbStrings.CreateRepNotify(fix.Arg(1)), edits);
                if (result.Applied == 0) { problem = "stale"; VsStatusBar.SetText(LightBulbStrings.FixProblem(problem)); return; }
                VsStatusBar.SetText(title != null ? LightBulbStrings.FixApplied(title) : LightBulbStrings.NoSourceForRepNotify(fix.Arg(1)));
            }
            catch (Exception ex)
            {
                problem = "failed";
                L.Warn("editor.ueRuleFix.failed", "Applying a UE rule fix failed", ex, ("code", fix.Code), ("file", Path.GetFileName(file ?? string.Empty)));
            }
            finally
            {
                L.Info("editor.ueRuleFix", null, ("code", fix.Code), ("args", string.Join("|", fix.Args ?? new List<string>())), ("variant", fix.Variant), ("properties", fix.Properties?.Count ?? 0),
                    ("public", fix.IsPublic), ("edits", edits?.Count ?? 0), ("problem", problem), ("file", Path.GetFileName(file ?? string.Empty)), ("line", fix.Line));
            }
        }

        /// <summary>状态栏里“已修复：…”用的标题，和灯泡里的一样。</summary>
        private static string UhtTypeFixTitle(UeRuleFix fix)
        {
            string type = fix.Arg(1) ?? string.Empty;
            switch (fix.Variant)
            {
                case UhtTypeFixVariants.ByValue: return CodingAssistStrings.FixByValue(fix.Arg(0), UhtTypeFixPlanner.Pointee(type));
                case UhtTypeFixVariants.RemoveUProperty: return CodingAssistStrings.FixRemoveUProperty(fix.Arg(0));
                case UhtTypeFixVariants.ConstRef: return CodingAssistStrings.FixToReference(fix.Arg(0), "const " + UhtTypeFixPlanner.Pointee(type) + "&");
                case UhtTypeFixVariants.Ref: return CodingAssistStrings.FixToReference(fix.Arg(0), UhtTypeFixPlanner.Pointee(type) + "&");
                case UhtTypeFixVariants.BlueprintNumber: return CodingAssistStrings.FixNumber(fix.Arg(0), type.TrimEnd('&'), fix.Target);
                case UhtTypeFixVariants.RemoveUFunction: return CodingAssistStrings.FixRemoveUFunction(fix.Arg(2));
                default: return fix.Code;
            }
        }

        // ---------------------------------------------------------------- 导航

        private static async Task GoToAsync(ActionContext ctx)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var loc = ctx.Get<SourceLocation>(RefactorKeys.Argument);
            if (loc == null) return;
            bool ok = NavigationService.Instance.JumpTo(loc, JumpReasons.Related);
            L.Info("editor.lightbulb.goTo", null, ("file", Path.GetFileName(loc.FilePath ?? string.Empty)), ("line", loc.Line), ("ok", ok));
        }

        private static async Task OpenGeneratedHeaderAsync(ActionContext ctx)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string name = ctx.Get<string>(RefactorKeys.Argument);
            var package = HostPackage.Instance;
            if (string.IsNullOrEmpty(name) || package == null) return;
            var engine = package.Engine;
            IReadOnlyList<string> files = engine?.IsConnected == true
                ? await EngineQueries.FindFilesByNameAsync(engine, name, package.DisposalToken)
                : (IReadOnlyList<string>)new string[0];
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // 同名的 generated.h 可能有多份（Editor / Game 目标各一份）：取最近写过的那份，就是上次编译生成的
            string path = files.Where(File.Exists).OrderByDescending(f => SafeWriteTime(f)).FirstOrDefault();
            L.Info("editor.lightbulb.generatedHeader", null, ("name", name), ("candidates", files.Count), ("found", path != null));
            if (path == null)
            {
                VsStatusBar.SetText(LightBulbStrings.GeneratedHeaderNotFound(name));
                return;
            }
            NavigationService.Instance.JumpTo(path, -1, 0, JumpReasons.Related);
        }

        private static DateTime SafeWriteTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception) { return DateTime.MinValue; }
        }
    }
}
