using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Keymap;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Keymap
{
    /// <summary>
    /// Rider keymap presets (Core/Keymap) applied through DTE command bindings, ported from the prototype. Conflicting
    /// bindings in Global / Text Editor are removed; every binding touched is backed up once (the user's original
    /// layout) in %LOCALAPPDATA%\UEToVS\keymap-backup.json so "restore" puts VS back exactly as it was.
    /// The default shortcuts of UEToVS's own commands live in the VSCT; this is the optional "full Rider layout".
    /// </summary>
    internal static class KeymapService
    {
        private static readonly Logger L = Log.For("Keymap");

        private static string BackupPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEToVS", "keymap-backup.json");

        public static bool HasBackup => File.Exists(BackupPath);

        /// <summary>OwnCommands name (Core/Keymap) → UEToVS command id. Names without a command are skipped.</summary>
        private static readonly Dictionary<string, int> OwnIds = BuildOwnIds();

        private static Dictionary<string, int> BuildOwnIds()
        {
            var m = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [OwnCommands.SearchEverywhere] = M1CommandIds.SearchEverywhere,
                [OwnCommands.GoToEverything] = M1CommandIds.SearchEverywhere,
                [OwnCommands.GoToType] = M1CommandIds.GoToType,
                [OwnCommands.GoToFile] = M1CommandIds.GoToFile,
                [OwnCommands.GoToSymbol] = M1CommandIds.GoToSymbol,
                [OwnCommands.GoToAsset] = M1CommandIds.GoToAsset,
                [OwnCommands.FindAction] = CommandIds.FindAction,
                [OwnCommands.NavigateBack] = M1CommandIds.NavigateBack,
                [OwnCommands.NavigateForward] = M1CommandIds.NavigateForward,
                [OwnCommands.RecentFiles] = M1CommandIds.RecentFiles,
                [OwnCommands.RecentLocations] = M1CommandIds.RecentLocations,
                [OwnCommands.LastEditLocation] = M1CommandIds.LastEditLocation,
                [OwnCommands.NextEditLocation] = M1CommandIds.NextEditLocation,
                [OwnCommands.FileStructure] = M1CommandIds.FileStructure,
                [OwnCommands.NavigateTo] = M1CommandIds.NavigateTo,
                [OwnCommands.GoToRelated] = M1CommandIds.GoToRelated,
                [OwnCommands.AddModuleDependency] = M1CommandIds.AddModuleDependency,
                [OwnCommands.ToggleBookmark] = M1CommandIds.ToggleBookmark,
                [OwnCommands.ShowBookmarks] = M1CommandIds.ShowBookmarks,
                [OwnCommands.LiveCoding] = M1CommandIds.LiveCoding,
                [OwnCommands.GenerateProjectFiles] = M1CommandIds.GenerateProjectFiles,
            };
            for (int d = 0; d <= 9; d++)
            {
                m[OwnCommands.SetBookmark(d)] = M1CommandIds.SetBookmark0 + d;
                m[OwnCommands.Bookmark(d)] = M1CommandIds.GoToBookmark0 + d;
            }
            return m;
        }

        /// <summary>Localized scope names ("Global" is "全局" in a Chinese VS), learned from well-known default bindings.</summary>
        private static Dictionary<string, string> DetectScopes(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var s = DteKeymap.DetectScopes(dte);
            return new Dictionary<string, string> { ["Global"] = s.LocalGlobal, ["Text Editor"] = s.LocalTextEditor };
        }

        private static Command FindCommand(DTE dte, KeyBindingEntry e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (e.IsOwnCommand)
                    return OwnIds.TryGetValue(e.OwnCommandName, out int id) ? dte.Commands.Item("{" + PackageGuids.CommandSetString + "}", id) : null;
                return dte.Commands.Item(e.Command);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string[] Bindings(Command c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return c.Bindings is object[] b ? b.Select(o => o?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToArray() : new string[0];
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        private static string KeysOf(string binding)
        {
            int i = binding.IndexOf("::", StringComparison.Ordinal);
            return i < 0 ? binding : binding.Substring(i + 2);
        }

        private static string ScopeOf(string binding)
        {
            int i = binding.IndexOf("::", StringComparison.Ordinal);
            return i < 0 ? string.Empty : binding.Substring(0, i);
        }

        private static string SafeName(Command c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return c.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Applies <paramref name="preset"/>. Returns the number of bindings set.</summary>
        public static int Apply(KeymapPreset preset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            using (L.Time("keymap.apply", double.MaxValue, ("preset", preset.Id)))
            {
                var dte = VsEditor.Dte;
                var scopes = DetectScopes(dte);
                string Localize(string scope) => scopes.TryGetValue(scope, out var s) ? s : scope;

                var targets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var commands = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
                var targetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<string>();
                foreach (var e in preset.Bindings)
                {
                    var cmd = FindCommand(dte, e);
                    string cmdName = cmd == null ? null : SafeName(cmd);
                    if (string.IsNullOrEmpty(cmdName))
                    {
                        missing.Add(e.Command);
                        continue;
                    }
                    commands[cmdName] = cmd;
                    if (!targets.TryGetValue(cmdName, out var list)) targets[cmdName] = list = new List<string>();
                    list.Add(Localize(e.Scope) + "::" + e.Keys);
                    targetKeys.Add(e.Keys);
                }

                // Back up everything we will touch (once: the first backup is the user's original layout).
                var backup = LoadBackup() ?? new Dictionary<string, string[]>();
                bool hadBackup = backup.Count > 0;
                var conflicts = new List<(Command Cmd, string[] Keep)>();
                string globalScope = Localize("Global"), editorScope = Localize("Text Editor");
                foreach (Command c in dte.Commands)
                {
                    string name = SafeName(c);
                    if (string.IsNullOrEmpty(name)) continue;
                    var current = Bindings(c);
                    if (current.Length == 0) continue;
                    targets.TryGetValue(name, out var mine);
                    // A preset key bound elsewhere in Global or Text Editor would shadow ours: remove it.
                    var keep = current.Where(b =>
                    {
                        if (!targetKeys.Contains(KeysOf(b))) return true;
                        string sc = ScopeOf(b);
                        if (sc != globalScope && sc != editorScope) return true;
                        return mine != null && mine.Contains(b, StringComparer.OrdinalIgnoreCase);
                    }).ToArray();
                    if (keep.Length != current.Length)
                    {
                        conflicts.Add((c, keep));
                        if (!backup.ContainsKey(name)) backup[name] = current;
                    }
                }
                foreach (var name in targets.Keys)
                    if (!backup.ContainsKey(name)) backup[name] = Bindings(commands[name]);
                SaveBackup(backup);

                foreach (var (cmd, keep) in conflicts)
                {
                    try
                    {
                        cmd.Bindings = keep.Cast<object>().ToArray();
                    }
                    catch (Exception ex)
                    {
                        L.Warn("keymap.unbind.failed", "Cannot change a conflicting binding", ex, ("command", SafeName(cmd)));
                    }
                }

                int set = 0;
                foreach (var kv in targets)
                {
                    var cmd = commands[kv.Key];
                    try
                    {
                        var existing = Bindings(cmd).Where(b => !targetKeys.Contains(KeysOf(b)) || kv.Value.Contains(b, StringComparer.OrdinalIgnoreCase));
                        cmd.Bindings = existing.Concat(kv.Value).Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray();
                        set += kv.Value.Count;
                    }
                    catch (Exception ex)
                    {
                        L.Warn("keymap.bind.failed", "Cannot bind a preset key", ex, ("command", kv.Key), ("keys", string.Join(", ", kv.Value)));
                    }
                }
                L.Info("keymap.applied", null, ("preset", preset.Id), ("set", set), ("conflictsAdjusted", conflicts.Count), ("missing", missing.ToArray()),
                       ("backupCreated", !hadBackup), ("backup", BackupPath));
                return set;
            }
        }

        /// <summary>Restores every binding saved before the first Apply; deletes the backup. Returns the commands restored.</summary>
        public static int Restore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var backup = LoadBackup();
            if (backup == null || backup.Count == 0) return 0;
            var dte = VsEditor.Dte;
            int restored = 0;
            foreach (var kv in backup)
            {
                try
                {
                    dte.Commands.Item(kv.Key).Bindings = kv.Value.Cast<object>().ToArray();
                    restored++;
                }
                catch (Exception ex)
                {
                    L.Warn("keymap.restore.failed", "Restoring a binding failed", ex, ("command", kv.Key));
                }
            }
            try
            {
                File.Delete(BackupPath);
            }
            catch (IOException)
            {
                // kept; restoring again is harmless
            }
            L.Info("keymap.restored", null, ("commands", restored));
            return restored;
        }

        private static Dictionary<string, string[]> LoadBackup()
        {
            try
            {
                return File.Exists(BackupPath) ? JsonConvert.DeserializeObject<Dictionary<string, string[]>>(File.ReadAllText(BackupPath)) : null;
            }
            catch (Exception ex)
            {
                L.Error("keymap.backup.unreadable", "Keymap backup unreadable", ex, ("file", BackupPath));
                return null;
            }
        }

        private static void SaveBackup(Dictionary<string, string[]> backup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath));
            File.WriteAllText(BackupPath, JsonConvert.SerializeObject(backup, Formatting.Indented));
        }
    }
}
