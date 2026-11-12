using System.Collections.Generic;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class SettingsUpgradeTests
    {
        private static readonly IReadOnlyList<UEToVS.UI.Logic.SettingDescriptor> Schema = HostSettingsSchema.Build(16);

        [Fact]
        public void TakeoverScope_DefaultsToUnrealSolutionsOnly()
        {
            Assert.Equal(IntelliSenseTakeoverMode.UnrealSolutionOnly, new HostSettings().IntelliSenseTakeoverMode);
            Assert.Equal(nameof(IntelliSenseTakeoverMode.UnrealSolutionOnly), Schema.Find("intelliSense.mode").Default);
        }

        [Fact]
        public void Schema1_DropsOldGlobalDefaultAndValuesEqualToDefaults()
        {
            var stored = new Dictionary<string, string>
            {
                ["intelliSense.mode"] = "Global",       // v0.3.0 migrated its old default
                ["nav.ctrlClick"] = "true",             // equals today's default
                ["nav.historyCapacity"] = "250",        // a real user choice
            };
            var drop = SettingsUpgrade.KeysToDrop("1", stored, Schema);
            Assert.Contains("intelliSense.mode", drop);
            Assert.Contains("nav.ctrlClick", drop);
            Assert.DoesNotContain("nav.historyCapacity", drop);
        }

        [Fact]
        public void CurrentVersion_KeepsEverything()
        {
            var stored = new Dictionary<string, string> { ["intelliSense.mode"] = "Global" };
            Assert.Empty(SettingsUpgrade.KeysToDrop(SettingsUpgrade.CurrentVersion, stored, Schema));
        }

        [Fact]
        public void Migration_WritesOnlyNonDefaultValues()
        {
            var values = HostSettingsSchema.Codec.ToValues(new HostSettings { NavigationHistoryCapacity = 250 });
            var written = SettingsUpgrade.NonDefault(values, Schema);
            Assert.Equal("250", written["nav.historyCapacity"]);
            Assert.False(written.ContainsKey("intelliSense.mode"));
            Assert.False(written.ContainsKey("nav.ctrlClick"));
        }
    }

    internal static class SchemaExt
    {
        public static UEToVS.UI.Logic.SettingDescriptor Find(this IReadOnlyList<UEToVS.UI.Logic.SettingDescriptor> s, string key)
        {
            foreach (var d in s) if (d.Key == key) return d;
            return null;
        }
    }
}
