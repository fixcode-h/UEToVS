using System;
using System.Collections.Generic;

namespace UEToVS.UI.Logic
{
    /// <summary>Resolved token values: colors and numbers, keyed by <see cref="TokenNames"/>.</summary>
    public sealed class TokenSet
    {
        public bool IsDark { get; internal set; }
        public bool IsHighContrast { get; internal set; }
        public string PaletteName { get; internal set; }
        /// <summary>Where <see cref="TokenNames.Accent"/> came from (logged by the theme manager).</summary>
        public AccentSource AccentSource { get; internal set; }
        public AppearanceSettings Settings { get; internal set; }

        public Dictionary<string, Rgba> Colors { get; } = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        public Dictionary<string, double> Numbers { get; } = new Dictionary<string, double>(StringComparer.Ordinal);

        public Rgba Color(string name) => Colors.TryGetValue(name, out var c) ? c : Rgba.Transparent;
        public double Number(string name) => Numbers.TryGetValue(name, out var n) ? n : 0;
    }

    /// <summary>
    /// Pure function (palette + settings → tokens). All theme-derived colors live here so they can be unit tested:
    /// badge colors are lighter/less saturated on dark themes and darker/softer on light themes, and every text-on-
    /// background pair is pushed to a readable contrast ratio.
    /// </summary>
    public static class TokenDerivation
    {
        /// <summary>Base hue (degrees) and saturation for each badge kind, chosen to stay distinct from each other.</summary>
        private static readonly Dictionary<BadgeKind, (double H, double S)> BadgeHues = new Dictionary<BadgeKind, (double, double)>
        {
            [BadgeKind.Neutral] = (220, 0.08),
            [BadgeKind.UClass] = (212, 0.70),
            [BadgeKind.UFunction] = (268, 0.55),
            [BadgeKind.UProperty] = (140, 0.45),
            [BadgeKind.UStruct] = (178, 0.55),
            [BadgeKind.UEnum] = (32, 0.75),
            [BadgeKind.UInterface] = (190, 0.60),
            [BadgeKind.Delegate] = (318, 0.45),
            [BadgeKind.Macro] = (352, 0.60),
            [BadgeKind.File] = (215, 0.18),
            [BadgeKind.Asset] = (46, 0.80),
            [BadgeKind.Module] = (95, 0.40),
        };

        private static readonly Dictionary<SymbolKind, (double H, double S)> GlyphHues = new Dictionary<SymbolKind, (double, double)>
        {
            [SymbolKind.Unknown] = (220, 0.08),
            [SymbolKind.Class] = (212, 0.70),
            [SymbolKind.Struct] = (178, 0.55),
            [SymbolKind.Interface] = (190, 0.60),
            [SymbolKind.Enum] = (32, 0.75),
            [SymbolKind.EnumMember] = (32, 0.55),
            [SymbolKind.Function] = (268, 0.55),
            [SymbolKind.Method] = (268, 0.55),
            [SymbolKind.Property] = (140, 0.45),
            [SymbolKind.Field] = (200, 0.35),
            [SymbolKind.Variable] = (200, 0.35),
            [SymbolKind.Parameter] = (200, 0.25),
            [SymbolKind.Macro] = (352, 0.60),
            [SymbolKind.Namespace] = (220, 0.15),
            [SymbolKind.Typedef] = (178, 0.40),
            [SymbolKind.Delegate] = (318, 0.45),
            [SymbolKind.File] = (215, 0.18),
            [SymbolKind.Asset] = (46, 0.80),
            [SymbolKind.Module] = (95, 0.40),
            [SymbolKind.Keyword] = (220, 0.50),
            [SymbolKind.Action] = (212, 0.30),
        };

        private static readonly Dictionary<Severity, uint> SeverityBase = new Dictionary<Severity, uint>
        {
            [Severity.Info] = 0x3574F0,
            [Severity.Success] = 0x3FA45B,
            [Severity.Warning] = 0xE0A21B,
            [Severity.Error] = 0xE5484D,
        };

        public static TokenSet Derive(ThemePalette palette, AppearanceSettings settings)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            settings = settings?.Clone() ?? new AppearanceSettings();
            settings.Normalize();

            var t = new TokenSet { Settings = settings, PaletteName = palette.Name };
            bool dark = palette.Background.IsDark;
            bool hc = palette.IsHighContrast;
            t.IsDark = dark;
            t.IsHighContrast = hc;
            var C = t.Colors;

