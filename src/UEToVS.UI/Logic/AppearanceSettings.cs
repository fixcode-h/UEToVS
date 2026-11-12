using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UEToVS.UI.Logic
{
    /// <summary>One user override of a badge color ("" or null = derive from theme).</summary>
    public sealed class BadgeColorOverride
    {
        public BadgeKind Kind { get; set; }
        public string Color { get; set; }
    }

    /// <summary>
    /// Every user-tunable visual aspect (设计文档 §8.2.1). Plain, WPF-free, XmlSerializer / DialogPage friendly
    /// (public get/set properties + parameterless ctor); also has a compact text form via
    /// <see cref="ToStorageString"/> / <see cref="Parse"/>. Always call <see cref="Normalize"/> after editing by hand;
    /// the ThemeManager does it for you on Apply.
    /// </summary>
    public sealed class AppearanceSettings
    {
        public const int CurrentVersion = 1;

        // ---- limits (also used by the settings panel steppers) ----
        public const double MinFontScale = 0.8, MaxFontScale = 1.6;
        public const double MinRowHeight = 18, MaxRowHeight = 44;
        public const double MaxRadius = 16;
        public const double MinSpacingScale = 0.6, MaxSpacingScale = 1.6;
        public const int MaxAnimationMs = 250;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>界面风格. Changing it through <see cref="ApplyPreset"/> rewrites density fields.</summary>
        public StylePreset Preset { get; set; } = StylePreset.Rider;

        /// <summary>字号缩放, relative to the VS environment font (1.0 = 100%).</summary>
        public double FontScale { get; set; } = 1.0;

        /// <summary>列表行高 in DIPs.</summary>
        public double RowHeight { get; set; } = 24;

        public double PopupRadius { get; set; } = 6;
        public double ControlRadius { get; set; } = 4;
        public double BadgeRadius { get; set; } = 3;

        /// <summary>Multiplies paddings (not the 4px grid itself). Preset-driven: Compact 0.75, Rider 1, Comfortable 1.25.</summary>
        public double SpacingScale { get; set; } = 1.0;

        public AccentMode AccentMode { get; set; } = AccentMode.FollowVs;

        /// <summary>#RRGGBB used when <see cref="AccentMode"/> is Custom.</summary>
        public string CustomAccent { get; set; } = "#3574F0";

        public MatchHighlightStyle MatchStyle { get; set; } = MatchHighlightStyle.Bold | MatchHighlightStyle.AccentColor;

        /// <summary>Per-badge overrides; badges not listed derive their color from the theme.</summary>
        public List<BadgeColorOverride> BadgeColors { get; set; } = new List<BadgeColorOverride>();

        public bool PopupShadow { get; set; } = true;
        public PopupChromeMode PopupChrome { get; set; } = PopupChromeMode.Layered;

        public bool Animations { get; set; } = true;
        public int AnimationMs { get; set; } = 100;

        public bool PreviewVisible { get; set; } = true;
        public PreviewPosition PreviewPosition { get; set; } = PreviewPosition.Bottom;

        public static AppearanceSettings CreateDefault() => new AppearanceSettings();

        public AppearanceSettings Clone()
        {
            var c = (AppearanceSettings)MemberwiseClone();
            c.BadgeColors = new List<BadgeColorOverride>();
            if (BadgeColors != null)
                foreach (var b in BadgeColors)
                    if (b != null) c.BadgeColors.Add(new BadgeColorOverride { Kind = b.Kind, Color = b.Color });
            return c;
        }

        /// <summary>Applies the density values of a preset (row height, radii, spacing). Colors and toggles are kept.</summary>
        public void ApplyPreset(StylePreset preset)
        {
            Preset = preset;
            switch (preset)
            {
                case StylePreset.Compact:
                    RowHeight = 20; PopupRadius = 4; ControlRadius = 3; BadgeRadius = 2; SpacingScale = 0.75;
                    break;
                case StylePreset.Comfortable:
                    RowHeight = 28; PopupRadius = 8; ControlRadius = 5; BadgeRadius = 4; SpacingScale = 1.25;
                    break;
                default:
                    RowHeight = 24; PopupRadius = 6; ControlRadius = 4; BadgeRadius = 3; SpacingScale = 1.0;
                    break;
            }
        }

        public string GetBadgeColor(BadgeKind kind)
        {
            if (BadgeColors == null) return null;
            foreach (var b in BadgeColors)
                if (b != null && b.Kind == kind && !string.IsNullOrWhiteSpace(b.Color)) return b.Color;
            return null;
        }

        /// <summary>Sets (or with null/empty clears) the override for one badge kind.</summary>
        public void SetBadgeColor(BadgeKind kind, string color)
        {
            if (BadgeColors == null) BadgeColors = new List<BadgeColorOverride>();
            BadgeColors.RemoveAll(b => b == null || b.Kind == kind);
            if (!string.IsNullOrWhiteSpace(color)) BadgeColors.Add(new BadgeColorOverride { Kind = kind, Color = color.Trim() });
        }

        /// <summary>
        /// Clamps every value into its valid range and fixes invalid colors/enums. Returns human-readable notes for
        /// each correction (empty when nothing changed) so callers can log them.
        /// </summary>
        public IReadOnlyList<string> Normalize()
        {
            var notes = new List<string>();
            if (!Enum.IsDefined(typeof(StylePreset), Preset)) { notes.Add("Preset invalid: " + Preset); Preset = StylePreset.Rider; }
            if (!Enum.IsDefined(typeof(AccentMode), AccentMode)) { notes.Add("AccentMode invalid"); AccentMode = AccentMode.FollowVs; }
            if (!Enum.IsDefined(typeof(PreviewPosition), PreviewPosition)) { notes.Add("PreviewPosition invalid"); PreviewPosition = PreviewPosition.Bottom; }
            if (!Enum.IsDefined(typeof(PopupChromeMode), PopupChrome)) { notes.Add("PopupChrome invalid"); PopupChrome = PopupChromeMode.Layered; }
            var allMatch = MatchHighlightStyle.Bold | MatchHighlightStyle.Background | MatchHighlightStyle.Underline | MatchHighlightStyle.AccentColor;
            if ((MatchStyle & ~allMatch) != 0) { notes.Add("MatchStyle has unknown bits"); MatchStyle &= allMatch; }

            FontScale = Clamp(nameof(FontScale), FontScale, MinFontScale, MaxFontScale, 1.0, notes);
            RowHeight = Clamp(nameof(RowHeight), RowHeight, MinRowHeight, MaxRowHeight, 24, notes);
            PopupRadius = Clamp(nameof(PopupRadius), PopupRadius, 0, MaxRadius, 6, notes);
            ControlRadius = Clamp(nameof(ControlRadius), ControlRadius, 0, MaxRadius, 4, notes);
            BadgeRadius = Clamp(nameof(BadgeRadius), BadgeRadius, 0, MaxRadius, 3, notes);
            SpacingScale = Clamp(nameof(SpacingScale), SpacingScale, MinSpacingScale, MaxSpacingScale, 1.0, notes);
            if (AnimationMs < 0 || AnimationMs > MaxAnimationMs)
            {
                notes.Add("AnimationMs out of range: " + AnimationMs);
                AnimationMs = AnimationMs < 0 ? 0 : MaxAnimationMs;
            }

            if (string.IsNullOrWhiteSpace(CustomAccent) || !Rgba.TryParse(CustomAccent, out _))
            {
                if (!string.IsNullOrEmpty(CustomAccent)) notes.Add("CustomAccent invalid: " + CustomAccent);
                CustomAccent = "#3574F0";
            }

            if (BadgeColors == null) BadgeColors = new List<BadgeColorOverride>();
            var seen = new HashSet<BadgeKind>();
            for (int i = BadgeColors.Count - 1; i >= 0; i--)
            {
                var b = BadgeColors[i];
                bool bad = b == null || !Enum.IsDefined(typeof(BadgeKind), b.Kind) || string.IsNullOrWhiteSpace(b.Color)
                           || !Rgba.TryParse(b.Color, out _) || !seen.Add(b.Kind);
                if (bad)
                {
                    if (b != null && !string.IsNullOrWhiteSpace(b.Color)) notes.Add("Badge color dropped: " + b.Kind + "=" + b.Color);
                    BadgeColors.RemoveAt(i);
                }
            }
            Version = CurrentVersion;
            return notes;
        }

        private static double Clamp(string name, double v, double min, double max, double fallback, List<string> notes)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { notes.Add(name + " not a number"); return fallback; }
            if (v < min) { notes.Add(name + " below minimum: " + v.ToString(CultureInfo.InvariantCulture)); return min; }
            if (v > max) { notes.Add(name + " above maximum: " + v.ToString(CultureInfo.InvariantCulture)); return max; }
            return v;
        }

        // ------------------------------------------------------------------ text form

        /// <summary>"key=value" lines, invariant culture. Stable, diff-friendly, forward compatible (unknown keys ignored).</summary>
        public string ToStorageString()
        {
            var sb = new StringBuilder();
            void W(string k, string v) => sb.Append(k).Append('=').Append(v).Append('\n');
            string D(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
            W("version", Version.ToString(CultureInfo.InvariantCulture));
            W("preset", Preset.ToString());
            W("fontScale", D(FontScale));
            W("rowHeight", D(RowHeight));
            W("radius.popup", D(PopupRadius));
            W("radius.control", D(ControlRadius));
            W("radius.badge", D(BadgeRadius));
            W("spacingScale", D(SpacingScale));
            W("accentMode", AccentMode.ToString());
            W("accent", CustomAccent ?? "");
            W("matchStyle", ((int)MatchStyle).ToString(CultureInfo.InvariantCulture));
            W("popupShadow", PopupShadow ? "true" : "false");
            W("popupChrome", PopupChrome.ToString());
            W("animations", Animations ? "true" : "false");
            W("animationMs", AnimationMs.ToString(CultureInfo.InvariantCulture));
            W("previewVisible", PreviewVisible ? "true" : "false");
            W("previewPosition", PreviewPosition.ToString());
            if (BadgeColors != null)
                foreach (var b in BadgeColors)
                    if (b != null && !string.IsNullOrWhiteSpace(b.Color)) W("badge." + b.Kind, b.Color);
            return sb.ToString();
        }

        /// <summary>
        /// Parses <see cref="ToStorageString"/> output. Never throws: bad lines are skipped and reported in
        /// <paramref name="problems"/>; the result is normalized.
        /// </summary>
        public static AppearanceSettings Parse(string text, out List<string> problems)
        {
            problems = new List<string>();
            var s = new AppearanceSettings();
            if (string.IsNullOrWhiteSpace(text)) return s;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) { problems.Add("Malformed line: " + line); continue; }
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();
                if (!s.TrySet(key, val)) problems.Add("Bad value: " + line);
            }
            problems.AddRange(s.Normalize());
            return s;
        }

        private bool TrySet(string key, string val)
        {
            double d;
            switch (key)
            {
                case "version": return int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ver) && (Version = ver) == ver;
                case "preset": return TryEnum(val, out StylePreset p) && Set(() => Preset = p);
                case "fontScale": return TryD(val, out d) && Set(() => FontScale = d);
                case "rowHeight": return TryD(val, out d) && Set(() => RowHeight = d);
                case "radius.popup": return TryD(val, out d) && Set(() => PopupRadius = d);
                case "radius.control": return TryD(val, out d) && Set(() => ControlRadius = d);
                case "radius.badge": return TryD(val, out d) && Set(() => BadgeRadius = d);
                case "spacingScale": return TryD(val, out d) && Set(() => SpacingScale = d);
                case "accentMode": return TryEnum(val, out AccentMode am) && Set(() => AccentMode = am);
                case "accent": CustomAccent = val; return Rgba.TryParse(val, out _);
                case "matchStyle":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)) { MatchStyle = (MatchHighlightStyle)ms; return true; }
                    return TryEnum(val, out MatchHighlightStyle mse) && Set(() => MatchStyle = mse);
                case "popupShadow": return TryB(val, out var b1) && Set(() => PopupShadow = b1);
                case "popupChrome": return TryEnum(val, out PopupChromeMode pc) && Set(() => PopupChrome = pc);
                case "animations": return TryB(val, out var b2) && Set(() => Animations = b2);
                case "animationMs": return int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var am2) && Set(() => AnimationMs = am2);
                case "previewVisible": return TryB(val, out var b3) && Set(() => PreviewVisible = b3);
                case "previewPosition": return TryEnum(val, out PreviewPosition pp) && Set(() => PreviewPosition = pp);
            }
            if (key.StartsWith("badge.", StringComparison.Ordinal))
            {
                if (!TryEnum(key.Substring(6), out BadgeKind bk) || !Rgba.TryParse(val, out _)) return false;
                SetBadgeColor(bk, val);
                return true;
            }
            return true; // unknown key from a newer version: ignore silently (forward compatible)
        }

        private static bool Set(Action a) { a(); return true; }

        private static bool TryD(string v, out double d) =>
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d);

        private static bool TryB(string v, out bool b) => bool.TryParse(v, out b);

        private static bool TryEnum<T>(string v, out T e) where T : struct =>
            Enum.TryParse(v, true, out e) && Enum.IsDefined(typeof(T), e);

        public override bool Equals(object obj) => obj is AppearanceSettings o && o.ToStorageString() == ToStorageString();
        public override int GetHashCode() => ToStorageString().GetHashCode();
    }
}
