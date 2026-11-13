using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class VsUnrealFeaturesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "uetovs-vsjson-" + Guid.NewGuid().ToString("N"));
        private readonly string _path;

        public VsUnrealFeaturesTests()
        {
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "settings.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private const string UserFile =
            "/* Visual Studio Settings File */\r\n{\r\n" +
            "  \"languages.cpp.intelliSense.browsingNavigation.wholeCodebaseSemanticIndex.maximumWorkerThreads\": 28,\r\n" +
            "  \"languages.cpp.intelliSense.browsingNavigation.wholeCodebaseSemanticIndex.enabled\": true,\r\n" +
            "  \"copilot.general.tools.toolSettings\": [ { \"toolName\": \"get_file\" } ],\r\n" +
            "  \"unrealEngine.enableCodeAnalysis\": false\r\n}";

        [Fact]
        public void TakeoverTurnsOffSettingsJsonFeaturesAndRestoreLeavesTheFileAsItWas()
        {
            File.WriteAllText(_path, UserFile);
            var dte = FakeStore.Vs2022();
            var store = new CompositeCppSettingsStore(dte, new UnifiedSettingsJsonStore(_path));
            var backup = new IntelliSenseBackup();
            var r = IntelliSenseTakeover.Apply(store, backup, new TakeoverOptions(), null);

            var json = new UnifiedSettingsJsonStore(_path);
            foreach (var key in new[] { VsUnrealFeatures.JsonSemanticIndex, VsUnrealFeatures.JsonCodeLensReferences, VsUnrealFeatures.JsonCodeLensFields,
                                        VsUnrealFeatures.JsonBlueprints, VsUnrealFeatures.JsonUhtAnalysis, VsUnrealFeatures.JsonCodeLens })
            {
                Assert.True(json.TryRead(VsUnrealFeatures.JsonPage, key, out object v, out _));
                Assert.Equal(false, v);
            }
            Assert.Equal(SettingOutcomeStatus.AlreadyAtTarget, r.Outcomes.Single(o => o.Key == "vsUhtAnalysis").Status);
            string text = File.ReadAllText(_path);
            Assert.StartsWith("/* Visual Studio Settings File */", text);
            Assert.Contains("maximumWorkerThreads\": 28", text);
            Assert.Contains("get_file", text);

            IntelliSenseTakeover.Restore(store, backup);
            Assert.True(backup.IsEmpty);
            text = File.ReadAllText(_path);
            // back to the defaults (true): written as absent keys, which VS reads as on
            Assert.True(json.TryRead(VsUnrealFeatures.JsonPage, VsUnrealFeatures.JsonSemanticIndex, out object semantic, out _));
            Assert.Equal(true, semantic);
            Assert.DoesNotContain("wholeCodebaseSemanticIndex.enabled", text);
            Assert.DoesNotContain("enableCodeLensForReferences", text);
            Assert.DoesNotContain("unrealEngine.enableBlueprints", text);
            Assert.Contains("\"unrealEngine.enableCodeAnalysis\": false", text); // was already off before UEToVS
        }

        [Fact]
        public void SwitchedOffFeatureIsLeftAloneAndRevertedWhenTurnedOffLater()
        {
            File.WriteAllText(_path, UserFile);
            var store = new CompositeCppSettingsStore(FakeStore.Vs2022(), new UnifiedSettingsJsonStore(_path));
            var backup = new IntelliSenseBackup();
            var keepBlueprints = new HashSet<string> { VsUnrealFeatures.SemanticIndex, VsUnrealFeatures.UhtAnalysis };
            IntelliSenseTakeover.Apply(store, backup, new TakeoverOptions { TurnOffVsFeatures = keepBlueprints }, null);
            Assert.DoesNotContain("unrealEngine.enableBlueprints", File.ReadAllText(_path));

            // keep the semantic index after it was turned off: reverted from the backup
            IntelliSenseTakeover.Apply(store, backup, new TakeoverOptions { TurnOffVsFeatures = new HashSet<string> { VsUnrealFeatures.UhtAnalysis } }, null);
            Assert.True(new UnifiedSettingsJsonStore(_path).TryRead(VsUnrealFeatures.JsonPage, VsUnrealFeatures.JsonSemanticIndex, out object semantic, out _));
            Assert.Equal(true, semantic);
            Assert.DoesNotContain(backup.Entries, e => e.Page == VsUnrealFeatures.JsonPage && e.Property == VsUnrealFeatures.JsonSemanticIndex);
        }

        [Fact]
        public void MissingOrBrokenSettingsJsonIsNeverWritten()
        {
            var json = new UnifiedSettingsJsonStore(_path);
            Assert.False(json.TryRead(VsUnrealFeatures.JsonPage, VsUnrealFeatures.JsonBlueprints, out _, out string error));
            Assert.Contains("not found", error);
            Assert.False(json.TryWrite(VsUnrealFeatures.JsonPage, VsUnrealFeatures.JsonBlueprints, false, out _));
            Assert.False(File.Exists(_path));

            File.WriteAllText(_path, "/* Visual Studio Settings File */\r\n{ \"a\": tru");
            Assert.False(json.TryWrite(VsUnrealFeatures.JsonPage, VsUnrealFeatures.JsonBlueprints, false, out _));
            Assert.EndsWith("tru", File.ReadAllText(_path));
        }

        [Fact]
        public void EveryFeatureHasASwitchDefaultingToOffAndARowInTheTable()
        {
            var schema = HostSettingsSchema.Build(8);
            var values = HostSettingsSchema.Codec.ToValues(new HostSettings());
            foreach (var f in VsUnrealFeatures.Features)
            {
                var d = schema.Single(x => x.Key == f.SettingKey);
                Assert.Equal("true", d.Default);
                Assert.Equal("true", values[f.SettingKey]);
                Assert.Contains(VsUnrealFeatures.Table, t => t.Feature == f.Id);
            }
            Assert.Equal(VsUnrealFeatures.Features.Select(f => f.Id).OrderBy(x => x), new HostSettings().TurnOffVsFeatures().OrderBy(x => x));
            var off = new HostSettings { TurnOffVsBlueprints = false };
            Assert.False(off.TurnOffVsFeatures().Contains(VsUnrealFeatures.Blueprints));
            Assert.True(off.VsBlueprintSupport);
        }
    }
}
