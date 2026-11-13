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
        /// <summary>Info row under the toggle: what VS shows now (fonts that need a VS restart, management off…).</summary>
        public const string VsStatusKey = "fonts.vs.status";
        public const string VsStatusInfoId = "uetovs.fonts.vsStatus";
        public const string VsPrefix = "fonts.vs.";
        public const string FamilySuffix = ".family";
        public const string SizeSuffix = ".size";

        private const string Group = "字体", GroupEn = "Fonts";
        private const string VsGroup = "VS 字体", VsGroupEn = "VS fonts";
        // 9 个类别分两组：显示代码 / 输出的窗口，和显示界面文字的窗口（VsFontCategory.Role）
        private const string VsCodeGroup = "VS 字体 · 代码类窗口", VsCodeGroupEn = "VS fonts · code windows";
        private const string VsUiGroup = "VS 字体 · 界面类窗口", VsUiGroupEn = "VS fonts · interface windows";
        private const string Keywords = "font fonts 字体 字号 Fonts and Colors 字体和颜色";

        public static string FamilyKey(VsFontCategory c) => VsPrefix + c.Id + FamilySuffix;
        public static string SizeKey(VsFontCategory c) => VsPrefix + c.Id + SizeSuffix;

        /// <summary>Keywords offered by each kind of row.</summary>
        public static IReadOnlyList<FontChoice> UiKeywords { get; } = new[] { FontChoice.FollowVs };
        public static IReadOnlyList<FontChoice> VsKeywords { get; } = new[] { FontChoice.Keep, FontChoice.SameAsCode, FontChoice.SameAsUi };

        public static bool IsFontKey(string key) => key != null && key.StartsWith("fonts.", StringComparison.Ordinal);

        public static void AddDescriptors(List<SettingDescriptor> list, string category)
        {
            // 三行的可选字体、文案和说明照 Agent 面板的设置 → 外观 → 字体（web/src/components/settings/FontSection.tsx）。
            // 下拉框仍可以直接输入本机装了的字体名，代替面板单独的「自定义字体」一行。
            list.Add(UiRow(category, UiLatinKey, "英文字体", "Latin font", "字母、数字和符号用的字体", "Font for letters, digits and symbols",
                "默认是内置的 IBM Plex Sans；也可以换内置的等宽 Maple Mono NL（不带符号连字）、Inter，或系统界面字体 Segoe UI。也可以直接输入本机装了的字体名。",
                "IBM Plex Sans (bundled) by default; the bundled monospace Maple Mono NL (no symbol ligatures), Inter, or the system UI font Segoe UI are the alternatives. You can also type the name of an installed font.",
                FontSettings.DefaultUiLatin, LatinOptions));
            list.Add(UiRow(category, UiCjkKey, "中文字体", "Chinese font", "汉字用的字体", "Font for Chinese characters",
                "英文字体里没有汉字，汉字用这里选的字体。Maple Mono CN 是 Maple Mono 的中文版：一个汉字正好占两个英文字母宽，和 Maple Mono 英文上下对齐，但汉字之间比思源黑体疏；它只有常规和中粗两档，标题由系统加粗。小字号觉得发虚可以换系统字体（微软雅黑）。",
                "The Latin font has no Chinese characters; they use the font chosen here. Maple Mono CN is Maple Mono's Chinese build: one character is exactly two Latin letters wide and lines up with Maple Mono, but is looser than Noto Sans SC; it has regular and medium only, titles are emboldened. If small text looks blurry, pick the system font (Microsoft YaHei UI).",
                FontSettings.DefaultUiCjk, CjkOptions));
            list.Add(UiRow(category, CodeKey, "代码字体", "Code font", "代码、命令和路径用的等宽字体", "Monospace font for code, commands and paths",
                null, null, FontSettings.DefaultCode, CodeOptions));

            list.Add(new SettingDescriptor
            {
                Category = category, Key = ManageVsKey, Kind = SettingKind.Bool, GroupZh = VsGroup, GroupEn = VsGroupEn,
                TitleZh = "由 UEToVS 管理 VS 字体", TitleEn = "Let UEToVS manage VS fonts",
                DescriptionZh = "代替 工具 > 选项 > 环境 > 字体和颜色：按下面的设置写入 VS 的字体（内置字体会先为当前用户安装，无需管理员）。首次修改前备份原字体，关闭后恢复。新装的字体可能要重启 VS 才完全生效。",
                DescriptionEn = "Replaces Tools > Options > Environment > Fonts and Colors: writes VS's fonts from the rows below (bundled fonts are installed for the current user first, no admin needed). The original fonts are backed up before the first change and restored when turned off. Newly installed fonts may need a VS restart to take full effect.",
                Default = "true", Keywords = Keywords,
            });
            list.Add(new SettingDescriptor
            {
                Category = category, Key = VsStatusKey, Kind = SettingKind.Info, ActionId = VsStatusInfoId, GroupZh = VsGroup, GroupEn = VsGroupEn,
                TitleZh = "当前状态", TitleEn = "Current state", Keywords = Keywords,
            });
            // Code-role categories first, then UI ones, so each group is one card; the size sits on the family's row.
            foreach (var c in VsFontCategories.All.OrderBy(c => c.Role == VsFontRole.Code ? 0 : 1))
            {
                bool code = c.Role == VsFontRole.Code;
                string g = code ? VsCodeGroup : VsUiGroup, gEn = code ? VsCodeGroupEn : VsUiGroupEn;
                var family = FontRow(category, FamilyKey(c), c.TitleZh, c.TitleEn, null, null, c.DefaultFamily, VsKeywords, code, g, gEn);
                family.EnabledWhenKey = ManageVsKey; // 关掉“由 UEToVS 管理”后改这些行不会写进 VS，置灰免得误以为没生效
                list.Add(family);
                list.Add(new SettingDescriptor
                {
                    Category = category, Key = SizeKey(c), Kind = SettingKind.Int, GroupZh = g, GroupEn = gEn, InlineWithKey = FamilyKey(c), EnabledWhenKey = ManageVsKey,
                    InlineLabelZh = "字号", InlineLabelEn = "Size",
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

        private static SettingDescriptor UiRow(string category, string key, string zh, string en, string descZh, string descEn, string hintZh, string hintEn,
                                               FontChoice def, IReadOnlyList<SettingOption> options) =>
            new SettingDescriptor
            {
                Category = category, Key = key, Kind = SettingKind.Choice, TitleZh = zh, TitleEn = en, DescriptionZh = descZh, DescriptionEn = descEn,
                HintZh = hintZh, HintEn = hintEn, GroupZh = Group, GroupEn = GroupEn, Default = def.ToString(), AllowCustomValue = true, Keywords = Keywords,
                Options = options,
            };

        private static SettingOption Bundled(BundledFont f) => Option(FontChoice.OfBundled(f));
        private static SettingOption Option(FontChoice c) => new SettingOption(c.ToString(), c.Display(false), c.Display(true));

        /// <summary>英文字体: Agent Panel's list and order (Maple Mono NL, 系统字体, Inter, IBM Plex Sans).</summary>
        public static IReadOnlyList<SettingOption> LatinOptions { get; } = new[]
        {
            Bundled(BundledFontCatalog.MapleMonoNl),
            new SettingOption(FontSettings.SystemLatin.ToString(), "系统字体", "System font"),
            Bundled(BundledFontCatalog.Inter),
            Bundled(BundledFontCatalog.IbmPlexSans),
        };

        /// <summary>中文字体: 思源黑体, Maple Mono CN, 系统字体（微软雅黑）.</summary>
        public static IReadOnlyList<SettingOption> CjkOptions { get; } = new[]
        {
            Bundled(BundledFontCatalog.NotoSansSc),
            Bundled(BundledFontCatalog.MapleMonoCn),
            new SettingOption(FontSettings.SystemCjk.ToString(), "系统字体（微软雅黑）", "System font (Microsoft YaHei UI)"),
        };

        /// <summary>代码字体: Maple Mono NL, JetBrains Mono.</summary>
        public static IReadOnlyList<SettingOption> CodeOptions { get; } = new[]
        {
            Bundled(BundledFontCatalog.MapleMonoNl),
            Bundled(BundledFontCatalog.JetBrainsMono),
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
                // 英文 / 中文 / 代码三行是照 Agent 面板挑好的固定列表（可以手输字体名），只有 VS 类别列出本机所有字体
                if (d.Kind != SettingKind.Choice || !d.Key.StartsWith(VsPrefix, StringComparison.Ordinal) || !d.Key.EndsWith(FamilySuffix, StringComparison.Ordinal)) continue;
                var cat = VsFontCategories.Find(d.Key.Substring(VsPrefix.Length, d.Key.Length - VsPrefix.Length - FamilySuffix.Length));
                bool mono = cat?.Role == VsFontRole.Code;
                d.OptionsSource = () => ToOptions(FontSettings.Options(VsKeywords, installed?.Invoke(), null, mono));
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
