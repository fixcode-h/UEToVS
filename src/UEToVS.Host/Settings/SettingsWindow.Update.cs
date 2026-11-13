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
        /// <summary>The update row's right-hand controls plus the release notes shown full-width under it.</summary>
        private sealed class UpdateRowEditor : StackPanel
        {
            public StackPanel Notes { get; } = new StackPanel();
        }

        private bool _updateAutoChecked;

        /// <summary>
        /// The "检查更新" row: renders <see cref="UpdateService"/>'s process-wide state (current version when idle, the found
        /// version and size, live download progress, readable errors) and follows it while visible, so a check or download
        /// started before the window was reopened still shows its progress and result instead of a stale "正在检查更新…".
        /// The main button changes with the state (检查 → 下载并安装 → 取消 / 重试). The latest release's notes are shown
        /// under the row (<see cref="UpdateRowEditor.Notes"/>); opening the page checks once when nothing was checked yet.
        /// "发布页" (open the GitHub release) only appears after a failure, for downloading by hand.
        /// </summary>
        private UIElement BuildUpdateRow(SettingDescriptor d)
        {
            string installed = HostPackage.HostVersion;
            var result = new UiText((string)null, TextVariant.Secondary)
            {
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MaxWidth = 300,
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, TextAlignment = TextAlignment.Right,
            };
            var page = new UiButton(SettingsText.UpdateReleasePage, ButtonKind.Ghost) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var button = new UiButton(SettingsText.UpdateButtonCheck) { MinWidth = SettingsPageLayout.FooterButtonMinWidth, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(button, d.Title(English));
            var panel = new UpdateRowEditor { Orientation = Orientation.Horizontal };
            ReleaseInfo notesShown = null;
            bool notesRendered = false;

            void Render()
            {
                var s = UpdateService.State(installed);
                result.Text = UpdateView.Text(s);
                string label = UpdateView.ButtonLabel(s);
                button.Content = label;
                System.Windows.Automation.AutomationProperties.SetName(button, label);
                button.IsEnabled = UpdateView.Command(s) != UpdateCommand.None;
                page.Visibility = s.Phase == UpdatePhase.Failed ? Visibility.Visible : Visibility.Collapsed;
                if (s.Latest != null && (!notesRendered || !ReferenceEquals(s.Latest, notesShown)))
                {
                    notesShown = s.Latest;
                    notesRendered = true;
                    RenderReleaseNotes(panel.Notes, s.Latest);
                }
                else if (s.Latest == null && !notesRendered)
                {
                    notesRendered = true;
                    RenderReleaseNotes(panel.Notes, null);
                }
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

            panel.Loaded += (s, e) => Safe.Execute(L, "settings.update.attach", () =>
            {
                UpdateService.Changed -= onChanged;
                UpdateService.Changed += onChanged;
                Render();
                // First visit with nothing checked yet: check now so the notes below have something to show.
                if (!_updateAutoChecked && UpdateService.State(installed).Phase == UpdatePhase.Idle)
                {
                    _updateAutoChecked = true;
                    L.Info("settings.update.autoCheck", null);
                    Safe.RunAsync(L, "settings.update.autoCheck", () => UpdateService.CheckAsync(installed));
                }
            });
            panel.Unloaded += (s, e) => Safe.Execute(L, "settings.update.detach", () => UpdateService.Changed -= onChanged);

            button.Click += (s, e) => Safe.RunAsync(L, "settings.action", async () =>
            {
                var command = UpdateView.Command(UpdateService.State(installed));
                L.Info("settings.action.run", null, ("key", d.Key), ("action", d.ActionId), ("command", command.ToString()));
                // The service raises Changed for every step (Checking, progress, result); Render() follows it.
                await UpdateService.RunPrimaryAsync(installed).ConfigureAwait(true);
                Render();
            }, ("action", d.ActionId));
            page.Click += (s, e) => Safe.Execute(L, "settings.update.releasePage", () => UpdateService.OpenReleasePage(installed));

            panel.Children.Add(result);
            panel.Children.Add(page);
            panel.Children.Add(button);
            Render();
            return panel;
        }

        /// <summary>"vX.Y.Z 更新说明" and the release body as plain lines (headings, bullets, text); a hint before any check.</summary>
        private static void RenderReleaseNotes(StackPanel target, ReleaseInfo latest)
        {
            target.Children.Clear();
            if (latest == null)
            {
                target.Children.Add(new UiText(SettingsText.UpdateNotesBeforeCheck, TextVariant.Description));
                return;
            }
            string version = UpdateView.Display(latest.Version, latest.Tag);
            target.Children.Add(new UiText(SettingsText.UpdateNotesTitle(version), TextVariant.Strong) { Margin = new Thickness(0, 0, 0, 4) });
            var lines = UpdateCheck.NotesLines(latest.Notes);
            int shown = 0;
            foreach (var line in lines)
            {
                // The body usually repeats the version as its first heading; the title above already says it.
                if (line.Kind == NoteLineKind.Heading && line.Text.IndexOf(version, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                shown++;
                switch (line.Kind)
                {
                    case NoteLineKind.Heading:
                        target.Children.Add(new UiText(line.Text, TextVariant.Strong) { Margin = new Thickness(0, 8, 0, 2) });
                        break;
                    case NoteLineKind.Bullet:
                    {
                        var g = new Grid { Margin = new Thickness(4, 2, 0, 2) };
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        g.Children.Add(new UiText("•", TextVariant.Secondary));
                        var t = new UiText(line.Text, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
                        Grid.SetColumn(t, 1);
                        g.Children.Add(t);
                        target.Children.Add(g);
                        break;
                    }
                    default:
                        target.Children.Add(new UiText(line.Text, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 2) });
                        break;
                }
            }
            if (shown == 0) target.Children.Add(new UiText(SettingsText.UpdateNotesEmpty, TextVariant.Description));
        }
    }
}
