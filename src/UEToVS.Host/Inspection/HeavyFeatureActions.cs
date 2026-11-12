using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Inspection
{
    /// <summary>
    /// v0.4.5 heavy features: whole-project inspection and the engine upgrade assistant (commands 0x01A0–0x01AF, Find
    /// Action, settings window buttons). Each is behind its plugin switch (Settings &gt; Inspections): switched off the
    /// actions are unavailable (greyed commands), and a direct call only shows a hint; nothing runs in the background.
    /// </summary>
    internal static class HeavyFeatureActions
    {
        private static readonly Logger L = Log.For("Heavy");

        public static void Register(ActionRegistry registry, HostPackage package)
        {
            registry.Register(new DelegateAction(ActionIds.RunProjectInspection, HeavyFeatureStrings.CmdRunInspection, ActionCategory.Unreal,
                ctx => ProjectInspectionFeature.RunAsync(package), ctx => ctx.IsUnrealSolution && InspectionEnabled(package), null, HeavyFeatureStrings.KwInspection));
            registry.Register(new DelegateAction(ActionIds.ShowInspectionResults, HeavyFeatureStrings.CmdShowInspection, ActionCategory.Unreal,
                ctx => ProjectInspectionFeature.ShowResultsAsync(package), ctx => ctx.IsUnrealSolution && InspectionEnabled(package), null, HeavyFeatureStrings.KwInspection));
            registry.Register(new DelegateAction(ActionIds.RunUpgradeAssistant, HeavyFeatureStrings.CmdRunUpgrade, ActionCategory.Unreal,
                ctx => UpgradeAssistantFeature.RunAsync(package), ctx => ctx.IsUnrealSolution && UpgradeEnabled(package), null, HeavyFeatureStrings.KwUpgrade));
            registry.Register(new DelegateAction(ActionIds.ShowUpgradeResults, HeavyFeatureStrings.CmdShowUpgrade, ActionCategory.Unreal,
                ctx => UpgradeAssistantFeature.ShowResultsAsync(package), ctx => ctx.IsUnrealSolution && UpgradeEnabled(package), null, HeavyFeatureStrings.KwUpgrade));
            registry.Register(new DelegateAction(ActionIds.CancelHeavyJob, HeavyFeatureStrings.CmdCancelHeavyJob, ActionCategory.Unreal,
                ctx => CancelAsync(package), ctx => ctx.IsUnrealSolution && (InspectionEnabled(package) || UpgradeEnabled(package)), null, HeavyFeatureStrings.KwCancel));
        }

        internal static bool InspectionEnabled(HostPackage package) => package.Settings?.Effective.ProjectInspectionEnabled ?? true;

        internal static bool UpgradeEnabled(HostPackage package) => package.Settings?.Effective.UpgradeAssistantEnabled ?? true;

        private static async Task CancelAsync(HostPackage package)
        {
            var engine = package.Engine;
            if (engine == null)
            {
                VsStatusBar.SetText(HeavyFeatureStrings.EngineUnavailable);
                return;
            }
            bool any = false;
            if (InspectionEnabled(package))
            {
                var r = await engine.ProjectInspectionAsync(new ProjectInspectionRequest { Action = HeavyJobAction.Cancel }, package.DisposalToken);
                any |= r.Value != null && r.Value.State == HeavyJobState.Running; // the run stops at its next file
            }
            if (UpgradeEnabled(package))
            {
                var r = await engine.DeprecationScanAsync(new DeprecationScanRequest { Action = HeavyJobAction.Cancel }, package.DisposalToken);
                any |= r.Value != null && r.Value.State == HeavyJobState.Running; // the run stops at its next file
            }
            L.Info("heavy.cancel", null, ("cancelled", any));
            if (!any) VsStatusBar.SetText(HeavyFeatureStrings.NothingRunning);
        }
    }
}
