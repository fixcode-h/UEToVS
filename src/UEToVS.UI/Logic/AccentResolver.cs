using System;

namespace UEToVS.UI.Logic
{
    /// <summary>Where the accent color in effect came from (logged so a wrong accent can be traced).</summary>
    public enum AccentSource
    {
        /// <summary>Windows system accent (DWM AccentColor), adjusted to the theme like Fluent does.</summary>
        System = 0,
        /// <summary>The VS theme (<see cref="ThemePalette.Accent"/>).</summary>
        VsTheme = 1,
        /// <summary>Fluent default blue #0078D4, adjusted to the theme.</summary>
        Default = 2,
        /// <summary>The user's custom accent (Appearance settings).</summary>
        Custom = 3,
        /// <summary>High-contrast palette color, used as is.</summary>
        HighContrast = 4,
    }

    /// <summary>
    /// Picks the accent color (VS 2026 / Fluent). Priority: custom (settings) → Windows system accent → VS theme
    /// accent → Fluent default blue. A candidate is skipped when it is unusable as a control fill: transparent, nearly
    /// gray, or almost invisible on the surface. Pure logic: unit tested.
    /// </summary>
    public static class AccentResolver
    {
        /// <summary>Fluent default accent ("communication blue").</summary>
        public static readonly Rgba FluentBlue = Rgba.FromHex(0x0078D4);

        /// <summary>Minimum HSL saturation of a usable accent (gray "highlight" colors are rejected).</summary>
        public const double MinSaturation = 0.20;

        /// <summary>Minimum contrast against the popup surface (a fill that vanishes into the background is rejected).</summary>
        public const double MinSurfaceContrast = 1.6;

        public static Rgba Resolve(ThemePalette palette, AppearanceSettings settings, out AccentSource source)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            var surface = (palette.PopupBackground ?? palette.Background).Opaque();
            bool dark = palette.Background.IsDark;

            if (palette.IsHighContrast)
            {
                source = AccentSource.HighContrast;
                return (palette.Accent ?? palette.Foreground).Opaque();
            }
            if (settings != null && settings.AccentMode == AccentMode.Custom && Rgba.TryParse(settings.CustomAccent, out var custom))
            {
                source = AccentSource.Custom;
                return custom.Opaque();
            }
            if (palette.SystemAccent.HasValue && IsUsable(palette.SystemAccent.Value, surface))
            {
                source = AccentSource.System;
                return ForTheme(palette.SystemAccent.Value, dark);
            }
            if (palette.Accent.HasValue && IsUsable(palette.Accent.Value, surface))
            {
                source = AccentSource.VsTheme;
                return palette.Accent.Value.Opaque();
            }
            source = AccentSource.Default;
            return ForTheme(FluentBlue, dark);
        }

        public static bool IsUsable(Rgba c, Rgba surface)
        {
            if (c.A < 128) return false;
            c.Opaque().ToHsl(out _, out double s, out double l);
            if (s < MinSaturation || l < 0.12 || l > 0.92) return false;
            return Rgba.Contrast(c.Opaque(), surface) >= MinSurfaceContrast;
        }

        /// <summary>
        /// Fluent picks a different shade of the system accent per theme: a deeper one on light themes
        /// (AccentFillColorDefault = Dark1, e.g. #0078D4 → ~#005FB8) and a brighter one on dark themes. The dark shade
        /// stays dark enough for white text/knobs (≥ 3:1) because VS 2026 draws white on its accent fills.
        /// </summary>
        public static Rgba ForTheme(Rgba accent, bool dark)
        {
            var a = accent.Opaque();
            double l = a.Lightness;
            if (dark)
            {
                var c = a.WithLightness(Math.Max(0.45, Math.Min(l + 0.08, 0.56)));
                // Keep white text readable: darken in small steps until the contrast holds.
                for (int i = 0; i < 20 && Rgba.Contrast(Rgba.White, c) < 3.0; i++) c = c.WithLightness(c.Lightness - 0.02);
                return c;
            }
            return a.WithLightness(Math.Min(l, 0.37));
        }
    }
}
