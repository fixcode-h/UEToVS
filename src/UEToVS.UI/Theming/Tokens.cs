using System.Collections.Generic;
using System.Windows;
using UEToVS.UI.Logic;

namespace UEToVS.UI.Theming
{
    /// <summary>
    /// WPF resource keys of the semantic design tokens. Use them with <c>SetResourceReference</c>
    /// (or <see cref="Ui.Bind"/>) so values update live when the theme or the appearance settings change.
    ///
    /// Value types: color tokens → frozen <c>SolidColorBrush</c> (the matching <c>Color</c> is under
    /// <see cref="ColorOf"/>); number tokens → <c>double</c>; <c>CornerRadius*</c> → <c>CornerRadius</c>;
    /// <c>Thickness*</c> → <c>Thickness</c>; <c>FontFamily*</c> → <c>FontFamily</c>; <see cref="MatchFontWeight"/> →
    /// <c>FontWeight</c>; <see cref="MatchDecorations"/> → <c>TextDecorationCollection</c> (may be null).
    /// </summary>
    public static class Tokens
    {
        private static readonly Dictionary<string, ComponentResourceKey> Keys = new Dictionary<string, ComponentResourceKey>();
        private static readonly Dictionary<string, ComponentResourceKey> ColorKeys = new Dictionary<string, ComponentResourceKey>();

        /// <summary>Resource key for any token name (see <see cref="TokenNames"/>).</summary>
        public static ComponentResourceKey Key(string name)
        {
            lock (Keys)
            {
                if (!Keys.TryGetValue(name, out var k)) Keys[name] = k = new ComponentResourceKey(typeof(Tokens), name);
                return k;
            }
        }

        /// <summary>Key of the <c>System.Windows.Media.Color</c> resource behind a color token.</summary>
        public static ComponentResourceKey ColorOf(string name)
        {
            lock (ColorKeys)
            {
                if (!ColorKeys.TryGetValue(name, out var k)) ColorKeys[name] = k = new ComponentResourceKey(typeof(Tokens), name + ".Color");
                return k;
            }
        }

        // ---- surfaces
        public static readonly ComponentResourceKey SurfaceBase = Key(TokenNames.SurfaceBase);
        public static readonly ComponentResourceKey SurfacePopup = Key(TokenNames.SurfacePopup);
        public static readonly ComponentResourceKey SurfaceRaised = Key(TokenNames.SurfaceRaised);
        public static readonly ComponentResourceKey SurfaceSunken = Key(TokenNames.SurfaceSunken);
        public static readonly ComponentResourceKey SurfaceHover = Key(TokenNames.SurfaceHover);
        public static readonly ComponentResourceKey SurfacePressed = Key(TokenNames.SurfacePressed);

        // ---- text
        public static readonly ComponentResourceKey TextPrimary = Key(TokenNames.TextPrimary);
        public static readonly ComponentResourceKey TextSecondary = Key(TokenNames.TextSecondary);
        public static readonly ComponentResourceKey TextDisabled = Key(TokenNames.TextDisabled);
        public static readonly ComponentResourceKey TextMatch = Key(TokenNames.TextMatch);
        public static readonly ComponentResourceKey TextOnAccent = Key(TokenNames.TextOnAccent);
        public static readonly ComponentResourceKey TextLink = Key(TokenNames.TextLink);
        public static readonly ComponentResourceKey TextLinkHover = Key(TokenNames.TextLinkHover);

        // ---- borders
        public static readonly ComponentResourceKey BorderSubtle = Key(TokenNames.BorderSubtle);
        public static readonly ComponentResourceKey BorderStrong = Key(TokenNames.BorderStrong);
        public static readonly ComponentResourceKey BorderFocus = Key(TokenNames.BorderFocus);

