using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Fonts
{
    /// <summary>
    /// Settings window rows of 外观 > 字体 (descriptors in <see cref="HostSettingsSchema.Appearance"/>) and their
    /// HostSettings codec. Kept out of HostSettingsSchema so the font feature stays in one place; the schema calls
    /// <see cref="AddDescriptors"/>, <see cref="ToValues"/> and <see cref="TrySet"/>.
    /// </summary>
    public static class FontSettingsSchema
    {
        public const string UiLatinKey = "fonts.uiLatin";
        public const string UiCjkKey = "fonts.uiCjk";
        public const string CodeKey = "fonts.code";
        public const string ManageVsKey = "fonts.vs.manage";
        public const string VsPrefix = "fonts.vs.";
        public const string FamilySuffix = ".family";
        public const string SizeSuffix = ".size";

        private const string Group = "字体", GroupEn = "Fonts";
        private const string VsGroup = "VS 字体", VsGroupEn = "VS fonts";
        private const string Keywords = "font fonts 字体 字号 Fonts and Colors 字体和颜色";

        public static string FamilyKey(VsFontCategory c) => VsPrefix + c.Id + FamilySuffix;
        public static string SizeKey(VsFontCategory c) => VsPrefix + c.Id + SizeSuffix;

        /// <summary>Keywords offered by each kind of row.</summary>
        public static IReadOnlyList<FontChoice> UiKeywords { get; } = new[] { FontChoice.FollowVs };
        public static IReadOnlyList<FontChoice> VsKeywords { get; } = new[] { FontChoice.Keep, FontChoice.SameAsCode, FontChoice.SameAsUi };

        public static bool IsFontKey(string key) => key != null && key.StartsWith("fonts.", StringComparison.Ordinal);

        public static void AddDescriptors(List<SettingDescriptor> list, string category)
        {
            list.Add(FontRow(category, UiLatinKey, "英文字体", "Latin font", "字母、数字和符号用的字体", "Font for letters, digits and symbols",
                FontSettings.DefaultUiLatin, UiKeywords, false, Group, GroupEn));
            list.Add(FontRow(category, UiCjkKey, "中文字体", "Chinese font", "汉字用的字体", "Font for Chinese characters",
                FontSettings.DefaultUiCjk, UiKeywords, false, Group, GroupEn));
            list.Add(FontRow(category, CodeKey, "代码字体", "Code font", "代码、命令和路径用的等宽字体", "Monospace font for code, commands and paths",
                FontSettings.DefaultCode, UiKeywords, true, Group, GroupEn));

            list.Add(new SettingDescriptor
            {
                Category = category, Key = ManageVsKey, Kind = SettingKind.Bool, GroupZh = VsGroup, GroupEn = VsGroupEn,
                TitleZh = "由 UEToVS 管理 VS 字体", TitleEn = "Let UEToVS manage VS fonts",
                DescriptionZh = "代替 工具 > 选项 > 环境 > 字体和颜色：按下面的设置写入 VS 的字体（内置字体会先为当前用户安装，无需管理员）。首次修改前备份原字体，关闭后恢复。新装的字体可能要重启 VS 才完全生效。",
                DescriptionEn = "Replaces Tools > Options > Environment > Fonts and Colors: writes VS's fonts from the rows below (bundled fonts are installed for the current user first, no admin needed). The original fonts are backed up before the first change and restored when turned off. Newly installed fonts may need a VS restart to take full effect.",
                Default = "true", Keywords = Keywords,
            });
            foreach (var c in VsFontCategories.All)
            {
                list.Add(FontRow(category, FamilyKey(c), c.TitleZh, c.TitleEn, null, null, c.DefaultFamily, VsKeywords, c.Role == VsFontRole.Code, VsGroup, VsGroupEn));
                list.Add(new SettingDescriptor
                {
                    Category = category, Key = SizeKey(c), Kind = SettingKind.Int, GroupZh = VsGroup, GroupEn = VsGroupEn,
                    TitleZh = c.TitleZh + " · 字号", TitleEn = c.TitleEn + " · size",
                    Default = c.DefaultSize.ToString(CultureInfo.InvariantCulture), Min = 0, Max = VsFontCategories.MaxSize,
                    AutoLabelZh = "不修改（保持 VS 当前字号）", AutoLabelEn = "Don't change (keep VS's size)",
                    Keywords = Keywords,
                });
            }
        }

        private static SettingDescriptor FontRow(string category, string key, string zh, string en, string descZh, string descEn, FontChoice def,
                                                 IReadOnlyList<FontChoice> keywords, bool monospaceFirst, string group, string groupEn) =>
            new SettingDescriptor
            {
                Category = category, Key = key, Kind = SettingKind.Choice, TitleZh = zh, TitleEn = en, DescriptionZh = descZh, DescriptionEn = descEn,
                GroupZh = group, GroupEn = groupEn, Default = def.ToString(), AllowCustomValue = true, Keywords = Keywords,
                Options = ToOptions(FontSettings.Options(keywords, null, null, monospaceFirst)),
            };

        public static IReadOnlyList<SettingOption> ToOptions(IEnumerable<FontChoice> choices) =>
            choices.Select(c => new SettingOption(c.ToString(), c.Display(false), c.Display(true))).ToList();

        /// <summary>
        /// Makes the font dropdowns list the installed families (asked when a page is built; the provider should cache).
        /// Stored values that are no longer installed still show (appended by <see cref="SettingDescriptor.ResolveOptions"/>).
        /// </summary>
        public static void AttachInstalledFonts(IEnumerable<SettingDescriptor> schema, Func<IReadOnlyList<string>> installed)
        {
            foreach (var d in schema)
            {
                if (d.Kind != SettingKind.Choice || !IsFontKey(d.Key)) continue;
                bool vs = d.Key.StartsWith(VsPrefix, StringComparison.Ordinal);
                var cat = vs ? VsFontCategories.Find(d.Key.Substring(VsPrefix.Length, d.Key.Length - VsPrefix.Length - FamilySuffix.Length)) : null;
                bool mono = d.Key == CodeKey || cat?.Role == VsFontRole.Code;
                var keywords = vs ? VsKeywords : UiKeywords;
                d.OptionsSource = () => ToOptions(FontSettings.Options(keywords, installed?.Invoke(), null, mono));
            }
        }

        public static void ToValues(HostSettings s, IDictionary<string, string> d)
        {
            d[UiLatinKey] = FontChoice.Parse(s.UiLatinFont).ToString();
            d[UiCjkKey] = FontChoice.Parse(s.UiCjkFont).ToString();
            d[CodeKey] = FontChoice.Parse(s.CodeFont).ToString();
            d[ManageVsKey] = s.ManageVsFonts ? "true" : "false";
            var map = VsFontSettingsText.Parse(s.VsFonts);
            foreach (var c in VsFontCategories.All)
            {
                d[FamilyKey(c)] = map[c.Id].Family.ToString();
                d[SizeKey(c)] = map[c.Id].Size.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Sets one normalized value; false when <paramref name="key"/> is not a font key.</summary>
        public static bool TrySet(HostSettings s, string key, string v)
        {
            if (!IsFontKey(key)) return false;
            switch (key)
            {
                case UiLatinKey: s.UiLatinFont = FontChoice.Parse(v).ToString(); return true;
                case UiCjkKey: s.UiCjkFont = FontChoice.Parse(v).ToString(); return true;
                case CodeKey: s.CodeFont = FontChoice.Parse(v).ToString(); return true;
                case ManageVsKey: s.ManageVsFonts = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase); return true;
            }
            foreach (var c in VsFontCategories.All)
            {
                if (key == FamilyKey(c)) { s.VsFonts = VsFontSettingsText.With(s.VsFonts, c.Id, FontChoice.Parse(v), null); return true; }
                if (key == SizeKey(c))
                {
                    int size = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : c.DefaultSize;
                    s.VsFonts = VsFontSettingsText.With(s.VsFonts, c.Id, null, size);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Flat values for the session header / diagnostics.</summary>
        public static void Describe(HostSettings s, IDictionary<string, object> d)
        {
            d["uiLatinFont"] = s.UiLatinFont ?? "";
            d["uiCjkFont"] = s.UiCjkFont ?? "";
            d["codeFont"] = s.CodeFont ?? "";
            d["manageVsFonts"] = s.ManageVsFonts;
            d["vsFonts"] = s.VsFonts ?? "";
        }

        /// <summary>Any font setting differs (UI fonts or VS fonts).</summary>
        public static bool Changed(HostSettings a, HostSettings b) =>
            a == null || b == null || a.UiLatinFont != b.UiLatinFont || a.UiCjkFont != b.UiCjkFont || a.CodeFont != b.CodeFont ||
            a.ManageVsFonts != b.ManageVsFonts || (a.VsFonts ?? "") != (b.VsFonts ?? "");
    }
}
