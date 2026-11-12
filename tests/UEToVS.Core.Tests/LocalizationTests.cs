using System.Globalization;
using UEToVS.Core.Localization;
using Xunit;

namespace UEToVS.Core.Tests
{
    public class LocalizationTests
    {
        [Theory]
        [InlineData(1033, UiLanguage.English)]   // en-US
        [InlineData(2057, UiLanguage.English)]   // en-GB
        [InlineData(2052, UiLanguage.Chinese)]   // zh-CN
        [InlineData(1028, UiLanguage.Chinese)]   // zh-TW
        [InlineData(1041, UiLanguage.Chinese)]   // ja-JP → non-English shows Chinese
        [InlineData(1031, UiLanguage.Chinese)]   // de-DE
        public void NonEnglishVsShowsChinese(int lcid, UiLanguage expected) =>
            Assert.Equal(expected, Loc.FromLcid(lcid));

        [Fact]
        public void PicksTextAndRaisesChange()
        {
            var old = Loc.Language;
            try
            {
                int changes = 0;
                Loc.Language = UiLanguage.Chinese;
                Loc.LanguageChanged += () => changes++;
                Loc.InitializeFromCulture(new CultureInfo("en-US"));
                Assert.Equal("Open Log", Loc.T("打开日志", "Open Log"));
                Loc.InitializeFromCulture(new CultureInfo("fr-FR"));
                Assert.Equal("打开日志", Loc.T("打开日志", "Open Log"));
                Assert.Equal(2, changes);
            }
            finally { Loc.Language = old; }
        }
    }
}
