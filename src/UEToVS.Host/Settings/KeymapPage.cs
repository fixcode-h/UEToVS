using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Localization;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Settings
{
    /// <summary>
    /// Settings window "快捷键" page: every UEToVS command with its current VS key binding (read from DTE), click a
    /// shortcut to record a new one, one-click presets (Visual Assist / Rider / VS default), conflict detection against
    /// all other VS commands, automatic backup before every change (%LocalAppData%\UEToVS\KeymapBackups) and restore.
    /// Changes are written to VS immediately (VS's keyboard settings are the store); Apply/Cancel don't affect them.
    /// All DTE calls run on the UI thread; reading all ~5000 commands for conflicts is chunked so the UI stays responsive.
    /// </summary>
    internal sealed class KeymapPage : ISettingsCustomPage
    {
        private static readonly Logger L = Log.For("Keymap");
        private readonly HostPackage _package;
        private readonly StackPanel _rows = new StackPanel();
        private readonly StackPanel _conflicts = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        private readonly UiText _status = new UiText((string)null, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        private Dictionary<string, string[]> _ours = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private List<KeyValuePair<string, string[]>> _all;
        private List<KeyConflict> _found = new List<KeyConflict>();
        private DTE _dte;

        public KeymapPage(HostPackage package)
        {
            _package = package;
            var root = new DockPanel { LastChildFill = true };
            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            top.Children.Add(new UiText(SettingsText.KeymapIntro, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 0, 0, 10) });
            var presets = new WrapPanel();
            presets.Children.Add(new UiText(SettingsText.KeymapPreset, TextVariant.Body) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            foreach (var (id, text) in new[] { (KeymapPresetId.VisualAssist, SettingsText.KeymapPresetVa), (KeymapPresetId.Rider, SettingsText.KeymapPresetRider), (KeymapPresetId.VisualStudio, SettingsText.KeymapPresetVs) })
            {
                var preset = id;
                var b = new UiButton(text) { MinWidth = 96, Margin = new Thickness(0, 0, 8, 4) };
                b.Click += (s, e) => Safe.RunAsync(L, "keymap.preset", () => ApplyPresetAsync(preset), ("preset", preset.ToString()));
                presets.Children.Add(b);
            }
            var restore = new UiButton(SettingsText.KeymapRestore, ButtonKind.Ghost) { Margin = new Thickness(8, 0, 0, 4) };
            restore.Click += (s, e) => Safe.RunAsync(L, "keymap.restore", RestoreLastBackupAsync);
            presets.Children.Add(restore);
            top.Children.Add(presets);
            top.Children.Add(new UiText(SettingsText.KeymapDoubleShift, TextVariant.Caption) { Margin = new Thickness(0, 6, 0, 0) });
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            bottom.Children.Add(_status);
            bottom.Children.Add(_conflicts);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(new UiScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            Element = root;
            _status.Text = SettingsText.KeymapLoading;
            Safe.RunAsync(L, "keymap.load", LoadAsync);
        }

        public UIElement Element { get; }
        public void Apply() { }
        public void Cancel() { }
        public void ResetToDefaults() => Safe.RunAsync(L, "keymap.reset", () => ApplyPresetAsync(KeymapPresetId.VisualAssist));

        // ------------------------------------------------------------------ reading

        private async Task LoadAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _dte = await _package.GetServiceAsync(typeof(DTE)) as DTE;
            if (_dte == null) { _status.Text = SettingsText.Failed; return; }
            ReadOurs();
            RenderRows();
            using (L.Time("keymap.readAll", 2000))
                _all = await ReadAllBindingsAsync();
            UpdateConflicts();
        }

        private void ReadOurs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ours = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in CommandCatalog.All) _ours[c.Name] = GetBindings(c.Name) ?? Array.Empty<string>();
        }

        private string[] GetBindings(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var cmd = _dte.Commands.Item(name, -1);
                return cmd?.Bindings is object[] arr ? arr.OfType<string>().ToArray() : Array.Empty<string>();
            }
            catch (ArgumentException)
            {
                return null; // command not registered (VSCT not loaded yet)
            }
        }

        private async Task<List<KeyValuePair<string, string[]>>> ReadAllBindingsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var list = new List<KeyValuePair<string, string[]>>(6000);
            int i = 0;
            foreach (Command cmd in _dte.Commands)
            {
                try
                {
                    string name = cmd.Name;
                    if (!string.IsNullOrEmpty(name) && cmd.Bindings is object[] arr && arr.Length > 0)
                        list.Add(new KeyValuePair<string, string[]>(name, arr.OfType<string>().ToArray()));
                }
                catch (Exception)
                {
                    // some commands throw on Bindings; skip
                }
                if (++i % 300 == 0) await Task.Yield(); // keep VS responsive
            }
            L.Info("keymap.read", null, ("commands", i), ("bound", list.Count));
            return list;
        }

        // ------------------------------------------------------------------ rows

        private void RenderRows()
        {
            _rows.Children.Clear();
            bool en = Loc.IsEnglish;
            foreach (var group in CommandCatalog.All.GroupBy(c => c.Group))
            {
                _rows.Children.Add(new UiText(GroupTitle(group.Key), TextVariant.Strong) { Margin = new Thickness(0, _rows.Children.Count == 0 ? 0 : 14, 0, 6) });
                foreach (var c in group) _rows.Children.Add(BuildRow(c, en));
            }
        }

        private static string GroupTitle(string g)
        {
            switch (g)
            {
                case "navigation": return Loc.T("导航", "Navigation");
                case "search": return Loc.T("搜索", "Search");
                case "tools": return Loc.T("工具", "Tools");
                case "settings": return Loc.T("设置", "Settings");
                default: return Loc.T("诊断", "Diagnostics");
            }
        }

        private UIElement BuildRow(CommandInfo c, bool en)
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new UiText(c.Title(en), TextVariant.Body));
            if (c.Reserved && _package.Actions.Get(c.ActionId) == null)
                title.Children.Add(new UiText(SettingsText.KeymapReserved, TextVariant.Caption) { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            grid.Children.Add(title);

            _ours.TryGetValue(c.Name, out var bindings);
            string current = string.Join("  ", (bindings ?? Array.Empty<string>()).Select(b => Shortcut.TryParseBinding(b, out _, out var k) ? Shortcut.Display(k) : b));
            var button = new UiButton(string.IsNullOrEmpty(current) ? SettingsText.KeymapNone : current, ButtonKind.Secondary)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, c.Title(en) + " " + current);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
            bool recording = false;
            button.Click += (s, e) => Safe.Execute(L, "keymap.record.start", () =>
            {
                recording = true;
                button.Content = SettingsText.KeymapPressKeys;
                button.Focus();
            });
            button.LostKeyboardFocus += (s, e) => Safe.Execute(L, "keymap.record.blur", () =>
            {
                if (!recording) return;
                recording = false;
                RenderRows();
            });
            button.PreviewKeyDown += (s, e) => Safe.Execute(L, "keymap.record.key", () =>
            {
                if (!recording) return;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift || key == Key.LeftAlt || key == Key.RightAlt
                    || key == Key.LWin || key == Key.RWin) { e.Handled = true; return; }
                e.Handled = true;
                recording = false;
                if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { RenderRows(); return; }
                if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None)
                {
                    Safe.RunAsync(L, "keymap.clear", () => SetBindingsAsync(new Dictionary<string, string[]> { [c.Name] = Array.Empty<string>() }, "clear:" + c.Name), ("command", c.Name));
                    return;
                }
                string keys = Compose(Keyboard.Modifiers, key);
                if (keys == null) { _status.Text = SettingsText.KeymapInvalid; RenderRows(); return; }
                L.Info("keymap.record", null, ("command", c.Name), ("keys", keys));
                Safe.RunAsync(L, "keymap.set", () => SetBindingsAsync(new Dictionary<string, string[]> { [c.Name] = new[] { Shortcut.Binding(c.DteScope, keys) } }, "edit:" + c.Name),
                              ("command", c.Name), ("keys", keys));
            });
            return grid;
        }

        /// <summary>WPF key + modifiers → VS shortcut text ("Ctrl+Alt+Left Arrow"), or null for keys VS can't bind.</summary>
        private static string Compose(ModifierKeys mods, Key key)
        {
            string k;
            if (key >= Key.A && key <= Key.Z) k = key.ToString();
            else if (key >= Key.D0 && key <= Key.D9) k = ((int)(key - Key.D0)).ToString();
            else if (key >= Key.NumPad0 && key <= Key.NumPad9) k = "Num " + (int)(key - Key.NumPad0);
            else if (key >= Key.F1 && key <= Key.F24) k = key.ToString();
            else
            {
                switch (key)
                {
                    case Key.Left: k = "Left Arrow"; break;
                    case Key.Right: k = "Right Arrow"; break;
                    case Key.Up: k = "Up Arrow"; break;
                    case Key.Down: k = "Down Arrow"; break;
                    case Key.Home: k = "Home"; break;
                    case Key.End: k = "End"; break;
                    case Key.PageUp: k = "PgUp"; break;
                    case Key.PageDown: k = "PgDn"; break;
                    case Key.Insert: k = "Ins"; break;
                    case Key.Delete: k = "Del"; break;
                    case Key.Back: k = "Bkspce"; break;
                    case Key.Enter: k = "Enter"; break;
                    case Key.Space: k = "Space"; break;
                    case Key.Tab: k = "Tab"; break;
                    case Key.OemMinus: k = "-"; break;
                    case Key.OemPlus: k = "="; break;
                    case Key.OemComma: k = ","; break;
                    case Key.OemPeriod: k = "."; break;
                    case Key.OemQuestion: k = "/"; break;
                    case Key.OemSemicolon: k = ";"; break;
                    case Key.OemQuotes: k = "'"; break;
                    case Key.OemOpenBrackets: k = "["; break;
                    case Key.OemCloseBrackets: k = "]"; break;
                    case Key.OemPipe: k = "\\"; break;
                    case Key.OemTilde: k = "`"; break;
                    default: return null;
                }
            }
            string m = ((mods & ModifierKeys.Control) != 0 ? "Ctrl+" : "") + ((mods & ModifierKeys.Shift) != 0 ? "Shift+" : "") + ((mods & ModifierKeys.Alt) != 0 ? "Alt+" : "");
            return Shortcut.Normalize(m + k);
        }

        // ------------------------------------------------------------------ writing

        private async Task ApplyPresetAsync(KeymapPresetId preset)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_dte == null) return;
            ReadOurs();
            var plan = KeymapPlanner.Plan(_ours, KeymapPlanner.PresetBindings(preset));
            L.Info("keymap.preset.plan", null, ("preset", preset.ToString()), ("changes", plan.Count));
            await SetBindingsAsync(plan.ToDictionary(p => p.Command, p => p.New, StringComparer.OrdinalIgnoreCase), "preset:" + preset);
        }

        /// <summary>Backs up the current bindings of every command in <paramref name="target"/>, then writes the new ones.</summary>
        private async Task SetBindingsAsync(Dictionary<string, string[]> target, string reason)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_dte == null || target.Count == 0) { RenderRows(); return; }
            var backup = new KeymapPlanner.Backup { CreatedUtc = DateTime.UtcNow, Reason = reason };
            foreach (var name in target.Keys) backup.Bindings[name] = GetBindings(name) ?? Array.Empty<string>();
            string file = WriteBackup(backup);
            if (file == null) { _status.Text = SettingsText.Failed; return; } // never change without a backup
            int changed = 0;
            foreach (var kv in target)
            {
                try
                {
                    var cmd = _dte.Commands.Item(kv.Key, -1);
                    cmd.Bindings = kv.Value.Cast<object>().ToArray();
                    changed++;
                }
                catch (Exception ex)
                {
                    L.Error("keymap.set.failed", "Setting a key binding failed", ex, ("command", kv.Key), ("bindings", kv.Value));
                }
            }
            L.Info("keymap.changed", null, ("reason", reason), ("commands", changed), ("backup", file));
            ReadOurs();
            RenderRows();
            if (_all != null)
            {
                // refresh the cached bindings of the commands we touched
                var touched = new HashSet<string>(target.Keys, StringComparer.OrdinalIgnoreCase);
                _all.RemoveAll(kv => touched.Contains(kv.Key));
                foreach (var name in touched) _all.Add(new KeyValuePair<string, string[]>(name, GetBindings(name) ?? Array.Empty<string>()));
            }
            UpdateConflicts();
            _status.Text = SettingsText.KeymapApplied(changed, Path.GetFileName(file)) + "  " + _status.Text;
        }

        private static string BackupDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEToVS", "KeymapBackups");

        private static string WriteBackup(KeymapPlanner.Backup backup)
        {
            try
            {
                Directory.CreateDirectory(BackupDirectory);
                string file = Path.Combine(BackupDirectory, "keymap-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
                File.WriteAllText(file, KeymapPlanner.SerializeBackup(backup));
                return file;
            }
            catch (Exception ex)
            {
                L.Error("keymap.backup.failed", "Writing the keymap backup failed; nothing was changed", ex, ("dir", BackupDirectory));
                return null;
            }
        }

        private async Task RestoreLastBackupAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string latest = null;
            try
            {
                if (Directory.Exists(BackupDirectory))
                    latest = Directory.GetFiles(BackupDirectory, "keymap-backup-*.json").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
            }
            catch (Exception ex)
            {
                L.Warn("keymap.backup.list.failed", "Listing keymap backups failed", ex);
            }
            var backup = latest == null ? null : KeymapPlanner.ParseBackup(File.ReadAllText(latest));
            if (backup == null) { _status.Text = SettingsText.KeymapNoBackup; return; }
            L.Info("keymap.restore", null, ("file", latest), ("commands", backup.Bindings.Count));
            await SetBindingsAsync(backup.Bindings, "restore:" + Path.GetFileName(latest));
            // The restore itself made a new backup; drop the one we just restored so "restore" walks back in time.
            try { File.Delete(latest); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ conflicts

        private void UpdateConflicts()
        {
            _conflicts.Children.Clear();
            if (_all == null) return;
            var bound = _ours.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            _found = KeymapPlanner.FindConflicts(bound, _all);
            _status.Text = _found.Count == 0 ? SettingsText.KeymapNoConflicts : SettingsText.KeymapConflicts(_found.Count);
            L.Info("keymap.conflicts", null, ("count", _found.Count), ("sample", _found.Take(10).Select(c => c.ToString()).ToList()));
            if (_found.Count == 0) return;
            foreach (var c in _found.Take(30))
            {
                var ours = CommandCatalog.ByName(c.Command)?.Title(Loc.IsEnglish) ?? c.Command;
                var line = new UiText(SettingsText.KeymapConflictLine(ours, Shortcut.Display(c.Keys), c.OtherCommand), TextVariant.Body) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
                line.Bind(TextBlock.ForegroundProperty, Tokens.Severity(UEToVS.UI.Logic.Severity.Warning));
                _conflicts.Children.Add(line);
            }
            if (_found.Any(c => CommandCatalog.ByName(c.OtherCommand) == null))
            {
                var fix = new UiButton(SettingsText.KeymapRemoveConflicts) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
                fix.Click += (s, e) => Safe.RunAsync(L, "keymap.resolve", ResolveConflictsAsync);
                _conflicts.Children.Add(fix);
            }
        }

        /// <summary>Removes the conflicting bindings from the other (non-UEToVS) commands; backed up like every change.</summary>
        private async Task ResolveConflictsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var target = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in _found.Where(c => CommandCatalog.ByName(c.OtherCommand) == null).GroupBy(c => c.OtherCommand, StringComparer.OrdinalIgnoreCase))
            {
                var current = GetBindings(group.Key) ?? Array.Empty<string>();
                var remove = new HashSet<string>(group.Select(c => c.OtherScope + "::" + c.OtherKeys), StringComparer.OrdinalIgnoreCase);
                target[group.Key] = current.Where(b => !(Shortcut.TryParseBinding(b, out var s, out var k) && remove.Contains(s + "::" + k))).ToArray();
            }
            await SetBindingsAsync(target, "resolveConflicts");
        }
    }
}
