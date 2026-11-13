using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace UEToVS.Host.Logic.Link
{
    /// <summary>What <see cref="UeLiveCodingStartup.Apply"/> did to one project's ini.</summary>
    public enum LiveCodingStartupChange
    {
        /// <summary>Nothing to do (already manual / nothing backed up to restore).</summary>
        None,
        /// <summary>Startup set to Manual (original value backed up).</summary>
        SetManual,
        /// <summary>The backed-up original value was written back.</summary>
        Restored,
    }

    /// <summary>
    /// v0.5.11: UE's Live Coding starts LiveCodingConsole together with the editor (Editor Preferences › Live Coding ›
    /// Startup, default "Automatic but hidden"), which holds GBs of memory even when Live Coding is never used. UEToVS
    /// sets the project's per-user setting to "Manual": the console only starts on the first Live Coding compile
    /// (Ctrl+Alt+F11 in UE or VS still works). The setting lives in
    /// <c>&lt;project&gt;/Saved/Config/WindowsEditor/EditorPerProjectUserSettings.ini</c> (UE 5.x),
    /// section <c>[/Script/LiveCoding.LiveCodingSettings]</c>, key <c>Startup</c>. The original value of every file is
    /// backed up (first original wins) and written back when the switch is turned off or by "恢复 VS 原样".
    /// Pure: text in, text out; the host does the file IO.
    /// </summary>
    public static class UeLiveCodingStartup
    {
        public const string Section = "/Script/LiveCoding.LiveCodingSettings";
        public const string Key = "Startup";
        public const string Manual = "Manual";
        public const string BackupFileName = "ue-livecoding-backup.json";

        /// <summary>Backup marker: the key was not in the file.</summary>
        public const string Absent = "<absent>";

        /// <summary>The ini UE 5.x reads the per-project editor user settings from.</summary>
        public static string IniPath(string projectDirectory) =>
            string.IsNullOrEmpty(projectDirectory) ? null
            : Path.Combine(projectDirectory, "Saved", "Config", "WindowsEditor", "EditorPerProjectUserSettings.ini");

        /// <summary>The Startup value in the ini text, or null when it is not set (UE default: automatic).</summary>
        public static string CurrentValue(string text) => IniText.Get(text, Section, Key);

        /// <summary>
        /// Applies the switch to one file: <paramref name="manual"/> → Startup=Manual (backing up the original once);
        /// otherwise the backed-up original goes back (key removed when it was absent) and the backup entry is dropped.
        /// <paramref name="text"/> is the current file content (null: no file).
        /// </summary>
        public static string Apply(string text, bool manual, string path, LiveCodingBackup backup, out LiveCodingStartupChange change)
        {
            change = LiveCodingStartupChange.None;
            string current = IniText.Get(text, Section, Key);
            if (manual)
            {
                if (string.Equals(current, Manual, StringComparison.OrdinalIgnoreCase)) return text;
                if (!backup.Originals.ContainsKey(path)) backup.Originals[path] = current ?? Absent;
                change = LiveCodingStartupChange.SetManual;
                return IniText.Set(text, Section, Key, Manual);
            }
            if (!backup.Originals.TryGetValue(path, out var original)) return text;
            backup.Originals.Remove(path);
            // The user changed it in UE since: leave their choice alone.
            if (!string.Equals(current, Manual, StringComparison.OrdinalIgnoreCase)) return text;
            change = LiveCodingStartupChange.Restored;
            return IniText.Set(text, Section, Key, original == Absent ? null : original);
        }
    }

    /// <summary>%LocalAppData%\UEToVS\ue-livecoding-backup.json: ini path → original Startup value (or <see cref="UeLiveCodingStartup.Absent"/>).</summary>
    public sealed class LiveCodingBackup
    {
        public Dictionary<string, string> Originals { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsEmpty => Originals == null || Originals.Count == 0;

        public static LiveCodingBackup Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new LiveCodingBackup();
            try
            {
                var b = JsonConvert.DeserializeObject<LiveCodingBackup>(json);
                var result = new LiveCodingBackup();
                if (b?.Originals != null)
                    foreach (var kv in b.Originals) result.Originals[kv.Key] = kv.Value;
                return result;
            }
            catch (JsonException) { return null; }
        }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
    }

    /// <summary>
    /// Minimal UE ini editing that keeps every other line as it is: find a key in a [section]; set, add or remove it.
    /// Keys compare case-insensitively like UE's config; the first matching line wins.
    /// </summary>
    public static class IniText
    {
        public static string Get(string text, string section, string key)
        {
            if (string.IsNullOrEmpty(text)) return null;
            bool inSection = false;
            foreach (var raw in Split(text, out _))
            {
                string line = raw.Trim();
                if (IsHeader(line, out var name)) { inSection = string.Equals(name, section, StringComparison.OrdinalIgnoreCase); continue; }
                if (inSection && TryKey(line, out var k, out var v) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v;
            }
            return null;
        }

        /// <summary>Sets <paramref name="key"/> (null removes it); adds the section at the end when it is missing.</summary>
        public static string Set(string text, string section, string key, string value)
        {
            var lines = new List<string>(Split(text ?? string.Empty, out string newline));
            if (lines.Count == 1 && lines[0].Length == 0) lines.Clear();
            int header = -1, end = lines.Count, found = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (IsHeader(line, out var name))
                {
                    if (header >= 0) { end = i; break; }
                    if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase)) header = i;
                    continue;
                }
                if (header >= 0 && found < 0 && TryKey(line, out var k, out _) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) found = i;
            }
            string entry = key + "=" + value;
            if (found >= 0)
            {
                if (value == null) lines.RemoveAt(found);
                else lines[found] = entry;
            }
            else if (value != null)
            {
                if (header < 0)
                {
                    // Keep a trailing blank line pattern: blank line, header, entry.
                    while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                    if (lines.Count > 0) lines.Add(string.Empty);
                    lines.Add("[" + section + "]");
                    lines.Add(entry);
                    lines.Add(string.Empty);
                }
                else
                {
                    // After the section's last non-blank line.
                    int at = end;
                    while (at - 1 > header && lines[at - 1].Trim().Length == 0) at--;
                    lines.Insert(at, entry);
                }
            }
            else return text;
            return string.Join(newline, lines);
        }

        private static string[] Split(string text, out string newline)
        {
            newline = text.Contains("\r\n") || !text.Contains("\n") ? "\r\n" : "\n";
            return text.Replace("\r\n", "\n").Split('\n');
        }

        private static bool IsHeader(string line, out string name)
        {
            name = null;
            if (line.Length < 2 || line[0] != '[' || line[line.Length - 1] != ']') return false;
            name = line.Substring(1, line.Length - 2).Trim();
            return true;
        }

        private static bool TryKey(string line, out string key, out string value)
        {
            key = value = null;
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') return false;
            int eq = line.IndexOf('=');
            if (eq <= 0) return false;
            key = line.Substring(0, eq).Trim();
            // "+Key" / "-Key" / ".Key" are array operations, not plain values.
            if (key.Length == 0 || key[0] == '+' || key[0] == '-' || key[0] == '.' || key[0] == '!') return false;
            value = line.Substring(eq + 1).Trim();
            return true;
        }
    }
}
