using System.Linq;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.7.3 hybrid takeover: VS keeps its IntelliSense engine, error list and squiggles.</summary>
    public class HybridTakeoverTests
    {
        private static readonly TakeoverOptions Hybrid = new TakeoverOptions { KeepVsErrorChecking = true };

        [Fact]
        public void HybridKeepsVsErrorCheckingAndTurnsOffTheRest()
        {
            var store = FakeStore.Vs2022();
            var r = IntelliSenseTakeover.Apply(store, new IntelliSenseBackup(), Hybrid, null);

            Assert.False((bool)store.Values["DisableIntelliSense"]);
            Assert.False((bool)store.Values["DisableErrorReporting"]);
            Assert.False((bool)store.Values["DisableSquiggles"]);
            Assert.True((bool)store.Values["DisableAutoQuickInfo"]);
            Assert.True((bool)store.Values["DisableSemanticColorization"]);
            Assert.True((bool)store.Values["DisableDatabase"]);
            Assert.Equal(3, r.Outcomes.Count(o => o.Status == SettingOutcomeStatus.Skipped));
            Assert.Equal(TakeoverState.TakenOver, IntelliSenseTakeover.GetState(store, Hybrid));
            Assert.Equal(TakeoverState.Partial, IntelliSenseTakeover.GetState(store, new TakeoverOptions()));
        }

        [Fact]
        public void SwitchingToHybridRestoresVsErrorCheckingFromTheBackup()
        {
            var store = FakeStore.Vs2022();
            var backup = new IntelliSenseBackup();
            IntelliSenseTakeover.Apply(store, backup, new TakeoverOptions(), null);
            Assert.True((bool)store.Values["DisableIntelliSense"]);

            var r = IntelliSenseTakeover.Apply(store, backup, Hybrid, null);

            Assert.False((bool)store.Values["DisableIntelliSense"]);
            Assert.False((bool)store.Values["DisableSquiggles"]);
            Assert.Equal(3, r.Count(SettingOutcomeStatus.Reverted));
            Assert.Null(backup.Find(CppIntelliSenseSettings.SpecificPage, "DisableIntelliSense"));
            Assert.NotNull(backup.Find(CppIntelliSenseSettings.SpecificPage, "DisableDatabase"));

            // and back to the full takeover
            IntelliSenseTakeover.Apply(store, backup, new TakeoverOptions(), null);
            Assert.True((bool)store.Values["DisableIntelliSense"]);
            Assert.False(backup.Find(CppIntelliSenseSettings.SpecificPage, "DisableIntelliSense").Original);
        }

        [Fact]
        public void SettingRoundTripsAndIsOffByDefault()
        {
            var schema = HostSettingsSchema.Build(8);
            var row = schema.Single(d => d.Key == "intelliSense.keepVsErrors");
            Assert.Equal("false", row.Default);
            Assert.False(new HostSettings().KeepVsErrorChecking);
        }
    }
}
