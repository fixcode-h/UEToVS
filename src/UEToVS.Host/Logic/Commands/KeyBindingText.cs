using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.Commands
{
    /// <summary>
    /// Binding scope names as DTE spells them. DTE returns and expects <b>localized</b> scope names
    /// ("全局::Alt+G" in a Chinese VS, "Global::Alt+G" in an English one). UEToVS works with the canonical English
    /// names internally ("Global", "Text Editor": <see cref="CommandInfo.DteScope"/>, presets, conflict detection) and
    /// converts at the DTE boundary: <see cref="Canonical"/> when reading, <see cref="Local"/> when writing.
    /// </summary>
    public sealed class BindingScopes
    {
        public const string Global = "Global";
        public const string TextEditor = "Text Editor";

        /// <summary>Known translations of "Global" / "Text Editor" in VS language packs (reading accepts all of them).</summary>
        private static readonly string[] KnownGlobal =
        {
            "Global", "全局", "全域", "Globale", "Globalny", "Globální", "Глобальный", "グローバル", "전역", "Genel",
        };

        private static readonly string[] KnownTextEditor =
        {
            "Text Editor", "文本编辑器", "文字編輯器", "Text-Editor", "Éditeur de texte", "Editor di testo", "Editor de texto",
            "Edytor tekstu", "Textový editor", "Текстовый редактор", "テキスト エディター", "텍스트 편집기", "Metin Düzenleyicisi",
        };

        public BindingScopes(string localGlobal, string localTextEditor)
        {
            LocalGlobal = string.IsNullOrWhiteSpace(localGlobal) ? Global : localGlobal.Trim();
            LocalTextEditor = string.IsNullOrWhiteSpace(localTextEditor) ? TextEditor : localTextEditor.Trim();
        }

        /// <summary>The name this VS uses for the Global scope (what DTE accepts when writing).</summary>
        public string LocalGlobal { get; }

        public string LocalTextEditor { get; }

        /// <summary>How the names were found ("probe", "culture" or "default"; for the log).</summary>
        public string Source { get; private set; } = "default";

        public static BindingScopes English => new BindingScopes(Global, TextEditor);

        /// <summary>Well-known VS default bindings used to learn the localized scope names: (command, keys, canonical scope).</summary>
        public static IReadOnlyList<(string Command, string Keys, string Scope)> Probes { get; } = new[]
        {
            ("File.SaveAll", "Ctrl+Shift+S", Global),
            ("Edit.Undo", "Ctrl+Z", Global),
            ("Build.BuildSolution", "Ctrl+Shift+B", Global),
            ("Edit.LineDelete", "Ctrl+Shift+L", TextEditor),
            ("Edit.CommentSelection", "Ctrl+K, Ctrl+C", TextEditor),
            ("Edit.LineCut", "Ctrl+L", TextEditor),
        };

        /// <summary>
        /// Learns the localized scope names from <see cref="Probes"/> (<paramref name="bindingsOf"/> returns a command's raw
        /// DTE bindings, or null when the command does not exist). Falls back to the VS UI culture (zh-Hans / zh-Hant), then English.
        /// </summary>
        public static BindingScopes Detect(Func<string, IEnumerable<string>> bindingsOf, string uiCulture = null)
        {
            string global = null, editor = null;
            foreach (var p in Probes)
            {
                if ((p.Scope == Global ? global : editor) != null) continue;
                IEnumerable<string> bindings;
                try
                {
                    bindings = bindingsOf?.Invoke(p.Command);
                }
                catch (Exception)
                {
                    bindings = null; // command missing in this VS edition
                }
                if (bindings == null) continue;
                string want = Shortcut.Normalize(p.Keys);
                foreach (var b in bindings)
                {
                    if (!Split(b, out var scope, out var keys) || Shortcut.Normalize(keys) != want) continue;
                    if (p.Scope == Global) global = scope;
                    else editor = scope;
                    break;
                }
            }
            string source = global != null || editor != null ? "probe" : "default";
            if (global == null || editor == null)
            {
                var (cg, ce) = ForCulture(uiCulture);
                if (cg != null) source = global == null && editor == null ? "culture" : source + "+culture";
                global = global ?? cg;
                editor = editor ?? ce;
            }
            return new BindingScopes(global, editor) { Source = source };
        }

        private static (string Global, string Editor) ForCulture(string culture)
        {
            if (string.IsNullOrEmpty(culture)) return (null, null);
            string c = culture.ToLowerInvariant();
            if (c == "zh-tw" || c == "zh-hk" || c == "zh-mo" || c.StartsWith("zh-hant", StringComparison.Ordinal)) return ("全域", "文字編輯器");
            if (c.StartsWith("zh", StringComparison.Ordinal)) return ("全局", "文本编辑器");
            return (null, null);
        }

        /// <summary>Localized / English scope name → canonical English ("全局" → "Global"); other scopes are returned unchanged.</summary>
        public string ToCanonical(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return scope;
            string s = scope.Trim();
            if (Eq(s, LocalGlobal) || KnownGlobal.Any(k => Eq(k, s))) return Global;
            if (Eq(s, LocalTextEditor) || KnownTextEditor.Any(k => Eq(k, s))) return TextEditor;
            return s;
        }

        /// <summary>Canonical English scope → this VS's name ("Global" → "全局"); other scopes are returned unchanged.</summary>
        public string ToLocal(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return LocalGlobal;
            string c = ToCanonical(scope);
            if (c == Global) return LocalGlobal;
            if (c == TextEditor) return LocalTextEditor;
            return scope.Trim();
        }

        /// <summary>Raw DTE binding ("全局::alt+g") → canonical ("Global::Alt+G"), or null when it cannot be parsed.</summary>
        public string Canonical(string raw)
        {
            if (!Split(raw, out var scope, out var keys)) return null;
            string k = Shortcut.Normalize(keys);
            return k == null ? null : ToCanonical(scope) + "::" + k;
        }

        /// <summary>
        /// Binding to write through DTE: the scope is localized; the keys are normalized when parseable, otherwise kept
        /// as they are (a binding restored from a backup is written back exactly as DTE returned it).
        /// </summary>
        public string Local(string binding)
        {
            if (!Split(binding, out var scope, out var keys)) return binding;
            return ToLocal(scope) + "::" + (Shortcut.Normalize(keys) ?? keys);
        }

        /// <summary>All canonical bindings of a raw DTE list (unparseable ones are dropped; duplicates removed, order kept).</summary>
        public string[] CanonicalAll(IEnumerable<string> raw)
        {
            if (raw == null) return Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var r in raw)
            {
                var c = Canonical(r);
                if (c != null && seen.Add(c)) list.Add(c);
            }
            return list.ToArray();
        }

        /// <summary>"scope::keys" split on the first "::" (a "::" can't be part of a scope; keys may contain ':' only as a key).</summary>
        public static bool Split(string binding, out string scope, out string keys)
        {
            scope = keys = null;
            if (string.IsNullOrWhiteSpace(binding)) return false;
            int i = binding.IndexOf("::", StringComparison.Ordinal);
            if (i <= 0 || i + 2 >= binding.Length) return false;
            scope = binding.Substring(0, i).Trim();
            keys = binding.Substring(i + 2).Trim();
            return scope.Length > 0 && keys.Length > 0;
        }

        /// <summary>DTE <c>Command.Bindings</c> value (object[] of strings, a single string, or null) → strings.</summary>
        public static string[] FromDte(object bindings)
        {
            switch (bindings)
            {
                case null: return Array.Empty<string>();
                case string s: return string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : new[] { s };
                case IEnumerable e: return e.Cast<object>().Select(o => o?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                default: return Array.Empty<string>();
            }
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public override string ToString() => $"{LocalGlobal} | {LocalTextEditor} ({Source})";
    }

    /// <summary>What the first-run step decided for one UEToVS command's default (Visual Assist preset / VSCT) binding.</summary>
    public enum DefaultBindingOutcome
    {
        /// <summary>No binding yet: bind it. Non-UEToVS commands using the key lose that binding (<see cref="DefaultBindingDecision.Overrides"/>): UEToVS wins.</summary>
        Apply = 0,
        /// <summary>The command already has a binding (the VSCT default reached VS, or the user chose one): left alone. When it is the default, VS bindings that shadow it are removed.</summary>
        AlreadyBound = 1,
        /// <summary>Another UEToVS command uses the key (the user moved one there): left alone.</summary>
        KeyTaken = 2,
        /// <summary>Handled on an earlier start (applied, already bound or skipped then): never re-applied.</summary>
        AlreadyHandled = 3,
        /// <summary>The command is not registered in VS (menu resources not merged yet): retried on the next start.</summary>
        CommandMissing = 4,
    }

    public sealed class DefaultBindingDecision
    {
        public string Command { get; set; }
        /// <summary>Canonical target binding ("Text Editor::Alt+G").</summary>
        public string Binding { get; set; }
        public DefaultBindingOutcome Outcome { get; set; }
        public string[] Existing { get; set; } = Array.Empty<string>();
        public string TakenBy { get; set; }
        public string TakenByBinding { get; set; }
        /// <summary>Bindings of non-UEToVS commands (command, canonical binding) that overlap the default and are removed so UEToVS wins.</summary>
        public List<KeyValuePair<string, string>> Overrides { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>Key recorded once handled, so the decision is not revisited (a later catalog default gets a new key).</summary>
        public string HandledKey => Command + "=" + Binding;

        /// <summary>Applying this decision needs every other command's bindings (the conflict scan).</summary>
        public bool NeedsConflictScan =>
            Outcome == DefaultBindingOutcome.Apply || (Outcome == DefaultBindingOutcome.AlreadyBound && Existing.Contains(Binding, StringComparer.OrdinalIgnoreCase));

        public override string ToString()
        {
            string ov = Overrides.Count == 0 ? string.Empty : " overrides " + string.Join(", ", Overrides.Select(o => o.Key + " [" + o.Value + "]"));
            return Outcome == DefaultBindingOutcome.KeyTaken ? $"{Command} [{Binding}] taken by {TakenBy} [{TakenByBinding}]"
                : Outcome == DefaultBindingOutcome.AlreadyBound ? $"{Command} [{Binding}] has {string.Join(" | ", Existing)}{ov}"
                : $"{Command} [{Binding}] {Outcome}{ov}";
        }
    }

    /// <summary>
    /// First-run defaults: VS applies VSCT KeyBindings only when it merges the package's menu resources, which is not
    /// guaranteed after an install / upgrade, and VS's own commands often use the same keys. UEToVS shortcuts take
    /// priority (user decision, v0.3.3): a command without a binding gets its Visual Assist default, and every
    /// non-UEToVS binding that overlaps it (same keys or chord prefix, overlapping scope) is removed — after a backup.
    /// A binding the user gave a UEToVS command is never touched, nor a key the user moved another UEToVS command onto.
    /// Every default is decided once and remembered (<see cref="DefaultBindingDecision.HandledKey"/>).
    /// </summary>
    public static class DefaultKeymapPlanner
    {
        /// <summary>
        /// <paramref name="ours"/>: catalog name → canonical bindings (a missing key = command not registered).
        /// <paramref name="others"/>: canonical bindings of every other command (null = not read yet; candidates come back
        /// as <see cref="DefaultBindingOutcome.Apply"/> without overrides — read all bindings, then plan again).
        /// </summary>
        public static List<DefaultBindingDecision> Plan(IEnumerable<CommandInfo> commands, IReadOnlyDictionary<string, string[]> ours,
                                                        IEnumerable<KeyValuePair<string, string[]>> others, ICollection<string> handled)
        {
            var result = new List<DefaultBindingDecision>();
            var otherList = others?.Where(kv => kv.Value != null && kv.Value.Length > 0 && (ours == null || !ours.ContainsKey(kv.Key))).ToList();
            var planned = new List<(string Command, string Scope, string Keys)>();
            foreach (var c in commands)
            {
                if (string.IsNullOrEmpty(c.VaKeys) || Shortcut.Normalize(c.VaKeys) == null) continue;
                var d = new DefaultBindingDecision { Command = c.Name, Binding = Shortcut.Binding(c.DteScope, c.VaKeys) };
                result.Add(d);
                if (handled != null && handled.Contains(d.HandledKey)) { d.Outcome = DefaultBindingOutcome.AlreadyHandled; continue; }
                if (ours == null || !ours.TryGetValue(c.Name, out var existing) || existing == null) { d.Outcome = DefaultBindingOutcome.CommandMissing; continue; }
                d.Existing = existing;
                string scope = c.DteScope, keys = Shortcut.Normalize(c.VaKeys);
                if (existing.Length > 0)
                {
                    d.Outcome = DefaultBindingOutcome.AlreadyBound;
                    if (otherList != null && d.NeedsConflictScan) AddOverlaps(d.Overrides, otherList, scope, keys);
                    continue;
                }
                // other UEToVS commands' current bindings (the user may have moved one onto this key) and defaults planned so far
                var hit = FirstOverlap(ours.Where(kv => !string.Equals(kv.Key, c.Name, StringComparison.OrdinalIgnoreCase)), scope, keys);
                if (hit == null)
                    foreach (var p in planned)
                        if (Shortcut.ScopesOverlap(p.Scope, scope) && Shortcut.Overlaps(p.Keys, keys)) { hit = (p.Command, p.Scope + "::" + p.Keys); break; }
                if (hit != null)
                {
                    d.Outcome = DefaultBindingOutcome.KeyTaken;
                    d.TakenBy = hit.Value.Command;
                    d.TakenByBinding = hit.Value.Binding;
                    continue;
                }
                if (otherList != null) AddOverlaps(d.Overrides, otherList, scope, keys);
                d.Outcome = DefaultBindingOutcome.Apply;
                planned.Add((c.Name, scope, keys));
            }
            return result;
        }

        private static void AddOverlaps(List<KeyValuePair<string, string>> into, IEnumerable<KeyValuePair<string, string[]>> all, string scope, string keys)
        {
            foreach (var kv in all)
                foreach (var b in kv.Value ?? Array.Empty<string>())
                    if (Shortcut.TryParseBinding(b, out var s, out var k) && Shortcut.ScopesOverlap(s, scope) && Shortcut.Overlaps(k, keys))
                        into.Add(new KeyValuePair<string, string>(kv.Key, b));
        }

        private static (string Command, string Binding)? FirstOverlap(IEnumerable<KeyValuePair<string, string[]>> all, string scope, string keys)
        {
            foreach (var kv in all)
                foreach (var b in kv.Value ?? Array.Empty<string>())
                    if (Shortcut.TryParseBinding(b, out var s, out var k) && Shortcut.ScopesOverlap(s, scope) && Shortcut.Overlaps(k, keys))
                        return (kv.Key, b);
            return null;
        }

        /// <summary>
        /// Every binding of a non-UEToVS command that collides with <paramref name="ours"/> (command → canonical bindings to
        /// remove). Used when a preset or an edited shortcut is written: UEToVS shortcuts take priority over VS's.
        /// </summary>
        public static Dictionary<string, HashSet<string>> Removals(IReadOnlyDictionary<string, string[]> ours, IEnumerable<KeyValuePair<string, string[]>> all)
        {
            var r = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in KeymapPlanner.FindConflicts(ours, all))
            {
                if (CommandCatalog.ByName(c.OtherCommand) != null || ours.ContainsKey(c.OtherCommand)) continue;
                if (!r.TryGetValue(c.OtherCommand, out var set)) r[c.OtherCommand] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(c.OtherScope + "::" + c.OtherKeys);
            }
            return r;
        }

        /// <summary>Decisions that are final and should be remembered (everything except a missing command).</summary>
        public static IEnumerable<string> ToRemember(IEnumerable<DefaultBindingDecision> decisions) =>
            decisions.Where(d => (d.Outcome == DefaultBindingOutcome.AlreadyBound && d.Overrides.Count == 0) || d.Outcome == DefaultBindingOutcome.KeyTaken || d.Outcome == DefaultBindingOutcome.AlreadyHandled)
                     .Select(d => d.HandledKey);

        public static HashSet<string> ParseHandled(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return set;
            foreach (var part in text.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (part.Trim().Length > 0) set.Add(part.Trim());
            return set;
        }

        public static string FormatHandled(IEnumerable<string> keys) =>
            string.Join(";", keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal));
    }
}
