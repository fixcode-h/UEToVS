using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Update;
using UEToVS.Host.Update;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Settings
{
    internal sealed partial class SettingsWindow
    {
        /// <summary>
        /// The "检查更新" row: renders <see cref="UpdateService"/>'s process-wide state (current version when idle, the found
        /// version and size, live download progress, readable errors) and follows it while visible, so a check or download
        /// started before the window was reopened still shows its progress and result instead of a stale "正在检查更新…".
        /// The main button changes with the state (检查 → 下载并安装 → 取消 / 重试); "更新说明" opens the release page.
        /// </summary>
        private UIElement BuildUpdateRow(SettingDescriptor d)
        {
            string installed = HostPackage.HostVersion;
            var result = new UiText((string)null, TextVariant.Secondary)
            {
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MaxWidth = 300,
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, TextAlignment = TextAlignment.Right,
            };
            var notes = new UiButton(SettingsText.UpdateReleaseNotes, ButtonKind.Ghost) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var button = new UiButton(SettingsText.UpdateButtonCheck) { MinWidth = SettingsPageLayout.FooterButtonMinWidth, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(button, d.Title(English));

            void Render()
            {
                var s = UpdateService.State(installed);
                result.Text = UpdateView.Text(s);
                string label = UpdateView.ButtonLabel(s);
                button.Content = label;
                System.Windows.Automation.AutomationProperties.SetName(button, label);
                button.IsEnabled = UpdateView.Command(s) != UpdateCommand.None;
                notes.Visibility = s.Latest != null || s.Phase == UpdatePhase.Failed ? Visibility.Visible : Visibility.Collapsed;
            }

            // Progress arrives on a thread-pool thread; coalesce onto the UI thread.
            bool queued = false;
            EventHandler onChanged = (s, e) =>
            {
                if (queued) return;
                queued = true;
                Safe.RunAsync(L, "settings.update.render", async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    queued = false;
                    Render();
                });
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Loaded += (s, e) => Safe.Execute(L, "settings.update.attach", () => { UpdateService.Changed -= onChanged; UpdateService.Changed += onChanged; Render(); });
            panel.Unloaded += (s, e) => Safe.Execute(L, "settings.update.detach", () => UpdateService.Changed -= onChanged);

            button.Click += (s, e) => Safe.RunAsync(L, "settings.action", async () =>
            {
                var command = UpdateView.Command(UpdateService.State(installed));
                L.Info("settings.action.run", null, ("key", d.Key), ("action", d.ActionId), ("command", command.ToString()));
                // The service raises Changed for every step (Checking, progress, result); Render() follows it.
                await UpdateService.RunPrimaryAsync(installed).ConfigureAwait(true);
                Render();
            }, ("action", d.ActionId));
            notes.Click += (s, e) => Safe.Execute(L, "settings.update.releasePage", () => UpdateService.OpenReleasePage(installed));

            panel.Children.Add(result);
            panel.Children.Add(notes);
            panel.Children.Add(button);
            Render();
            return panel;
        }
    }
}