        // ---- accent / selection / match
        public static readonly ComponentResourceKey Accent = Key(TokenNames.Accent);
        public static readonly ComponentResourceKey AccentHover = Key(TokenNames.AccentHover);
        public static readonly ComponentResourceKey AccentPressed = Key(TokenNames.AccentPressed);
        public static readonly ComponentResourceKey AccentSubtle = Key(TokenNames.AccentSubtle);
        public static readonly ComponentResourceKey SelectionBackground = Key(TokenNames.SelectionBackground);
        public static readonly ComponentResourceKey SelectionForeground = Key(TokenNames.SelectionForeground);
        public static readonly ComponentResourceKey SelectionSecondary = Key(TokenNames.SelectionSecondary);
        public static readonly ComponentResourceKey SelectionInactiveBackground = Key(TokenNames.SelectionInactiveBackground);
        public static readonly ComponentResourceKey MatchBackground = Key(TokenNames.MatchBackground);

        // ---- inputs / buttons
        public static readonly ComponentResourceKey InputBackground = Key(TokenNames.InputBackground);
        public static readonly ComponentResourceKey InputForeground = Key(TokenNames.InputForeground);
        public static readonly ComponentResourceKey InputBorder = Key(TokenNames.InputBorder);
        public static readonly ComponentResourceKey InputPlaceholder = Key(TokenNames.InputPlaceholder);
        public static readonly ComponentResourceKey ButtonSecondaryBackground = Key(TokenNames.ButtonSecondaryBackground);
        public static readonly ComponentResourceKey ButtonSecondaryHover = Key(TokenNames.ButtonSecondaryHover);
        public static readonly ComponentResourceKey ButtonSecondaryPressed = Key(TokenNames.ButtonSecondaryPressed);
        public static readonly ComponentResourceKey ButtonSecondaryBorder = Key(TokenNames.ButtonSecondaryBorder);
        public static readonly ComponentResourceKey ButtonGhostHover = Key(TokenNames.ButtonGhostHover);
        public static readonly ComponentResourceKey ButtonGhostPressed = Key(TokenNames.ButtonGhostPressed);
        public static readonly ComponentResourceKey ToggleTrackOff = Key(TokenNames.ToggleTrackOff);
        public static readonly ComponentResourceKey ToggleThumb = Key(TokenNames.ToggleThumb);

        // ---- misc chrome
        public static readonly ComponentResourceKey ScrollThumb = Key(TokenNames.ScrollThumb);
        public static readonly ComponentResourceKey ScrollThumbHover = Key(TokenNames.ScrollThumbHover);
        public static readonly ComponentResourceKey Shadow = Key(TokenNames.Shadow);
        public static readonly ComponentResourceKey KbdBackground = Key(TokenNames.KbdBackground);
        public static readonly ComponentResourceKey KbdBorder = Key(TokenNames.KbdBorder);
        public static readonly ComponentResourceKey KbdForeground = Key(TokenNames.KbdForeground);
        public static readonly ComponentResourceKey TooltipBackground = Key(TokenNames.TooltipBackground);
        public static readonly ComponentResourceKey TooltipForeground = Key(TokenNames.TooltipForeground);
        public static readonly ComponentResourceKey TooltipBorder = Key(TokenNames.TooltipBorder);
        public static readonly ComponentResourceKey CodeLineHighlight = Key(TokenNames.CodeLineHighlight);
        public static readonly ComponentResourceKey CodeLineNumber = Key(TokenNames.CodeLineNumber);
        public static readonly ComponentResourceKey FocusStroke = Key(TokenNames.FocusStroke);
        public static readonly ComponentResourceKey InputFocusUnderline = Key(TokenNames.InputFocusUnderline);
        public static readonly ComponentResourceKey ControlStrokeBottom = Key(TokenNames.ControlStrokeBottom);
        public static readonly ComponentResourceKey SegmentTrack = Key(TokenNames.SegmentTrack);
        public static readonly ComponentResourceKey SelectionIndicator = Key(TokenNames.SelectionIndicator);
        public static readonly ComponentResourceKey SurfaceNav = Key(TokenNames.SurfaceNav);

