using System;
using System.Collections.Generic;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// Upgrades of the stored settings (UEToVS\Settings in the VS user store). The store should hold only values the user
    /// changed, so a later default change reaches everyone who never touched the setting.
    /// </summary>
    public static class SettingsUpgrade
    {
        public const string CurrentVersion = "3";

        /// <summary>
        /// Defaults of an older schema that changed later, keyed by the version that still had them. v0.3.0 (schema 1)
        /// migrated every value, defaults included, so its old defaults cannot be told apart from a user choice and are dropped.
        /// </summary>
        private static readonly Dictionary<string, (string Key, string OldDefault)[]> ChangedDefaults = new Dictionary<string, (string, string)[]>(StringComparer.Ordinal)
        {
            ["1"] = new[] { ("intelliSense.mode", nameof(IntelliSenseTakeoverMode.Global)) },
        };

        /// <summary>
        /// v0.5.1 (schema 3): settings that no longer exist. The takeover switch became the "Off" scope; the browse
        /// database / background code analysis / VS Blueprint scan switches follow the scope.
        /// </summary>
        public static readonly string[] RetiredKeys =
        {
            "intelliSense.takeover", "intelliSense.disableDatabase", "intelliSense.disableBackgroundCodeAnalysis", "vs.blueprintSupport",
        };

        /// <summary>
        /// Values to write for retired settings found in the store (<paramref name="retired"/>: key → stored value):
        /// a stored "intelliSense.takeover" = false (the user turned the takeover off) becomes scope Off, whatever scope was stored. The retired keys are deleted afterwards.
        /// </summary>
        public static Dictionary<string, string> MigrateRetired(IReadOnlyDictionary<string, string> retired)
        {
            var set = new Dictionary<string, string>(StringComparer.Ordinal);
            if (retired != null && retired.TryGetValue("intelliSense.takeover", out var takeover) &&
                string.Equals(takeover, "false", StringComparison.OrdinalIgnoreCase))
                set["intelliSense.mode"] = nameof(IntelliSenseTakeoverMode.Off);
            return set;
        }

        /// <summary>Values to write when migrating from the old option pages: only those that differ from the defaults.</summary>
        public static Dictionary<string, string> NonDefault(IReadOnlyDictionary<string, string> values, IReadOnlyList<SettingDescriptor> schema)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var defaults = Defaults(schema);
            foreach (var kv in values)
                if (!defaults.TryGetValue(kv.Key, out var def) || !string.Equals(def ?? string.Empty, kv.Value ?? string.Empty, StringComparison.Ordinal))
                    result[kv.Key] = kv.Value;
            return result;
        }

        /// <summary>Stored keys to delete when upgrading from <paramref name="storedVersion"/>: values equal to today's default and changed old defaults.</summary>
        public static List<string> KeysToDrop(string storedVersion, IReadOnlyDictionary<string, string> stored, IReadOnlyList<SettingDescriptor> schema)
        {
            var drop = new List<string>();
            if (string.Equals(storedVersion, CurrentVersion, StringComparison.Ordinal)) return drop;
            var defaults = Defaults(schema);
            foreach (var kv in stored)
            {
                if (defaults.TryGetValue(kv.Key, out var def) && string.Equals(def ?? string.Empty, kv.Value ?? string.Empty, StringComparison.Ordinal))
                    drop.Add(kv.Key);
            }
            if (storedVersion != null && ChangedDefaults.TryGetValue(storedVersion, out var changed))
                foreach (var (key, oldDefault) in changed)
                    if (stored.TryGetValue(key, out var v) && string.Equals(v, oldDefault, StringComparison.Ordinal) && !drop.Contains(key))
                        drop.Add(key);
            return drop;
        }

        private static Dictionary<string, string> Defaults(IReadOnlyList<SettingDescriptor> schema)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var x in schema) if (x.HasValue) d[x.Key] = x.Default;
            return d;
        }
    }
}
