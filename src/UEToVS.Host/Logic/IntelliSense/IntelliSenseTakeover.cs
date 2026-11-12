using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.IntelliSense
{
    public enum SettingOutcomeStatus
    {
        /// <summary>Value changed by UEToVS (takeover) or restored to the original (restore).</summary>
        Changed,

        /// <summary>Already at the wanted value; nothing written.</summary>
        AlreadyAtTarget,

        /// <summary>None of the candidate property names exists on this VS.</summary>
        Missing,

        /// <summary>Excluded by an option (e.g. Disable Database off).</summary>
        Skipped,

        /// <summary>Property exists but holds a non-boolean value; left alone.</summary>
        NotBoolean,

        /// <summary>Read or write failed (error text attached).</summary>
        Failed,

        /// <summary>No longer wanted (option turned off) and restored to the backed-up original.</summary>
        Reverted,
    }

    public sealed class SettingOutcome
    {
        public string Key { get; set; }
        public string Page { get; set; }
        public string Property { get; set; }
        public SettingOutcomeStatus Status { get; set; }
        public bool? Before { get; set; }
        public bool? After { get; set; }
        public string Error { get; set; }

        /// <summary>Candidate names that were probed and not found (for calibration logs).</summary>
        public List<string> MissingCandidates { get; set; } = new List<string>();

        public override string ToString() => $"{Key}: {Status} {Page}/{Property} {Before}->{After} {Error}";
    }

    public sealed class TakeoverOptions
    {
        public bool DisableDatabase { get; set; } = true;
    }

    public sealed class TakeoverReport
    {
        public List<SettingOutcome> Outcomes { get; } = new List<SettingOutcome>();

        public int Count(SettingOutcomeStatus s) => Outcomes.Count(o => o.Status == s);

        public bool BackupWritten { get; set; }

        /// <summary>Error when the backup could not be persisted; nothing was changed in that case.</summary>
        public string BackupError { get; set; }

        /// <summary>True when every property that exists is at its takeover value (or was skipped / missing).</summary>
        public bool FullyTakenOver => Outcomes.All(o => o.Status == SettingOutcomeStatus.Changed || o.Status == SettingOutcomeStatus.AlreadyAtTarget ||
                                                         o.Status == SettingOutcomeStatus.Missing || o.Status == SettingOutcomeStatus.Skipped ||
                                                         o.Status == SettingOutcomeStatus.Reverted);
    }

    public enum TakeoverState
    {
        /// <summary>VS's own C++ IntelliSense is active (nothing taken over).</summary>
        NotTakenOver,

        /// <summary>Some, but not all, of the existing properties are at their takeover value.</summary>
        Partial,

        /// <summary>All existing properties are at their takeover value.</summary>
        TakenOver,

        /// <summary>None of the known properties exists (unknown VS version): cannot take over.</summary>
        Unknown,
    }

    /// <summary>
    /// Pure takeover / restore algorithm for VS's C++ IntelliSense settings (§7.5). The caller supplies the settings
    /// store (DTE on the UI thread in VS) and a persist callback; the backup is persisted BEFORE any property is written,
    /// so a crash in between never loses the user's original values.
    /// </summary>
    public static class IntelliSenseTakeover
    {
        /// <summary>Resolves which candidate property exists for a definition. Null when none.</summary>
        public static CppPropertyCandidate Resolve(ICppSettingsStore store, CppSettingDefinition def, out object value, List<string> missing)
        {
            foreach (var c in def.Candidates)
            {
                if (store.TryRead(c.Page, c.Name, out value, out _)) return c;
                missing?.Add(c.QualifiedName);
            }
            value = null;
            return null;
        }

        /// <summary>
        /// Turns VS's C++ IntelliSense off. Original values are added to <paramref name="backup"/> (never overwritten) and
        /// <paramref name="persistBackup"/> is called once, before the first write, if the backup changed.
        /// Settings that are no longer wanted (gated off) but present in the backup are reverted to their original value.
        /// </summary>
        public static TakeoverReport Apply(ICppSettingsStore store, IntelliSenseBackup backup, TakeoverOptions options,
                                           Action<IntelliSenseBackup> persistBackup, IEnumerable<CppSettingDefinition> table = null,
                                           Func<DateTime> clock = null)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (backup == null) throw new ArgumentNullException(nameof(backup));
            options = options ?? new TakeoverOptions();
            clock = clock ?? (() => DateTime.UtcNow);
            var report = new TakeoverReport();

            // Phase 1: plan (read only).
            var writes = new List<(SettingOutcome Outcome, CppPropertyCandidate Candidate, bool Value)>();
            bool backupChanged = false;
            foreach (var def in table ?? CppIntelliSenseSettings.All)
            {
                var o = new SettingOutcome { Key = def.Key };
                report.Outcomes.Add(o);
                var c = Resolve(store, def, out object raw, o.MissingCandidates);
                if (c == null)
                {
                    o.Status = SettingOutcomeStatus.Missing;
                    continue;
                }
                o.Page = c.Page;
                o.Property = c.Name;
                if (!(raw is bool current))
                {
                    o.Status = SettingOutcomeStatus.NotBoolean;
                    o.Error = "value type " + (raw?.GetType().Name ?? "null");
                    continue;
                }
                o.Before = current;
                var existing = backup.Find(c.Page, c.Name);

                if (!IsWanted(def, options))
                {
                    if (existing != null && current != existing.Original)
                    {
                        o.Status = SettingOutcomeStatus.Reverted;
                        writes.Add((o, c, existing.Original));
                    }
                    else
                    {
                        if (existing != null) backup.Entries.Remove(existing);
                        if (existing != null) backupChanged = true;
                        o.Status = SettingOutcomeStatus.Skipped;
                        o.After = current;
                    }
                    continue;
                }

                if (current == c.TakeoverValue)
                {
                    o.Status = SettingOutcomeStatus.AlreadyAtTarget;
                    o.After = current;
                    continue;
                }

                if (existing == null)
                {
                    backup.Entries.Add(new IntelliSenseBackupEntry
                    {
                        Key = def.Key, Page = c.Page, Property = c.Name, Original = current, Applied = c.TakeoverValue, ChangedUtc = clock(),
                    });
                    backupChanged = true;
                }
                o.Status = SettingOutcomeStatus.Changed;
                writes.Add((o, c, c.TakeoverValue));
            }

            // Phase 2: persist the backup before touching anything.
            if (backupChanged)
            {
                try
                {
                    persistBackup?.Invoke(backup);
                    report.BackupWritten = true;
                }
                catch (Exception ex)
                {
                    report.BackupError = ex.GetType().Name + ": " + ex.Message;
                    foreach (var w in writes)
                    {
                        w.Outcome.Status = SettingOutcomeStatus.Failed;
                        w.Outcome.Error = "backup not saved; property left unchanged";
                    }
                    return report;
                }
            }

            // Phase 3: write.
            bool reverted = false;
            foreach (var (o, c, value) in writes)
            {
                if (store.TryWrite(c.Page, c.Name, value, out string error))
                {
                    o.After = value;
                    if (o.Status == SettingOutcomeStatus.Reverted)
                    {
                        var e = backup.Find(c.Page, c.Name);
                        if (e != null) backup.Entries.Remove(e);
                        reverted = true;
                    }
                }
                else
                {
                    o.Status = SettingOutcomeStatus.Failed;
                    o.Error = error;
                }
            }
            if (reverted)
            {
                try { persistBackup?.Invoke(backup); }
                catch (Exception ex) { report.BackupError = ex.GetType().Name + ": " + ex.Message; }
            }
            return report;
        }

        /// <summary>
        /// Writes back exactly the backed-up original values. Entries that were restored (or whose property no longer
        /// exists) are removed from <paramref name="backup"/>; failed ones stay so the user can retry.
        /// </summary>
        public static TakeoverReport Restore(ICppSettingsStore store, IntelliSenseBackup backup)
        {
            var report = new TakeoverReport();
            if (backup == null || backup.IsEmpty) return report;
            foreach (var e in backup.Entries.ToList())
            {
                var o = new SettingOutcome { Key = e.Key, Page = e.Page, Property = e.Property, After = e.Original };
                report.Outcomes.Add(o);
                if (!store.TryRead(e.Page, e.Property, out object raw, out string readError))
                {
                    o.Status = SettingOutcomeStatus.Missing;
                    o.Error = readError;
                    backup.Entries.Remove(e); // property is gone on this VS: nothing to restore
                    continue;
                }
                if (raw is bool b) o.Before = b;
                if (o.Before == e.Original)
                {
                    o.Status = SettingOutcomeStatus.AlreadyAtTarget;
                    backup.Entries.Remove(e);
                    continue;
                }
                if (store.TryWrite(e.Page, e.Property, e.Original, out string error))
                {
                    o.Status = SettingOutcomeStatus.Changed;
                    backup.Entries.Remove(e);
                }
                else
                {
                    o.Status = SettingOutcomeStatus.Failed;
                    o.Error = error;
                    o.After = o.Before;
                }
            }
            return report;
        }

        /// <summary>Current state, judged from the properties that exist on this VS.</summary>
        public static TakeoverState GetState(ICppSettingsStore store, TakeoverOptions options, IEnumerable<CppSettingDefinition> table = null)
        {
            options = options ?? new TakeoverOptions();
            int present = 0, atTarget = 0;
            foreach (var def in table ?? CppIntelliSenseSettings.All)
            {
                if (!IsWanted(def, options)) continue;
                var c = Resolve(store, def, out object raw, null);
                if (c == null || !(raw is bool v)) continue;
                present++;
                if (v == c.TakeoverValue) atTarget++;
            }
            if (present == 0) return TakeoverState.Unknown;
            if (atTarget == present) return TakeoverState.TakenOver;
            return atTarget == 0 ? TakeoverState.NotTakenOver : TakeoverState.Partial;
        }

        public static bool IsWanted(CppSettingDefinition def, TakeoverOptions options)
        {
            switch (def.Gate)
            {
                case CppSettingGate.DisableDatabaseOption: return options.DisableDatabase;
                default: return true;
            }
        }

        /// <summary>
        /// Property names on the page that look related to IntelliSense but are not in the table, for calibration logs
        /// (e.g. a renamed option in a future VS).
        /// </summary>
        public static List<string> UnknownInterestingProperties(IReadOnlyList<string> names, IEnumerable<CppSettingDefinition> table = null)
        {
            var result = new List<string>();
            if (names == null) return result;
            var known = new HashSet<string>((table ?? CppIntelliSenseSettings.All.Concat(CppIntelliSenseSettings.BackgroundCodeAnalysis)).SelectMany(d => d.Candidates).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            string[] hints = { "Disable", "IntelliSense", "Squiggle", "Coloriz", "QuickInfo", "MemberList", "Database", "ExternalDependenc", "CodeAnalysis" };
            foreach (var n in names)
            {
                if (known.Contains(n)) continue;
                if (hints.Any(h => n.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)) result.Add(n);
            }
            return result;
        }
    }
}
