using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace UEToVS.Host.Logic.Commands
{
    /// <summary>
    /// VS shortcut text ("Ctrl+Shift+Alt+N", chords "Ctrl+K, Ctrl+O") parsing and canonical form. Canonical modifier
    /// order is Ctrl, Shift, Alt (what DTE returns); key aliases ("Left", "←", "Backspace") map to DTE key names.
    /// </summary>
    public static class Shortcut
    {
        private static readonly Dictionary<string, string> KeyAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = "Left Arrow", ["leftarrow"] = "Left Arrow", ["left arrow"] = "Left Arrow", ["←"] = "Left Arrow",
            ["right"] = "Right Arrow", ["rightarrow"] = "Right Arrow", ["right arrow"] = "Right Arrow", ["→"] = "Right Arrow",
            ["up"] = "Up Arrow", ["uparrow"] = "Up Arrow", ["up arrow"] = "Up Arrow", ["↑"] = "Up Arrow",
            ["down"] = "Down Arrow", ["downarrow"] = "Down Arrow", ["down arrow"] = "Down Arrow", ["↓"] = "Down Arrow",
            ["backspace"] = "Bkspce", ["bkspce"] = "Bkspce", ["back"] = "Bkspce",
            ["delete"] = "Del", ["del"] = "Del", ["insert"] = "Ins", ["ins"] = "Ins",
            ["pageup"] = "PgUp", ["pgup"] = "PgUp", ["pagedown"] = "PgDn", ["pgdn"] = "PgDn",
            ["escape"] = "Esc", ["esc"] = "Esc", ["enter"] = "Enter", ["return"] = "Enter", ["space"] = "Space", ["tab"] = "Tab",
            ["home"] = "Home", ["end"] = "End", ["minus"] = "-", ["oemminus"] = "-", ["comma"] = ",", ["oemcomma"] = ",",
        };

        /// <summary>Canonical form, or null when the text is not a valid shortcut (no key, unknown modifier, too many chords).</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var chords = SplitChords(text);
            if (chords.Count == 0 || chords.Count > 2) return null;
            var parts = new List<string>();
            foreach (var chord in chords)
            {
                var n = NormalizeChord(chord);
                if (n == null) return null;
                parts.Add(n);
            }
            return string.Join(", ", parts);
        }

        /// <summary>Splits on ", " between chords; a lone "," key ("Ctrl+,") is kept.</summary>
        private static List<string> SplitChords(string text)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            string t = text.Trim();
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                bool keyComma = c == ',' && (sb.Length == 0 || sb[sb.Length - 1] == '+');
                if (c == ',' && !keyComma)
                {
                    result.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) result.Add(sb.ToString().Trim());
            result.RemoveAll(string.IsNullOrEmpty);
            return result;
        }

        private static string NormalizeChord(string chord)
        {
            bool ctrl = false, shift = false, alt = false;
            string key = null;
            // "Ctrl++" means Ctrl and the '+' key.
            var tokens = new List<string>();
            var cur = new StringBuilder();
            for (int i = 0; i < chord.Length; i++)
            {
                if (chord[i] == '+' && cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(chord[i]);
            }
            if (cur.Length > 0) tokens.Add(cur.ToString());
            foreach (var raw in tokens)
            {
                var tok = raw.Trim();
                if (tok.Length == 0) return null;
                switch (tok.ToLowerInvariant())
                {
                    case "ctrl": case "control": ctrl = true; continue;
                    case "shift": shift = true; continue;
                    case "alt": alt = true; continue;
                }
                if (key != null) return null; // two keys in one chord
                key = CanonicalKey(tok);
            }
            if (key == null) return null;
            var sb = new StringBuilder();
            if (ctrl) sb.Append("Ctrl+");
            if (shift) sb.Append("Shift+");
            if (alt) sb.Append("Alt+");
            sb.Append(key);
            return sb.ToString();
        }

        private static string CanonicalKey(string k)
        {
            if (KeyAliases.TryGetValue(k, out var alias)) return alias;
            if (k.Length == 1) return char.ToUpperInvariant(k[0]).ToString();
            if (k.Length >= 2 && (k[0] == 'F' || k[0] == 'f') && int.TryParse(k.Substring(1), out int f) && f >= 1 && f <= 24) return "F" + f;
            if (k.StartsWith("Num ", StringComparison.OrdinalIgnoreCase)) return "Num " + k.Substring(4);
            return char.ToUpperInvariant(k[0]) + k.Substring(1);
        }

        /// <summary>Splits a DTE binding "Text Editor::Ctrl+B" into (scope, keys).</summary>
        public static bool TryParseBinding(string binding, out string scope, out string keys)
        {
            scope = null;
            keys = null;
            if (string.IsNullOrEmpty(binding)) return false;
            int i = binding.IndexOf("::", StringComparison.Ordinal);
            if (i <= 0) return false;
            scope = binding.Substring(0, i);
            keys = Normalize(binding.Substring(i + 2));
            return keys != null;
        }

        public static string Binding(string scope, string keys) => (string.IsNullOrEmpty(scope) ? "Global" : scope) + "::" + Normalize(keys);

        /// <summary>True when the first chord of a two-chord shortcut equals a single-chord one (the shorter wins and hides the other).</summary>
        public static bool Overlaps(string a, string b)
        {
            a = Normalize(a);
            b = Normalize(b);
            if (a == null || b == null) return false;
            if (a == b) return true;
            string fa = a.Split(new[] { ", " }, StringSplitOptions.None)[0], fb = b.Split(new[] { ", " }, StringSplitOptions.None)[0];
            bool aSingle = fa == a, bSingle = fb == b;
            return (aSingle && a == fb) || (bSingle && b == fa);
        }

        /// <summary>Global applies everywhere, so it overlaps every scope; other scopes only overlap themselves.</summary>
        public static bool ScopesOverlap(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(a, "Global", StringComparison.OrdinalIgnoreCase) || string.Equals(b, "Global", StringComparison.OrdinalIgnoreCase);

        /// <summary>WPF-like display text ("Alt+←").</summary>
        public static string Display(string keys)
        {
            var n = Normalize(keys);
            if (n == null) return string.Empty;
            return n.Replace("Left Arrow", "←").Replace("Right Arrow", "→").Replace("Up Arrow", "↑").Replace("Down Arrow", "↓");
        }
    }

    /// <summary>A key binding of another (non-UEToVS) command that collides with a planned UEToVS binding.</summary>
    public sealed class KeyConflict
    {
        public string Command { get; set; }
        public string OtherCommand { get; set; }
        public string Scope { get; set; }
        public string OtherScope { get; set; }
        public string Keys { get; set; }
        public string OtherKeys { get; set; }

        public override string ToString() => $"{Command} [{Scope}::{Keys}] ↔ {OtherCommand} [{OtherScope}::{OtherKeys}]";
    }

    /// <summary>Planned change of one command's bindings.</summary>
    public sealed class BindingChange
    {
        public string Command { get; set; }
        public string[] Old { get; set; }
        public string[] New { get; set; }
    }

    /// <summary>
    /// Pure keymap planning: preset → bindings, conflict detection against all other commands' bindings, and the
    /// backup format written before any change (<c>keymap-backup-*.json</c>).
    /// </summary>
    public static class KeymapPlanner
    {
        /// <summary>Bindings each UEToVS command gets under <paramref name="preset"/> (commands without a key get none).</summary>
        public static Dictionary<string, string[]> PresetBindings(KeymapPresetId preset)
        {
            var r = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in CommandCatalog.All)
            {
                var keys = c.KeysFor(preset);
                r[c.Name] = keys == null ? Array.Empty<string>() : new[] { Shortcut.Binding(c.DteScope, keys) };
            }
            return r;
        }

        /// <summary>Changes needed to go from <paramref name="current"/> to <paramref name="target"/> (only commands that differ).</summary>
        public static List<BindingChange> Plan(IReadOnlyDictionary<string, string[]> current, IReadOnlyDictionary<string, string[]> target)
        {
            var changes = new List<BindingChange>();
            foreach (var kv in target)
            {
                current.TryGetValue(kv.Key, out var old);
                old = old ?? Array.Empty<string>();
                var a = new HashSet<string>(old.Select(NormalizeBinding).Where(x => x != null), StringComparer.OrdinalIgnoreCase);
                var b = new HashSet<string>(kv.Value.Select(NormalizeBinding).Where(x => x != null), StringComparer.OrdinalIgnoreCase);
                if (!a.SetEquals(b)) changes.Add(new BindingChange { Command = kv.Key, Old = old, New = kv.Value });
            }
            return changes;
        }

        private static string NormalizeBinding(string binding) =>
            Shortcut.TryParseBinding(binding, out var scope, out var keys) ? scope + "::" + keys : null;

        /// <summary>
        /// Bindings of commands outside <paramref name="ours"/> that overlap (same keys or chord prefix, overlapping
        /// scope) with any of <paramref name="ours"/>' bindings. Also reports collisions between two UEToVS commands.
        /// </summary>
        public static List<KeyConflict> FindConflicts(IReadOnlyDictionary<string, string[]> ours, IEnumerable<KeyValuePair<string, string[]>> all)
        {
            var conflicts = new List<KeyConflict>();
            var mine = new List<(string Cmd, string Scope, string Keys)>();
            foreach (var kv in ours)
                foreach (var b in kv.Value ?? Array.Empty<string>())
                    if (Shortcut.TryParseBinding(b, out var s, out var k)) mine.Add((kv.Key, s, k));
            for (int i = 0; i < mine.Count; i++)
                for (int j = i + 1; j < mine.Count; j++)
                    if (!string.Equals(mine[i].Cmd, mine[j].Cmd, StringComparison.OrdinalIgnoreCase) && Shortcut.ScopesOverlap(mine[i].Scope, mine[j].Scope) && Shortcut.Overlaps(mine[i].Keys, mine[j].Keys))
                        conflicts.Add(new KeyConflict { Command = mine[i].Cmd, Scope = mine[i].Scope, Keys = mine[i].Keys, OtherCommand = mine[j].Cmd, OtherScope = mine[j].Scope, OtherKeys = mine[j].Keys });
            if (mine.Count == 0 || all == null) return conflicts;
            // index our keys by first chord for a cheap pre-filter (VS has ~5000 commands)
            var firstChords = new HashSet<string>(mine.Select(m => m.Keys.Split(new[] { ", " }, StringSplitOptions.None)[0]), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in all)
            {
                if (ours.ContainsKey(kv.Key) || kv.Value == null) continue;
                foreach (var b in kv.Value)
                {
                    if (!Shortcut.TryParseBinding(b, out var s, out var k)) continue;
                    if (!firstChords.Contains(k.Split(new[] { ", " }, StringSplitOptions.None)[0])) continue;
                    foreach (var m in mine)
                        if (Shortcut.ScopesOverlap(m.Scope, s) && Shortcut.Overlaps(m.Keys, k))
                            conflicts.Add(new KeyConflict { Command = m.Cmd, Scope = m.Scope, Keys = m.Keys, OtherCommand = kv.Key, OtherScope = s, OtherKeys = k });
                }
            }
            return conflicts;
        }

        public sealed class Backup
        {
            public int Version { get; set; } = 1;
            public DateTime CreatedUtc { get; set; }
            public string Reason { get; set; }
            /// <summary>Command name → its bindings before the change.</summary>
            public Dictionary<string, string[]> Bindings { get; set; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }

        public static string SerializeBackup(Backup b) => JsonConvert.SerializeObject(b, Formatting.Indented);

        public static Backup ParseBackup(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var b = JsonConvert.DeserializeObject<Backup>(json);
                return b?.Bindings == null ? null : b;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
