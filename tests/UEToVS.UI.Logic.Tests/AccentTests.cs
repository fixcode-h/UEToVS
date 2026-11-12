using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class AccentTests
    {
        [Fact]
        public void FallbackPalettesUseFluentBlueNotPurple()
        {
            foreach (var palette in new[] { ThemePalette.FallbackDark(), ThemePalette.FallbackLight() })
            {
                var accent = AccentResolver.Resolve(palette, new AppearanceSettings(), out var source);
                Assert.Equal(AccentSource.Default, source);
                accent.ToHsl(out double h, out double s, out _);
                Assert.InRange(h, 195, 215); // #0078D4 hue ≈ 206
                Assert.True(s > 0.8);
                Assert.True(Rgba.Contrast(Rgba.White, accent) >= 3.0, palette.Name);
            }
            var light = AccentResolver.Resolve(ThemePalette.FallbackLight(), null, out _);
            var dark = AccentResolver.Resolve(ThemePalette.FallbackDark(), null, out _);
            Assert.True(dark.Lightness > light.Lightness); // brighter shade on dark themes, deeper on light ones
        }

        [Fact]
        public void LightThemeShadeMatchesFluentDark1()
        {
            var c = AccentResolver.ForTheme(AccentResolver.FluentBlue, false);
            // Windows 11 light AccentFillColorDefault for #0078D4 is #005FB8 (L ≈ 0.36).
            Assert.InRange(c.Lightness, 0.33, 0.38);
        }

        [Fact]
        public void PriorityIsCustomThenSystemThenVsThenDefault()
        {
            var p = ThemePalette.FallbackDark();
            p.Accent = Rgba.FromHex(0x8A63D2);          // VS theme gives purple
            p.SystemAccent = Rgba.FromHex(0x0078D4);    // Windows accent is blue
            AccentResolver.Resolve(p, new AppearanceSettings(), out var src);
            Assert.Equal(AccentSource.System, src);

            p.SystemAccent = null;
            Assert.Equal(Rgba.FromHex(0x8A63D2), AccentResolver.Resolve(p, null, out src));
            Assert.Equal(AccentSource.VsTheme, src);

            var custom = new AppearanceSettings { AccentMode = AccentMode.Custom, CustomAccent = "#FF8800" };
            Assert.Equal(Rgba.FromHex(0xFF8800), AccentResolver.Resolve(p, custom, out src));
            Assert.Equal(AccentSource.Custom, src);
        }

        [Fact]
        public void GrayOrInvisibleCandidatesAreSkipped()
        {
            var p = ThemePalette.FallbackDark();
            p.SystemAccent = Rgba.FromHex(0x808080);                // gray
            p.Accent = p.PopupBackground ?? p.Background;           // same as the surface
            AccentResolver.Resolve(p, null, out var src);
            Assert.Equal(AccentSource.Default, src);
        }

        [Fact]
        public void HighContrastUsesPaletteAccentAsIs()
        {
            var p = ThemePalette.FallbackHighContrast();
            p.SystemAccent = Rgba.FromHex(0x0078D4);
            Assert.Equal(p.Accent.Value, AccentResolver.Resolve(p, null, out var src));
            Assert.Equal(AccentSource.HighContrast, src);
        }

        [Fact]
        public void TokenSetRecordsAccentSource()
        {
            var p = ThemePalette.FallbackLight();
            p.SystemAccent = Rgba.FromHex(0x0078D4);
            var t = TokenDerivation.Derive(p, new AppearanceSettings { ColorScheme = ColorScheme.FollowVs });
            Assert.Equal(AccentSource.System, t.AccentSource);
        }

        [Fact]
        public void AccentHoverAndPressedFollowFluentOpacityRamp()
        {
            foreach (var palette in new[] { ThemePalette.FallbackDark(), ThemePalette.FallbackLight() })
            {
                var t = TokenDerivation.Derive(palette, new AppearanceSettings());
                var a = t.Color(TokenNames.Accent);
                var hover = t.Color(TokenNames.AccentHover);
                var pressed = t.Color(TokenNames.AccentPressed);
                var popup = t.Color(TokenNames.SurfacePopup);
                Assert.Equal(TokenDerivation.AccentHoverOf(a, popup), hover);
                Assert.Equal(TokenDerivation.AccentPressedOf(a, popup), pressed);
                // Moves toward the surface: darker on dark themes, lighter on light themes; pressed moves further.
                if (t.IsDark) { Assert.True(hover.Luminance < a.Luminance); Assert.True(pressed.Luminance < hover.Luminance); }
                else { Assert.True(hover.Luminance > a.Luminance); Assert.True(pressed.Luminance > hover.Luminance); }
                // The knob / text on it stays readable.
                Assert.True(Rgba.Contrast(t.Color(TokenNames.TextOnAccent), hover) >= 3.0, palette.Name);
            }
        }
    }
}
