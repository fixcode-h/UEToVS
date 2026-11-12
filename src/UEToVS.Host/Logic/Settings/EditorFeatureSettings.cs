using System;
using System.Collections.Generic;
using System.Globalization;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// v0.4.5 编辑体验 settings rows (代码感知 page) and their HostSettings codec, kept out of HostSettingsSchema so the feature
    /// stays in one place: the schema calls <see cref="AddDescriptors"/>, <see cref="ToValues"/> and <see cref="TrySet"/>.
    /// The inlay hint rows (inlay.*) existed before and stay in HostSettingsSchema.
    /// </summary>
    public static class EditorFeatureSettings
    {
        public const string SurroundKey = "editor.surroundWith";
        public const string RenameModeKey = "editor.renameMode";
        public const string ClipboardKey = "editor.clipboardHistory";
        public const string ClipboardSizeKey = "editor.clipboardHistorySize";
        public const string DeprecationKey = "diagnostics.deprecated";
        public const string NamingKey = "diagnostics.naming";
        public const string SpellingKey = "spelling.enabled";
        public const string SpellingWordsKey = "spelling.userWords";
        public const string HashtagsKey = "editor.hashtags";
        public const string StepFilterKey = "debug.stepFilter";

        public const int MinClipboard = 1, MaxClipboard = 100;

        private static SettingDescriptor D(string category, string key, SettingKind kind, string zh, string en, string descZh, string descEn, string def,
                                           string groupZh, string groupEn, string keywords) =>
            new SettingDescriptor
            {
                Category = category, Key = key, Kind = kind, TitleZh = zh, TitleEn = en, DescriptionZh = descZh, DescriptionEn = descEn,
                Default = def, GroupZh = groupZh, GroupEn = groupEn, Keywords = keywords,
            };

        public static void AddDescriptors(List<SettingDescriptor> list, string category)
        {
            const string gEdit = "编辑", gEditEn = "Editing";
            list.Add(D(category, SurroundKey, SettingKind.Bool, "Surround With", "Surround With",
                "选中代码后按 Ctrl+Shift+Alt+U, 1（Rider 方案 Ctrl+Alt+J），选择 if (IsValid(X))、#if WITH_EDITOR、SCOPE_CYCLE_COUNTER 等模板把整行包起来。",
                "Select code and press Ctrl+Shift+Alt+U, 1 (Rider keymap Ctrl+Alt+J) to wrap the lines in if (IsValid(X)), #if WITH_EDITOR, SCOPE_CYCLE_COUNTER and more.",
                "true", gEdit, gEditEn, "surround with 包裹 模板 template if #if region"));
            var mode = D(category, RenameModeKey, SettingKind.Choice, "重命名方式", "Rename mode",
                "就地：Ctrl+R, Ctrl+R 后直接在编辑器里改名，当前文件的所有出现处同步修改，Enter 打开全工程预览，Esc 取消；弹窗：直接打开预览窗口。",
                "Inline: after Ctrl+R, Ctrl+R type the new name in the editor (every occurrence in the file follows), Enter opens the project-wide preview, Esc cancels; Dialog: open the preview right away.",
                nameof(RenameMode.Inline), gEdit, gEditEn, "rename inline 重命名 就地 弹窗");
            mode.Options = new[]
            {
                new SettingOption(nameof(RenameMode.Inline), "就地", "Inline"),
                new SettingOption(nameof(RenameMode.Dialog), "弹窗", "Dialog"),
            };
            list.Add(mode);
            list.Add(D(category, ClipboardKey, SettingKind.Bool, "多重剪贴板", "Clipboard history",
                "记住在编辑器里复制 / 剪切的文本，Ctrl+Shift+V 弹出列表选择粘贴。只存文本，单条最多 1 MB，只保存在内存中。",
                "Remembers text copied / cut in editors; Ctrl+Shift+V lists it to paste. Text only, at most 1 MB per entry, kept in memory only.",
                "true", gEdit, gEditEn, "clipboard paste history 剪贴板 粘贴 历史"));
            var size = D(category, ClipboardSizeKey, SettingKind.Int, "剪贴板历史条数", "Clipboard history entries", null, null, "16", gEdit, gEditEn,
                "clipboard 剪贴板");
            size.Min = MinClipboard;
            size.Max = MaxClipboard;
            list.Add(size);
            list.Add(D(category, HashtagsKey, SettingKind.Bool, "注释 #tag 书签", "Comment #tag bookmarks",
                "注释里写的 #tag（例如 // #todo 修复）收进“#tag 书签”列表（Ctrl+Shift+Alt+U, 3），按 tag 分组。只扫描已打开的文件。",
                "#tags written in comments (e.g. // #todo fix) are listed in \"#tag bookmarks\" (Ctrl+Shift+Alt+U, 3), grouped by tag. Only open files are scanned.",
                "true", gEdit, gEditEn, "hashtag tag bookmark 书签 todo"));

            const string gCheck = "代码检查", gCheckEn = "Code checks";
            list.Add(D(category, DeprecationKey, SettingKind.Bool, "弃用 API 提示", "Deprecated API hints",
                "当前文件里用到 UE_DEPRECATED / [[deprecated]] 的符号时画删除线和警告波浪线（带弃用版本和说明），Quick Info 显示说明；说明里写了“Use X instead”时 Alt+Enter 可一键替换。只检查可见区域，不扫描全工程。",
                "Uses of UE_DEPRECATED / [[deprecated]] symbols in the current file get a strikethrough and a warning squiggle (version and message), Quick Info shows the message; Alt+Enter replaces it when the message says \"Use X instead\". Visible lines only, no project-wide scan.",
                "true", gCheck, gCheckEn, "deprecated deprecation 弃用 UE_DEPRECATED"));
            list.Add(D(category, NamingKey, SettingKind.Bool, "UE 命名规范检查", "UE naming conventions",
                "检查当前文件声明的类型和 bool 成员的前缀（A / U / F / E / T / I / b），以“建议”提示，Alt+Enter 一键改名。只检查项目代码。",
                "Checks the prefixes (A / U / F / E / T / I / b) of types and bool members declared in the current file and suggests a fix; Alt+Enter renames. Project code only.",
                "false", gCheck, gCheckEn, "naming convention prefix 命名 规范 前缀"));
            list.Add(D(category, SpellingKey, SettingKind.Bool, "注释拼写检查", "Comment spell check",
                "只检查注释和字符串里的英文单词；当前文件里出现的标识符自动忽略。",
                "Checks English words in comments and strings only; identifiers used in the file are ignored.",
                "false", gCheck, gCheckEn, "spell spelling typo 拼写"));
            list.Add(D(category, SpellingWordsKey, SettingKind.Text, "用户词典", "User dictionary",
                "不当作拼写错误的单词，用空格或逗号分隔（Alt+Enter“加入词典”也会写到这里）。",
                "Words never reported, separated by spaces or commas (Alt+Enter \"Add to dictionary\" writes here too).",
                "", gCheck, gCheckEn, "spell dictionary 词典"));

            const string gDebug = "调试", gDebugEn = "Debugging";
            list.Add(D(category, StepFilterKey, SettingKind.Bool, "调试时跳过引擎小函数（Step Filter）", "Skip small engine functions when stepping",
                "按 F11 单步进入时跳过 TArray / TMap operator[]、FString / FName 构造、TObjectPtr 转换、Cast<>、IsValid、check / ensure 内部等。写入 文档\\Visual Studio 版本\\Visualizers\\UEToVS.natstepfilter，关闭后删除；重启调试会话后生效。",
                "F11 steps over TArray / TMap operator[], FString / FName constructors, TObjectPtr conversions, Cast<>, IsValid, check / ensure internals and more. Written to Documents\\Visual Studio <version>\\Visualizers\\UEToVS.natstepfilter and removed when off; takes effect in the next debug session.",
                "true", gDebug, gDebugEn, "debug step filter natstepfilter 调试 单步"));
        }

        public static void ToValues(HostSettings s, IDictionary<string, string> d)
        {
            d[SurroundKey] = B(s.SurroundWithEnabled);
            d[RenameModeKey] = s.RenameMode.ToString();
            d[ClipboardKey] = B(s.ClipboardHistoryEnabled);
            d[ClipboardSizeKey] = s.ClipboardHistorySize.ToString(CultureInfo.InvariantCulture);
            d[HashtagsKey] = B(s.HashtagsEnabled);
            d[DeprecationKey] = B(s.DeprecationHints);
            d[NamingKey] = B(s.NamingConventionCheck);
            d[SpellingKey] = B(s.SpellCheckEnabled);
            d[SpellingWordsKey] = s.SpellCheckUserWords ?? string.Empty;
            d[StepFilterKey] = B(s.DebugStepFilter);
        }

        /// <summary>Sets one value; false when <paramref name="key"/> is not one of these rows.</summary>
        public static bool TrySet(HostSettings s, string key, string v)
        {
            switch (key)
            {
                case SurroundKey: s.SurroundWithEnabled = PB(v); return true;
                case RenameModeKey: s.RenameMode = Enum.TryParse(v, true, out RenameMode m) && Enum.IsDefined(typeof(RenameMode), m) ? m : RenameMode.Inline; return true;
                case ClipboardKey: s.ClipboardHistoryEnabled = PB(v); return true;
                case ClipboardSizeKey:
                    s.ClipboardHistorySize = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Max(MinClipboard, Math.Min(MaxClipboard, n)) : 16;
                    return true;
                case HashtagsKey: s.HashtagsEnabled = PB(v); return true;
                case DeprecationKey: s.DeprecationHints = PB(v); return true;
                case NamingKey: s.NamingConventionCheck = PB(v); return true;
                case SpellingKey: s.SpellCheckEnabled = PB(v); return true;
                case SpellingWordsKey: s.SpellCheckUserWords = v ?? string.Empty; return true;
                case StepFilterKey: s.DebugStepFilter = PB(v); return true;
                default: return false;
            }
        }

        /// <summary>Flat values for the session header / diagnostics package.</summary>
        public static void Describe(HostSettings s, IDictionary<string, object> d)
        {
            d["surroundWith"] = s.SurroundWithEnabled;
            d["renameMode"] = s.RenameMode.ToString();
            d["clipboardHistory"] = s.ClipboardHistoryEnabled;
            d["clipboardHistorySize"] = s.ClipboardHistorySize;
            d["hashtags"] = s.HashtagsEnabled;
            d["deprecationHints"] = s.DeprecationHints;
            d["namingConventionCheck"] = s.NamingConventionCheck;
            d["spellCheck"] = s.SpellCheckEnabled;
            d["spellCheckUserWords"] = (s.SpellCheckUserWords ?? string.Empty).Length;
            d["debugStepFilter"] = s.DebugStepFilter;
            d["inlayParameterNames"] = s.InlayParameterNames;
            d["inlayAutoTypes"] = s.InlayAutoTypes;
        }

        private static string B(bool v) => v ? "true" : "false";
        private static bool PB(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
