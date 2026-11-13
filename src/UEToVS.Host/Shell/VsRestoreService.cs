using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Keymap;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Fonts;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Navigation;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Shell
{
    /// <summary>
    /// "恢复 VS 原样": VS never tells an extension that it is being disabled or uninstalled, so the VS settings UEToVS
    /// changed (C++ IntelliSense / browse database / semantic index, background code analysis, Blueprint support, VS fonts,
    /// shortcuts) would stay changed afterwards. This restores all of them from their backups, then sets the takeover scope
    /// to Off and stops managing VS fonts so nothing is applied again. Restores run first and the settings change after, so
    /// the settings-changed handlers find no backup left and do not race the restore.
    /// </summary>
    internal static class VsRestoreService
    {
        private static readonly Logger L = Log.For("Restore");

        public static async Task<string> RunAsync(HostPackage package)
        {
            if (package == null) return SettingsText.ActionUnavailable;
            var failed = new List<string>();
            L.Info("vs.restoreAll.start", "Restoring every VS setting UEToVS changed");

            var svc = package.IntelliSense;
            if (svc != null)
            {
                await StepAsync("intellisense", failed, () => svc.RestoreAsync("restoreAll"));
                await StepAsync("codeAnalysis", failed, () => svc.RestoreBackgroundCodeAnalysisAsync("restoreAll"));
                await StepAsync("vsBlueprint", failed, () => svc.RestoreVsBlueprintSupportAsync("restoreAll"));
            }

            var fonts = package.Fonts;
            if (fonts != null)
            {
                var s = package.Settings.Effective.Clone();
                s.ManageVsFonts = false; // the planner then restores every backed-up category
                await StepAsync("fonts", failed, () => fonts.ApplyVsFontsAsync(s, "restoreAll"));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            int keymap = 0;
            try { keymap = RestoreKeymap(); }
            catch (Exception ex)
            {
                failed.Add("keymap");
                L.Warn("vs.restoreAll.keymap.failed", "Restoring the original shortcuts failed", ex);
            }
            try { keymap += KeymapService.Restore(); } // optional Rider/VA preset (own backup)
            catch (Exception ex)
            {
                failed.Add("keymapPreset");
                L.Warn("vs.restoreAll.preset.failed", "Restoring the keymap preset backup failed", ex);
            }

            // Nothing re-applies the takeover or the fonts any more (first-run default keys stay "handled").
            var values = package.Settings.UserValues();
            values["intelliSense.mode"] = nameof(IntelliSenseTakeoverMode.Off);
            values[FontSettingsSchema.ManageVsKey] = "false";
            package.Settings.ApplyUserValues(values, "restoreAll");

            L.Info("vs.restoreAll.done", null, ("keymapCommands", keymap), ("failed", failed));
            return failed.Count == 0 ? SettingsText.RestoreVsDone(keymap) : SettingsText.RestoreVsPartly(string.Join(", ", failed));
        }

        private static async Task StepAsync(string name, List<string> failed, Func<Task> run)
        {
            try { await run(); }
            catch (Exception ex)
            {
                failed.Add(name);
                L.Warn("vs.restoreAll.step.failed", "A restore step failed", ex, ("step", name));
            }
        }

        /// <summary>
        /// Writes back, for every command UEToVS ever changed (its own and the VS commands that gave up a key), the
        /// bindings it had before the first change, then moves the backups to KeymapBackups\restored. UI thread.
        /// </summary>
        private static int RestoreKeymap()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = KeymapBackups.Directory;
            if (!Directory.Exists(dir)) return 0;
            var files = Directory.GetFiles(dir, "keymap-backup-*.json");
            var backups = new List<KeymapPlanner.Backup>();
            foreach (var f in files)
            {
                var b = KeymapPlanner.ParseBackup(File.ReadAllText(f));
                if (b == null) { L.Warn("vs.restoreAll.keymap.unreadable", "Keymap backup unreadable; skipped", ("file", f)); continue; }
                if (b.CreatedUtc == default) b.CreatedUtc = File.GetLastWriteTimeUtc(f);
                backups.Add(b);
            }
            var originals = KeymapPlanner.Originals(backups);
            var dte = VsEditor.Dte;
            int restored = 0;
            var notFound = new List<string>();
            foreach (var kv in originals)
            {
                var cmd = DteKeymap.FindByName(dte, kv.Key);
                if (cmd == null) { notFound.Add(kv.Key); continue; }
                try
                {
                    cmd.Bindings = kv.Value.Cast<object>().ToArray();
                    restored++;
                }
                catch (Exception ex)
                {
                    L.Warn("vs.restoreAll.keymap.command.failed", "Restoring a command's shortcuts failed", ex, ("command", kv.Key));
                }
            }
            string done = Path.Combine(dir, "restored");
            Directory.CreateDirectory(done);
            foreach (var f in files)
            {
                try { File.Move(f, Path.Combine(done, Path.GetFileName(f))); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            L.Info("vs.restoreAll.keymap", null, ("backups", backups.Count), ("commands", originals.Count), ("restored", restored), ("notFound", notFound));
            return restored;
        }
    }
}