            var bg = palette.Background.Opaque();
            var fg = palette.Foreground.Opaque().EnsureContrast(bg, 7);
            var popup = (palette.PopupBackground ?? bg).Opaque();
            if (hc) popup = bg;
            var popupFg = fg.EnsureContrast(popup, 7);

            // ---- accent
            var accent = AccentResolver.Resolve(palette, settings, out var accentSource);
            t.AccentSource = accentSource;

            // ---- surfaces
            C[TokenNames.SurfaceBase] = bg;
            C[TokenNames.SurfacePopup] = popup;
            C[TokenNames.SurfaceRaised] = hc ? popup : Rgba.Mix(popup, popupFg, dark ? 0.045 : 0.03);
            C[TokenNames.SurfaceSunken] = hc ? popup : Rgba.Mix(popup, dark ? Rgba.Black : Rgba.FromHex(0xE9EBEF), dark ? 0.18 : 0.45);
            C[TokenNames.SurfaceHover] = hc ? popup : Rgba.Mix(popup, popupFg, dark ? 0.07 : 0.055);
            C[TokenNames.SurfacePressed] = hc ? popup : Rgba.Mix(popup, popupFg, dark ? 0.12 : 0.10);

            // ---- text
            var secondary = palette.GrayText.HasValue && Rgba.Contrast(palette.GrayText.Value, popup) >= 3.0
                ? palette.GrayText.Value.Opaque()
                : Rgba.Mix(popupFg, popup, 0.42).EnsureContrast(popup, 3.5);
            C[TokenNames.TextPrimary] = popupFg;
            C[TokenNames.TextSecondary] = hc ? popupFg : secondary.EnsureContrast(popup, 3.5);
            C[TokenNames.TextDisabled] = hc ? (palette.GrayText ?? popupFg) : Rgba.Mix(popupFg, popup, 0.6);
            var link = (palette.Link ?? accent).Opaque();
            C[TokenNames.TextLink] = hc ? link : link.EnsureContrast(popup, 4.5);
            C[TokenNames.TextLinkHover] = hc ? link : (dark ? link.WithLightness(Math.Min(0.85, link.Lightness + 0.1)) : link.WithLightness(Math.Max(0.2, link.Lightness - 0.1))).EnsureContrast(popup, 4.5);

            // ---- borders
            C[TokenNames.BorderSubtle] = hc ? (palette.Border ?? fg) : Rgba.Mix(popup, popupFg, dark ? 0.12 : 0.13);
            C[TokenNames.BorderStrong] = hc ? (palette.Border ?? fg) : Rgba.Mix(popup, popupFg, dark ? 0.24 : 0.26);
            C[TokenNames.BorderFocus] = hc ? accent : accent.EnsureContrast(popup, 3);

            // ---- accent family
            var accentReadable = accent.EnsureContrast(popup, 3);
            C[TokenNames.Accent] = accent;
            // Fluent: AccentFillColorSecondary/Tertiary = the accent at 90% / 80% opacity over the surface, i.e. a bit
            // darker on dark themes and a bit lighter on light themes.
            C[TokenNames.AccentHover] = hc ? accent : AccentHoverOf(accent, popup);
            C[TokenNames.AccentPressed] = hc ? accent : AccentPressedOf(accent, popup);
            C[TokenNames.AccentSubtle] = hc ? popup : Rgba.Mix(popup, accent, dark ? 0.22 : 0.12);
            C[TokenNames.TextOnAccent] = Rgba.Contrast(Rgba.White, accent) >= 3.0 ? Rgba.White : Rgba.FromHex(0x111111);

            // ---- selection (VS 2026 / Fluent: soft accent-tinted row + accent indicator pill, text keeps its color)
            Rgba selBg, selFg;
            if (hc)
            {
                selBg = (palette.SelectionBackground ?? accent).Opaque();
                selFg = (palette.SelectionText ?? (selBg.IsDark ? Rgba.White : Rgba.Black)).Opaque();
            }
            else
            {
                selBg = Rgba.Mix(popup, accent, dark ? 0.30 : 0.16);
                selFg = popupFg.EnsureContrast(selBg, 7);
            }
            C[TokenNames.SelectionBackground] = selBg;
            C[TokenNames.SelectionForeground] = selFg;
            C[TokenNames.SelectionSecondary] = hc ? selFg : Rgba.Mix(selFg, selBg, 0.35).EnsureContrast(selBg, 3.5);
            C[TokenNames.SelectionInactiveBackground] = hc ? selBg : Rgba.Mix(popup, popupFg, dark ? 0.10 : 0.08);

