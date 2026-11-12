using System;
using System.Globalization;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>
    /// Reference-highlight background colors (design §7.7): read / write, dark and light theme defaults, overridable by the
    /// settings as "#RRGGBB" or "#AARRGGBB" (empty = theme default). Pure.
    /// </summary>
    public static class HighlightColors
    {
        public const uint DarkRead = 0xFF264F78;
        public const uint DarkWrite = 0xFF5A3D1E;
        public const uint LightRead = 0xFFADD6FF;
        public const uint LightWrite = 0xFFF6D6A8;

        /// <summary>ARGB to use: the override when it parses, else the theme default.</summary>
        public static uint Resolve(string setting, bool write, bool darkTheme)
        {
            if (TryParse(setting, out uint argb)) return argb;
            return darkTheme ? (write ? DarkWrite : DarkRead) : (write ? LightWrite : LightRead);
        }

        public static bool TryParse(string text, out uint argb)
        {
            argb = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.StartsWith("#", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.Length != 6 && t.Length != 8) return false;
            if (!uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
            argb = t.Length == 6 ? 0xFF000000 | v : v;
            return true;
        }

        public static string Format(uint argb) =>
            (argb >> 24) == 0xFF ? "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture) : "#" + argb.ToString("X8", CultureInfo.InvariantCulture);

        /// <summary>Dark editor background? (relative luminance below 0.5).</summary>
        public static bool IsDark(byte r, byte g, byte b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 < 0.5;
    }
}
