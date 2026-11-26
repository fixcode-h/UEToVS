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
        public const string IncludesKey = "diagnostics.includes";
        public const string SyntaxKey = "diagnostics.syntax";
        public const string MissingIncludeKey = "diagnostics.missingInclude";
        public const string UnknownIdentifierKey = "diagnostics.unknownIdentifiers";
        public const string AutoIncludeKey = "completion.autoInclude";
        public const string UbtExportKey = "build.ubtExport";
        public const string SaveCheckKey = "build.saveCheck";
        public const string HashtagsKey = "editor.hashtags";
        public const string StepFilterKey = "debug.stepFilter";
        public const string AutoSaveKey = "editor.autoSave";
        public const string AutoSaveIdleKey = "editor.autoSaveIdleSeconds";
        public const string EngineReadOnlyKey = "editor.engineReadOnly";

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

            const string gEngine = "引擎代码", gEngineEn = "Engine code";
            list.Add(D(category, EngineReadOnlyKey, SettingKind.Bool, "引擎代码默认只读", "Engine code is read-only by default",
                "和 IDEA 打开库源码一样：在 UE 项目里打开引擎目录（Engine\\）下的文件时不能直接改，编辑器顶部显示提示条，点“启用编辑”（Ctrl+Shift+Alt+U, E）后这个文件才能改，本次 VS 会话内一直有效。只在编辑器里锁，不改磁盘文件的只读属性；打开引擎自己的解决方案（没有游戏项目）时不锁。",
                "Like IDEA's library sources: in a UE project, files under the engine's Engine\\ folder open locked. A bar at the top of the editor offers \"Enable Editing\" (Ctrl+Shift+Alt+U, E), which unlocks that file for the rest of the VS session. Only the editor is locked; the file's read-only attribute on disk is never changed. Nothing is locked when the engine's own solution (without a game project) is open.",
                "true", gEngine, gEngineEn, "engine read-only readonly lock 引擎 只读 锁定 修改 编辑 idea"));

            const string gCheck = "代码检查", gCheckEn = "Code checks";
            list.Add(D(category, DeprecationKey, SettingKind.Bool, "弃用 API 提示", "Deprecated API hints",
                "当前文件里用到 UE_DEPRECATED / [[deprecated]] 的符号时画删除线和警告波浪线（带弃用版本和说明），Quick Info 显示说明；说明里写了“Use X instead”时 Shift+Alt+Q 可一键替换。只检查可见区域，不扫描全工程。",
                "Uses of UE_DEPRECATED / [[deprecated]] symbols in the current file get a strikethrough and a warning squiggle (version and message), Quick Info shows the message; Shift+Alt+Q replaces it when the message says \"Use X instead\". Visible lines only, no project-wide scan.",
                "true", gCheck, gCheckEn, "deprecated deprecation 弃用 UE_DEPRECATED"));
            list.Add(D(category, NamingKey, SettingKind.Bool, "UE 命名规范检查", "UE naming conventions",
                "检查当前文件声明的类型和 bool 成员的前缀（A / U / F / E / T / I / b），以“建议”提示，Shift+Alt+Q 一键改名。只检查项目代码。",
                "Checks the prefixes (A / U / F / E / T / I / b) of types and bool members declared in the current file and suggests a fix; Shift+Alt+Q renames. Project code only.",
                "false", gCheck, gCheckEn, "naming convention prefix 命名 规范 前缀"));

            // v0.11.0
            list.Add(D(category, IncludesKey, SettingKind.Bool, "头文件检查", "Include checks",
                "#include 找不到（按 Build.cs 和 UBT 的 include 路径算）、include 了别的模块的 Private 头文件、.generated.h 缺失 / 名字不对 / 不是最后一个 include 时报错；只靠旧版 include 路径才找得到的写法给出更短的写法。灯泡里一键改。",
                "Errors for an #include that resolves nowhere (using the include paths from Build.cs and UBT), a private header of another module, and a missing / misnamed / not-last .generated.h; suggests a shorter spelling for includes found only through legacy include paths. One-click fixes in the light bulb.",
                "true", gCheck, gCheckEn, "include header 头文件 generated.h 路径 path private"));
            list.Add(D(category, SyntaxKey, SettingKind.Bool, "语法错误", "Syntax errors",
                "大括号 / 小括号不配对、类型定义后面或 “}” 前面缺分号、#endif 没有 #if。#if 两个分支都被解析的文件不报大括号问题。",
                "Unbalanced braces / parentheses, a missing ';' after a type definition or before '}', #endif without #if. Brace problems are not reported in files where both branches of an #if are parsed.",
                "true", gCheck, gCheckEn, "syntax semicolon brace parenthesis 语法 分号 括号"));
            list.Add(D(category, MissingIncludeKey, SettingKind.Bool, "用了但没 include 头文件", "Used without its #include",
                "用到类型的定义（X::、Cast<X>、按值声明、基类等）但它的头文件不在当前文件的 include 链里时给出警告，灯泡里一键添加 include（需要时一并提示加 Build.cs 依赖）。只用指针 / 引用的不报。",
                "Warns when a type's definition is needed (X::, Cast<X>, a by-value declaration, a base class…) but its header is not reachable from the file's includes; the light bulb adds the #include (and offers the Build.cs dependency when needed). Pointer / reference uses are not reported.",
                "true", gCheck, gCheckEn, "missing include iwyu 头文件 没有 include 警告"));
            list.Add(D(category, UnknownIdentifierKey, SettingKind.Bool, "未定义标识符", "Undefined identifiers",
                "函数体里的名字在局部变量、参数、类成员、当前文件和整个索引里都找不到时报错，并给出拼写相近的建议。只在索引完成、没有其他未保存文件时检查；模板、宏参数、全小写的名字（C 运行库 / std）不检查。",
                "Errors for a name in a function body that is not a local, parameter, member, declaration of the file or anything in the index, with close spellings as fixes. Checked only once the index is ready and no other file has unsaved changes; templates, macro arguments and all-lower-case names (C runtime / std) are skipped.",
                "true", gCheck, gCheckEn, "unknown undefined identifier typo 未定义 标识符 拼写"));
            list.Add(D(category, AutoIncludeKey, SettingKind.Bool, "补全时自动添加 include", "Add the #include on completion",
                "从补全列表选中一个头文件还没 include 的类型或函数时，自动在合适的位置加上 #include（需要时提示加 Build.cs 依赖）。",
                "Committing a type or function whose header is not included yet adds the #include in the right place (and offers the Build.cs dependency when needed).",
                "true", gCheck, gCheckEn, "completion auto include 补全 自动 头文件"));

            const string gBuild = "编译", gBuildEn = "Build";
            list.Add(D(category, UbtExportKey, SettingKind.Bool, "Build.cs 变化时后台导出 include 路径", "Export include paths when a Build.cs changes",
                "Build.cs / Target.cs 改动并保存后，在后台低优先级跑一次 UnrealBuildTool -Mode=JsonExport（不编译，通常十几秒到一分钟），得到 UBT 实际使用的 include 路径，头文件检查和 include 补全据此判断；没有导出时用编译留下的响应文件和 Build.cs 推导。VS 正在生成时不跑。结果在 .vs\\UEToVS\\ubt\\export.json。",
                "After a Build.cs / Target.cs is saved, runs UnrealBuildTool -Mode=JsonExport once in the background at low priority (no compile, usually tens of seconds) to get the include paths UBT really uses for the include checks and #include completion; without it the response files of the last compile and the Build.cs are used. Never while VS is building. Written to .vs\\UEToVS\\ubt\\export.json.",
                "true", gBuild, gBuildEn, "ubt export include path build.cs 导出 编译参数"));
            list.Add(D(category, SaveCheckKey, SettingKind.Bool, "保存后后台编译检查", "Compile check after saving",
                "保存 .cpp 后用 UnrealBuildTool -SingleFile 在后台单独编译这个文件（和正式编译完全一致，没有误报），错误显示为编译错误波浪线并进入错误列表。每次保存占一个核 5–30 秒、峰值 1–2 GB 内存，跑完就退出；VS 正在生成或编辑器的 Live Coding 正在运行时跳过。",
                "After a .cpp is saved, compiles just that file in the background with UnrealBuildTool -SingleFile (exactly like the real build, no false positives); errors show as compiler squiggles and in the Error List. Each save uses one core for 5–30 s and 1–2 GB at peak, then exits; skipped while VS builds or the editor's Live Coding is active.",
                "false", gBuild, gBuildEn, "compile check save single file 保存 编译 检查 单文件 ubt"));

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
            d[IncludesKey] = B(s.IncludeChecks);
            d[SyntaxKey] = B(s.SyntaxChecks);
            d[MissingIncludeKey] = B(s.MissingIncludeCheck);
            d[UnknownIdentifierKey] = B(s.UnknownIdentifierCheck);
            d[AutoIncludeKey] = B(s.CompletionAutoInclude);
            d[UbtExportKey] = B(s.UbtIncludeExport);
            d[SaveCheckKey] = B(s.SaveCompileCheck);
            d[StepFilterKey] = B(s.DebugStepFilter);
            d[AutoSaveKey] = B(s.AutoSaveEnabled);
            d[AutoSaveIdleKey] = s.AutoSaveIdleSeconds.ToString(CultureInfo.InvariantCulture);
            d[EngineReadOnlyKey] = B(s.EngineReadOnly);
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
                case IncludesKey: s.IncludeChecks = PB(v); return true;
                case SyntaxKey: s.SyntaxChecks = PB(v); return true;
                case MissingIncludeKey: s.MissingIncludeCheck = PB(v); return true;
                case UnknownIdentifierKey: s.UnknownIdentifierCheck = PB(v); return true;
                case AutoIncludeKey: s.CompletionAutoInclude = PB(v); return true;
                case UbtExportKey: s.UbtIncludeExport = PB(v); return true;
                case SaveCheckKey: s.SaveCompileCheck = PB(v); return true;
                case StepFilterKey: s.DebugStepFilter = PB(v); return true;
                case AutoSaveKey: s.AutoSaveEnabled = PB(v); return true;
                case EngineReadOnlyKey: s.EngineReadOnly = PB(v); return true;
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
            d["includeChecks"] = s.IncludeChecks;
            d["syntaxChecks"] = s.SyntaxChecks;
            d["missingIncludeCheck"] = s.MissingIncludeCheck;
            d["unknownIdentifierCheck"] = s.UnknownIdentifierCheck;
            d["completionAutoInclude"] = s.CompletionAutoInclude;
            d["ubtIncludeExport"] = s.UbtIncludeExport;
            d["saveCompileCheck"] = s.SaveCompileCheck;
            d["debugStepFilter"] = s.DebugStepFilter;
            d["inlayParameterNames"] = s.InlayParameterNames;
            d["inlayAutoTypes"] = s.InlayAutoTypes;
            d["inlayCodeVision"] = s.InlayCodeVision;
            d["autoSave"] = s.AutoSaveEnabled;
            d["autoSaveIdleSeconds"] = s.AutoSaveIdleSeconds;
            d["engineReadOnly"] = s.EngineReadOnly;
        }

        private static string B(bool v) => v ? "true" : "false";
        private static bool PB(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
