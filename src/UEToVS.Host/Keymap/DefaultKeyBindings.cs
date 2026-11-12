using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Commands;

namespace UEToVS.Host.Keymap
{
    /// <summary>
    /// First-run default key bindings. VS only applies VSCT KeyBindings when it merges the package's menu resources,
    /// which is not guaranteed after an install / upgrade (v0.3.0 report: every UEToVS command unbound in VS 18), and VS's
    /// own commands often hold the same keys. Shortly after the package loads, every catalog command whose default has not
    /// been decided yet is planned (<see cref="DefaultKeymapPlanner"/>): UEToVS shortcuts take priority, so a command
    /// without a binding gets its default and the VS bindings that overlap it are removed. A binding the user gave a
    /// UEToVS command is left alone. Each default is decided once and remembered in the VS user settings store
    /// (UEToVS\Keymap\defaultsHandled.v2 — v0.3.3 starts a new list, so defaults skipped by v0.3.1/v0.3.2 because the key was
    /// taken are applied now). A command not registered yet is retried on the next start. One backup covering every
    /// command touched (ours and VS's) is written before any change (keymap page → 还原上次备份 undoes it).
    /// UI thread for DTE; the full command scan yields to keep VS responsive.
    /// </summary>
    internal static class DefaultKeyBindings
    {
        private const string Collection = "UEToVS\\Keymap";
        private const string HandledProperty = "defaultsHandled.v2";
        private static readonly Logger L = Log.For("Keymap");

        public static async Task EnsureAsync(DTE dte, CancellationToken ct)
        {
            if (dte == null) return;
            // Not on the startup path: the bindings matter once the user starts typing.
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var handled = DefaultKeymapPlanner.ParseHandled(VsUserSettings.Get(Collection, HandledProperty));
            var defaults = CommandCatalog.All.Where(c => c.VaKeys != null).ToList();
            if (defaults.All(c => handled.Contains(c.Name + "=" + Shortcut.Binding(c.DteScope, c.VaKeys))))
            {
                L.Debug("keymap.defaults.done", "All default key bindings were handled on an earlier start", ("handled", handled.Count));
                return;
            }

            using (L.Time("keymap.defaults", 500))
            {
                var scopes = DteKeymap.DetectScopes(dte);
                var ours = DteKeymap.ReadOurs(dte, scopes, "defaults");
                var decisions = DefaultKeymapPlanner.Plan(CommandCatalog.All, ours.Bindings, null, handled);
                if (decisions.Any(d => d.NeedsConflictScan))
                {
                    // Find the VS bindings that would shadow our defaults (removed below: UEToVS wins).
                    var all = await DteKeymap.ReadAllAsync(dte, scopes);
                    ct.ThrowIfCancellationRequested();
                    ours = DteKeymap.ReadOurs(dte, scopes, "defaults.recheck");
                    decisions = DefaultKeymapPlanner.Plan(CommandCatalog.All, ours.Bindings, all, handled);
                }

                var apply = decisions.Where(d => d.Outcome == DefaultBindingOutcome.Apply || d.Overrides.Count > 0).ToList();
                // VS command → canonical bindings to remove
                var removals = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in apply)
                    foreach (var o in d.Overrides)
                    {
                        if (!removals.TryGetValue(o.Key, out var set)) removals[o.Key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(o.Value);
                    }
                var applied = new List<DefaultBindingDecision>();
                var failed = new List<string>();
                var removed = new List<string>();
                string backupFile = null;
                if (apply.Count > 0)
                {
                    var backup = new KeymapPlanner.Backup { CreatedUtc = DateTime.UtcNow, Reason = "firstRunDefaults" };
                    foreach (var d in apply.Where(x => x.Outcome == DefaultBindingOutcome.Apply)) backup.Bindings[d.Command] = DteKeymap.Raw(DteKeymap.FindByName(dte, d.Command));
                    foreach (var name in removals.Keys) backup.Bindings[name] = DteKeymap.Raw(DteKeymap.FindByName(dte, name));
                    backupFile = KeymapBackups.Write(backup);
                    if (backupFile == null)
                    {
                        L.Warn("keymap.defaults.skipped", "No backup could be written; default key bindings not applied (retried next start)",
                               ("pending", apply.Select(d => d.ToString()).ToList()));
                        apply.Clear();
                        removals.Clear();
                    }
                }
                // 1) take the keys away from VS's commands, 2) bind ours
                var removeFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in removals)
                {
                    var cmd = DteKeymap.FindByName(dte, kv.Key);
                    if (cmd == null) { removeFailed.Add(kv.Key); continue; }
                    var raw = DteKeymap.Raw(cmd);
                    var keep = raw.Where(b => !(scopes.Canonical(b) is string canon && kv.Value.Contains(canon))).ToArray();
                    var back = DteKeymap.Write(cmd, keep, scopes, kv.Key);
                    if (back == null || back.Any(b => kv.Value.Contains(b))) removeFailed.Add(kv.Key);
                    else removed.Add(kv.Key + " -[" + string.Join(" | ", kv.Value) + "]");
                }
                foreach (var d in apply)
                {
                    bool overridesOk = d.Overrides.All(o => !removeFailed.Contains(o.Key));
                    if (d.Outcome != DefaultBindingOutcome.Apply)
                    {
                        if (overridesOk) applied.Add(d);
                        else failed.Add(d.ToString());
                        continue;
                    }
                    var cmd = DteKeymap.FindByName(dte, d.Command);
                    var back = cmd == null ? null : DteKeymap.Write(cmd, new[] { d.Binding }, scopes, d.Command);
                    if (back != null && back.Contains(d.Binding, StringComparer.OrdinalIgnoreCase) && overridesOk) applied.Add(d);
                    else failed.Add(d.ToString());
                }

                var remember = new HashSet<string>(handled, StringComparer.OrdinalIgnoreCase);
                foreach (var k in DefaultKeymapPlanner.ToRemember(decisions)) remember.Add(k);
                foreach (var d in applied) remember.Add(d.HandledKey);
                if (remember.Count != handled.Count) VsUserSettings.Set(Collection, HandledProperty, DefaultKeymapPlanner.FormatHandled(remember));

                List<string> Of(DefaultBindingOutcome o) => decisions.Where(d => d.Outcome == o).Select(d => d.ToString()).ToList();
                L.Info("keymap.apply", null, ("reason", "firstRunDefaults"), ("applied", applied.Select(d => d.Command + "=" + d.Binding).ToList()),
                       ("removedFromVs", removed), ("removeFailed", removeFailed.ToList()),
                       ("failed", failed), ("alreadyBound", Of(DefaultBindingOutcome.AlreadyBound)), ("keyTaken", Of(DefaultBindingOutcome.KeyTaken)),
                       ("commandMissing", Of(DefaultBindingOutcome.CommandMissing)), ("previouslyHandled", Of(DefaultBindingOutcome.AlreadyHandled).Count),
                       ("backup", backupFile), ("scopes", scopes.ToString()));
                if (decisions.Any(d => d.Outcome == DefaultBindingOutcome.KeyTaken))
                    L.Info("keymap.defaults.keyTaken", "Some UEToVS defaults use a key the user gave another UEToVS command; left alone",
                           ("keyTaken", Of(DefaultBindingOutcome.KeyTaken)));
            }
        }
    }
}
