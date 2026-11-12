using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Unreal;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Shell;
using UEToVS.Host.Unreal;

namespace UEToVS.Host.Refactoring
{
    /// <summary>
    /// Add #include for the type at the caret (UE style path relative to the module's Public / Classes folder, quotes),
    /// inserted in the include group before the .generated.h; then the module dependency check of
    /// <see cref="ModuleDependencyFixer"/> offers to add the header's module to Build.cs when it is missing.
    /// </summary>
    internal static class AddIncludeFeature
    {
        private static readonly Logger L = Log.For("Refactor");

        public static async Task ExecuteAsync(ActionContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var (view, rc) = await RefactoringFeature.ResolveAsync(ctx, checkIncludes: true);
            if (rc == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var type = rc.Engine.Type;
            if (type == null || string.IsNullOrEmpty(type.HeaderPath) || type.Include == IncludeState.SameFile)
            {
                VsStatusBar.SetText(RefactorStrings.NoActions);
                return;
            }
            if (!rc.IsProjectFile)
            {
                VsStatusBar.SetText(RefactorStrings.NotProjectFile);
                return;
            }
            // module-relative path: relative to the module folder (Build.cs) when the header has no Public/Classes folder
            string buildCs = ModuleLocator.ForFileSystem().FindBuildCs(type.HeaderPath);
            string include = ctx.Get<string>(RefactorKeys.Argument) ?? IncludePlanner.UeIncludePath(type.HeaderPath, buildCs == null ? null : Path.GetDirectoryName(buildCs));
            string text = view.TextBuffer.CurrentSnapshot.GetText();
            if (IncludePlanner.AlreadyIncluded(text, include))
            {
                VsStatusBar.SetText(RefactorStrings.IncludeAlreadyThere(include));
                return;
            }
            var point = IncludePlanner.Plan(text, include);
            int applied = RefactorEdits.ApplyToBuffer(view.TextBuffer, new[] { TextEdit.Insert(rc.FilePath, point.Line, point.Text) }, out _);
            L.Info("refactor.addInclude", null, ("type", type.Symbol?.Name), ("include", include), ("state", type.Include.ToString()), ("via", type.IncludedVia),
                ("line", point.Line), ("reason", point.Reason), ("filesChanged", applied > 0 ? 1 : 0), ("ms", sw.ElapsedMilliseconds), ("source", ctx.Source.ToString()));
            if (applied == 0)
            {
                VsStatusBar.SetText(RefactorStrings.EditFailed);
                return;
            }
            VsStatusBar.SetText(RefactorStrings.IncludeAdded(include));
            // Build.cs: the header's module must be a dependency of this module (banner with the fix when it is not)
            ModuleDependencyFixer.OnIncludeCommitted(HostPackage.Instance, rc.FilePath, include, point.Line);
        }
    }
}
