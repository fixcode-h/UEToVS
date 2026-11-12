using System;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class ToggleSwitchTests
    {
        [Fact]
        public void TrackIsFluent40x20Pill()
        {
            Assert.Equal(40, ToggleSwitchGeometry.TrackWidth);
            Assert.Equal(20, ToggleSwitchGeometry.TrackHeight);
            Assert.Equal(10, ToggleSwitchGeometry.TrackRadius);
        }

        [Fact]
        public void RestKnobIs12WithFourPixelInset()
        {
            var off = ToggleSwitchGeometry.Knob(0, ToggleSwitchPointer.Rest, true);
            Assert.Equal(4, off.X);
            Assert.Equal(4, off.Y);
            Assert.Equal(12, off.Width);
            Assert.Equal(12, off.Height);
            var on = ToggleSwitchGeometry.Knob(1, ToggleSwitchPointer.Rest, true);
            Assert.Equal(24, on.X);
            Assert.Equal(36, on.Right);
            Assert.Equal(4, on.Y);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        public void HoverKnobGrowsTo14AndStaysCentred(double progress)
        {
            var k = ToggleSwitchGeometry.Knob(progress, ToggleSwitchPointer.Hover, true);
            Assert.Equal(14, k.Width);
            Assert.Equal(14, k.Height);
            Assert.Equal(ToggleSwitchGeometry.TrackHeight / 2, k.CenterY);
            var rest = ToggleSwitchGeometry.Knob(progress, ToggleSwitchPointer.Rest, true);
            Assert.Equal(rest.X + rest.Width / 2, k.X + k.Width / 2); // grows around the same centre
        }

        [Fact]
        public void PressedKnobStretchesTowardTravelDirection()
        {
            var off = ToggleSwitchGeometry.Knob(0, ToggleSwitchPointer.Pressed, true);
            Assert.Equal(17, off.Width);
            Assert.Equal(14, off.Height);
            Assert.Equal(3, off.X);          // anchored left, grows to the right (toward "on")
            Assert.Equal(20, off.Right);
            var on = ToggleSwitchGeometry.Knob(1, ToggleSwitchPointer.Pressed, true);
            Assert.Equal(37, on.Right);      // anchored right, grows to the left (toward "off")
            Assert.Equal(20, on.X);
        }

        [Fact]
        public void KnobStaysInsideTrackForEveryStateAndProgress()
        {
            foreach (ToggleSwitchPointer p in Enum.GetValues(typeof(ToggleSwitchPointer)))
                foreach (bool enabled in new[] { true, false })
                    for (double t = -0.5; t <= 1.5; t += 0.05)
                    {
                        var k = ToggleSwitchGeometry.Knob(t, p, enabled);
                        Assert.InRange(k.X, 1, ToggleSwitchGeometry.TrackWidth - k.Width - 1);
                        Assert.InRange(k.Y, 1, ToggleSwitchGeometry.TrackHeight - k.Height - 1);
                    }
            var nan = ToggleSwitchGeometry.Knob(double.NaN, ToggleSwitchPointer.Rest, true);
            Assert.Equal(4, nan.X);
        }

        [Fact]
        public void DisabledKnobDoesNotReactToPointer()
        {
            var k = ToggleSwitchGeometry.Knob(1, ToggleSwitchPointer.Pressed, false);
            Assert.Equal(12, k.Width);
            Assert.Equal(12, k.Height);
        }

        [Fact]
        public void EaseOutIsMonotonicDecelerating()
        {
            Assert.Equal(0, ToggleSwitchGeometry.EaseOut(0));
            Assert.Equal(1, ToggleSwitchGeometry.EaseOut(1));
            Assert.Equal(0, ToggleSwitchGeometry.EaseOut(-1));
            Assert.Equal(1, ToggleSwitchGeometry.EaseOut(2));
            Assert.True(ToggleSwitchGeometry.EaseOut(0.5) > 0.5); // ease-out: more than half way at half time
            double prev = 0;
            for (double t = 0.05; t <= 1; t += 0.05)
            {
                double v = ToggleSwitchGeometry.EaseOut(t);
                Assert.True(v >= prev);
                prev = v;
            }
        }

        [Theory]
        [InlineData(true, 100, 120)]
        [InlineData(true, 0, 0)]      // UEToVS animations off
        [InlineData(false, 100, 0)]   // Windows "show animations" off (SystemParameters.ClientAreaAnimation)
        public void TravelDurationRespectsSystemAndSettings(bool system, double appearanceMs, int expected)
        {
            Assert.Equal(expected, ToggleSwitchGeometry.TravelMs(system, appearanceMs));
            Assert.InRange(ToggleSwitchGeometry.AnimationMs, 100, 150);
        }

        [Fact]
        public void ColorRolesFollowFluentSpec()
        {
            var off = ToggleSwitchGeometry.Colors(false, ToggleSwitchPointer.Rest, true);
            Assert.Equal(TokenNames.ToggleTrackOff, off.Fill);
            Assert.Equal(TokenNames.ToggleStroke, off.Stroke);
            Assert.Equal(TokenNames.ToggleStroke, off.Knob);
            Assert.Equal(TokenNames.ToggleTrackOffHover, ToggleSwitchGeometry.Colors(false, ToggleSwitchPointer.Hover, true).Fill);

            var on = ToggleSwitchGeometry.Colors(true, ToggleSwitchPointer.Rest, true);
            Assert.Equal(TokenNames.Accent, on.Fill);
            Assert.Null(on.Stroke);
            Assert.Equal(TokenNames.ToggleThumb, on.Knob);
            Assert.Equal(TokenNames.AccentHover, ToggleSwitchGeometry.Colors(true, ToggleSwitchPointer.Hover, true).Fill);
            Assert.Equal(TokenNames.AccentPressed, ToggleSwitchGeometry.Colors(true, ToggleSwitchPointer.Pressed, true).Fill);

            var onDisabled = ToggleSwitchGeometry.Colors(true, ToggleSwitchPointer.Hover, false);
            Assert.Equal(TokenNames.ToggleTrackOnDisabled, onDisabled.Fill);
            Assert.Equal(TokenNames.ToggleThumbDisabled, onDisabled.Knob);
            var offDisabled = ToggleSwitchGeometry.Colors(false, ToggleSwitchPointer.Rest, false);
            Assert.Equal(TokenNames.ToggleStrokeDisabled, offDisabled.Stroke);
        }

        [Fact]
        public void EveryToggleRoleTokenIsDerivedForEveryPalette()
        {
            foreach (var palette in new[] { ThemePalette.FallbackDark(), ThemePalette.FallbackLight(), ThemePalette.FallbackBlue(), ThemePalette.FallbackHighContrast() })
            {
                var t = TokenDerivation.Derive(palette, new AppearanceSettings());
                foreach (bool on in new[] { true, false })
                    foreach (ToggleSwitchPointer p in Enum.GetValues(typeof(ToggleSwitchPointer)))
                        foreach (bool enabled in new[] { true, false })
                        {
                            var r = ToggleSwitchGeometry.Colors(on, p, enabled);
                            Assert.True(t.Colors.ContainsKey(r.Fill), palette.Name + " " + r.Fill);
                            Assert.True(t.Colors.ContainsKey(r.Knob), palette.Name + " " + r.Knob);
                            if (r.Stroke != null) Assert.True(t.Colors.ContainsKey(r.Stroke), palette.Name + " " + r.Stroke);
                        }
            }
        }

        [Fact]
        public void KnobIsVisibleOnItsTrack()
        {
            foreach (var palette in new[] { ThemePalette.FallbackDark(), ThemePalette.FallbackLight(), ThemePalette.FallbackBlue() })
            {
                var t = TokenDerivation.Derive(palette, new AppearanceSettings());
                var popup = t.Color(TokenNames.SurfacePopup);
                Assert.True(Rgba.Contrast(t.Color(TokenNames.ToggleThumb), t.Color(TokenNames.Accent)) >= 3.0, palette.Name + " on knob");
                Assert.True(Rgba.Contrast(t.Color(TokenNames.ToggleStroke), popup) >= 3.0, palette.Name + " off stroke");
                Assert.Equal(0, t.Color(TokenNames.ToggleTrackOff).A); // off track is an outline
                Assert.True(Rgba.Contrast(t.Color(TokenNames.ToggleThumbDisabled), t.Color(TokenNames.ToggleTrackOnDisabled)) >= 1.5, palette.Name + " disabled knob");
            }
        }
    }
}
