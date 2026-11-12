using System;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class Vs2026StyleTests
    {
        [Fact]
        public void DefaultPresetIsVs2026WithFluentRadii()
        {
            var s = new AppearanceSettings();
            Assert.Equal(StylePreset.Vs2026, s.Preset);
            Assert.Equal(12, s.PopupRadius);
            Assert.Equal(8, s.ControlRadius);
            var fresh = new AppearanceSettings();
            fresh.ApplyPreset(StylePreset.Vs2026);
            Assert.Equal(s.ToStorageString(), fresh.ToStorageString());
        }

        [Fact]
        public void LegacyRiderPresetMigratesUntouchedDensity()
        {
            var s = AppearanceSettings.Parse("version=1\npreset=Rider\nradius.popup=6\nradius.control=4\nradius.badge=3\naccent=#3574F0\n", out var problems);
            Assert.Equal(StylePreset.Vs2026, s.Preset);
            Assert.Equal(12, s.PopupRadius); // Rider 6 → VS 2026 8 → warm 12
            Assert.Equal(4, s.BadgeRadius);
            Assert.Equal(AppearanceSettings.DefaultAccent, s.CustomAccent);
            Assert.Equal(AppearanceSettings.CurrentVersion, s.Version);
            Assert.DoesNotContain(problems, p => p.StartsWith("Bad value"));
        }

        [Fact]
        public void LegacyRiderPresetKeepsHandTunedValues()
        {
            var s = AppearanceSettings.Parse("version=1\npreset=Rider\nradius.popup=12\nradius.badge=2\n", out _);
            Assert.Equal(StylePreset.Vs2026, s.Preset);
            Assert.Equal(12, s.PopupRadius);
            Assert.Equal(2, s.BadgeRadius);
        }

        [Fact]
        public void CurrentFilesRoundTrip()
        {
            var s = new AppearanceSettings { PopupRadius = 6 };
            var back = AppearanceSettings.Parse(s.ToStorageString(), out _);
            Assert.Equal(6, back.PopupRadius); // v2 files are never migrated
        }

        [Theory]
        [InlineData("dark")]
        [InlineData("light")]
        [InlineData("hc")]
        public void FluentTokensExistAndAreReadable(string name)
        {
            var p = name == "dark" ? ThemePalette.FallbackDark() : name == "light" ? ThemePalette.FallbackLight() : ThemePalette.FallbackHighContrast();
            var t = TokenDerivation.Derive(p, new AppearanceSettings());
            foreach (var k in new[] { TokenNames.FocusStroke, TokenNames.InputFocusUnderline, TokenNames.ControlStrokeBottom, TokenNames.SegmentTrack, TokenNames.SelectionIndicator, TokenNames.SurfaceNav })
                Assert.True(t.Colors.ContainsKey(k), k);
            Assert.True(Rgba.Contrast(t.Colors[TokenNames.FocusStroke], t.Colors[TokenNames.SurfacePopup]) >= 4.5);
            Assert.True(name == "hc" || Rgba.Contrast(t.Colors[TokenNames.SelectionIndicator], t.Colors[TokenNames.SelectionBackground]) >= 3.0);
            Assert.True(t.Numbers[TokenNames.ControlHeight] >= t.Numbers[TokenNames.FontSizeBody] * 1.6);
            Assert.Equal(2, t.Numbers[TokenNames.FocusThickness]);
        }

        [Fact]
        public void ControlHeightFollowsDensity()
        {
            var s = new AppearanceSettings();
            s.ApplyPreset(StylePreset.Compact);
            var c = TokenDerivation.Derive(ThemePalette.FallbackDark(), s).Numbers[TokenNames.ControlHeight];
            s.ApplyPreset(StylePreset.Comfortable);
            var w = TokenDerivation.Derive(ThemePalette.FallbackDark(), s).Numbers[TokenNames.ControlHeight];
            Assert.True(w > c);
        }
    }
}
