using System;
using System.Collections.Generic;
using System.Globalization;
using UEToVS.Host.Logic.Settings;

namespace UEToVS.Host.Logic.Horde
{
    /// <summary>One BuildConfiguration.xml key UEToVS manages: <c>&lt;Section&gt;&lt;Key&gt;Value&lt;/Key&gt;&lt;/Section&gt;</c>.</summary>
    public sealed class HordeConfigEntry
    {
        public HordeConfigEntry(string section, string key, string value)
        {
            Section = section;
            Key = key;
            Value = value;
        }

        public string Section { get; }
        public string Key { get; }

        /// <summary>Value to write; null = UEToVS has nothing to say (put back what was there before UEToVS wrote it).</summary>
        public string Value { get; }

        public string Id => Section + "/" + Key;

        public override string ToString() => Id + "=" + (Value ?? "(default)");
    }

    /// <summary>
    /// Settings → the BuildConfiguration.xml keys UEToVS writes. Every managed key is always in the plan (null when it is
    /// left to UE), so turning a setting back to "UE default" or turning the feature off restores what was there before.
    /// Keys the engine's UBT does not know (<see cref="UbtConfigKeys"/>) are dropped and reported in <see cref="Skipped"/>.
    /// </summary>
    public sealed class HordeConfigPlan
    {
        public const string SectionBuild = "BuildConfiguration";
        public const string SectionUba = "UnrealBuildAccelerator";
        public const string SectionHorde = "Horde";

        /// <summary>First engine version with UBA (and the Horde section in BuildConfiguration.xml).</summary>
        public static readonly Version MinimumEngine = new Version(5, 4);

        public List<HordeConfigEntry> Entries { get; } = new List<HordeConfigEntry>();

        /// <summary>Ids with a value that the engine's UBT does not read (not written).</summary>
        public List<string> Skipped { get; } = new List<string>();

        /// <summary>BuildConfiguration/MaxParallelActions written (0 when off).</summary>
        public int LocalCores { get; private set; }

        /// <summary>Every key UEToVS may write, in file order.</summary>
        public static IReadOnlyList<(string Section, string Key)> ManagedKeys { get; } = new[]
        {
            (SectionBuild, "bAllowUBAExecutor"),
            (SectionBuild, "MaxParallelActions"),
            (SectionHorde, "Server"),
            (SectionHorde, "Pool"),
            (SectionHorde, "MaxCores"),
            (SectionHorde, "Cluster"),
            (SectionHorde, "Encryption"),
            (SectionHorde, "ConnectionMode"),
            (SectionUba, "bLinkRemote"),
            (SectionUba, "bPrintSummary"),
            (SectionUba, "bDisableRemote"),
            (SectionUba, "bForceBuildAllRemote"),
            (SectionUba, "bForcedRetryRemote"),
            (SectionUba, "bLaunchVisualizer"),
            (SectionUba, "bStoreObjFilesCompressed"),
            (SectionUba, "Zone"),
            (SectionUba, "RootDir"),
            (SectionUba, "StoreCapacityGb"),
        };

        /// <param name="ubaElsewhere">
        /// UBA is turned on by a BuildConfiguration.xml UEToVS does not write (e.g. a hand-made user file). With the feature
        /// off, only the local core limit is written then, so "本机参与编译的核心数" still holds.
        /// </param>
        public static HordeConfigPlan Build(HostSettings s, int logicalCores, UbtConfigKeys known = null, bool ubaElsewhere = false)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            int local = 0;
            if (s != null && !s.HordeEnabled && ubaElsewhere)
            {
                local = HordeSettingsSchema.EffectiveLocalCores(s.HordeLocalCores, logicalCores);
                values[SectionBuild + "/MaxParallelActions"] = local.ToString(CultureInfo.InvariantCulture);
            }
            else if (s != null && s.HordeEnabled)
            {
                local = HordeSettingsSchema.EffectiveLocalCores(s.HordeLocalCores, logicalCores);
                values[SectionBuild + "/bAllowUBAExecutor"] = "true";
                values[SectionBuild + "/MaxParallelActions"] = local.ToString(CultureInfo.InvariantCulture);
                Text(values, SectionHorde + "/Server", s.HordeServer);
                Text(values, SectionHorde + "/Pool", s.HordePool);
                if (s.HordeMaxCores > 0) values[SectionHorde + "/MaxCores"] = s.HordeMaxCores.ToString(CultureInfo.InvariantCulture);
                Text(values, SectionHorde + "/Cluster", s.HordeCluster);
                Text(values, SectionHorde + "/Encryption", s.HordeEncryption);
                Text(values, SectionHorde + "/ConnectionMode", s.HordeConnectionMode);
                Tri(values, SectionUba + "/bLinkRemote", s.HordeLinkRemote);
                Tri(values, SectionUba + "/bPrintSummary", s.HordePrintSummary);
                Tri(values, SectionUba + "/bDisableRemote", s.HordeDisableRemote);
                Tri(values, SectionUba + "/bForceBuildAllRemote", s.HordeForceRemote);
                Tri(values, SectionUba + "/bForcedRetryRemote", s.HordeAllowRetry);
                Tri(values, SectionUba + "/bLaunchVisualizer", s.HordeLaunchVisualizer);
                Tri(values, SectionUba + "/bStoreObjFilesCompressed", s.HordeStoreObjCompressed);
                Text(values, SectionUba + "/Zone", s.HordeZone);
                Text(values, SectionUba + "/RootDir", s.HordeRootDir);
                if (s.HordeStoreCapacityGb > 0) values[SectionUba + "/StoreCapacityGb"] = s.HordeStoreCapacityGb.ToString(CultureInfo.InvariantCulture);
            }

            var plan = new HordeConfigPlan { LocalCores = local };
            foreach (var (section, key) in ManagedKeys)
            {
                values.TryGetValue(section + "/" + key, out var v);
                if (v != null && known != null && !known.Supports(section, key))
                {
                    plan.Skipped.Add(section + "/" + key);
                    v = null;
                }
                plan.Entries.Add(new HordeConfigEntry(section, key, v));
            }
            return plan;
        }

        private static void Text(Dictionary<string, string> d, string id, string v)
        {
            if (!string.IsNullOrWhiteSpace(v)) d[id] = v.Trim();
        }

        private static void Tri(Dictionary<string, string> d, string id, string v)
        {
            if (v == "true" || v == "false") d[id] = v;
        }
    }
}
