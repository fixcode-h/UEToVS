using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using UEToVS.Commands;
using UEToVS.Core.Keymap;
using UEToVS.UI;

namespace UEToVS.Keymap
{
    /// <summary>
    /// Applies a Rider keymap preset through DTE command bindings, removing conflicting bindings, and keeps a
    /// backup of every binding it touches so "恢复键位" puts Visual Studio back exactly as it was.
    /// </summary>
    internal static class KeymapManager
    {
        private static string BackupPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEToVS", "keymap-backup.json");

        public static bool HasBackup => File.Exists(BackupPath);

        /// <summary>Localized scope names ("Global" is "全局" in a Chinese VS). Learned from well-known default bindings.</summary>
        private static Dictionary<string, string> DetectScopes(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var map = new Dictionary<string, string> { ["Global"] = "Global", ["Text Editor"] = "Text Editor" };
            string Scope(string command, string keySuffix)
            {
                try
                {
                    if (dte.Commands.Item(command).Bindings is object[] b)
                        foreach (var x in b.Cast<object>().Select(o => o?.ToString()))
                        {
                            int i = x?.IndexOf("::", StringComparison.Ordinal) ?? -1;
                            if (i > 0 && x.EndsWith(keySuffix, StringComparison.OrdinalIgnoreCase)) return x.Substring(0, i);
                        }
                }
                catch (Exception)
                {
                }
                return null;
            }
            map["Global"] = Scope("File.SaveAll", "Ctrl+Shift+S") ?? Scope("Edit.Undo", "Ctrl+Z") ?? "Global";
            map["Text Editor"] = Scope("Edit.LineDelete", "Ctrl+Shift+L") ?? Scope("Edit.CommentSelection", "Ctrl+K, Ctrl+C") ?? "Text Editor";
            return map;
        }

        private static Command FindCommand(DTE dte, KeyBindingEntry e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (e.IsOwnCommand)
                {
                    if (!CommandIds.ByName.TryGetValue(e.OwnCommandName, out int id)) return null;
                    return dte.Commands.Item("{" + CommandIds.CommandSetString + "}", id);
                }
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
                return c.Bindings is object[] b ? b.Cast<object>().Select(o => o?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToArray() : new string[0];
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
            return i < 0 ? "" : binding.Substring(0, i);
        }

        /// <summary>Applies <paramref name="preset"/>. Returns the number of bindings set.</summary>
        public static int Apply(KeymapPreset preset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = VsHelpers.Dte;
            var scopes = DetectScopes(dte);
            string Localize(string scope) => scopes.TryGetValue(scope, out var s) ? s : scope;

            // Resolve target bindings per command.
            var targets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var commands = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
            var targetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int missing = 0;
            foreach (var e in preset.Bindings)
            {
                var cmd = FindCommand(dte, e);
                if (cmd == null)
                {
                    missing++;
                    OutputLog.Write("Keymap: command not found: " + e.Command);
                    continue;
                }
                string cmdName = SafeName(cmd);
                if (string.IsNullOrEmpty(cmdName)) continue;
                commands[cmdName] = cmd;
                if (!targets.TryGetValue(cmdName, out var list)) targets[cmdName] = list = new List<string>();
                list.Add(Localize(e.Scope) + "::" + e.Keys);
                targetKeys.Add(e.Keys);
            }

            // Back up everything we will touch (once: the first backup is the user's original layout).
            var backup = LoadBackup() ?? new Dictionary<string, string[]>();
            bool hadBackup = backup.Count > 0;
            var conflicts = new List<(Command cmd, string[] keep)>();
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
                    OutputLog.Write("Keymap: cannot change " + SafeName(cmd) + ": " + ex.Message);
                }
            }

            int set = 0;
            foreach (var kv in targets)
            {
                var cmd = commands[kv.Key];
                try
                {
                    var existing = Bindings(cmd).Where(b => !targetKeys.Contains(KeysOf(b)) || kv.Value.Contains(b, StringComparer.OrdinalIgnoreCase));
                    var all = existing.Concat(kv.Value).Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray();
                    cmd.Bindings = all;
                    set += kv.Value.Count;
                }
                catch (Exception ex)
                {
                    OutputLog.Write("Keymap: cannot bind " + kv.Key + " to " + string.Join(", ", kv.Value) + ": " + ex.Message);
                }
            }
            OutputLog.Write($"Keymap '{preset.DisplayName}': {set} bindings set, {conflicts.Count} conflicting commands adjusted, {missing} commands not available{(hadBackup ? "" : ", backup saved to " + BackupPath)}.");
            return set;
        }

        /// <summary>Restores every binding saved before the first Apply.</summary>
        public static int Restore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var backup = LoadBackup();
            if (backup == null || backup.Count == 0) return 0;
            var dte = VsHelpers.Dte;
            int restored = 0;
            foreach (var kv in backup)
            {
                try
                {
                    var cmd = dte.Commands.Item(kv.Key);
                    cmd.Bindings = kv.Value.Cast<object>().ToArray();
                    restored++;
                }
                catch (Exception ex)
                {
                    OutputLog.Write("Keymap restore: " + kv.Key + ": " + ex.Message);
                }
            }
            try
            {
                File.Delete(BackupPath);
            }
            catch (IOException)
            {
            }
            OutputLog.Write($"Keymap restored for {restored} commands.");
            return restored;
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

        private static Dictionary<string, string[]> LoadBackup()
        {
            try
            {
                return File.Exists(BackupPath) ? JsonConvert.DeserializeObject<Dictionary<string, string[]>>(File.ReadAllText(BackupPath)) : null;
            }
            catch (Exception ex)
            {
                OutputLog.Write("Keymap backup unreadable: " + ex.Message);
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
