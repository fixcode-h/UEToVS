using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Options
{
    /// <summary>
    /// User layer of the host settings since v0.3.0: one string property per descriptor key in the VS per-user settings
    /// store (UEToVS\Settings), written by the UEToVS settings window. On first use the values are migrated from the
    /// old Tools &gt; Options pages (GeneralOptionsPage / LoggingOptionsPage, still loaded as hidden DialogPages so their
    /// stored values can be read). UI thread only (the VS settings store is not free-threaded for writes).
    /// </summary>
    internal sealed class SettingsStore
    {
        private const string Collection = "UEToVS\\Settings";
        private const string VersionProperty = "_schemaVersion";
        private const string CurrentVersion = "1";
        private static readonly Logger L = Log.For("Settings");

        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public SettingsStore(IReadOnlyList<SettingDescriptor> schema, Func<HostSettings> legacy)
        {
            Schema = schema;
            Load(legacy);
        }

        public IReadOnlyList<SettingDescriptor> Schema { get; }

        public IReadOnlyDictionary<string, string> Values => _values;

        private void Load(Func<HostSettings> legacy)
        {
            string version = VsUserSettings.Get(Collection, VersionProperty);
            if (version == null)
            {
                // First run of v0.3.0: copy the old option-page values once, so nobody loses their settings.
                HostSettings old = null;
                try { old = legacy?.Invoke(); }
                catch (Exception ex) { L.Warn("settings.migrate.read.failed", "Reading the old option pages failed; starting from defaults", ex); }
                var values = HostSettingsSchema.Codec.ToValues(old ?? new HostSettings());
                int written = 0;
                foreach (var kv in values)
                    if (VsUserSettings.Set(Collection, kv.Key, kv.Value)) { _values[kv.Key] = kv.Value; written++; }
                VsUserSettings.Set(Collection, VersionProperty, CurrentVersion);
                L.Info("settings.migrated", "Settings moved from Tools > Options to the UEToVS settings window", ("values", written), ("fromOptionPages", old != null));
                return;
            }
            foreach (var d in Schema.Where(x => x.HasValue))
            {
                string v = VsUserSettings.Get(Collection, d.Key);
                if (v != null) _values[d.Key] = v;
            }
            L.Debug("settings.store.loaded", null, ("values", _values.Count), ("version", version));
        }

        /// <summary>Writes the given values (only those that changed). Returns the changed keys.</summary>
        public List<string> Write(IReadOnlyDictionary<string, string> values)
        {
            var changed = new List<string>();
            foreach (var d in Schema.Where(x => x.HasValue))
            {
                if (!values.TryGetValue(d.Key, out var raw)) continue;
                string v = SettingsModel.Normalize(d, raw, out _);
                _values.TryGetValue(d.Key, out var old);
                if (string.Equals(old ?? d.Default, v, StringComparison.Ordinal) && old != null) continue;
                if (VsUserSettings.Set(Collection, d.Key, v))
                {
                    _values[d.Key] = v;
                    changed.Add(d.Key);
                }
            }
            return changed;
        }
    }
}
