using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class AutoNumberModelTests
    {
        private static SettingDescriptor Threads() => new SettingDescriptor
        {
            Key = "indexing.cppThreads", Kind = SettingKind.Int, Default = "0", Min = 0, Max = 16, AutoLabelZh = "自动（4）", AutoLabelEn = "Auto (4)", AutoValue = 4,
        };

        private static SettingDescriptor Port() => new SettingDescriptor
        {
            Key = "unreal.editorLinkPort", Kind = SettingKind.Int, Default = "0", Min = 0, Max = 65535, AutoLabelZh = "自动", AutoLabelEn = "Auto",
        };

        [Fact]
        public void SupportsAutoOnlyForIntWithAutoLabel()
        {
            Assert.True(AutoNumberModel.SupportsAuto(Threads()));
            Assert.False(AutoNumberModel.SupportsAuto(new SettingDescriptor { Kind = SettingKind.Int }));
            Assert.False(AutoNumberModel.SupportsAuto(new SettingDescriptor { Kind = SettingKind.Text, AutoLabelZh = "x" }));
            Assert.False(AutoNumberModel.SupportsAuto(null));
        }

        [Theory]
        [InlineData("0", true)]
        [InlineData("", true)]
        [InlineData(null, true)]
        [InlineData("garbage", true)]
        [InlineData("3", false)]
        public void IsAuto(string stored, bool expected) => Assert.Equal(expected, AutoNumberModel.IsAuto(stored));

        [Fact]
        public void AutoShowsWhatItResolvesTo()
        {
            Assert.Equal(4, AutoNumberModel.DisplayValue(Threads(), "0"));
            Assert.Null(AutoNumberModel.DisplayValue(Port(), "0")); // unknown → empty box, never a misleading "0"
        }

        [Fact]
        public void ManualValuesAreClampedToOneOrMore()
        {
            var d = Threads();
            Assert.Equal(1, AutoNumberModel.ManualMin(d));
            Assert.Equal(6, AutoNumberModel.DisplayValue(d, "6"));
            Assert.Equal(16, AutoNumberModel.DisplayValue(d, "99"));
            Assert.Equal("1", AutoNumberModel.ToStored(d, false, 0));
            Assert.Equal("16", AutoNumberModel.ToStored(d, false, 40));
            Assert.Equal("7", AutoNumberModel.ToStored(d, false, 6.6));
        }

        [Fact]
        public void TogglingAutoKeepsStoredSemantics()
        {
            var d = Threads();
            Assert.Equal("0", AutoNumberModel.ToStored(d, true, 8));
            Assert.Equal(4, AutoNumberModel.ManualSeed(d));
            Assert.Equal("4", AutoNumberModel.ToStored(d, false, double.NaN));
            Assert.Equal(1, AutoNumberModel.ManualSeed(Port()));
            // stored → normalized by the schema rules stays the same string
            Assert.Equal("0", SettingsModel.Normalize(d, AutoNumberModel.ToStored(d, true, 3), out bool ok));
            Assert.True(ok);
        }
    }

    public class SettingsPageLayoutTests
    {
        [Fact]
        public void GroupsAre22ApartAndCards4Apart()
        {
            Assert.Equal(4, SettingsPageLayout.CardGap);
            // the visible gap between the last card of a group and the next header = card margin + header top margin
            Assert.Equal(SettingsPageLayout.GroupGap, SettingsPageLayout.CardGap + SettingsPageLayout.GroupHeaderTop(false));
            Assert.Equal(0, SettingsPageLayout.GroupHeaderTop(true));
        }

        [Fact]
        public void HeadersHaveMoreSpaceAboveThanBelow()
        {
            Assert.True(SettingsPageLayout.GroupHeaderTop(false) + SettingsPageLayout.CardGap > SettingsPageLayout.GroupHeaderGapBelow);
            Assert.True(SettingsPageLayout.SearchCategoryTop(false) > SettingsPageLayout.GroupHeaderTop(false));
            Assert.Equal(0, SettingsPageLayout.SearchCategoryTop(true));
        }
    }
}
