namespace UEToVS.UI.Logic
{
    /// <summary>
    /// The handful of raw colors read from the host theme (VS EnvironmentColors / CommonControlsColors).
    /// Only <see cref="Background"/> and <see cref="Foreground"/> are required; every null entry is derived.
    /// </summary>
    public sealed class ThemePalette
    {
        public string Name { get; set; } = "Custom";
        public Rgba Background { get; set; }
        public Rgba Foreground { get; set; }
        public Rgba? PopupBackground { get; set; }
        public Rgba? Border { get; set; }
        public Rgba? GrayText { get; set; }
        /// <summary>VS theme accent (EnvironmentColors.SystemHighlight).</summary>
        public Rgba? Accent { get; set; }
        /// <summary>Windows system accent (DWM AccentColor), or null outside Windows / when unreadable. Fluent controls
        /// (toggle switches, primary buttons) prefer it; see <see cref="AccentResolver"/>.</summary>
        public Rgba? SystemAccent { get; set; }
        public Rgba? SelectionBackground { get; set; }
        public Rgba? SelectionText { get; set; }
        public Rgba? InputBackground { get; set; }
        public Rgba? InputText { get; set; }
        public Rgba? InputBorder { get; set; }
        public Rgba? InputBorderFocused { get; set; }
        public Rgba? ToolTipBackground { get; set; }
        public Rgba? ToolTipText { get; set; }
        public Rgba? Link { get; set; }

        /// <summary>Windows high-contrast mode: tokens use the palette colors directly, no tints.</summary>
        public bool IsHighContrast { get; set; }

        /// <summary>VS environment font size in DIPs (VS default 9pt = 12 DIP).</summary>
        public double BaseFontSize { get; set; } = 12;

        public bool IsDark => Background.IsDark;

        public ThemePalette Clone() => (ThemePalette)MemberwiseClone();

        /// <summary>Approximation of VS 2026 Dark, used when no VS resources are available (gallery, tests).</summary>
        public static ThemePalette FallbackDark() => new ThemePalette
        {
            Name = "Dark",
            Background = Rgba.FromHex(0x1F1F1F),
            Foreground = Rgba.FromHex(0xDEDEDE),
            PopupBackground = Rgba.FromHex(0x2B2D30),
            Border = Rgba.FromHex(0x3F3F46),
            GrayText = Rgba.FromHex(0x9DA0A6),
            // Accent left null: resolves to the Fluent default blue, dark-theme shade (AccentResolver).
            InputBackground = Rgba.FromHex(0x1E1F22),
            InputText = Rgba.FromHex(0xDFE1E5),
            InputBorder = Rgba.FromHex(0x43454A),
            InputBorderFocused = Rgba.FromHex(0x0078D4),
            Link = Rgba.FromHex(0x57A6FF),
        };

        /// <summary>Approximation of VS 2026 Light.</summary>
        public static ThemePalette FallbackLight() => new ThemePalette
        {
            Name = "Light",
            Background = Rgba.FromHex(0xF5F5F5),
            Foreground = Rgba.FromHex(0x1E1E1E),
            PopupBackground = Rgba.FromHex(0xFFFFFF),
            Border = Rgba.FromHex(0xCCCEDB),
            GrayText = Rgba.FromHex(0x6C707E),
            // Accent left null: resolves to the Fluent default blue, light-theme shade (AccentResolver).
            InputBackground = Rgba.FromHex(0xFFFFFF),
            InputText = Rgba.FromHex(0x1E1E1E),
            InputBorder = Rgba.FromHex(0xC9CCD6),
            InputBorderFocused = Rgba.FromHex(0x0078D4),
            Link = Rgba.FromHex(0x2A64D6),
        };

        /// <summary>Approximation of VS Blue.</summary>
        public static ThemePalette FallbackBlue() => new ThemePalette
        {
            Name = "Blue",
            Background = Rgba.FromHex(0xEEF2F8),
            Foreground = Rgba.FromHex(0x1E1E1E),
            PopupBackground = Rgba.FromHex(0xFFFFFF),
            Border = Rgba.FromHex(0x8E9BBC),
            GrayText = Rgba.FromHex(0x5B6278),
            Accent = Rgba.FromHex(0x0E70C0),
            Link = Rgba.FromHex(0x0E70C0),
        };

        /// <summary>Windows "High Contrast Black"-like palette.</summary>
        public static ThemePalette FallbackHighContrast() => new ThemePalette
        {
            Name = "HighContrast",
            Background = Rgba.Black,
            Foreground = Rgba.White,
            Border = Rgba.White,
            GrayText = Rgba.FromHex(0x3FF23F),
            Accent = Rgba.FromHex(0x1AEBFF),
            SelectionBackground = Rgba.FromHex(0x1AEBFF),
            SelectionText = Rgba.Black,
            Link = Rgba.FromHex(0xFFFF00),
            IsHighContrast = true,
        };
    }
}
