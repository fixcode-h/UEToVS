using System.Collections.Generic;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// The default UEToVS color scheme (v0.4.5): warm neutral grays, hairline borders and a clay accent, in the style of
    /// the Claude Code app (approved settings mockup). <see cref="Palette"/> replaces the VS palette as the input of
    /// <see cref="TokenDerivation"/> (so badges, glyphs and severities derive from the warm surfaces), and
    /// <see cref="Overrides"/> then pins the chrome tokens to the exact mockup values. High contrast never uses it.
    /// </summary>
    public static class WarmScheme
    {
        public static readonly Rgba ClayDark = Rgba.FromHex(0xD97757);
        public static readonly Rgba ClayLight = Rgba.FromHex(0xC2603F);

        public static ThemePalette Palette(bool dark, double baseFontSize, Rgba? systemAccent)
        {
            var p = dark
                ? new ThemePalette
                {
                    Name = "WarmDark",
                    Background = Rgba.FromHex(0x1F1E1C),
                    Foreground = Rgba.FromHex(0xECEAE3),
                    PopupBackground = Rgba.FromHex(0x1F1E1C),
                    Border = Rgba.FromHex(0x34322E),
                    GrayText = Rgba.FromHex(0x9B988F),
                    Accent = ClayDark,
                    InputBackground = Rgba.FromHex(0x1C1B19),
                    InputText = Rgba.FromHex(0xECEAE3),
                    InputBorder = Rgba.FromHex(0x3A3833),
                    Link = Rgba.FromHex(0xE39A7B),
                    ToolTipBackground = Rgba.FromHex(0x2B2926),
                    ToolTipText = Rgba.FromHex(0xECEAE3),
                }
                : new ThemePalette
                {
                    Name = "WarmLight",
                    Background = Rgba.FromHex(0xFAF9F5),
                    Foreground = Rgba.FromHex(0x1F1E1C),
                    PopupBackground = Rgba.FromHex(0xFAF9F5),
                    Border = Rgba.FromHex(0xE5E2D9),
                    GrayText = Rgba.FromHex(0x5E5B54),
                    Accent = ClayLight,
                    InputBackground = Rgba.FromHex(0xFFFFFF),
                    InputText = Rgba.FromHex(0x1F1E1C),
                    InputBorder = Rgba.FromHex(0xD6D2C6),
                    Link = Rgba.FromHex(0xA8502F),
                    ToolTipBackground = Rgba.FromHex(0xFFFFFF),
                    ToolTipText = Rgba.FromHex(0x1F1E1C),
                };
            p.BaseFontSize = baseFontSize;
            p.PreferOwnAccent = true;
            p.SystemAccent = systemAccent;
            return p;
        }

        /// <summary>Exact chrome colors of the mockup (dark) and their light counterparts.</summary>
        public static IReadOnlyDictionary<string, Rgba> Overrides(bool dark) => dark ? DarkOverrides : LightOverrides;

        private static Dictionary<string, Rgba> Table(params (string Name, uint Hex)[] rows)
        {
            var d = new Dictionary<string, Rgba>();
            foreach (var (n, h) in rows) d[n] = Rgba.FromHex(h);
            return d;
        }

        private static readonly Dictionary<string, Rgba> DarkOverrides = Table(
            (TokenNames.SurfaceBase, 0x1F1E1C),
            (TokenNames.SurfacePopup, 0x1F1E1C),
            (TokenNames.SurfaceRaised, 0x262522),
            (TokenNames.SurfaceSunken, 0x1C1B19),
            (TokenNames.SurfaceHover, 0x2B2926),
            (TokenNames.SurfacePressed, 0x33302B),
            (TokenNames.SurfaceNav, 0x1A1917),
            (TokenNames.TextPrimary, 0xECEAE3),
            (TokenNames.TextSecondary, 0x9B988F),
            (TokenNames.TextTertiary, 0x8A877F),
            (TokenNames.TextDisabled, 0x6B6862),
            (TokenNames.TextLink, 0xE39A7B),
            (TokenNames.TextLinkHover, 0xF0B59C),
            (TokenNames.TextOnAccent, 0x1F1E1C),
            (TokenNames.AccentSubtle, 0x3A2A23),
            (TokenNames.AccentBorder, 0x6B4535),
            (TokenNames.BorderSubtle, 0x34322E),
            (TokenNames.BorderStrong, 0x3A3833),
            (TokenNames.Divider, 0x2F2D29),
            (TokenNames.SelectionBackground, 0x33302B),
            (TokenNames.SelectionForeground, 0xF5F3EC),
            (TokenNames.SelectionSecondary, 0x9B988F),
            (TokenNames.SelectionInactiveBackground, 0x2B2926),
            (TokenNames.NavSelected, 0x2B2926),
            (TokenNames.InputBackground, 0x1C1B19),
            (TokenNames.InputForeground, 0xECEAE3),
            (TokenNames.InputBorder, 0x3A3833),
            (TokenNames.InputPlaceholder, 0x75726B),
            (TokenNames.ButtonSecondaryBackground, 0x2B2926),
            (TokenNames.ButtonSecondaryHover, 0x33302B),
            (TokenNames.ButtonSecondaryPressed, 0x3A3834),
            (TokenNames.ButtonSecondaryBorder, 0x3A3833),
            (TokenNames.ButtonGhostHover, 0x2B2926),
            (TokenNames.ButtonGhostPressed, 0x33302B),
            (TokenNames.SegmentTrack, 0x1C1B19),
            (TokenNames.SegmentSelected, 0x3A3834),
            (TokenNames.ToggleStroke, 0x8A877F),
            (TokenNames.ToggleThumb, 0x1F1E1C),
            (TokenNames.KbdBackground, 0x262522),
            (TokenNames.KbdBorder, 0x3A3833),
            (TokenNames.KbdForeground, 0x8A877F),
            (TokenNames.TooltipBackground, 0x2B2926),
            (TokenNames.TooltipForeground, 0xECEAE3),
            (TokenNames.TooltipBorder, 0x3A3833),
            (TokenNames.CodeLineHighlight, 0x33302B),
            (TokenNames.ControlStrokeBottom, 0x3A3833));

        private static readonly Dictionary<string, Rgba> LightOverrides = Table(
            (TokenNames.SurfaceBase, 0xFAF9F5),
            (TokenNames.SurfacePopup, 0xFAF9F5),
            (TokenNames.SurfaceRaised, 0xFFFFFF),
            (TokenNames.SurfaceSunken, 0xF3F1EA),
            (TokenNames.SurfaceHover, 0xF0EEE6),
            (TokenNames.SurfacePressed, 0xE8E5DC),
            (TokenNames.SurfaceNav, 0xF3F1EA),
            (TokenNames.TextPrimary, 0x1F1E1C),
            (TokenNames.TextSecondary, 0x5E5B54),
            (TokenNames.TextTertiary, 0x6F6B63),
            (TokenNames.TextDisabled, 0xA8A49A),
            (TokenNames.TextLink, 0xA8502F),
            (TokenNames.TextLinkHover, 0x8A3F23),
            (TokenNames.TextOnAccent, 0xFFFFFF),
            (TokenNames.AccentSubtle, 0xF7E6DE),
            (TokenNames.AccentBorder, 0xE3B7A3),
            (TokenNames.BorderSubtle, 0xE5E2D9),
            (TokenNames.BorderStrong, 0xD6D2C6),
            (TokenNames.Divider, 0xECE9E1),
            (TokenNames.SelectionBackground, 0xEDE7DD),
            (TokenNames.SelectionForeground, 0x1F1E1C),
            (TokenNames.SelectionSecondary, 0x5E5B54),
            (TokenNames.SelectionInactiveBackground, 0xF0EEE6),
            (TokenNames.NavSelected, 0xE8E5DC),
            (TokenNames.InputBackground, 0xFFFFFF),
            (TokenNames.InputForeground, 0x1F1E1C),
            (TokenNames.InputBorder, 0xD6D2C6),
            (TokenNames.InputPlaceholder, 0x8C887E),
            (TokenNames.ButtonSecondaryBackground, 0xFFFFFF),
            (TokenNames.ButtonSecondaryHover, 0xF5F3EE),
            (TokenNames.ButtonSecondaryPressed, 0xECE9E1),
            (TokenNames.ButtonSecondaryBorder, 0xD6D2C6),
            (TokenNames.ButtonGhostHover, 0xF0EEE6),
            (TokenNames.ButtonGhostPressed, 0xE8E5DC),
            (TokenNames.SegmentTrack, 0xF0EEE6),
            (TokenNames.SegmentSelected, 0xFFFFFF),
            (TokenNames.ToggleStroke, 0x77736B),
            (TokenNames.ToggleThumb, 0xFFFFFF),
            (TokenNames.KbdBackground, 0xF5F3EE),
            (TokenNames.KbdBorder, 0xD6D2C6),
            (TokenNames.KbdForeground, 0x6F6B63),
            (TokenNames.TooltipBackground, 0xFFFFFF),
            (TokenNames.TooltipForeground, 0x1F1E1C),
            (TokenNames.TooltipBorder, 0xD6D2C6),
            (TokenNames.CodeLineHighlight, 0xF3EDE4),
            (TokenNames.ControlStrokeBottom, 0xC9C4B7));
    }
}
