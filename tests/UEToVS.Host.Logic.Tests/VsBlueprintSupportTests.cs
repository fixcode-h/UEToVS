using System.Collections.Generic;
using System.Linq;
using UEToVS.Host.Logic.IntelliSense;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class VsBlueprintSupportTests
    {
        private static KeyValuePair<string, string> P(string page, string name) => new KeyValuePair<string, string>(page, name);

        [Fact]
        public void OnlyBlueprintOptionsBecomeDefinitionsWithTheRightOffValue()
        {
            var table = VsBlueprintSupport.BuildTable(new[]
            {
                P("C/C++ Specific", "DisableIntelliSense"),
                P("C/C++ Specific", "EnableBlueprintSupport"),
                P("settings:UnrealEngine\\Integration", "DisableBlueprintScanning"),
                P("settings:UnrealEngine\\Integration", "enableblueprintsupport"),
                P("settings:UnrealEngine\\Integration", "EnableBlueprintSupport"), // duplicate (case)
                P("settings:UEToVS", "UEToVSBlueprintThreads"),
            });
            Assert.Equal(3, table.Count);
            var c = table.Select(d => d.Candidates.Single()).ToList();
            Assert.False(c.Single(x => x.Page == "C/C++ Specific").TakeoverValue);
            Assert.True(c.Single(x => x.Name == "DisableBlueprintScanning").TakeoverValue);
        }

        [Fact]
        public void CollectionFilterLooksAtUnrealAndBlueprintOnly()
        {
            Assert.True(VsBlueprintSupport.IsUnrealCollection("ApplicationPrivateSettings\\Microsoft\\VisualStudio\\UnrealEngine"));
            Assert.True(VsBlueprintSupport.IsUnrealCollection("Blueprints"));
            Assert.False(VsBlueprintSupport.IsUnrealCollection("Text Editor\\C/C++"));
            Assert.False(VsBlueprintSupport.IsUnrealCollection("UEToVS\\Unreal"));
            Assert.False(VsBlueprintSupport.IsUnrealCollection(""));
        }

        [Fact]
        public void ApplyAndRestoreRoundTripThroughTheBackup()
        {
            var store = new MemStore();
            store.Values[("settings:UnrealEngine", "BlueprintSupportEnabled")] = true;
            var table = VsBlueprintSupport.BuildTable(new[] { P("settings:UnrealEngine", "BlueprintSupportEnabled") });
            var backup = new IntelliSenseBackup();
            var report = IntelliSenseTakeover.Apply(store, backup, null, b => { }, table);
            Assert.Equal(1, report.Count(SettingOutcomeStatus.Changed));
            Assert.False((bool)store.Values[("settings:UnrealEngine", "BlueprintSupportEnabled")]);
            IntelliSenseTakeover.Restore(store, backup);
            Assert.True((bool)store.Values[("settings:UnrealEngine", "BlueprintSupportEnabled")]);
            Assert.True(backup.IsEmpty);
        }

        private sealed class MemStore : ICppSettingsStore
        {
            public readonly Dictionary<(string, string), object> Values = new Dictionary<(string, string), object>();

            public IReadOnlyList<string> GetPropertyNames(string page) => Values.Keys.Where(k => k.Item1 == page).Select(k => k.Item2).ToList();

            public bool TryRead(string page, string name, out object value, out string error)
            {
                error = null;
                return Values.TryGetValue((page, name), out value);
            }

            public bool TryWrite(string page, string name, object value, out string error)
            {
                error = null;
                Values[(page, name)] = value;
                return true;
            }
        }
    }
}
