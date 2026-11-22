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

        /// <summary>
        /// Menu text: the <see cref="CommandCatalog"/> title for every catalog command, so the menu, Find Action and the keymap
        /// page use the same name (the modules' own command texts are only the fallback for commands outside the catalog,
        /// e.g. the bookmark digits and the semantic-colouring commands).
        /// </summary>
        private static Func<string> MenuText(int cmdId, Func<string> fallback)
        {
            var info = cmdId == CommandIds.ToggleIntelliSense ? null : CommandCatalog.ById(cmdId);
            return info != null ? () => Loc.T(info.TitleZh, info.TitleEn) : fallback;
        }

        public void Register(OleMenuCommandService mcs)
        {
            int count = 0;
            foreach (var c in CommandCatalog.All)
            {
                if (c.Owner != CommandOwner.Host || c.Id == CommandIds.ToggleIntelliSense) continue;
                var info = c;
                Bind(mcs, c.Id, () => info.ActionId, MenuText(c.Id, null));
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
                Bind(mcs, cmdId, () => actionId, MenuText(cmdId, text));
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
            foreach (var m in Submenus)
            {
                var menu = m;
                var cmd = new OleMenuCommand((s, e) => { }, new CommandID(PackageGuids.CommandSet, menu.Id));
                cmd.BeforeQueryStatus += (s, e) => Safe.Execute(L, "menu.queryStatus", () => ((OleMenuCommand)s).Text = menu.Text(), ("menu", menu.Id));
                mcs.AddCommand(cmd);
            }
            L.Info("commands.registered", null, ("count", count), ("submenus", Submenus.Length));
        }

        /// <summary>
        /// Extensions &gt; UEToVS submenus (VSCT menuSearch … menuBuild, menuM1Bookmarks and its numbered-bookmark submenus).
        /// They have TextChanges, so the shell asks for their text like for a command; titles are the catalog group names.
        /// </summary>
        private static readonly (int Id, Func<string> Text)[] Submenus =
        {
            (0x1200, () => CommandCatalog.GroupTitle("search", Loc.IsEnglish)),
            (0x1201, () => CommandCatalog.GroupTitle("navigation", Loc.IsEnglish)),
            (0x1202, () => CommandCatalog.GroupTitle("history", Loc.IsEnglish)),
            (0x1100, () => CommandCatalog.GroupTitle("bookmarks", Loc.IsEnglish)),
            (0x1203, () => CommandCatalog.GroupTitle("refactor", Loc.IsEnglish)),
            (0x1204, () => CommandCatalog.GroupTitle("generate", Loc.IsEnglish)),
            (0x1205, () => CommandCatalog.GroupTitle("editing", Loc.IsEnglish)),
            (0x1206, () => CommandCatalog.GroupTitle("unreal", Loc.IsEnglish)),
            (0x1207, () => CommandCatalog.GroupTitle("build", Loc.IsEnglish)),
            (0x120A, () => CommandCatalog.GroupTitle("vcs", Loc.IsEnglish)),
            (0x1003, () => Loc.T("UEToVS 版本管理", "UEToVS Version Control")), // 编辑器右键
            (0x1208, () => Loc.T("设置编号书签", "Set Numbered Bookmark")),
            (0x1209, () => Loc.T("跳到编号书签", "Go to Numbered Bookmark")),
        };

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
                Bind(mcs, cmdId, () => actionId, MenuText(cmdId, text));
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
