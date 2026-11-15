using System.Linq;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class Vs18OnlySwitchTests
    {
        [Fact]
        public void Vs2022GreysOutSettingsJsonOnlySwitches()
        {
            var schema = HostSettingsSchema.Build(8, 17);
            foreach (var f in VsUnrealFeatures.Features)
            {
                var row = schema.Single(d => d.Key == f.SettingKey);
                if (f.Vs18Only)
                {
                    Assert.Equal(HostSettingsSchema.Vs18OnlyGateKey, row.EnabledWhenKey);
                    Assert.EndsWith("（仅 VS 2026）", row.TitleZh);
                }
                else
                {
                    Assert.Null(row.EnabledWhenKey);
                }
            }
            // the gate key is never a real setting, so the rows stay greyed out
            Assert.DoesNotContain(schema, d => d.Key == HostSettingsSchema.Vs18OnlyGateKey);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(18)]
        public void Vs2026AndUnknownVersionKeepEverySwitchEditable(int vsMajor)
        {
            var schema = HostSettingsSchema.Build(8, vsMajor);
            foreach (var f in VsUnrealFeatures.Features)
                Assert.Null(schema.Single(d => d.Key == f.SettingKey).EnabledWhenKey);
        }

        [Fact]
        public void SemanticIndexAlsoHasADteSwitch()
        {
            Assert.False(VsUnrealFeatures.ById(VsUnrealFeatures.SemanticIndex).Vs18Only);
            Assert.True(VsUnrealFeatures.ById(VsUnrealFeatures.Blueprints).Vs18Only);
        }
    }
}
