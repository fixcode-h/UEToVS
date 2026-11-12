using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.UI.Logic
{
    /// <summary>What a font setting points at.</summary>
    public enum FontChoiceKind
    {
        /// <summary>"跟随 VS": the font VS uses for the same purpose (environment font / Text Editor font).</summary>
        FollowVs = 0,

        /// <summary>"不修改": leave VS's own value alone (VS font categories only).</summary>
        Keep = 1,

        /// <summary>"同代码字体": whatever the 代码字体 row resolves to (VS font categories only).</summary>
        SameAsCode = 2,

        /// <summary>"同界面字体": whatever the 英文字体 row resolves to (VS font categories only).</summary>
        SameAsUi = 3,

        /// <summary>A font shipped in the VSIX (<see cref="BundledFontCatalog"/>).</summary>
        Bundled = 4,

        /// <summary>A font installed in Windows, by family name.</summary>
        System = 5,
    }

    /// <summary>
    /// One font setting value. Stored as an invariant string: "vs", "keep", "code", "ui", "bundled:&lt;family&gt;" or a
    /// plain installed family name. <see cref="Parse"/> never throws; an empty value means <see cref="FontChoiceKind.FollowVs"/>.
    /// </summary>
    public readonly struct FontChoice : IEquatable<FontChoice>
    {
        public const string FollowVsValue = "vs";
        public const string KeepValue = "keep";
        public const string SameAsCodeValue = "code";
        public const string SameAsUiValue = "ui";
        public const string BundledPrefix = "bundled:";

        public FontChoice(FontChoiceKind kind, string family)
        {
            Kind = kind;
            Family = kind == FontChoiceKind.Bundled || kind == FontChoiceKind.System ? (family ?? string.Empty).Trim() : null;
        }

        public FontChoiceKind Kind { get; }

        /// <summary>Family name for <see cref="FontChoiceKind.Bundled"/> / <see cref="FontChoiceKind.System"/>; null otherwise.</summary>
        public string Family { get; }

        public static FontChoice FollowVs => new FontChoice(FontChoiceKind.FollowVs, null);
        public static FontChoice Keep => new FontChoice(FontChoiceKind.Keep, null);
        public static FontChoice SameAsCode => new FontChoice(FontChoiceKind.SameAsCode, null);
        public static FontChoice SameAsUi => new FontChoice(FontChoiceKind.SameAsUi, null);
        public static FontChoice OfBundled(BundledFont font) => new FontChoice(FontChoiceKind.Bundled, font.Family);
        public static FontChoice OfSystem(string family) => new FontChoice(FontChoiceKind.System, family);

        /// <summary>
        /// Parses a stored value. Keywords are case-insensitive. "bundled:X" with an unknown X, and a plain name that is a
        /// bundled family, both resolve to the bundled font when it exists (so a value typed by hand still works).
        /// </summary>
        public static FontChoice Parse(string value)
        {
            var v = (value ?? string.Empty).Trim();
            if (v.Length == 0 || v.Equals(FollowVsValue, StringComparison.OrdinalIgnoreCase)) return FollowVs;
            if (v.Equals(KeepValue, StringComparison.OrdinalIgnoreCase)) return Keep;
            if (v.Equals(SameAsCodeValue, StringComparison.OrdinalIgnoreCase)) return SameAsCode;
            if (v.Equals(SameAsUiValue, StringComparison.OrdinalIgnoreCase)) return SameAsUi;
            if (v.StartsWith(BundledPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = v.Substring(BundledPrefix.Length).Trim();
                var b = BundledFontCatalog.Find(name);
                return b != null ? OfBundled(b) : (name.Length == 0 ? FollowVs : OfSystem(name));
            }
            var bundled = BundledFontCatalog.Find(v);
            return bundled != null ? OfBundled(bundled) : OfSystem(v);
        }

        /// <summary>The stored form (round-trips through <see cref="Parse"/>).</summary>
        public override string ToString()
        {
            switch (Kind)
            {
                case FontChoiceKind.Keep: return KeepValue;
                case FontChoiceKind.SameAsCode: return SameAsCodeValue;
                case FontChoiceKind.SameAsUi: return SameAsUiValue;
                case FontChoiceKind.Bundled: return BundledPrefix + Family;
                case FontChoiceKind.System: return Family;
                default: return FollowVsValue;
            }
        }

        /// <summary>The bundled font this choice names, or null.</summary>
        public BundledFont Bundled => Kind == FontChoiceKind.Bundled ? BundledFontCatalog.Find(Family) : null;

        /// <summary>Text shown in the settings dropdown, e.g. "IBM Plex Sans（内置）", "跟随 VS", "Consolas".</summary>
        public string Display(bool english)
        {
            switch (Kind)
            {
                case FontChoiceKind.FollowVs: return english ? "Follow VS" : "跟随 VS";
                case FontChoiceKind.Keep: return english ? "Don't change (VS setting)" : "不修改（使用 VS 当前设置）";
                case FontChoiceKind.SameAsCode: return english ? "Same as code font" : "同代码字体";
                case FontChoiceKind.SameAsUi: return english ? "Same as UI font" : "同界面字体";
                case FontChoiceKind.Bundled:
                    var b = Bundled;
                    return b == null ? Family : (english ? b.DisplayEn + " (bundled)" : b.DisplayZh + "（内置）");
                default: return Family;
            }
        }

        public bool Equals(FontChoice other) => Kind == other.Kind && string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object obj) => obj is FontChoice o && Equals(o);
        public override int GetHashCode() => ((int)Kind * 397) ^ (Family == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Family));
    }

    /// <summary>A font family shipped in the VSIX under &lt;extension&gt;\Fonts\&lt;Folder&gt;\.</summary>
    public sealed class BundledFont
    {
        public BundledFont(string family, string folder, string displayZh, string displayEn, bool monospace, params string[] files)
        {
            Family = family;
            Folder = folder;
            DisplayZh = displayZh;
            DisplayEn = displayEn;
            Monospace = monospace;
            Files = files;
        }

        /// <summary>Family name inside the font files (what WPF and VS look up).</summary>
        public string Family { get; }

        /// <summary>Sub folder of the extension's Fonts folder.</summary>
        public string Folder { get; }

        public string DisplayZh { get; }
        public string DisplayEn { get; }
        public bool Monospace { get; }

        /// <summary>TTF file names in <see cref="Folder"/> (every weight).</summary>
        public IReadOnlyList<string> Files { get; }

        /// <summary>WPF family reference relative to the Fonts folder: "./IBMPlexSans/#IBM Plex Sans".</summary>
        public string WpfReference => "./" + Folder + "/#" + Family;

        public override string ToString() => Family;
    }

    /// <summary>The three fonts UEToVS ships (scripts/build_fonts.py builds them; OFL text next to each family).</summary>
    public static class BundledFontCatalog
    {
        /// <summary>Folder (under the extension directory) that holds one sub folder per family.</summary>
        public const string RootFolder = "Fonts";

        public static readonly BundledFont IbmPlexSans = new BundledFont("IBM Plex Sans", "IBMPlexSans", "IBM Plex Sans", "IBM Plex Sans", false,
            "IBMPlexSans-Regular.ttf", "IBMPlexSans-Medium.ttf", "IBMPlexSans-SemiBold.ttf");

        public static readonly BundledFont NotoSansSc = new BundledFont("Noto Sans SC", "NotoSansSC", "思源黑体 Noto Sans SC", "Noto Sans SC", false,
            "NotoSansSC-Regular.ttf", "NotoSansSC-Medium.ttf");

        public static readonly BundledFont MapleMonoNl = new BundledFont("Maple Mono NL", "MapleMonoNL", "Maple Mono NL", "Maple Mono NL", true,
            "MapleMonoNL-Regular.ttf"); // regular only (user 2026-11-12): bold is synthesized; saves ~5 MB

        public static IReadOnlyList<BundledFont> All { get; } = new[] { IbmPlexSans, NotoSansSc, MapleMonoNl };

        /// <summary>By family name (case-insensitive); null when not bundled.</summary>
        public static BundledFont Find(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return null;
            var f = family.Trim();
            return All.FirstOrDefault(b => string.Equals(b.Family, f, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>One entry of a WPF font fallback list: a bundled family (loaded from the VSIX) or an installed family name.</summary>
    public readonly struct FontRef : IEquatable<FontRef>
    {
        public FontRef(string family, BundledFont bundled)
        {
            Family = family;
            Bundled = bundled;
        }

        public string Family { get; }
        public BundledFont Bundled { get; }
        public bool IsBundled => Bundled != null;

        /// <summary>Entry for a WPF family list whose base URI is the extension's Fonts folder.</summary>
        public string WpfReference => IsBundled ? Bundled.WpfReference : Family;

        public bool Equals(FontRef other) => string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase) && IsBundled == other.IsBundled;
        public override bool Equals(object obj) => obj is FontRef o && Equals(o);
        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Family ?? string.Empty);
        public override string ToString() => WpfReference;
    }

    /// <summary>
    /// The UEToVS font settings (设置 > 外观 > 字体): 英文字体 + 中文字体 make the UI font (a WPF fallback list: Latin
    /// glyphs from the first family, CJK from the second), 代码字体 is used by code previews. Pure; the theme turns the
    /// result into shared FontFamily objects.
    /// </summary>
    public sealed class FontSettings
    {
        public static FontChoice DefaultUiLatin => FontChoice.OfBundled(BundledFontCatalog.IbmPlexSans);
        public static FontChoice DefaultUiCjk => FontChoice.OfBundled(BundledFontCatalog.NotoSansSc);
        public static FontChoice DefaultCode => FontChoice.OfBundled(BundledFontCatalog.MapleMonoNl);

        public FontChoice UiLatin { get; set; } = DefaultUiLatin;
        public FontChoice UiCjk { get; set; } = DefaultUiCjk;
        public FontChoice Code { get; set; } = DefaultCode;

        /// <summary>
        /// UI font fallback list. <paramref name="vsUiFamily"/> is VS's environment font (e.g. "Segoe UI" or
        /// "Microsoft YaHei UI"; may itself be a list). Returns an empty list when both rows follow VS: the caller keeps
        /// VS's own FontFamily object then. Duplicates are removed; the VS font is always last as the final fallback.
        /// </summary>
        public IReadOnlyList<FontRef> ComposeUi(string vsUiFamily)
        {
            var list = new List<FontRef>();
            if (UiLatin.Kind == FontChoiceKind.FollowVs && UiCjk.Kind == FontChoiceKind.FollowVs) return list;
            AddChoice(list, UiLatin, vsUiFamily);
            AddChoice(list, UiCjk, vsUiFamily);
            AddNames(list, vsUiFamily);
            return list;
        }

        /// <summary>Code font fallback list; <paramref name="vsEditorFamily"/> = VS Text Editor font (null = unknown).</summary>
        public IReadOnlyList<FontRef> ComposeCode(string vsEditorFamily, string fallback)
        {
            var list = new List<FontRef>();
            AddChoice(list, Code, vsEditorFamily);
            // CJK in comments / strings: the chosen Chinese UI font, then the generic monospace fallbacks.
            if (UiCjk.Kind == FontChoiceKind.Bundled || UiCjk.Kind == FontChoiceKind.System) AddChoice(list, UiCjk, null);
            AddNames(list, fallback);
            return list;
        }

        private static void AddChoice(List<FontRef> list, FontChoice c, string vsFamily)
        {
            switch (c.Kind)
            {
                case FontChoiceKind.Bundled:
                    var b = c.Bundled;
                    if (b != null) Add(list, new FontRef(b.Family, b));
                    break;
                case FontChoiceKind.System:
                    AddNames(list, c.Family);
                    break;
                default:
                    AddNames(list, vsFamily);
                    break;
            }
        }

        private static void AddNames(List<FontRef> list, string names)
        {
            if (string.IsNullOrWhiteSpace(names)) return;
            foreach (var part in names.Split(','))
            {
                var n = part.Trim();
                // A WPF location reference ("file:///…#X") from VS is kept verbatim.
                if (n.Length > 0) Add(list, new FontRef(n, null));
            }
        }

        private static void Add(List<FontRef> list, FontRef r)
        {
            if (!list.Any(x => string.Equals(x.Family, r.Family, StringComparison.OrdinalIgnoreCase))) list.Add(r);
        }

        /// <summary>WPF family list string ("./IBMPlexSans/#IBM Plex Sans, ./NotoSansSC/#Noto Sans SC, Segoe UI").</summary>
        public static string ToWpfFamilyList(IEnumerable<FontRef> refs) => string.Join(", ", refs.Select(r => r.WpfReference));

        /// <summary>
        /// Options of a font dropdown: the keywords that make sense for the row, the bundled fonts, then the installed
        /// families (sorted, bundled names and duplicates removed). <paramref name="current"/> is appended when it is an
        /// installed font that is missing (uninstalled since), so the row still shows what is stored.
        /// </summary>
        public static List<FontChoice> Options(IEnumerable<FontChoice> keywords, IEnumerable<string> installed, FontChoice? current, bool monospaceFirst = false)
        {
            var list = new List<FontChoice>();
            if (keywords != null) list.AddRange(keywords);
            var bundled = monospaceFirst ? BundledFontCatalog.All.OrderBy(b => b.Monospace ? 0 : 1) : BundledFontCatalog.All.AsEnumerable();
            foreach (var b in bundled) list.Add(FontChoice.OfBundled(b));
            var seen = new HashSet<string>(BundledFontCatalog.All.Select(b => b.Family), StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            if (installed != null)
                foreach (var n in installed)
                {
                    var t = (n ?? string.Empty).Trim();
                    if (t.Length == 0 || t.StartsWith("@", StringComparison.Ordinal) || !seen.Add(t)) continue; // "@Font" = vertical GDI face
                    names.Add(t);
                }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            list.AddRange(names.Select(FontChoice.OfSystem));
            if (current.HasValue && !list.Contains(current.Value)) list.Add(current.Value);
            return list;
        }
    }
}
