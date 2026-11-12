using System;
using System.Globalization;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// "自动" + number editor for Int settings where a stored 0 means auto (thread counts, the UEToVSLink port).
    /// The window shows a check box "自动" and a number box that is disabled (showing what auto resolves to) while
    /// auto is on, so the old "自动 (8)" label next to a box containing 0 is gone. Stored semantics are unchanged: 0 = auto.
    /// </summary>
    public static class AutoNumberModel
    {
        /// <summary>The descriptor uses the auto editor (Int with an auto label).</summary>
        public static bool SupportsAuto(SettingDescriptor d) =>
            d != null && d.Kind == SettingKind.Int && !string.IsNullOrEmpty(d.AutoLabelZh ?? d.AutoLabelEn);

        public static bool IsAuto(string stored) =>
            !int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v == 0;

        /// <summary>Smallest value the number box accepts when auto is off (0 is reserved for auto).</summary>
        public static int ManualMin(SettingDescriptor d) => Math.Max(1, d.Min);

        public static int ManualMax(SettingDescriptor d) => Math.Max(ManualMin(d), d.Max);

        /// <summary>
        /// Value shown in the number box. Auto: what auto resolves to (null = leave the box empty, e.g. the port).
        /// Manual: the stored value clamped to the manual range.
        /// </summary>
        public static int? DisplayValue(SettingDescriptor d, string stored)
        {
            if (IsAuto(stored)) return d.AutoValue;
            int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v);
            return Clamp(d, v);
        }

        /// <summary>Value the box starts from when the user turns auto off: the auto value if known, else the minimum.</summary>
        public static int ManualSeed(SettingDescriptor d) => Clamp(d, d.AutoValue ?? ManualMin(d));

        /// <summary>Stored string for the editor state.</summary>
        public static string ToStored(SettingDescriptor d, bool auto, double number)
        {
            if (auto) return "0";
            int v = double.IsNaN(number) ? ManualSeed(d) : (int)Math.Round(number);
            return Clamp(d, v).ToString(CultureInfo.InvariantCulture);
        }

        private static int Clamp(SettingDescriptor d, int v) => Math.Max(ManualMin(d), Math.Min(ManualMax(d), v));
    }
}
