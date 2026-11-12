using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Shell
{
    /// <summary>
    /// Binds VSCT commands to actions. BeforeQueryStatus sets the localized text (VSCT holds the English default and the
    /// TextChanges flag) and enables the command from <see cref="IAction.IsAvailable"/>; Invoke dispatches to the action
    /// hub. Both are wrapped (exceptions logged; UI-thread time measured against 16 ms).
    /// Binds the host commands of <see cref="CommandCatalog"/> (Owner = Host), the M2 navigation map and the toolbar
    /// buttons; M1 binds its commands through <see cref="RegisterMap"/>. Toolbar buttons take their visibility from the settings.
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

        /// <summary>Localized text of the commands that already had one (others use the catalog titles).</summary>
        private static readonly Dictionary<int, Func<string>> Texts = new Dictionary<int, Func<string>>
        {
            [CommandIds.OpenLogFolder] = () => Strings.CmdOpenLogFolder,
            [CommandIds.OpenCurrentLog] = () => Strings.CmdOpenCurrentLog,
            [CommandIds.CreateDiagnostics] = () => Strings.CmdCreateDiagnostics,
            [CommandIds.ShowPerformancePanel] = () => Strings.CmdPerformancePanel,
            [CommandIds.RestartEngine] = () => Strings.CmdRestartEngine,
            [CommandIds.ShowComponentGallery] = () => Strings.CmdComponentGallery,
            [CommandIds.Settings] = () => Strings.CmdSettings,
            [CommandIds.FindAction] = () => Strings.CmdFindAction,
        };

        public void Register(OleMenuCommandService mcs)
        {
            int count = 0;
            foreach (var c in CommandCatalog.All)
            {
                if (c.Owner != CommandOwner.Host || c.Id == CommandIds.ToggleIntelliSense) continue;
                var info = c;
                Texts.TryGetValue(c.Id, out var text);
                Bind(mcs, c.Id, () => info.ActionId, text ?? (() => Loc.T(info.TitleZh, info.TitleEn)));
                count++;
            }

            // One IntelliSense command that toggles: "接管" while VS's own IntelliSense is active, "恢复" once UEToVS has
            // taken it over (a backup of the user's originals exists). Find Action still lists both actions.
            Bind(mcs, CommandIds.ToggleIntelliSense,
                () => IsIntelliSenseTakenOver() ? ActionIds.RestoreIntelliSense : ActionIds.TakeOverIntelliSense,
                () => IsIntelliSenseTakenOver() ? Strings.CmdRestoreIntelliSense : Strings.CmdTakeOverIntelliSense);
            count++;

            // M2 navigation commands (0x0200–0x02FF); M1 binds its own through RegisterMap.
            foreach (var (cmdId, actionId, text) in Navigation.NavigationActions.Map)
            {
                Bind(mcs, cmdId, () => actionId, text);
                count++;
            }

            foreach (var b in ToolbarButtons.All)
            {
                var button = b;
                Bind(mcs, b.CommandId, () => button.ActionId, () => Loc.T(button.TitleZh, button.TitleEn), false,
                     () => ToolbarButtons.IsVisible(button.Key, _package.Settings?.Effective.ShowToolbar ?? true,
                                                    ToolbarButtons.ParseHidden(_package.Settings?.Effective.HiddenToolbarButtons)));
                count++;
            }
            L.Info("commands.registered", null, ("count", count));
        }

        /// <summary>Taken over = UEToVS holds a backup of VS's C++ IntelliSense settings, i.e. it changed them and can restore.</summary>
        private bool IsIntelliSenseTakenOver() => _package.IntelliSense?.BackupExists == true;

        private void Bind(OleMenuCommandService mcs, int cmdId, Func<string> actionId, Func<string> text, bool hideWhenMissing = false, Func<bool> visible = null)
        {
            var cmd = new OleMenuCommand((s, e) => Invoke(actionId()), new CommandID(PackageGuids.CommandSet, cmdId));
            cmd.BeforeQueryStatus += (s, e) => Safe.Execute(L, "command.queryStatus", () =>
            {
                var c = (OleMenuCommand)s;
                c.Text = text();
                var action = _registry.Get(actionId());
                c.Enabled = action != null && action.IsAvailable(_package.CreateActionContext(ActionSource.Menu));
                c.Visible = (visible?.Invoke() ?? true) && (!hideWhenMissing || action != null);
            }, ("command", cmdId));
            mcs.AddCommand(cmd);
        }

        /// <summary>Binds additional command → action maps (feature modules keep their own id tables, e.g. M1Commands).</summary>
        public void RegisterMap(OleMenuCommandService mcs, IReadOnlyList<(int CommandId, string ActionId, Func<string> Text)> map)
        {
            foreach (var (cmdId, actionId, text) in map)
                Bind(mcs, cmdId, () => actionId, text);
            L.Info("commands.registered", null, ("count", map.Count), ("first", map.Count > 0 ? map[0].CommandId : 0));
        }

        private void Invoke(string actionId)
        {
            Safe.Execute(L, "command.invoked", () =>
            {
                L.Info("command.invoked", null, ("action", actionId));
                if (_registry.Get(actionId) == null)
                {
                    // Reserved command whose module is not there yet (key binding pressed).
                    L.Info("command.unavailable", "Command has no registered action yet", ("action", actionId));
                    VsStatusBar.SetText(SettingsText.ActionUnavailable);
                    return;
                }
                _package.ExecuteAction(actionId, ActionSource.Menu);
            }, ("action", actionId));
        }
    }
}
