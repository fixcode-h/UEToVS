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
    /// Safety net for the VSCT default key bindings (Alt+G, Shift+Alt+G, Alt+←/→, …). VS only applies VSCT
    /// KeyBindings when it merges the package's menu resources, which is not guaranteed after an install / upgrade
    /// (v0.3.0 report: every UEToVS command unbound in VS 18). Shortly after the package loads, every catalog command
    /// whose default has not been decided yet is checked (<see cref="DefaultKeymapPlanner"/>): a command without any
    /// binding gets its default through DTE when no other command uses the key; a command that already has a binding
    /// (VSCT worked, or the user's choice) and a key used elsewhere are left alone. Each default is decided once and
    /// remembered in the VS user settings store (UEToVS\Keymap\defaultsHandled), so a binding the user removes later is
    /// not brought back; a command not registered yet is retried on the next start. A backup is written before any change
    /// (keymap page → 还原上次备份 undoes it). UI thread for DTE; the full command scan yields to keep VS responsive.
    /// </summary>
    internal static class DefaultKeyBindings
    {
        private const string Collection = "UEToVS\\Keymap";
        private const string HandledProperty = "defaultsHandled";
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
                if (decisions.Any(d => d.Outcome == DefaultBindingOutcome.Apply))
                {
                    // Some command has no binding at all: check its key against every other command before binding it.
                    var all = await DteKeymap.ReadAllAsync(dte, scopes);
                    ct.ThrowIfCancellationRequested();
                    ours = DteKeymap.ReadOurs(dte, scopes, "defaults.recheck");
                    decisions = DefaultKeymapPlanner.Plan(CommandCatalog.All, ours.Bindings, all, handled);
                }

                var apply = decisions.Where(d => d.Outcome == DefaultBindingOutcome.Apply).ToList();
                var applied = new List<DefaultBindingDecision>();
                var failed = new List<string>();
                string backupFile = null;
                if (apply.Count > 0)
                {
                    var backup = new KeymapPlanner.Backup { CreatedUtc = DateTime.UtcNow, Reason = "firstRunDefaults" };
                    foreach (var d in apply) backup.Bindings[d.Command] = DteKeymap.Raw(DteKeymap.FindByName(dte, d.Command));
                    backupFile = KeymapBackups.Write(backup);
                    if (backupFile == null)
                    {
                        L.Warn("keymap.defaults.skipped", "No backup could be written; default key bindings not applied (retried next start)",
                               ("pending", apply.Select(d => d.ToString()).ToList()));
                        apply.Clear();
                    }
                }
                foreach (var d in apply)
                {
                    var cmd = DteKeymap.FindByName(dte, d.Command);
                    var back = cmd == null ? null : DteKeymap.Write(cmd, new[] { d.Binding }, scopes, d.Command);
                    if (back != null && back.Contains(d.Binding, StringComparer.OrdinalIgnoreCase)) applied.Add(d);
                    else failed.Add(d.ToString());
                }

                var remember = new HashSet<string>(handled, StringComparer.OrdinalIgnoreCase);
                foreach (var k in DefaultKeymapPlanner.ToRemember(decisions)) remember.Add(k);
                foreach (var d in applied) remember.Add(d.HandledKey);
                if (remember.Count != handled.Count) VsUserSettings.Set(Collection, HandledProperty, DefaultKeymapPlanner.FormatHandled(remember));

                List<string> Of(DefaultBindingOutcome o) => decisions.Where(d => d.Outcome == o).Select(d => d.ToString()).ToList();
                L.Info("keymap.apply", null, ("reason", "firstRunDefaults"), ("applied", applied.Select(d => d.Command + "=" + d.Binding).ToList()),
                       ("failed", failed), ("alreadyBound", Of(DefaultBindingOutcome.AlreadyBound)), ("keyTaken", Of(DefaultBindingOutcome.KeyTaken)),
                       ("commandMissing", Of(DefaultBindingOutcome.CommandMissing)), ("previouslyHandled", Of(DefaultBindingOutcome.AlreadyHandled).Count),
                       ("backup", backupFile), ("scopes", scopes.ToString()));
                if (decisions.Any(d => d.Outcome == DefaultBindingOutcome.KeyTaken))
                    L.Warn("keymap.defaults.keyTaken", "Some UEToVS default shortcuts are used by other commands and were not bound (see the keymap page)",
                           ("keyTaken", Of(DefaultBindingOutcome.KeyTaken)));
            }
        }
    }
}