            // ---- match highlight: readable on both the plain surface and the selected row
            var ms = settings.MatchStyle;
            Rgba matchFg;
            if (hc) matchFg = (palette.Link ?? accent).Opaque();
            else if ((ms & MatchHighlightStyle.AccentColor) != 0)
            {
                var m = dark ? accent.WithLightness(Math.Max(accent.Lightness, 0.66)) : accent.WithLightness(Math.Min(accent.Lightness, 0.45));
                matchFg = m.EnsureContrast(popup, 4.5).EnsureContrast(selBg, 3.0);
            }
            else matchFg = popupFg;
            C[TokenNames.TextMatch] = matchFg;
            C[TokenNames.MatchBackground] = (ms & MatchHighlightStyle.Background) != 0 && !hc
                ? Rgba.Mix(popup, dark ? Rgba.FromHex(0xE0B341) : Rgba.FromHex(0xF5D547), dark ? 0.28 : 0.55)
                : Rgba.Transparent;

            // ---- inputs
            var inBg = hc ? popup : (palette.InputBackground ?? Rgba.Mix(popup, dark ? Rgba.Black : Rgba.White, dark ? 0.15 : 0.6)).Opaque();
            C[TokenNames.InputBackground] = inBg;
            C[TokenNames.InputForeground] = (palette.InputText ?? popupFg).Opaque().EnsureContrast(inBg, 7);
            C[TokenNames.InputBorder] = hc ? (palette.Border ?? fg) : (palette.InputBorder ?? C[TokenNames.BorderStrong]).Opaque();
            C[TokenNames.InputPlaceholder] = hc ? popupFg : Rgba.Mix(C[TokenNames.InputForeground], inBg, 0.45).EnsureContrast(inBg, 3);

            // ---- buttons / toggles
            C[TokenNames.ButtonSecondaryBackground] = hc ? popup : Rgba.Mix(popup, popupFg, dark ? 0.08 : 0.05);
            C[TokenNames.ButtonSecondaryHover] = hc ? popup : Rgba.Mix(popup, popupFg, dark ? 0.13 : 0.09);
            C[TokenNames.ButtonSecondaryPressed] = hc ? popup : Rgba.Mix(popup, popupFg, dark ? 0.18 : 0.14);
            C[TokenNames.ButtonSecondaryBorder] = hc ? (palette.Border ?? fg) : Rgba.Mix(popup, popupFg, dark ? 0.18 : 0.2);
            C[TokenNames.ButtonGhostHover] = C[TokenNames.SurfaceHover];
            C[TokenNames.ButtonGhostPressed] = C[TokenNames.SurfacePressed];
            // Fluent ToggleSwitch: off = outline track + knob in the strong control stroke (secondary text), on = accent
            // fill + on-accent knob; disabled uses the disabled text color (≈ 40% emphasis) and never the accent.
            C[TokenNames.ToggleTrackOff] = Rgba.Transparent;
            C[TokenNames.ToggleTrackOffHover] = hc ? popup : Rgba.Mix(popup, popupFg, dark ? 0.06 : 0.04);
            C[TokenNames.ToggleTrackOffPressed] = hc ? popup : Rgba.Mix(popup, popupFg, dark ? 0.10 : 0.08);
            C[TokenNames.ToggleStroke] = C[TokenNames.TextSecondary];
            C[TokenNames.ToggleStrokeDisabled] = C[TokenNames.TextDisabled];
            C[TokenNames.ToggleTrackOnDisabled] = C[TokenNames.TextDisabled];
            C[TokenNames.ToggleThumb] = hc ? popup : C[TokenNames.TextOnAccent];
            C[TokenNames.ToggleThumbDisabled] = popup;

            // ---- misc chrome
            C[TokenNames.ScrollThumb] = hc ? fg : popupFg.WithAlpha(dark ? 0.30 : 0.32);
            C[TokenNames.ScrollThumbHover] = hc ? fg : popupFg.WithAlpha(dark ? 0.48 : 0.52);
            C[TokenNames.Shadow] = Rgba.Black.WithAlpha(hc ? 0 : dark ? 0.45 : 0.16);
            C[TokenNames.KbdBackground] = hc ? popup : Rgba.Mix(popup, popupFg, dark ? 0.09 : 0.05);
            C[TokenNames.KbdBorder] = hc ? fg : Rgba.Mix(popup, popupFg, dark ? 0.22 : 0.22);
            C[TokenNames.KbdForeground] = hc ? fg : C[TokenNames.TextSecondary];
            var tipBg = hc ? popup : (palette.ToolTipBackground ?? Rgba.Mix(popup, popupFg, dark ? 0.06 : 0.0)).Opaque();
            C[TokenNames.TooltipBackground] = tipBg;
            C[TokenNames.TooltipForeground] = (palette.ToolTipText ?? popupFg).Opaque().EnsureContrast(tipBg, 7);
            C[TokenNames.TooltipBorder] = C[TokenNames.BorderStrong];
            C[TokenNames.CodeLineHighlight] = hc ? selBg : Rgba.Mix(C[TokenNames.SurfaceSunken], accent, dark ? 0.20 : 0.12);
            C[TokenNames.CodeLineNumber] = hc ? fg : Rgba.Mix(popupFg, popup, 0.55);

