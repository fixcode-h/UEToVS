using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Shell
{
    /// <summary>
    /// Binds VSCT commands to actions. BeforeQueryStatus sets the localized text (VSCT holds the English default and the
    /// TextChanges flag) and enables the command from <see cref="IAction.IsAvailable"/>; Invoke dispatches to the action
    /// hub. Both are wrapped (exceptions logged; UI-thread time measured against 16 ms).
    /// </summary>
    internal sealed class CommandRouter
    {
        private static readonly Logger L = Log.For("Commands");
        private readonly HostPackage _package;
        private readonly ActionRegistry _registry;

        public CommandRouter(HostPackage package, ActionRegistry registry)
        {
            _package = package;
            _registry = registry;
        }

        /// <summary>command id → (action id, localized text).</summary>
        public static IReadOnlyList<(int CommandId, string ActionId, Func<string> Text)> Map { get; } = new (int, string, Func<string>)[]
        {
            (CommandIds.OpenLogFolder, ActionIds.OpenLogFolder, () => Strings.CmdOpenLogFolder),
            (CommandIds.OpenCurrentLog, ActionIds.OpenCurrentLog, () => Strings.CmdOpenCurrentLog),
            (CommandIds.CreateDiagnostics, ActionIds.CreateDiagnostics, () => Strings.CmdCreateDiagnostics),
            (CommandIds.ShowPerformancePanel, ActionIds.ShowPerformancePanel, () => Strings.CmdPerformancePanel),
            (CommandIds.RestartEngine, ActionIds.RestartEngine, () => Strings.CmdRestartEngine),
            (CommandIds.TakeOverIntelliSense, ActionIds.TakeOverIntelliSense, () => Strings.CmdTakeOverIntelliSense),
            (CommandIds.RestoreIntelliSense, ActionIds.RestoreIntelliSense, () => Strings.CmdRestoreIntelliSense),
            (CommandIds.ShowComponentGallery, ActionIds.ShowComponentGallery, () => Strings.CmdComponentGallery),
            (CommandIds.Settings, ActionIds.Settings, () => Strings.CmdSettings),
            (CommandIds.FindAction, ActionIds.FindAction, () => Strings.CmdFindAction),
        };

        public void Register(OleMenuCommandService mcs)
        {
            foreach (var (cmdId, actionId, text) in Map)
            {
                var cmd = new OleMenuCommand((s, e) => Invoke(actionId), new CommandID(PackageGuids.CommandSet, cmdId));
                cmd.BeforeQueryStatus += (s, e) => Safe.Execute(L, "command.queryStatus", () =>
                {
                    var c = (OleMenuCommand)s;
                    c.Text = text();
                    var action = _registry.Get(actionId);
                    c.Enabled = action != null && action.IsAvailable(_package.CreateActionContext(ActionSource.Menu));
                    c.Visible = true;
                }, ("action", actionId));
                mcs.AddCommand(cmd);
            }
            L.Info("commands.registered", null, ("count", Map.Count));
        }

        private void Invoke(string actionId)
        {
            Safe.Execute(L, "command.invoke", () =>
            {
                L.Info("command.invoked", null, ("action", actionId));
                _package.ExecuteAction(actionId, ActionSource.Menu);
            }, ("action", actionId));
        }
    }
}
