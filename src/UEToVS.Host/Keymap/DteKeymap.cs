using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Keymap
{
    /// <summary>Result of reading the bindings of every UEToVS command (<see cref="DteKeymap.ReadOurs"/>).</summary>
    internal sealed class OurBindings
    {
        /// <summary>Catalog name → canonical bindings ("Text Editor::Alt+G"). Commands VS doesn't know are absent.</summary>
        public Dictionary<string, string[]> Bindings { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Catalog name → the name DTE gives the command (differs from the VSCT CanonicalName).</summary>
        public Dictionary<string, string> DteNames { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Missing { get; } = new List<string>();
        /// <summary>Raw bindings that could not be parsed (shown as-is, logged).</summary>
        public List<string> Unparsed { get; } = new List<string>();
    }

    /// <summary>
    /// DTE access for key bindings, shared by the keymap page and the first-run default bindings. UEToVS commands are
    /// looked up by command-set GUID + id (<c>Commands.Item("{guid}", id)</c>): the DTE <i>name</i> of a VSCT command is
    /// not its CanonicalName (VS prefixes the parent menu path, and the localized name differs), so looking up
    /// "UEToVS.GoToDefinition" by name fails and every command looked unbound (v0.3.0 bug). Bindings are converted to
    /// canonical English scope names when read and back to this VS's localized names when written (<see cref="BindingScopes"/>).
    /// UI thread only.
    /// </summary>
    internal static class DteKeymap
    {
        private static readonly Logger L = Log.For("Keymap");

        public static string CommandSetGuid => "{" + PackageGuids.CommandSetString + "}";

        public static Command Find(DTE dte, CommandInfo c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte == null || c == null) return null;
            try
            {
                return dte.Commands.Item(CommandSetGuid, c.Id);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is COMException)
            {
                return null; // not registered (menu resources not merged yet)
            }
        }

        /// <summary>A UEToVS catalog name (by guid + id) or any other DTE command name.</summary>
        public static Command FindByName(DTE dte, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var info = CommandCatalog.ByName(name);
            if (info != null) return Find(dte, info);
            try
            {
                return dte.Commands.Item(name, -1);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is COMException)
            {
                return null;
            }
        }

        /// <summary>Bindings exactly as DTE returns them (localized scope names).</summary>
        public static string[] Raw(Command c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (c == null) return Array.Empty<string>();
            try
            {
                return BindingScopes.FromDte(c.Bindings);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is COMException || ex is InvalidCastException)
            {
                return Array.Empty<string>();
            }
        }

        public static string SafeName(Command c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return c?.Name;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is COMException)
            {
                return null;
            }
        }

        /// <summary>Learns this VS's localized "Global" / "Text Editor" scope names.</summary>
        public static BindingScopes DetectScopes(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var scopes = BindingScopes.Detect(name =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    return BindingScopes.FromDte(dte.Commands.Item(name, -1).Bindings);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is COMException)
                {
                    return null;
                }
            }, CultureInfo.CurrentUICulture.Name);
            L.Info("keymap.scopes", null, ("global", scopes.LocalGlobal), ("textEditor", scopes.LocalTextEditor), ("source", scopes.Source),
                   ("uiCulture", CultureInfo.CurrentUICulture.Name));
            return scopes;
        }

        /// <summary>Reads every catalog command's bindings by guid + id; logs a per-command summary (<c>keymap.read.ours</c>).</summary>
        public static OurBindings ReadOurs(DTE dte, BindingScopes scopes, string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var r = new OurBindings();
            foreach (var c in CommandCatalog.All)
            {
                var cmd = Find(dte, c);
                if (cmd == null) { r.Missing.Add(c.Name); continue; }
                r.DteNames[c.Name] = SafeName(cmd) ?? string.Empty;
                var raw = Raw(cmd);
                r.Bindings[c.Name] = scopes.CanonicalAll(raw);
                foreach (var b in raw)
                    if (scopes.Canonical(b) == null) r.Unparsed.Add(c.Name + " → " + b);
            }
            var bound = r.Bindings.Where(kv => kv.Value.Length > 0).Select(kv => Short(kv.Key) + "=" + string.Join(" | ", kv.Value)).ToList();
            var unbound = r.Bindings.Where(kv => kv.Value.Length == 0).Select(kv => Short(kv.Key)).ToList();
            var defaultsMissing = CommandCatalog.All.Where(c => c.VaKeys != null && r.Bindings.TryGetValue(c.Name, out var b) && b.Length == 0).Select(c => Short(c.Name) + "(" + c.VaKeys + ")").ToList();
            var renamed = r.DteNames.Where(kv => !string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase)).ToList();
            L.Info("keymap.read.ours", null, ("reason", reason), ("commands", CommandCatalog.All.Count), ("resolved", r.Bindings.Count), ("bound", bound.Count),
                   ("bindings", bound), ("unbound", unbound), ("vaDefaultsNotBound", defaultsMissing), ("missing", r.Missing), ("unparsed", r.Unparsed),
                   ("dteNameDiffers", renamed.Count), ("dteNameSample", renamed.Take(3).Select(kv => kv.Key + " → " + kv.Value).ToList()),
                   ("scopes", scopes.ToString()));
            if (r.Missing.Count > 0)
                L.Warn("keymap.read.missing", "UEToVS commands not registered in VS (menu resources not merged?)", ("missing", r.Missing));
            return r;
        }

        private static string Short(string catalogName) => catalogName.StartsWith("UEToVS.", StringComparison.Ordinal) ? catalogName.Substring(7) : catalogName;

        /// <summary>
        /// Canonical bindings of every command that has one (~5000 commands; yields every 300 to keep VS responsive).
        /// UEToVS commands are keyed by their catalog name (guid + id), so they line up with <see cref="OurBindings"/>.
        /// </summary>
        public static async Task<List<KeyValuePair<string, string[]>>> ReadAllAsync(DTE dte, BindingScopes scopes)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var list = new List<KeyValuePair<string, string[]>>(6000);
            int i = 0, failed = 0;
            var ourSet = PackageGuids.CommandSet;
            using (L.Time("keymap.readAll", 2000))
            {
                foreach (Command cmd in dte.Commands)
                {
                    try
                    {
                        var raw = Raw(cmd);
                        if (raw.Length > 0)
                        {
                            string name = cmd.Name;
                            if (Guid.TryParse(cmd.Guid, out var g) && g == ourSet) name = CommandCatalog.ById(cmd.ID)?.Name ?? name;
                            if (string.IsNullOrEmpty(name)) name = cmd.Guid + ":" + cmd.ID;
                            list.Add(new KeyValuePair<string, string[]>(name, scopes.CanonicalAll(raw)));
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is COMException)
                    {
                        failed++; // some commands throw on Name/Bindings; skip
                    }
                    if (++i % 300 == 0) await Task.Yield();
                }
            }
            L.Info("keymap.read", null, ("commands", i), ("bound", list.Count), ("failed", failed));
            return list;
        }

        /// <summary>
        /// Writes <paramref name="bindings"/> (canonical or raw) to <paramref name="cmd"/> with localized scopes, then reads
        /// them back. Returns the read-back canonical bindings, or null when DTE refused the change.
        /// </summary>
        public static string[] Write(Command cmd, IEnumerable<string> bindings, BindingScopes scopes, string label)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var local = (bindings ?? Array.Empty<string>()).Where(b => !string.IsNullOrWhiteSpace(b)).Select(scopes.Local)
                                                           .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            try
            {
                cmd.Bindings = local.Cast<object>().ToArray();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is COMException)
            {
                L.Error("keymap.write.failed", "DTE refused the key binding", ex, ("command", label), ("dteName", SafeName(cmd)), ("bindings", local));
                return null;
            }
            var back = scopes.CanonicalAll(Raw(cmd));
            var want = scopes.CanonicalAll(local);
            if (!new HashSet<string>(back, StringComparer.OrdinalIgnoreCase).SetEquals(want))
                L.Warn("keymap.write.mismatch", "Bindings read back differ from what was written", ("command", label), ("written", local), ("readBack", back));
            return back;
        }
    }

    /// <summary>Backups written before every keymap change: %LocalAppData%\UEToVS\KeymapBackups\keymap-backup-*.json.</summary>
    internal static class KeymapBackups
    {
        private static readonly Logger L = Log.For("Keymap");

        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEToVS", "KeymapBackups");

        /// <summary>Returns the file written, or null (then nothing may be changed).</summary>
        public static string Write(KeymapPlanner.Backup backup)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string file = Path.Combine(Directory, "keymap-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(file, KeymapPlanner.SerializeBackup(backup));
                return file;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Error("keymap.backup.failed", "Writing the keymap backup failed; nothing was changed", ex, ("dir", Directory));
                return null;
            }
        }

        public static string Latest()
        {
            try
            {
                return System.IO.Directory.Exists(Directory)
                    ? System.IO.Directory.GetFiles(Directory, "keymap-backup-*.json").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
                    : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Warn("keymap.backup.list.failed", "Listing keymap backups failed", ex);
                return null;
            }
        }
    }
}