            // ---- VS 2026 (Fluent) additions
            C[TokenNames.FocusStroke] = hc ? fg : popupFg;
            C[TokenNames.InputFocusUnderline] = hc ? accent : accent.EnsureContrast(inBg, 3);
            C[TokenNames.ControlStrokeBottom] = hc ? (palette.Border ?? fg) : Rgba.Mix(popup, popupFg, dark ? 0.12 : 0.32);
            C[TokenNames.SegmentTrack] = hc ? popup : Rgba.Mix(popup, dark ? Rgba.Black : popupFg, dark ? 0.16 : 0.04);
            C[TokenNames.SelectionIndicator] = hc ? selFg : accent.EnsureContrast(selBg, 3);
            C[TokenNames.SurfaceNav] = hc ? bg : Rgba.Mix(bg, dark ? Rgba.Black : popupFg, dark ? 0.10 : 0.025);

            // ---- severities
            foreach (var kv in SeverityBase)
            {
                var baseC = Rgba.FromHex(kv.Value);
                C[TokenNames.Severity(kv.Key)] = hc ? fg : (dark ? baseC.WithLightness(Math.Max(baseC.Lightness, 0.6)) : baseC.WithLightness(Math.Min(baseC.Lightness, 0.45))).EnsureContrast(popup, 3.5);
                C[TokenNames.SeverityBackground(kv.Key)] = hc ? popup : Rgba.Mix(popup, baseC, dark ? 0.18 : 0.12);
            }

            // ---- badges and glyphs
            foreach (var kv in BadgeHues)
            {
                Rgba? overrideColor = null;
                var o = settings.GetBadgeColor(kv.Key);
                if (o != null && Rgba.TryParse(o, out var oc)) overrideColor = oc.Opaque();
                DeriveBadge(t, kv.Key, kv.Value.H, kv.Value.S, overrideColor, popup, popupFg, dark, hc);
            }
            foreach (var kv in GlyphHues)
            {
                var gbg = hc ? popup : Rgba.Mix(popup, Rgba.FromHsl(kv.Value.H, kv.Value.S, 0.5), dark ? 0.24 : 0.14);
                C[TokenNames.GlyphBackground(kv.Key)] = gbg;
                C[TokenNames.Glyph(kv.Key)] = hc ? fg : Tone(kv.Value.H, kv.Value.S, dark).EnsureContrast(gbg, 3.5);
            }

            DeriveMetrics(t, palette, settings, dark, hc);
            return t;
        }

        /// <summary>Foreground tone for a hue: lighter + softer on dark themes, darker on light themes.</summary>
        internal static Rgba Tone(double hue, double sat, bool dark) =>
            dark ? Rgba.FromHsl(hue, sat * 0.85, 0.70) : Rgba.FromHsl(hue, sat * 0.80, 0.40);

        private static void DeriveBadge(TokenSet t, BadgeKind kind, double hue, double sat, Rgba? custom, Rgba popup, Rgba fg, bool dark, bool hc)
        {
            var C = t.Colors;
            if (hc)
            {
                C[TokenNames.BadgeForeground(kind)] = fg;
                C[TokenNames.BadgeBackground(kind)] = popup;
                C[TokenNames.BadgeSolid(kind)] = fg;
                return;
            }
            Rgba fgc, solid;
            if (custom.HasValue)
            {
                var c = custom.Value;
                fgc = c;
                solid = c;
            }
            else
            {
                fgc = Tone(hue, sat, dark);
                solid = Rgba.FromHsl(hue, sat * (dark ? 0.75 : 0.70), dark ? 0.45 : 0.48);
            }
            var bgc = Rgba.Mix(popup, custom ?? Rgba.FromHsl(hue, sat, 0.5), dark ? 0.20 : 0.13);
            C[TokenNames.BadgeBackground(kind)] = bgc;
            C[TokenNames.BadgeForeground(kind)] = fgc.EnsureContrast(bgc, 4.5);
            // Solid badges carry white text: keep the fill dark enough for it.
            C[TokenNames.BadgeSolid(kind)] = Rgba.Contrast(solid, Rgba.White) >= 3.0 ? solid : solid.EnsureContrast(Rgba.White, 3.0);
        }

