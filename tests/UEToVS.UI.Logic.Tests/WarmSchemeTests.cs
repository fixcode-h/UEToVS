using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class WarmSchemeTests
    {
        [Fact]
        public void WarmIsTheDefaultAndPinsTheMockupColors()
        {
            var t = TokenDerivation.Derive(ThemePalette.FallbackDark(), new AppearanceSettings());
            Assert.Equal("#262522", t.Color(TokenNames.SurfaceRaised).ToHex());
            Assert.Equal("#34322E", t.Color(TokenNames.BorderSubtle).ToHex());
            Assert.Equal("#D97757", t.Color(TokenNames.Accent).ToHex());
            Assert.Equal("#1F1E1C", t.Color(TokenNames.TextOnAccent).ToHex());
            Assert.Equal("#3A3834", t.Color(TokenNames.SegmentSelected).ToHex());
        }

        [Fact]
        public void ClayWinsOverTheWindowsAccentButNotOverACustomOne()
        {
            var p = ThemePalette.FallbackDark();
            p.SystemAccent = Rgba.FromHex(0x0078D4);
            Assert.Equal("#D97757", TokenDerivation.Derive(p, new AppearanceSettings()).Color(TokenNames.Accent).ToHex());
            var custom = new AppearanceSettings { AccentMode = AccentMode.Custom, CustomAccent = "#3B82F6" };
            var t = TokenDerivation.Derive(p, custom);
            Assert.Equal("#3B82F6", t.Color(TokenNames.Accent).ToHex());
            Assert.Equal(AccentSource.Custom, t.AccentSource);
        }

        [Fact]
        public void LightThemesGetTheLightWarmPalette()
        {
            var t = TokenDerivation.Derive(ThemePalette.FallbackLight(), new AppearanceSettings());
            Assert.Equal("#FFFFFF", t.Color(TokenNames.SurfaceRaised).ToHex());
            Assert.True(Rgba.Contrast(t.Color(TokenNames.TextTertiary), t.Color(TokenNames.SurfaceRaised)) >= 4.5);
            Assert.True(Rgba.Contrast(t.Color(TokenNames.TextOnAccent), t.Color(TokenNames.Accent)) >= 3.0);
        }

        [Fact]
        public void FollowVsAndHighContrastKeepTheirOwnColors()
        {
            var vs = TokenDerivation.Derive(ThemePalette.FallbackDark(), new AppearanceSettings { ColorScheme = ColorScheme.FollowVs });
            Assert.NotEqual("#262522", vs.Color(TokenNames.SurfaceRaised).ToHex());
            var hc = TokenDerivation.Derive(ThemePalette.FallbackHighContrast(), new AppearanceSettings());
            Assert.Equal("#000000", hc.Color(TokenNames.SurfacePopup).ToHex());
        }

        [Fact]
        public void V2RadiiMigrateToWarmDefaults()
        {
            var s = AppearanceSettings.Parse("version=2\nradius.popup=8\nradius.control=4\n", out _);
            Assert.Equal(12, s.PopupRadius);
            Assert.Equal(8, s.ControlRadius);
            var kept = AppearanceSettings.Parse("version=2\nradius.popup=10\nradius.control=4\n", out _);
            Assert.Equal(10, kept.PopupRadius);
        }

        [Fact]
        public void SchemeRoundTrips()
        {
            var s = new AppearanceSettings { ColorScheme = ColorScheme.FollowVs };
            Assert.Equal(ColorScheme.FollowVs, AppearanceSettings.Parse(s.ToStorageString(), out _).ColorScheme);
        }
    }
}
