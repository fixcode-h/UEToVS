using System;
using System.Collections.Generic;
using System.Globalization;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// v0.4.5 编辑体验 settings rows (编辑 page, HostSettingsPages) and their HostSettings codec, kept out of HostSettingsSchema so the feature
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
        public const string HashtagsKey = "editor.hashtags";
        public const string StepFilterKey = "debug.stepFilter";
        public const string AutoSaveKey = "editor.autoSave";
        public const string AutoSaveIdleKey = "editor.autoSaveIdleSeconds";
        public const string GitTabMarkerKey = "editor.gitTabMarker";

        public const int MinClipboard = 1, MaxClipboard = 100;
        public const int MinAutoSaveIdle = 0, MaxAutoSaveIdle = 600;

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
            list.Add(D(category, SurroundKey, SettingKind.Bool, "用代码块包围（Surround With）", "Surround With",
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
            list.Add(D(category, ClipboardKey, SettingKind.Bool, "剪贴板历史", "Clipboard history",
                "记住在编辑器里复制 / 剪切的文本，Ctrl+Shift+V 弹出列表选择粘贴。只存文本，单条最多 1 MB，只保存在内存中。",
                "Remembers text copied / cut in editors; Ctrl+Shift+V lists it to paste. Text only, at most 1 MB per entry, kept in memory only.",
                "true", gEdit, gEditEn, "clipboard paste history 剪贴板 粘贴 历史"));
            var size = D(category, ClipboardSizeKey, SettingKind.Int, "剪贴板历史条数", "Clipboard history entries", "最多记住多少条，超出时丢掉最早的。", "How many entries to keep; the oldest go first.", "16", gEdit, gEditEn,
                "clipboard 剪贴板");
            size.Min = MinClipboard;
            size.Max = MaxClipboard;
            list.Add(size);
            list.Add(D(category, HashtagsKey, SettingKind.Bool, "#tag 注释列表", "#tag comment list",
                "注释里写的 #tag（例如 // #todo 修复）收进“#tag 注释列表”（Ctrl+Shift+Alt+U, 3），按 tag 分组。只扫描已打开的文件。",
                "#tags written in comments (e.g. // #todo fix) are listed in \"#tag Comments\" (Ctrl+Shift+Alt+U, 3), grouped by tag. Only open files are scanned.",
                "true", gEdit, gEditEn, "hashtag tag bookmark 书签 todo"));

            const string gSave = "自动保存", gSaveEn = "Auto-save";
            list.Add(D(category, AutoSaveKey, SettingKind.Bool, "自动保存", "Auto-save",
                "和 IDEA 一样自动保存修改过的文件：切换到其他程序（VS 失去焦点）时，以及停止编辑一段时间后。只保存磁盘上已有、不是只读的文件，新建未命名的文件不会弹出另存为。只在 UE 解决方案里生效。",
                "Saves modified files automatically, like IDEA: when you switch to another application (VS loses focus) and after you stop editing for a while. Only files that exist on disk and are not read-only; new untitled files never open Save As. UE solutions only.",
                "true", gSave, gSaveEn, "auto save autosave 自动保存 保存 idea"));
            var idle = D(category, AutoSaveIdleKey, SettingKind.Int, "停止编辑多少秒后保存", "Save after this many idle seconds",
                "最后一次编辑后经过这么多秒就保存所有修改过的文件；0 表示只在 VS 失去焦点时保存。",
                "Modified files are saved this many seconds after the last edit; 0 saves only when VS loses focus.", "15", gSave, gSaveEn, "auto save idle 自动保存 空闲 秒");
            idle.Min = MinAutoSaveIdle;
            idle.Max = MaxAutoSaveIdle;
            list.Add(idle);
            list.Add(D(category, GitTabMarkerKey, SettingKind.Bool, "标签页标出 Git 有改动的文件", "Mark files changed in Git on tabs",
                "自动保存后 VS 的 * 很快消失，这里改用 Git 状态区分：和 Git 版本相比有改动（修改、新增、未跟踪）的文件，标签页名字后面显示 ●。保存、切回 VS 时刷新；不是 Git 仓库或找不到 git 时不显示。",
                "With auto-save VS's * disappears quickly, so files that differ from Git (modified, added, untracked) get a ● after the name on their tab. Refreshed on save and when VS is activated; nothing is shown outside a Git repository or without git.",
                "true", gSave, gSaveEn, "git modified tab 标签页 修改 改动 vcs"));

            const string gCheck = "代码检查", gCheckEn = "Code checks";
            list.Add(D(category, DeprecationKey, SettingKind.Bool, "弃用 API 提示", "Deprecated API hints",
                "当前文件里用到 UE_DEPRECATED / [[deprecated]] 的符号时画删除线和警告波浪线（带弃用版本和说明），Quick Info 显示说明；说明里写了“Use X instead”时 Alt+Enter 可一键替换。只检查可见区域，不扫描全工程。",
                "Uses of UE_DEPRECATED / [[deprecated]] symbols in the current file get a strikethrough and a warning squiggle (version and message), Quick Info shows the message; Alt+Enter replaces it when the message says \"Use X instead\". Visible lines only, no project-wide scan.",
                "true", gCheck, gCheckEn, "deprecated deprecation 弃用 UE_DEPRECATED"));
            list.Add(D(category, NamingKey, SettingKind.Bool, "UE 命名规范检查", "UE naming conventions",
                "检查当前文件声明的类型和 bool 成员的前缀（A / U / F / E / T / I / b），以“建议”提示，Alt+Enter 一键改名。只检查项目代码。",
                "Checks the prefixes (A / U / F / E / T / I / b) of types and bool members declared in the current file and suggests a fix; Alt+Enter renames. Project code only.",
                "false", gCheck, gCheckEn, "naming convention prefix 命名 规范 前缀"));

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
            d[StepFilterKey] = B(s.DebugStepFilter);
            d[AutoSaveKey] = B(s.AutoSaveEnabled);
            d[AutoSaveIdleKey] = s.AutoSaveIdleSeconds.ToString(CultureInfo.InvariantCulture);
            d[GitTabMarkerKey] = B(s.GitTabMarker);
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
                case StepFilterKey: s.DebugStepFilter = PB(v); return true;
                case AutoSaveKey: s.AutoSaveEnabled = PB(v); return true;
                case GitTabMarkerKey: s.GitTabMarker = PB(v); return true;
                case AutoSaveIdleKey:
                    s.AutoSaveIdleSeconds = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idle)
                        ? Math.Max(MinAutoSaveIdle, Math.Min(MaxAutoSaveIdle, idle)) : 15;
                    return true;
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
            d["debugStepFilter"] = s.DebugStepFilter;
            d["inlayParameterNames"] = s.InlayParameterNames;
            d["inlayAutoTypes"] = s.InlayAutoTypes;
            d["inlayCodeVision"] = s.InlayCodeVision;
            d["autoSave"] = s.AutoSaveEnabled;
            d["autoSaveIdleSeconds"] = s.AutoSaveIdleSeconds;
            d["gitTabMarker"] = s.GitTabMarker;
        }

        private static string B(bool v) => v ? "true" : "false";
        private static bool PB(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
