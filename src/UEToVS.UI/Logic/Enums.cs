using System;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// Overall look. Picking a preset rewrites the density-related settings (row height, radii, spacing).
    /// Value 0 was called "Rider" before v0.3.0; <see cref="AppearanceSettings.Parse"/> still reads that name.
    /// </summary>
    public enum StylePreset
    {
        /// <summary>Standard density (shown as "标准"; the enum name is kept for stored settings).</summary>
        Vs2026 = 0,
        Compact = 1,
        Comfortable = 2,
    }

    /// <summary>Color scheme of every UEToVS surface.</summary>
    public enum ColorScheme
    {
        /// <summary>Warm neutrals + clay accent (Claude Code style, v0.4.5 default).</summary>
        Warm = 0,
        /// <summary>Colors derived from the VS theme (pre-v0.4.5 look).</summary>
        FollowVs = 1,
    }

    public enum AccentMode
    {
        /// <summary>Use the VS theme's highlight color.</summary>
        FollowVs = 0,
        Custom = 1,
    }

    /// <summary>How matched characters are drawn in <c>HighlightedText</c>. Flags combine.</summary>
    [Flags]
    public enum MatchHighlightStyle
    {
        None = 0,
        Bold = 1,
        Background = 2,
        Underline = 4,
        /// <summary>Draw matched characters in the accent color.</summary>
        AccentColor = 8,
    }

    public enum PreviewPosition
    {
        Bottom = 0,
        Right = 1,
    }

    /// <summary>
    /// How popup chrome (rounded corners + shadow) is produced.
    /// Layered: transparent WPF window, exact radius and soft shadow on every Windows version.
    /// Native: opaque window, DWM draws shadow and (Windows 11) rounded corners; cheapest to render.
    /// </summary>
    public enum PopupChromeMode
    {
        Layered = 0,
        Native = 1,
    }

    /// <summary>Badge categories. Each has a themed color token <c>Badge.&lt;Kind&gt;.*</c>.</summary>
    public enum BadgeKind
    {
        Neutral = 0,
        UClass,
        UFunction,
        UProperty,
        UStruct,
        UEnum,
        UInterface,
        Delegate,
        Macro,
        File,
        Asset,
        Module,
    }

    /// <summary>Symbol kinds shown by <c>TypeGlyph</c>. Each has a themed color token <c>Glyph.&lt;Kind&gt;</c>.</summary>
    public enum SymbolKind
    {
        Unknown = 0,
        Class,
        Struct,
        Interface,
        Enum,
        EnumMember,
        Function,
        Method,
        Property,
        Field,
        Variable,
        Parameter,
        Macro,
        Namespace,
        Typedef,
        Delegate,
        File,
        Asset,
        Module,
        Keyword,
        Action,
    }

    public enum Severity
    {
        Info = 0,
        Success = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>How an item is opened (keyboard conventions §8.4).</summary>
    public enum InvokeKind
    {
        /// <summary>Enter / double-click.</summary>
        Open = 0,
        /// <summary>Shift+Enter: open in a new tab / side.</summary>
        OpenInNewTab = 1,
        /// <summary>Ctrl+Enter: send all results to the results window.</summary>
        OpenInResults = 2,
    }
}