        // ---- numbers (double)
        public static readonly ComponentResourceKey FontSizeBody = Key(TokenNames.FontSizeBody);
        public static readonly ComponentResourceKey FontSizeSmall = Key(TokenNames.FontSizeSmall);
        public static readonly ComponentResourceKey FontSizeCaption = Key(TokenNames.FontSizeCaption);
        public static readonly ComponentResourceKey FontSizeTitle = Key(TokenNames.FontSizeTitle);
        public static readonly ComponentResourceKey FontSizeInput = Key(TokenNames.FontSizeInput);
        public static readonly ComponentResourceKey FontSizeCode = Key(TokenNames.FontSizeCode);
        public static readonly ComponentResourceKey RowHeight = Key(TokenNames.RowHeight);
        public static readonly ComponentResourceKey RowHeaderHeight = Key(TokenNames.RowHeaderHeight);
        public static readonly ComponentResourceKey IconSize = Key(TokenNames.IconSize);
        public static readonly ComponentResourceKey RadiusPopup = Key(TokenNames.RadiusPopup);
        public static readonly ComponentResourceKey RadiusControl = Key(TokenNames.RadiusControl);
        public static readonly ComponentResourceKey RadiusBadge = Key(TokenNames.RadiusBadge);
        public static readonly ComponentResourceKey PadRowX = Key(TokenNames.PadRowX);
        public static readonly ComponentResourceKey GapInline = Key(TokenNames.GapInline);
        public static readonly ComponentResourceKey AnimationMs = Key(TokenNames.AnimationMs);
        public static readonly ComponentResourceKey ControlHeight = Key(TokenNames.ControlHeight);
        public static readonly ComponentResourceKey FocusThickness = Key(TokenNames.FocusThickness);

        // ---- WPF-typed derived resources
        public static readonly ComponentResourceKey CornerRadiusPopup = Key("CornerRadius.Popup");
        public static readonly ComponentResourceKey CornerRadiusControl = Key("CornerRadius.Control");
        public static readonly ComponentResourceKey CornerRadiusBadge = Key("CornerRadius.Badge");
        /// <summary>Fully rounded (pill) corner radius. Only for the toggle-switch track and tiny dots; chips and
        /// segmented controls are rounded rectangles (<see cref="CornerRadiusControl"/>) since v0.3.0.</summary>
        public static readonly ComponentResourceKey CornerRadiusPill = Key("CornerRadius.Pill");
        /// <summary>Control radius minus 1 (inner element inside a control-radius container, e.g. a segment).</summary>
        public static readonly ComponentResourceKey CornerRadiusInner = Key("CornerRadius.Inner");
        /// <summary>Control radius plus the focus-ring offset (the ring sits 3px outside the control).</summary>
        public static readonly ComponentResourceKey CornerRadiusFocus = Key("CornerRadius.Focus");
        /// <summary>Focus ring thickness as a <c>Thickness</c>.</summary>
        public static readonly ComponentResourceKey ThicknessFocus = Key("Thickness.Focus");
        public static readonly ComponentResourceKey ThicknessControlPadding = Key("Thickness.ControlPadding");
        public static readonly ComponentResourceKey ThicknessRowPadding = Key("Thickness.RowPadding");
        public static readonly ComponentResourceKey ThicknessPopupPadding = Key("Thickness.PopupPadding");
        public static readonly ComponentResourceKey FontFamilyUi = Key("FontFamily.Ui");
        public static readonly ComponentResourceKey FontFamilyCode = Key("FontFamily.Code");
        public static readonly ComponentResourceKey MatchFontWeight = Key("Match.FontWeight");
        public static readonly ComponentResourceKey MatchDecorations = Key("Match.Decorations");

        public static ComponentResourceKey Space(int n) => Key(TokenNames.Space(n));
        public static ComponentResourceKey Severity(Severity s) => Key(TokenNames.Severity(s));
        public static ComponentResourceKey SeverityBackground(Severity s) => Key(TokenNames.SeverityBackground(s));
        public static ComponentResourceKey BadgeForeground(BadgeKind k) => Key(TokenNames.BadgeForeground(k));
        public static ComponentResourceKey BadgeBackground(BadgeKind k) => Key(TokenNames.BadgeBackground(k));
        public static ComponentResourceKey BadgeSolid(BadgeKind k) => Key(TokenNames.BadgeSolid(k));
        public static ComponentResourceKey Glyph(SymbolKind k) => Key(TokenNames.Glyph(k));
        public static ComponentResourceKey GlyphBackground(SymbolKind k) => Key(TokenNames.GlyphBackground(k));
    }
}
