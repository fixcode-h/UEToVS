using System;
using System.Collections.Generic;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// Names of all semantic tokens. The WPF layer (<c>UEToVS.UI.Theming.Tokens</c>) exposes one resource key per
    /// name; <see cref="TokenDerivation"/> produces a value for every name. Feature code only uses semantic tokens.
    /// Color tokens become frozen SolidColorBrush resources (plus a Color resource under <c>name + ".Color"</c>).
    /// </summary>
    public static class TokenNames
    {
        // ---- surfaces
        public const string SurfaceBase = "Surface.Base";
        public const string SurfacePopup = "Surface.Popup";
        public const string SurfaceRaised = "Surface.Raised";
        public const string SurfaceSunken = "Surface.Sunken";
        public const string SurfaceHover = "Surface.Hover";
        public const string SurfacePressed = "Surface.Pressed";

        // ---- text
        public const string TextPrimary = "Text.Primary";
        public const string TextSecondary = "Text.Secondary";
        public const string TextDisabled = "Text.Disabled";
        public const string TextMatch = "Text.Match";
        public const string TextOnAccent = "Text.OnAccent";
        public const string TextLink = "Text.Link";
        public const string TextLinkHover = "Text.LinkHover";

        // ---- borders
        public const string BorderSubtle = "Border.Subtle";
        public const string BorderStrong = "Border.Strong";
        public const string BorderFocus = "Border.Focus";

        // ---- accent / selection / match
        public const string Accent = "Accent";
        public const string AccentHover = "Accent.Hover";
        public const string AccentPressed = "Accent.Pressed";
        public const string AccentSubtle = "Accent.Subtle";
        public const string SelectionBackground = "Selection.Background";
        public const string SelectionForeground = "Selection.Foreground";
        public const string SelectionSecondary = "Selection.Secondary";
        public const string SelectionInactiveBackground = "Selection.InactiveBackground";
        public const string MatchBackground = "Match.Background";

        // ---- inputs / buttons
        public const string InputBackground = "Input.Background";
        public const string InputForeground = "Input.Foreground";
        public const string InputBorder = "Input.Border";
        public const string InputPlaceholder = "Input.Placeholder";
        public const string ButtonSecondaryBackground = "Button.Secondary.Background";
        public const string ButtonSecondaryHover = "Button.Secondary.Hover";
        public const string ButtonSecondaryPressed = "Button.Secondary.Pressed";
        public const string ButtonSecondaryBorder = "Button.Secondary.Border";
        public const string ButtonGhostHover = "Button.Ghost.Hover";
        public const string ButtonGhostPressed = "Button.Ghost.Pressed";
        /// <summary>Toggle switch, off: track fill at rest (transparent, Fluent outline style).</summary>
        public const string ToggleTrackOff = "Toggle.Track.Off";
        /// <summary>Toggle switch, off + hover: faint fill.</summary>
        public const string ToggleTrackOffHover = "Toggle.Track.OffHover";
        /// <summary>Toggle switch, off + pressed.</summary>
        public const string ToggleTrackOffPressed = "Toggle.Track.OffPressed";
        /// <summary>Toggle switch, on + disabled: neutral (no accent) fill.</summary>
        public const string ToggleTrackOnDisabled = "Toggle.Track.OnDisabled";
        /// <summary>Toggle switch, off: track border and knob (Fluent ControlStrongStroke ≈ secondary text).</summary>
        public const string ToggleStroke = "Toggle.Stroke";
        /// <summary>Toggle switch, off + disabled: border and knob.</summary>
        public const string ToggleStrokeDisabled = "Toggle.StrokeDisabled";
        /// <summary>Toggle switch, on: knob (text-on-accent color).</summary>
        public const string ToggleThumb = "Toggle.Thumb";
        /// <summary>Toggle switch, on + disabled: knob.</summary>
        public const string ToggleThumbDisabled = "Toggle.ThumbDisabled";

        // ---- misc chrome
        public const string ScrollThumb = "ScrollBar.Thumb";
        public const string ScrollThumbHover = "ScrollBar.ThumbHover";
        public const string Shadow = "Shadow";
        public const string KbdBackground = "Kbd.Background";
        public const string KbdBorder = "Kbd.Border";
        public const string KbdForeground = "Kbd.Foreground";
        public const string TooltipBackground = "Tooltip.Background";
        public const string TooltipForeground = "Tooltip.Foreground";
        public const string TooltipBorder = "Tooltip.Border";
        public const string CodeLineHighlight = "Code.LineHighlight";
        public const string CodeLineNumber = "Code.LineNumber";

        // ---- VS 2026 (Fluent) additions (v0.3.0)
        /// <summary>Keyboard focus ring (Fluent focus visual: high-contrast outer stroke).</summary>
        public const string FocusStroke = "Focus.Stroke";
        /// <summary>Accent bar under a focused text input (Fluent "active" bottom stroke).</summary>
        public const string InputFocusUnderline = "Input.FocusUnderline";
        /// <summary>Bottom edge of raised controls (Fluent elevation stroke; a touch darker than the border).</summary>
        public const string ControlStrokeBottom = "Control.StrokeBottom";
        /// <summary>Background of the segmented-control track (the container behind the segments).</summary>
        public const string SegmentTrack = "Segment.Track";
        /// <summary>Small accent pill at the left of the selected list row (Fluent selection indicator).</summary>
        public const string SelectionIndicator = "Selection.Indicator";
        /// <summary>Title / navigation pane background of windows (settings window left rail).</summary>
        public const string SurfaceNav = "Surface.Nav";

        // ---- numbers (DIPs unless noted)
        public const string FontSizeBody = "Font.Size.Body";
        public const string FontSizeSmall = "Font.Size.Small";
        public const string FontSizeCaption = "Font.Size.Caption";
        public const string FontSizeTitle = "Font.Size.Title";
        public const string FontSizeSubtitle = "Font.Size.Subtitle";
        public const string FontSizeDisplay = "Font.Size.Display";
        public const string FontSizeInput = "Font.Size.Input";
        public const string FontSizeCode = "Font.Size.Code";
        public const string RowHeight = "Row.Height";
        public const string RowHeaderHeight = "Row.HeaderHeight";
        public const string IconSize = "Icon.Size";
        public const string RadiusPopup = "Radius.Popup";
        public const string RadiusControl = "Radius.Control";
        public const string RadiusBadge = "Radius.Badge";
        public const string PadRowX = "Pad.Row.X";
        public const string PadControlX = "Pad.Control.X";
        public const string PadControlY = "Pad.Control.Y";
        public const string PadPopup = "Pad.Popup";
        public const string GapInline = "Gap.Inline";
        public const string ShadowBlur = "Shadow.Blur";
        public const string ShadowDepth = "Shadow.Depth";
        public const string ShadowMargin = "Shadow.Margin";
        public const string AnimationMs = "Animation.Ms";
        /// <summary>Standard height of buttons, inputs and segmented controls.</summary>
        public const string ControlHeight = "Control.Height";
        /// <summary>Thickness of the keyboard focus ring.</summary>
        public const string FocusThickness = "Focus.Thickness";
        /// <summary>1 when matched characters are bold, else 0.</summary>
        public const string MatchBold = "Match.Bold";
        /// <summary>1 when matched characters are underlined, else 0.</summary>
        public const string MatchUnderline = "Match.Underline";

        /// <summary>4px grid: Space(1) = 4 … Space(8) = 32. Not scaled by density.</summary>
        public static string Space(int n) => "Space." + n;

        public static string Severity(Severity s) => "Severity." + s;
        public static string SeverityBackground(Severity s) => "Severity." + s + ".Background";
        public static string BadgeForeground(BadgeKind k) => "Badge." + k + ".Foreground";
        public static string BadgeBackground(BadgeKind k) => "Badge." + k + ".Background";
        public static string BadgeSolid(BadgeKind k) => "Badge." + k + ".Solid";
        public static string Glyph(SymbolKind k) => "Glyph." + k;
        public static string GlyphBackground(SymbolKind k) => "Glyph." + k + ".Background";

        public static IEnumerable<BadgeKind> AllBadgeKinds => (BadgeKind[])Enum.GetValues(typeof(BadgeKind));
        public static IEnumerable<SymbolKind> AllSymbolKinds => (SymbolKind[])Enum.GetValues(typeof(SymbolKind));
        public static IEnumerable<Severity> AllSeverities => (Severity[])Enum.GetValues(typeof(Severity));
    }
}