        private static void DeriveMetrics(TokenSet t, ThemePalette palette, AppearanceSettings s, bool dark, bool hc)
        {
            var N = t.Numbers;
            double baseSize = palette.BaseFontSize > 6 && palette.BaseFontSize < 40 ? palette.BaseFontSize : 12;
            double body = Math.Round(baseSize * s.FontScale * 2) / 2; // half-DIP steps keep text crisp
            N[TokenNames.FontSizeBody] = body;
            N[TokenNames.FontSizeSmall] = Math.Max(8, body - 1);
            N[TokenNames.FontSizeCaption] = Math.Max(8, body - 2);
            N[TokenNames.FontSizeTitle] = body + 3;
            // Fluent type ramp for settings pages: group headers ("Body Strong" + 2) and page titles ("Subtitle").
            N[TokenNames.FontSizeSubtitle] = body + 2;
            N[TokenNames.FontSizeDisplay] = body + 8;
            // VS 2026 search boxes use the body size (Rider used a much larger input font).
            N[TokenNames.FontSizeInput] = body + (s.Preset == StylePreset.Compact ? 0 : 1);
            N[TokenNames.FontSizeCode] = body;

            // Row must fit the text: never smaller than ~1.45 line heights of body text.
            double minRow = Math.Ceiling(body * 1.45 + 2);
            double row = Math.Max(Math.Round(s.RowHeight), minRow);
            N[TokenNames.RowHeight] = row;
            N[TokenNames.RowHeaderHeight] = Math.Max(row - 2, Math.Ceiling(N[TokenNames.FontSizeCaption] * 1.6 + 6));
            N[TokenNames.IconSize] = 16;

            N[TokenNames.RadiusPopup] = hc ? 0 : s.PopupRadius;
            N[TokenNames.RadiusControl] = hc ? 0 : s.ControlRadius;
            N[TokenNames.RadiusBadge] = hc ? 0 : s.BadgeRadius;

            for (int i = 1; i <= 8; i++) N[TokenNames.Space(i)] = 4 * i;
            double k = s.SpacingScale;
            N[TokenNames.PadRowX] = Snap(8 * k);
            N[TokenNames.PadControlX] = Snap(12 * k);
            N[TokenNames.PadControlY] = Snap(4 * k);
            N[TokenNames.PadPopup] = Snap(8 * k);
            N[TokenNames.GapInline] = Snap(8 * k);
            // Fluent standard control height is 32 at 14px text; VS 2026 shell uses ~26 at 12px.
            N[TokenNames.ControlHeight] = Math.Max(Math.Ceiling(body * 1.6 + 4), Snap(26 * (0.5 + 0.5 * k)));
            N[TokenNames.FocusThickness] = hc ? 2 : 2;

            // Fluent flyout shadow: soft and wide, lighter than Rider's.
            bool shadow = s.PopupShadow && !hc;
            N[TokenNames.ShadowBlur] = shadow ? (dark ? 24 : 20) : 0;
            N[TokenNames.ShadowDepth] = shadow ? 4 : 0;
            N[TokenNames.ShadowMargin] = shadow ? 20 : 1;
            N[TokenNames.AnimationMs] = s.Animations ? Math.Min(s.AnimationMs, AppearanceSettings.MaxAnimationMs) : 0;
            N[TokenNames.MatchBold] = (s.MatchStyle & MatchHighlightStyle.Bold) != 0 ? 1 : 0;
            N[TokenNames.MatchUnderline] = (s.MatchStyle & MatchHighlightStyle.Underline) != 0 ? 1 : 0;
        }

        /// <summary>Fluent hover fill of an accent control: 90% accent over the surface.</summary>
        public static Rgba AccentHoverOf(Rgba accent, Rgba surface) => Rgba.Mix(surface.Opaque(), accent.Opaque(), 0.90);

        /// <summary>Fluent pressed fill of an accent control: 80% accent over the surface.</summary>
        public static Rgba AccentPressedOf(Rgba accent, Rgba surface) => Rgba.Mix(surface.Opaque(), accent.Opaque(), 0.80);

        private static double Snap(double v) => Math.Max(0, Math.Round(v));
    }
}
