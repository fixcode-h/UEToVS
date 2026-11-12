using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// VS's own Unreal Engine integration ("Blueprint Support" on Project &gt; Configure Tools for Unreal Engine) scans every
    /// Blueprint of the .uproject on each solution load. Microsoft does not document where that switch is stored, so UEToVS
    /// discovers it: boolean options whose name mentions Blueprint, on the C/C++ options page or in the user settings store
    /// under a collection about Unreal / Blueprints. They are switched off with the same backup-before-write algorithm as the
    /// IntelliSense takeover (<see cref="IntelliSenseTakeover.Apply"/>), and restored from the backup when the user turns VS's
    /// Blueprint support back on. Pure rules here; the stores are in the host.
    /// </summary>
    public static class VsBlueprintSupport
    {
        /// <summary>Prefix of pages that live in the VS user settings store (collection path follows).</summary>
        public const string SettingsStorePrefix = "settings:";

        /// <summary>Backup file of the Blueprint options UEToVS changed.</summary>
        public const string BackupFileName = "vs-blueprint-backup.json";

        /// <summary>Option names that switch VS's Blueprint scan: they mention Blueprint (excluding UEToVS's own settings).</summary>
        public static bool IsCandidateName(string name) =>
            !string.IsNullOrEmpty(name) && name.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) >= 0
            && name.IndexOf("UEToVS", StringComparison.OrdinalIgnoreCase) < 0;

        /// <summary>Settings-store collections that may hold VS's Unreal options (name mentions Unreal or Blueprint, not UEToVS).</summary>
        public static bool IsUnrealCollection(string collectionPath) =>
            !string.IsNullOrEmpty(collectionPath)
            && (collectionPath.IndexOf("Unreal", StringComparison.OrdinalIgnoreCase) >= 0 || collectionPath.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) >= 0)
            && collectionPath.IndexOf("UEToVS", StringComparison.OrdinalIgnoreCase) < 0;

        /// <summary>The value that turns the scan off: true for "Disable…" style names, otherwise false.</summary>
        public static bool OffValue(string name) =>
            name != null && (name.StartsWith("Disable", StringComparison.OrdinalIgnoreCase)
                             || name.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>One definition per discovered (page, name), in a stable order, ready for <see cref="IntelliSenseTakeover.Apply"/>.</summary>
        public static IReadOnlyList<CppSettingDefinition> BuildTable(IEnumerable<KeyValuePair<string, string>> pageAndName)
        {
            if (pageAndName == null) return Array.Empty<CppSettingDefinition>();
            return pageAndName
                .Where(p => !string.IsNullOrEmpty(p.Key) && IsCandidateName(p.Value))
                .Distinct(new PairComparer())
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
                .Select(p => new CppSettingDefinition("vsBlueprint:" + p.Key + "/" + p.Value, "VS Unreal Engine Blueprint support: " + p.Value,
                    CppSettingGate.Always, new CppPropertyCandidate(p.Key, p.Value, OffValue(p.Value))))
                .ToList();
        }

        private sealed class PairComparer : IEqualityComparer<KeyValuePair<string, string>>
        {
            public bool Equals(KeyValuePair<string, string> a, KeyValuePair<string, string> b) =>
                string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(KeyValuePair<string, string> p) =>
                StringComparer.OrdinalIgnoreCase.GetHashCode(p.Key ?? "") ^ StringComparer.OrdinalIgnoreCase.GetHashCode(p.Value ?? "");
        }
    }
}
