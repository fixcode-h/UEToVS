using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Commands;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// Declarative description of every host setting shown in the UEToVS settings window (设置窗口). To add a setting:
    /// add a property to <see cref="HostSettings"/>, a descriptor below, and its get/set line in <see cref="Codec"/> —
    /// the window, search, validation, "restore defaults", persistence and the diagnostics dump pick it up from here.
    /// Pages with custom content (外观, 快捷键) are categories without (or with only a few) descriptors.
    /// </summary>
    public static class HostSettingsSchema
    {
        public const string General = "general";

        /// <summary>Never a real setting (always "off"): rows gated on it stay greyed out, e.g. VS 18-only switches on VS 2022.</summary>
        public const string Vs18OnlyGateKey = "vs.version18Only";
        public const string Appearance = "appearance";
        public const string Indexing = "indexing";
        public const string Keymap = "keymap";
        public const string Navigation = "navigation";
        public const string IntelliSense = "intellisense";
        /// <summary>Custom page: APIs + models for VS chat (Settings/AgentApiPage).</summary>
        public const string Agent = "agent";
        /// <summary>诊断: the UEToVS engine process, logs, the performance panel (v0.7.1: the old 性能 page is merged in).</summary>
        public const string Logging = "logging";
        // ---- v0.7.1 pages (HostSettingsPages decides which rows they show)
        /// <summary>接管 VS: C++ IntelliSense takeover and every VS feature UEToVS turns off.</summary>
        public const string Takeover = "takeover";
        /// <summary>着色与检查: semantic coloring, reference highlighting, squiggles, build errors.</summary>
        public const string Highlighting = "highlighting";
        public const string Editing = "editing";
        public const string Translation = "translation";
        /// <summary>Unreal Engine: engine root, module dependencies, Live Coding, debugging, whole-project inspection / upgrade assistant.</summary>
        public const string Unreal = "unreal";
        /// <summary>v0.5.1: "检查更新" has a page of its own, the last one.</summary>
        public const string Updates = "updates";

        /// <summary>Action ids only the settings window resolves (not registered in the ActionRegistry).</summary>
        public static class WindowActions
        {
            public const string RebuildProjectIndex = "settings.indexing.rebuildProject";
            public const string RebuildEngineIndex = "settings.indexing.rebuildEngine";
            public const string RebuildBlueprintIndex = "settings.indexing.rebuildBlueprint";
            /// <summary>Index status card "重建" after a failure: C++ (project + engine) and Blueprint indexes.</summary>
            public const string RebuildAllIndexes = "settings.indexing.rebuildAll";
            public const string ToggleIntelliSense = "settings.intelliSense.toggle";
            /// <summary>"恢复 VS 原样": restores every VS setting UEToVS changed and turns the takeover / font management off.</summary>
            public const string RestoreVsDefaults = "settings.general.restoreVs";
            public const string IndexStatus = "settings.indexing.status";
            public const string IntelliSenseStatus = "settings.intelliSense.status";
            public const string LogDirectory = "settings.logging.directory";
            public const string VsUnrealIntegration = "settings.general.vsUnrealIntegration";
            /// <summary>"检查更新": latest GitHub release → download VSIX → start VSIXInstaller.</summary>
            public const string CheckForUpdates = "settings.general.checkForUpdates";
        }

        public static IReadOnlyList<SettingCategory> Categories { get; } = new[]
        {
            // v0.7.1: ordered by what the user is doing — UEToVS itself, taking over VS, writing / reading code, the UE
            // project, the machine. Which rows each page shows: HostSettingsPages.
            new SettingCategory(General, "常规", "General"),
            new SettingCategory(Appearance, "外观", "Appearance"),
            new SettingCategory(Keymap, "快捷键", "Keyboard Shortcuts"),
            new SettingCategory(Takeover, "接管 VS", "VS Takeover"),
            new SettingCategory(IntelliSense, "补全与提示", "Completion & Hints"),
            new SettingCategory(Highlighting, "着色与检查", "Coloring & Checks"),
            new SettingCategory(Editing, "编辑", "Editing"),
            new SettingCategory(Navigation, "导航", "Navigation"),
            new SettingCategory(Translation, "注释翻译", "Comment Translation"),
            new SettingCategory(Indexing, "索引", "Indexing"),
            new SettingCategory(Unreal, "Unreal Engine", "Unreal Engine"),
            // ---- v0.5.4 联合编译（Horde / UBA）
            new SettingCategory(UEToVS.Host.Logic.Horde.HordeSettingsSchema.Category, "联合编译", "Distributed Builds"),
            new SettingCategory(Agent, "Agent / API", "Agent / API"),
            new SettingCategory(Logging, "诊断", "Diagnostics"),
            new SettingCategory(Updates, "检查更新", "Updates"),
        };

        /// <summary>Default indexing thread count: a quarter of the logical cores, at least 1.</summary>
        public static int DefaultIndexThreads(int logicalCores) => Math.Max(1, logicalCores / 4);

        /// <summary>Effective thread count for a stored value (0 = default).</summary>
        public static int EffectiveThreads(int stored, int logicalCores) =>
            stored > 0 ? Math.Min(stored, Math.Max(1, logicalCores)) : DefaultIndexThreads(logicalCores);

        private static SettingDescriptor D(string category, string key, SettingKind kind, string zh, string en, string descZh, string descEn, string def,
                                           string groupZh = null, string groupEn = null) =>
            new SettingDescriptor
            {
                Category = category, Key = key, Kind = kind, TitleZh = zh, TitleEn = en, DescriptionZh = descZh, DescriptionEn = descEn,
                Default = def, GroupZh = groupZh, GroupEn = groupEn,
            };

        private static SettingDescriptor A(string category, string key, string actionId, string zh, string en, string descZh, string descEn,
                                           string groupZh = null, string groupEn = null, SettingKind kind = SettingKind.Action)
        {
            var d = D(category, key, kind, zh, en, descZh, descEn, null, groupZh, groupEn);
            d.ActionId = actionId;
            return d;
        }

        /// <summary>
        /// The schema for a machine with <paramref name="logicalCores"/> cores (thread ranges and "auto" labels depend on it)
        /// running VS of major version <paramref name="vsMajor"/> (0 = unknown; 17 = VS 2022 greys out the VS 18-only switches).
        /// </summary>
        public static List<SettingDescriptor> Build(int logicalCores, int vsMajor = 0)
        {
            int cores = Math.Max(1, logicalCores);
            int auto = DefaultIndexThreads(cores);
            var list = new List<SettingDescriptor>();

            // ================= 常规
            const string gEngine = "引擎", gEngineEn = "Engine";
            list.Add(D(General, "engine.path", SettingKind.Path, "代码引擎程序", "Code engine executable",
                "UEToVS.Engine.exe 或其所在文件夹；留空使用扩展自带的引擎。", "UEToVS.Engine.exe or its folder; empty = the bundled engine.", "", gEngine, gEngineEn));
            list.Add(D(General, "engine.unrealEngineRoot", SettingKind.Path, "Unreal Engine 根目录", "Unreal Engine root",
                "留空则按 .uproject 的 EngineAssociation 自动查找。", "Empty = found from the .uproject EngineAssociation.", "", gEngine, gEngineEn));
            var timeout = D(General, "engine.requestTimeoutSeconds", SettingKind.Int, "单次请求超时（秒）", "Request timeout (s)",
                "补全、跳转等向代码引擎发出的单个请求最长等多久。", "Longest wait for one request (completion, navigation…) to the code engine.", "30", gEngine, gEngineEn);
            timeout.Min = 1; timeout.Max = 600;
            list.Add(timeout);
            list.Add(A(General, "engine.restart", "uetovs.engine.restart", "重启代码引擎", "Restart code engine",
                "补全或跳转卡住时用：重新启动 UEToVS 的后台代码分析进程（重置崩溃计数）。", "When completion or navigation hangs: restarts UEToVS's background code-analysis process (resets the crash budget).", gEngine, gEngineEn));

            const string gCpp = "C++ IntelliSense", gCppEn = "C++ IntelliSense";
            list.Add(A(General, "intelliSense.status", WindowActions.IntelliSenseStatus, "当前状态", "Current state", null, null, gCpp, gCppEn, SettingKind.Info));
            // v0.5.1: the scope has an "Off" choice instead of a separate takeover switch; VS's browse database, background
            // code analysis and Blueprint scan follow it (listed in the table below) instead of having switches of their own.
            var mode = D(General, "intelliSense.mode", SettingKind.Choice, "接管范围", "Takeover scope",
                "仅 UE 解决方案（默认）：打开 UE 解决方案时接管；全局：安装期间一直接管；关闭：不接管，使用 VS 自带的 C++ IntelliSense。" +
                "接管时同时关闭 VS 的 C++ 浏览数据库和后台代码分析，以及下面勾选的 VS 功能（修改前备份，选“关闭”即恢复），见下方“接管后 VS 自带功能”。",
                "UE solutions only (default): taken over while a UE solution is open; Global: always while installed; Off: not taken over, VS's own C++ IntelliSense is used. " +
                "Taking over also turns off VS's C++ browse database, background code analysis and the VS features ticked below (backed up first; choosing Off restores them), see “VS features after the takeover” below.",
                nameof(IntelliSenseTakeoverMode.UnrealSolutionOnly), gCpp, gCppEn);
            mode.Options = new[]
            {
                new SettingOption(nameof(IntelliSenseTakeoverMode.UnrealSolutionOnly), "仅 UE 解决方案", "UE solutions only"),
                new SettingOption(nameof(IntelliSenseTakeoverMode.Global), "全局", "Global"),
                new SettingOption(nameof(IntelliSenseTakeoverMode.Off), "关闭", "Off"),
            };
            list.Add(mode);
            // v0.7.3 hybrid takeover
            list.Add(D(General, "intelliSense.keepVsErrors", SettingKind.Bool, "保留 VS 的实时错误检查（混合接管）", "Keep VS's live error checking (hybrid)",
                "接管时仍让 VS 的 C++ IntelliSense 引擎运行，用它的错误列表和红色波浪线报真实的编译错误；补全、导航、着色、悬停提示仍由 UEToVS 提供，浏览数据库和下面勾选的后台功能照样关闭。" +
                "代价是多占 VS 的 IntelliSense 内存（大项目常见 2–6 GB）。需要 UHT 实时分析时，再取消勾选下面的“关闭 VS 的 UHT 代码分析”。",
                "While taken over, VS's C++ IntelliSense engine keeps running so its error list and squiggles report real compiler errors; completion, navigation, colouring and hover stay with UEToVS, and the browse database and the background features ticked below stay off. " +
                "Costs the memory of VS's IntelliSense (often 2–6 GB on large projects). For live UHT analysis, also untick “Turn off VS's UHT code analysis” below.",
                "false", gCpp, gCppEn));
            list.Add(A(General, "intelliSense.toggle", WindowActions.ToggleIntelliSense, "立即接管 / 恢复", "Take over / restore now",
                "根据当前状态接管或恢复 VS 自带的 C++ IntelliSense。", "Takes over or restores VS's own C++ IntelliSense, depending on the current state.", gCpp, gCppEn));
            list.Add(A(General, "vs.unrealIntegration", WindowActions.VsUnrealIntegration, "VS 自带的 Unreal Engine 集成", "VS's built-in Unreal Engine integration",
                null, null, gCpp, gCppEn, SettingKind.Info));
            // v0.5.6: one switch per VS Unreal / C++ background feature; on = turned off while taken over (backed up, restored with scope Off)
            foreach (var f in UEToVS.Host.Logic.IntelliSense.VsUnrealFeatures.Features)
            {
                var row = D(General, f.SettingKey, SettingKind.Bool, f.TitleZh, f.TitleEn,
                    f.DescriptionZh + "（接管时生效，修改前备份；取消勾选或接管范围选“关闭”即恢复）",
                    f.DescriptionEn + " (applies while taken over, backed up first; unticking it or scope “Off” restores it)", f.DefaultOn ? "true" : "false", gCpp, gCppEn);
                if (UEToVS.Host.Logic.IntelliSense.VsUnrealFeatures.NotApplicable(f, vsMajor))
                {
                    // v0.7.3: only VS 18's settings.json has this switch; grey it out on VS 2022 instead of letting it look applied
                    row.TitleZh += "（仅 VS 2026）";
                    row.TitleEn += " (VS 2026 only)";
                    row.HintZh = "VS 2022 没有这项设置，勾选与否都不会改动 VS。";
                    row.HintEn = "VS 2022 has no such setting; ticking it changes nothing in VS.";
                    row.EnabledWhenKey = Vs18OnlyGateKey;
                }
                list.Add(row);
            }
            // ---- v0.4.5 navigation gaps: what VS loses after the takeover and the UEToVS replacement (table drawn by the settings window)
            list.Add(A(General, "intelliSense.featureMatrix", UEToVS.Host.Logic.IntelliSense.TakeoverFeatures.SettingsInfoId, "接管后 VS 自带功能", "VS features after the takeover",
                "接管后 VS 的 C++ IntelliSense、浏览数据库、后台代码分析和蓝图扫描关闭（接管范围选“关闭”即恢复），依赖它们的 VS 功能会失效；下表列出 UEToVS 的替代命令和默认快捷键。",
                "With the takeover, VS's C++ IntelliSense, browse database, background code analysis and Blueprint scan are off (scope “Off” restores them) and the VS features built on them stop working; the table lists the UEToVS replacements and their default keys.",
                gCpp, gCppEn, SettingKind.Info));

            list.Add(A(General, "vs.restoreAll", WindowActions.RestoreVsDefaults, "恢复 VS 原样", "Restore Visual Studio",
                "禁用或卸载 UEToVS 之前点一下：按备份还原 UEToVS 改过的所有 VS 设置——C++ IntelliSense、浏览数据库、语义索引、后台代码分析、蓝图支持、UHT 代码分析、CodeLens、解决方案资源管理器的文件成员、" +
                "VS 字体和快捷键（包括为 UEToVS 让出按键的 VS 命令），并把接管范围设为“关闭”、不再管理 VS 字体，之后不会再自动改回去。" +
                "VS 禁用或卸载扩展时不会通知扩展，所以这一步要手动做。",
                "Click this before disabling or uninstalling UEToVS: restores, from the backups, every VS setting UEToVS changed — C++ IntelliSense, browse database, " +
                "semantic index, background code analysis, Blueprint support, UHT code analysis, CodeLens, Solution Explorer file members, VS fonts and shortcuts (including VS commands that gave their keys to UEToVS) — " +
                "and sets the takeover scope to Off and stops managing VS fonts so nothing is changed again. VS does not tell an extension it is being disabled or uninstalled, so this is a manual step.",
                "卸载前", "Before uninstalling"));

            list.Add(A(Updates, "update.check", WindowActions.CheckForUpdates, "检查更新", "Check for updates",
                "查询 GitHub 上的最新版本；有新版本时显示版本号和安装包大小，点“下载并安装”后显示下载进度，下载完启动 VS 的扩展安装程序，关闭 VS 后完成安装。",
                "Looks up the latest release on GitHub; when there is a newer one it shows the version and size, and “Download and install” downloads it with progress and starts VS's extension installer, which finishes after VS closes.",
                "更新", "Updates"));

            const string gTb = "工具栏", gTbEn = "Toolbar";
            list.Add(D(General, "toolbar.show", SettingKind.Bool, "显示 UEToVS 工具栏", "Show the UEToVS toolbar",
                "VS 顶部的 UEToVS 快捷按钮栏。", "The UEToVS quick-access toolbar at the top of VS.", "true", gTb, gTbEn));
            foreach (var b in ToolbarButtons.All)
            {
                if (b.Key == "settings") continue;
                list.Add(D(General, "toolbar.button." + b.Key, SettingKind.Bool, "按钮：" + b.TitleZh, "Button: " + b.TitleEn,
                    "在 UEToVS 工具栏上显示这个按钮。", "Show this button on the UEToVS toolbar.", "true", gTb, gTbEn));
            }

            const string gKb = "键盘", gKbEn = "Keyboard";
            list.Add(D(General, "keyboard.doubleShift", SettingKind.Bool, "连按两次 Shift 打开全局搜索", "Press Shift twice for Search Everywhere",
                "和 Rider 一样，快速按两次 Shift 打开全局搜索（Search Everywhere）。", "Press Shift twice quickly, like Rider.", "true", gKb, gKbEn));
            var interval = D(General, "keyboard.doubleShiftIntervalMs", SettingKind.Int, "两次 Shift 的最大间隔（毫秒）", "Max interval between the two presses (ms)",
                "两次 Shift 之间允许的最长时间。", "Longest time allowed between the two Shift presses.", "300", gKb, gKbEn);
            interval.Min = 150; interval.Max = 1000;
            list.Add(interval);
            list.Add(D(General, "keyboard.doubleShiftLeftOnly", SettingKind.Bool, "只识别左 Shift", "Left Shift only",
                "只有左侧 Shift 键的双击才会触发。", "Only a double press of the left Shift key triggers it.", "false", gKb, gKbEn));

            const string gMcp = "MCP（AI Agent）", gMcpEn = "MCP (AI agents)";
            list.Add(D(Agent, "mcp.enable", SettingKind.Bool, "启用 MCP 服务", "Enable the MCP server",
                "打开 UE 解决方案时在 .vs\\mcp.json 注册 uetovs MCP 服务器，VS Copilot Agent 模式自动发现；关闭则移除该注册。",
                "Registers the uetovs MCP server in .vs\\mcp.json when a UE solution opens (Copilot agent mode discovers it); off removes the registration.",
                "true", gMcp, gMcpEn));
            list.Add(D(Agent, "mcp.debugControl", SettingKind.Bool, "允许 Agent 通过 VS 调试 UE", "Let agents debug UE through VS",
                "打开 UE 解决方案时开放 VS 调试控制（只给本机当前用户，带令牌）：Agent 可以启动、停止、重启、附加，下断点、单步、看调用栈和变量、求表达式。关闭后 debug_* 工具和 Agent 面板的 ue_vs_* 工具不可用。",
                "When a UE solution opens, exposes VS debug control (current Windows user only, token-protected): agents can start, stop, restart and attach, set breakpoints, step, read the call stack and variables and evaluate expressions. Off disables the debug_* tools and the Agent panel's ue_vs_* tools.",
                "true", gMcp, gMcpEn));
            list.Add(D(Agent, "mcp.debugKeepFocus", SettingKind.Bool, "Agent 调试时不把 VS 拉到前台", "Keep VS in the background while an agent debugs",
                "VS 调试器一停下（断点、单步、暂停、异常）就会跳到前台。打开时，Agent 控制的调试会话里 VS 停下后马上把前台还给原来的窗口（最小化着的再最小化回去），会闪一下；你自己按 F5 调试时照常跳到前台。",
                "The VS debugger brings VS to the front whenever it stops (breakpoint, step, pause, exception). With this on, in a session an agent is driving VS hands the foreground back to the previous window right away (and minimizes again if it was minimized), with a brief flash; your own F5 sessions are unchanged.",
                "true", gMcp, gMcpEn));
            list.Add(A(Agent, "mcp.writeConfig", "uetovs.mcp.writeConfig", "写入 .mcp.json（给 Claude Code 等用）", "Write .mcp.json (for Claude Code and others)",
                "给 Claude Code 用：有版本管理（Git / SVN / Perforce 等）时写到版本库根目录，否则写到 UE 项目根目录；已有文件会合并，不覆盖其他服务器。",
                "For Claude Code: written to the version-control root (Git / SVN / Perforce, ...) if there is one, else to the UE project root; an existing file is merged, other servers are kept.", gMcp, gMcpEn));

            const string gUe = "Unreal Engine", gUeEn = "Unreal Engine";
            list.Add(D(General, "unreal.checkIncludesOnSave", SettingKind.Bool, "保存时检查 #include 的模块依赖", "Check #include module dependencies on save",
                "保存 .h/.cpp 时对照 Build.cs 检查 #include 所属模块，缺失时提示添加依赖。", "On saving a .h/.cpp, checks the #include modules against Build.cs and offers to add missing dependencies.",
                "true", gUe, gUeEn));
            var lcManual = D(General, "link.ueLiveCodingManual", SettingKind.Bool, "UE 编辑器启动时不启动 Live Coding", "Don't start Live Coding with the UE editor",
                "UE 默认在编辑器启动时就拉起 LiveCodingConsole，大型项目可占用数 GB 内存，即使从不用 Live Coding。打开此项时，UEToVS 在打开 UE 解决方案时把项目的 Live Coding 启动方式改为“手动”" +
                "（Saved/Config/WindowsEditor/EditorPerProjectUserSettings.ini，修改前备份）：控制台在第一次 Live Coding 编译时才启动。下次启动编辑器生效；关闭此项或“恢复 VS 原样”会还原。",
                "UE starts LiveCodingConsole with the editor by default, which can hold several GB on large projects even if Live Coding is never used. With this on, opening a UE solution sets the project's Live Coding startup to “Manual” " +
                "(Saved/Config/WindowsEditor/EditorPerProjectUserSettings.ini, backed up first): the console starts on the first Live Coding compile. Takes effect at the next editor start; turning this off or “Restore Visual Studio” puts it back.",
                "true", gUe, gUeEn);
            lcManual.Keywords = "live coding livecoding LiveCodingConsole 内存 memory startup 启动 manual 手动";
            list.Add(lcManual);

            // ================= 索引
            const string gIdx = "索引", gIdxEn = "Indexing";
            list.Add(A(Indexing, "indexing.status", WindowActions.IndexStatus, "当前索引状态", "Index status", null, null, gIdx, gIdxEn, SettingKind.Info));
            var cpp = D(Indexing, "indexing.cppThreads", SettingKind.Int, "C++ 索引线程数", "C++ indexing threads",
                $"并发解析 C++ 的线程数。勾选“自动”时使用逻辑核心数的 1/4（本机 {auto} 个）。", $"Threads parsing C++ concurrently. “Auto” uses a quarter of the logical cores ({auto} here).", "0", gIdx, gIdxEn);
            cpp.Min = 0; cpp.Max = cores; cpp.AutoLabelZh = $"自动（{auto}）"; cpp.AutoLabelEn = $"Auto ({auto})"; cpp.AutoValue = auto; cpp.Keywords = "cores 核心 并发 concurrency 自动 auto";
            list.Add(cpp);
            var bp = D(Indexing, "indexing.blueprintThreads", SettingKind.Int, "蓝图/资产索引线程数", "Blueprint / asset indexing threads",
                $"并发扫描 .uasset/.umap 的线程数。勾选“自动”时本机使用 {auto} 个。", $"Threads scanning .uasset/.umap concurrently. “Auto” uses {auto} here.", "0", gIdx, gIdxEn);
            bp.Min = 0; bp.Max = cores; bp.AutoLabelZh = cpp.AutoLabelZh; bp.AutoLabelEn = cpp.AutoLabelEn; bp.AutoValue = auto; bp.Keywords = "cores 核心 blueprint 蓝图 asset 资产 自动 auto";
            list.Add(bp);
            var during = D(Indexing, "indexing.duringBuild", SettingKind.Choice, "编译期间", "While building",
                "VS/UBT 编译时后台索引的行为。", "What background indexing does while VS/UBT builds.", nameof(IndexDuringBuild.SingleThread), gIdx, gIdxEn);
            during.Options = new[]
            {
                new SettingOption(nameof(IndexDuringBuild.Pause), "暂停", "Pause"),
                new SettingOption(nameof(IndexDuringBuild.SingleThread), "单线程继续", "One thread"),
                new SettingOption(nameof(IndexDuringBuild.FullSpeed), "全速", "Full speed"),
            };
            list.Add(during);
            list.Add(D(Indexing, "indexing.includeEngine", SettingKind.Bool, "索引引擎源码", "Index engine sources",
                "同时索引 Unreal Engine 源码（先头文件后 .cpp）。关闭可大幅缩短首次索引。", "Also index the Unreal Engine sources (headers first). Off = much faster first index.", "true", gIdx, gIdxEn));
            const string gEi = "引擎索引", gEiEn = "Engine index";
            list.Add(D(Indexing, "indexing.shareEngineIndex", SettingKind.Bool, "在多个工程间共享引擎索引", "Share the engine index between projects",
                "关闭（默认）：引擎索引放在本解决方案的 .vs\\UEToVS\\Index 下。打开：同一引擎版本的工程共用一份，放在下面的位置。",
                "Off (default): the engine index lives in this solution's .vs\\UEToVS\\Index. On: projects on the same engine version share one copy at the location below.",
                "false", gEi, gEiEn));
            list.Add(D(Indexing, "indexing.engineIndexPath", SettingKind.Path, "共享引擎索引位置", "Shared engine index location",
                "仅在共享时使用；留空 = %LocalAppData%\\UEToVS\\EngineIndex\\<引擎版本>。", "Used only when sharing; empty = %LocalAppData%\\UEToVS\\EngineIndex\\<engine version>.", "", gEi, gEiEn));
            const string gRb = "重建", gRbEn = "Rebuild";
            list.Add(A(Indexing, "indexing.rebuildProject", WindowActions.RebuildProjectIndex, "重建项目索引", "Rebuild project index",
                "丢弃项目（游戏/插件）C++ 索引并在后台重新解析。", "Drops the project (game / plugin) C++ index and re-parses it in the background.", gRb, gRbEn));
            list.Add(A(Indexing, "indexing.rebuildEngine", WindowActions.RebuildEngineIndex, "重建引擎索引", "Rebuild engine index",
                "丢弃 Unreal Engine 源码索引并重新解析（共享时会影响使用同一引擎的其他项目）。", "Drops the Unreal Engine source index and re-parses it (shared with other projects on the same engine when sharing is on).", gRb, gRbEn));
            list.Add(A(Indexing, "indexing.rebuildBlueprint", WindowActions.RebuildBlueprintIndex, "重建蓝图索引", "Rebuild Blueprint index",
                "丢弃蓝图/资产索引并重新扫描。", "Drops the Blueprint / asset index and re-scans.", gRb, gRbEn));

            // ================= 导航（M2 编辑器 + M1 历史 / 搜索）
            const string gNav = "编辑器", gNavEn = "Editor";
            list.Add(D(Navigation, "nav.ctrlClick", SettingKind.Bool, "Ctrl+单击 转到定义", "Ctrl+Click goes to definition",
                "按住 Ctrl 悬停会给标识符加下划线，单击跳转（Alt+← / Alt+→ 后退/前进）。", "Holding Ctrl underlines the identifier under the mouse; click to jump (Alt+← / Alt+→ to go back/forward).", "true", gNav, gNavEn));
            list.Add(D(Navigation, "nav.highlightReferences", SettingKind.Bool, "高亮光标处符号的引用", "Highlight references of the symbol at the caret",
                "仅 C++；读和写用不同底色。", "C++ only; reads and writes get different backgrounds.", "true", gNav, gNavEn));
            list.Add(D(Navigation, "nav.highlightReadColor", SettingKind.Text, "读取引用的底色", "Read reference background",
                "#RRGGBB 或 #AARRGGBB；留空 = 跟随主题。", "#RRGGBB or #AARRGGBB; empty = theme default.", "", gNav, gNavEn));
            list.Add(D(Navigation, "nav.highlightWriteColor", SettingKind.Text, "写入引用的底色", "Write reference background",
                "#RRGGBB 或 #AARRGGBB；留空 = 跟随主题。", "#RRGGBB or #AARRGGBB; empty = theme default.", "", gNav, gNavEn));
            list.Add(D(Navigation, "nav.navigationBar", SettingKind.Bool, "显示导航栏", "Show the navigation bar",
                "C++ 编辑器顶部的 当前类型 / 当前函数 导航栏。", "Current type / current function bar at the top of C++ editors.", "true", gNav, gNavEn));
            var sync = D(Navigation, "nav.syncSolutionExplorer", SettingKind.Bool, "在解决方案资源管理器中定位 C++ 文件", "Reveal C++ files in Solution Explorer",
                "打开或切换到 C++ 文件时，在解决方案资源管理器中展开并选中它（类似 Rider 的 Autoscroll from Source；不抢焦点）。",
                "Opening or switching to a C++ file expands and selects it in Solution Explorer (like Rider's Autoscroll from Source; focus stays in the editor).",
                "true", gNav, gNavEn);
            sync.Keywords = "track active item 跟踪活动项 同步 sync autoscroll from source 定位 locate reveal select opened file";
            list.Add(sync);
            const string gHist = "导航历史", gHistEn = "Navigation history";
            list.Add(D(Navigation, "nav.mouseButtons", SettingKind.Bool, "鼠标侧键后退 / 前进", "Mouse side buttons go back / forward",
                "鼠标 XButton1 / XButton2 在 UEToVS 导航历史中后退 / 前进。", "Mouse XButton1 / XButton2 move back / forward through UEToVS's history.", "true", gHist, gHistEn));
            var lines = D(Navigation, "nav.lineThreshold", SettingKind.Int, "记录历史的最小跳行数", "Minimum line jump recorded",
                "同一文件内光标移动超过这么多行才记入后退历史。", "A caret move within one file is recorded only beyond this many lines.", "15", gHist, gHistEn);
            lines.Min = 1; lines.Max = 1000;
            list.Add(lines);
            var cap = D(Navigation, "nav.historyCapacity", SettingKind.Int, "历史容量", "History capacity",
                "后退历史最多保留的位置数。", "Most positions kept in the Back history.", "100", gHist, gHistEn);
            cap.Min = 10; cap.Max = 1000;
            list.Add(cap);
            const string gSearch = "Search Everywhere", gSearchEn = "Search Everywhere";
            var perCat = D(Navigation, "search.maxResultsPerCategory", SettingKind.Int, "单个分类最多结果数", "Results per category",
                "单个标签页每类最多显示的结果（“全部”页每组 8 个）。", "Results per category on a single tab (the All tab shows 8 per group).", "60", gSearch, gSearchEn);
            perCat.Min = 10; perCat.Max = 1000;
            list.Add(perCat);

            // ================= 代码感知（M3：补全 / Quick Info / 参数信息 / 语义着色）
            const string gCmp = "补全", gCmpEn = "Completion";
            list.Add(D(IntelliSense, "completion.enabled", SettingKind.Bool, "启用 UEToVS 补全", "Enable UEToVS completion",
                "在 VS 原生补全列表中提供 UE 感知的候选项。", "Adds UE-aware items to VS's completion list.", "true", gCmp, gCmpEn));
            var scope = D(IntelliSense, "completion.scope", SettingKind.Choice, "补全范围", "Completion scope",
                "自动：接管 VS IntelliSense 前只补 UE 增强项（说明符、#include、override、UE 反射成员），接管后提供全部。",
                "Auto: UE enhancements only (specifiers, #include, overrides, reflected members) until VS IntelliSense is taken over, then everything.",
                nameof(CompletionScope.Auto), gCmp, gCmpEn);
            scope.Options = new[]
            {
                new SettingOption(nameof(CompletionScope.Auto), "自动", "Auto"),
                new SettingOption(nameof(CompletionScope.UeOnly), "仅 UE 增强项", "UE enhancements only"),
                new SettingOption(nameof(CompletionScope.All), "全部", "Everything"),
            };
            list.Add(scope);
            list.Add(D(IntelliSense, "completion.onIdentifier", SettingKind.Bool, "输入标识符时自动弹出", "Open while typing identifiers",
                "VA 风格；关闭后只在 . -> :: #include、说明符括号和 Ctrl+Space 时弹出。", "VA style; off = only on . -> :: #include, specifier lists and Ctrl+Space.", "true", gCmp, gCmpEn));
            var caseMode = D(IntelliSense, "completion.caseMode", SettingKind.Choice, "大小写", "Letter case",
                "一致优先：都列出，大小写和输入一致的排前面（首字母最重要）；首字母一致 / 全部一致：不一致的不列出（同 Rider）。",
                "Prefer: everything is listed, items matching the typed case first (the first letter weighs most); first letter / all letters: others are hidden (like Rider).",
                nameof(CompletionCaseMode.Prefer), gCmp, gCmpEn);
            caseMode.Options = new[]
            {
                new SettingOption(nameof(CompletionCaseMode.Prefer), "一致的优先", "Matching case first"),
                new SettingOption(nameof(CompletionCaseMode.FirstLetter), "首字母一致", "First letter must match"),
                new SettingOption(nameof(CompletionCaseMode.All), "全部一致", "All letters must match"),
                new SettingOption(nameof(CompletionCaseMode.Ignore), "忽略大小写", "Ignore case"),
            };
            list.Add(caseMode);
            list.Add(D(IntelliSense, "completion.tolerant", SettingKind.Bool, "容错匹配", "Forgiving matching",
                "也列出包含输入的、按顺序含这些字母的（umcmp → UMeshComponent）和打错一两个字母的（Umehs → UMeshComponent），排在正常匹配后面。",
                "Also lists names containing the text, with the letters in order (umcmp → UMeshComponent) or with a typo or two (Umehs → UMeshComponent), after the regular matches.",
                "true", gCmp, gCmpEn));
            list.Add(D(IntelliSense, "completion.accessorFix", SettingKind.Bool, "提交时自动修正 . 和 ->", "Fix . / -> on commit",
                "按变量是否为指针把 . 改成 ->（或反之）。", "Turns . into -> (or back) depending on whether the variable is a pointer.", "true", gCmp, gCmpEn));
            var maxItems = D(IntelliSense, "completion.maxResults", SettingKind.Int, "每次最多候选项", "Items per request",
                "超过时随输入继续向引擎重新查询。", "More are fetched from the engine as you keep typing.", "500", gCmp, gCmpEn);
            maxItems.Min = 50; maxItems.Max = 5000;
            list.Add(maxItems);
            const string gQi = "Quick Info 与参数信息", gQiEn = "Quick Info & parameter info";
            list.Add(D(IntelliSense, "quickInfo.enabled", SettingKind.Bool, "鼠标悬停显示信息（Quick Info）", "Quick Info on hover",
                "声明、文档注释、UE 反射摘要与宏展开（也可 Ctrl+K,I）。", "Declaration, docs, UE reflection summary and macro expansion (also Ctrl+K,I).", "true", gQi, gQiEn));
            list.Add(D(IntelliSense, "quickInfo.reflectionTags", SettingKind.Bool, "解读 UE 说明符", "Explain UE specifiers",
                "在 UFUNCTION / UPROPERTY 原文下面加一行说明，如“蓝图可调用 · 可编辑”。", "Adds a line under UFUNCTION / UPROPERTY, e.g. \"Blueprint callable · Editable\".", "false", gQi, gQiEn));
            list.Add(D(IntelliSense, "signatureHelp.enabled", SettingKind.Bool, "参数信息", "Parameter info",
                "输入 ( 或 , 时显示（也可 Ctrl+Shift+Space）。", "Shown on ( and , (also Ctrl+Shift+Space).", "true", gQi, gQiEn));
            UEToVS.Host.Logic.Translation.TranslationSettingsSchema.AddDescriptors(list, IntelliSense);
            const string gCol = "语义着色", gColEn = "Semantic coloring";
            list.Add(D(IntelliSense, "coloring.enabled", SettingKind.Bool, "VA 风格语义着色", "VA-style semantic coloring",
                "类型、函数、变量、宏、枚举值等分别着色；颜色可在 VS 字体和颜色（“UEToVS …”）中修改。",
                "Colors types, functions, variables, macros, enum members …; colors are editable in VS Fonts and Colors (\"UEToVS …\").", "true", gCol, gColEn));
            list.Add(D(IntelliSense, "coloring.overrides", SettingKind.Text, "颜色覆盖", "Color overrides",
                "例如 Class=#4EC9B0; Functions=#DCDCAA；留空 = 跟随主题 / 字体和颜色。", "e.g. Class=#4EC9B0; Functions=#DCDCAA; empty = theme / Fonts and Colors.", "", gCol, gColEn));
            var margin = D(IntelliSense, "coloring.marginLines", SettingKind.Int, "预着色行数", "Lines colored ahead",
                "可见区域上下额外请求的行数。", "Extra lines above and below the viewport.", "50", gCol, gColEn);
            margin.Min = 0; margin.Max = 1000;
            list.Add(margin);
            list.Add(A(IntelliSense, "coloring.importVa", "uetovs.intellisense.importVaColors", "导入 Visual Assist 配色…", "Import Visual Assist colors…",
                "选择 VA 导出的 .vssettings 文件。", "Pick a .vssettings file exported from Visual Assist.", gCol, gColEn));
            list.Add(A(IntelliSense, "coloring.reset", "uetovs.intellisense.resetSemanticColors", "恢复默认语义配色", "Reset semantic colors",
                null, null, gCol, gColEn));
            const string gSquiggle = "波浪线", gSquiggleEn = "Squiggles";
            list.Add(D(IntelliSense, "diagnostics.enabled", SettingKind.Bool, "代码错误波浪线（只报确定的）", "Error squiggles (certain problems only)",
                "接管 VS IntelliSense 后提供：未知成员、缺少 GENERATED_BODY()、#include 的模块不在 Build.cs 依赖中。只报确定的问题，不因 .generated.h 缺失报错。",
                "Shown once VS IntelliSense is taken over: unknown members, missing GENERATED_BODY(), #include of a module missing from Build.cs. Only certain problems; a missing .generated.h is never reported.",
                "true", gSquiggle, gSquiggleEn));
            // ---- v0.4.5 构建结果 / 错误列表
            const string gBuild = "构建结果与错误列表", gBuildEn = "Build results & Error List";
            list.Add(D(IntelliSense, "build.squiggles", SettingKind.Bool, "编译错误波浪线", "Compiler error squiggles",
                "VS 生成结束后解析“生成”输出（UBT / MSVC / clang / UHT），在源码里标出错误和警告；下次生成开始时清除。不依赖接管 IntelliSense。",
                "After a VS build, parses the Build output (UBT / MSVC / clang / UHT) and marks errors and warnings in the source; cleared when the next build starts. Works without the IntelliSense takeover.",
                "true", gBuild, gBuildEn));
            list.Add(D(IntelliSense, "errorList.enabled", SettingKind.Bool, "写入错误列表", "Show in the Error List",
                "把 UEToVS 的 UE 规则诊断（已打开文件的可见部分）写到 VS 错误列表，来源为“UEToVS”。",
                "Lists UEToVS's UE rule diagnostics (visible part of open files) in the VS Error List, source \"UEToVS\".",
                "true", gBuild, gBuildEn));
            list.Add(D(IntelliSense, "errorList.includeBuild", SettingKind.Bool, "错误列表包含编译错误", "Error List includes build errors",
                "同时以“UEToVS”来源列出解析出的编译错误。VS 自己的“生成”来源通常已列出它们，打开后可能重复。",
                "Also lists the parsed build errors under \"UEToVS\". VS's own \"Build\" source usually lists them already, so they may appear twice.",
                "false", gBuild, gBuildEn));
            const string gTpl = "代码模板", gTplEn = "Templates";
            list.Add(D(IntelliSense, "snippets.enabled", SettingKind.Bool, "UE 代码模板", "UE live templates",
                "在行首输入缩写（uclass、ustruct、uprop、ufunc、uelog …）后按 Tab 展开，或在补全列表里选择；Tab / Shift+Tab 在占位符之间跳转，Enter / Esc 结束。",
                "Type an abbreviation at line start (uclass, ustruct, uprop, ufunc, uelog …) and press Tab, or pick it from the completion list; Tab / Shift+Tab move between fields, Enter / Esc finish.",
                "true", gTpl, gTplEn));
            list.Add(D(IntelliSense, "postfix.enabled", SettingKind.Bool, "Postfix 模板", "Postfix templates",
                "在表达式后输入 .if、.notnull、.cast、.isvalid、.for 等，把表达式包进对应代码。", "Type .if, .notnull, .cast, .isvalid, .for … after an expression to wrap it.",
                "true", gTpl, gTplEn));
            const string gInlay = "内嵌提示", gInlayEn = "Inlay hints";
            list.Add(D(IntelliSense, "inlay.parameterNames", SettingKind.Bool, "参数名提示", "Parameter name hints",
                "实参是字面量、nullptr、true / false 时，在它前面显示形参名（和 Rider 一致）。只计算可见区域，Ctrl+Shift+Alt+U, 2 临时开关。",
                "Shows the parameter name before literal / nullptr / true / false arguments (like Rider). Only the visible lines are computed; Ctrl+Shift+Alt+U, 2 toggles.",
                "true", gInlay, gInlayEn));
            list.Add(D(IntelliSense, "inlay.autoTypes", SettingKind.Bool, "auto 推导类型", "Deduced auto types",
                "在 auto 变量名后显示推导出的类型（推导不出时不显示）。", "Shows the deduced type after auto variable names (nothing when it cannot be deduced).", "false", gInlay, gInlayEn));
            list.Add(D(IntelliSense, "inlay.codeVision", SettingKind.Bool, "派生类和蓝图数量（Code Vision）", "Derived types and Blueprints (Code Vision)",
                "在 UCLASS / USTRUCT 声明行末显示有几个 C++ 派生类、几个派生蓝图，在 UFUNCTION / UPROPERTY 行末显示有几个蓝图或关卡用到它（蓝图来自离线读取的 .uasset，按名称匹配）。数量为 0 时不显示。",
                "Shows at the end of UCLASS / USTRUCT lines how many C++ types and Blueprints derive from it, and at the end of UFUNCTION / UPROPERTY lines how many Blueprints or levels use it (Blueprints read offline from the .uasset files, matched by name). Nothing is shown for 0.",
                "true", gInlay, gInlayEn));
            // ---- v0.4.5 编辑体验
            EditorFeatureSettings.AddDescriptors(list, IntelliSense);
            FileEncodingSettings.AddDescriptors(list, IntelliSense); // v0.9.13 文件编码

            // ---- v0.4.5 heavy features: generic template instantiation (IntelliSense page)
            const string gTi = "模板实例化", gTiEn = "Template instantiation";
            list.Add(D(IntelliSense, "templates.enabled", SettingKind.Bool, "推导模板成员的类型", "Deduce template member types",
                "把模板实参代入成员的返回 / 字段类型（TOptional、TSharedPtr、TWeakObjectPtr、自定义类模板、typedef / using 别名、Cast<T> 等），补全更准。可能占用较多 CPU/内存；关闭后回到内置的 UE 模板规则。",
                "Substitutes template arguments into member return / field types (TOptional, TSharedPtr, TWeakObjectPtr, your class templates, typedef / using aliases, Cast<T>…) for more exact completion. May use more CPU/memory; off = the built-in UE template rules only.",
                "true", gTi, gTiEn));
            var tplCache = D(IntelliSense, "templates.cacheCapacity", SettingKind.Int, "实例缓存容量", "Instance cache capacity",
                "引擎里缓存的（模板, 实参）实例数（LRU），越大命中率越高、占内存越多。命中率见日志 intellisense.templates。",
                "(template, arguments) instances cached in the engine (LRU); larger = more hits and more memory. Hit rate: log event intellisense.templates.",
                "2048", gTi, gTiEn);
            tplCache.Min = 64; tplCache.Max = 65536;
            list.Add(tplCache);

            // ================= v0.4.5 检查（全项目检查 / 引擎升级助手）
            const string gInsp = "全项目检查", gInspEn = "Whole-project inspection";
            list.Add(D(Unreal, "inspection.enabled", SettingKind.Bool, "启用全项目检查", "Enable whole-project inspection",
                "按需（命令 / 本页按钮 / MCP run_project_inspection）用波浪线的 UE 规则检查整个项目源码，结果写到 .vs\\UEToVS\\inspections。可能占用较多 CPU/内存；关闭后不允许运行。",
                "On demand (command / this page / MCP run_project_inspection) runs the squiggle UE rules over all project sources; results go to .vs\\UEToVS\\inspections. May use a lot of CPU/memory; when off it cannot run.",
                "true", gInsp, gInspEn));
            list.Add(D(Unreal, "inspection.includePlugins", SettingKind.Bool, "包含项目插件", "Include project plugins",
                "同时检查项目 Plugins 目录下的源码（文件更多，耗时更长）。", "Also inspects sources under the project's Plugins folder (more files, takes longer).", "true", gInsp, gInspEn));
            list.Add(A(Unreal, "inspection.run", "uetovs.inspection.run", "运行全项目检查", "Run whole-project inspection",
                "在后台低优先级线程上运行（线程数同 C++ 索引），可随时取消。", "Runs on low-priority background threads (as many as C++ indexing), cancellable at any time.", gInsp, gInspEn));
            list.Add(A(Unreal, "inspection.results", "uetovs.inspection.show", "查看检查结果", "Show inspection results", null, null, gInsp, gInspEn));
            const string gUpg = "引擎升级助手", gUpgEn = "Engine upgrade assistant";
            list.Add(D(Unreal, "upgrade.enabled", SettingKind.Bool, "启用引擎升级助手", "Enable the engine upgrade assistant",
                "按需查找项目对 UE_DEPRECATED / [[deprecated]] API 的引用，按弃用版本分组，签名兼容时可批量替换（一次撤销）。可能占用较多 CPU/内存；关闭后不允许运行。",
                "On demand, finds project references to UE_DEPRECATED / [[deprecated]] APIs grouped by deprecation version, with batch replacement (one undo) when the replacement is signature-compatible. May use a lot of CPU/memory; when off it cannot run.",
                "true", gUpg, gUpgEn));
            list.Add(A(Unreal, "upgrade.run", "uetovs.upgrade.run", "扫描弃用 API", "Scan for deprecated APIs", null, null, gUpg, gUpgEn));

            // ================= v0.5.4 联合编译（Horde / UBA）
            UEToVS.Host.Logic.Horde.HordeSettingsSchema.AddDescriptors(list, cores);

            // ================= 日志与诊断
            const string gLog = "日志", gLogEn = "Logs";
            var level = D(Logging, "log.level", SettingKind.Choice, "日志级别", "Log level",
                "写入 .vs\\UEToVS\\Logs 的最低级别。排查问题时用 Debug。", "Lowest level written to .vs\\UEToVS\\Logs. Use Debug when investigating.", nameof(LogLevel.Info), gLog, gLogEn);
            level.Options = new[]
            {
                new SettingOption(nameof(LogLevel.Trace), "Trace", "Trace"),
                new SettingOption(nameof(LogLevel.Debug), "Debug", "Debug"),
                new SettingOption(nameof(LogLevel.Info), "Info", "Info"),
                new SettingOption(nameof(LogLevel.Warn), "Warn", "Warn"),
                new SettingOption(nameof(LogLevel.Error), "Error", "Error"),
            };
            list.Add(level);
            var days = D(Logging, "log.retentionDays", SettingKind.Int, "保留天数", "Keep logs for (days)", "超过天数的日志文件自动删除。", "Log files older than this are deleted.", "7", gLog, gLogEn);
            days.Min = 1; days.Max = 365;
            list.Add(days);
            var mb = D(Logging, "log.maxTotalMB", SettingKind.Int, "日志总大小上限（MB）", "Total log size limit (MB)", "所有日志加起来超过上限时，先删最旧的。", "When all logs together exceed this, the oldest go first.", "200", gLog, gLogEn);
            mb.Min = 10; mb.Max = 10000;
            list.Add(mb);
            list.Add(A(Logging, "log.directory", WindowActions.LogDirectory, "日志目录", "Log folder", null, null, gLog, gLogEn, SettingKind.Info));
            list.Add(A(Logging, "log.openFolder", "uetovs.diagnostics.openLogFolder", "打开日志文件夹", "Open log folder", null, null, gLog, gLogEn));
            list.Add(A(Logging, "log.openCurrent", "uetovs.diagnostics.openCurrentLog", "打开当前日志", "Open current log", null, null, gLog, gLogEn));
            const string gDiag = "诊断", gDiagEn = "Diagnostics";
            list.Add(A(Logging, "diagnostics.createPackage", "uetovs.diagnostics.createPackage", "生成诊断包", "Create diagnostics package",
                "打包日志、脱敏设置、索引清单与环境信息，便于反馈问题。", "Zips logs, sanitized settings, index manifest and environment info for bug reports.", gDiag, gDiagEn));
            list.Add(A(Logging, "diagnostics.componentGallery", "uetovs.ui.componentGallery", "界面组件预览", "UI component gallery",
                "查看 UEToVS 界面组件（开发与截图用）。", "Shows UEToVS UI components (development / screenshots).", gDiag, gDiagEn));

            // ================= 性能
            const string gPerf = "性能面板", gPerfEn = "Performance panel";
            var sample = D(Logging, "perf.sampleSeconds", SettingKind.Int, "采样间隔（秒）", "Sampling interval (s)",
                "性能面板打开时的刷新间隔；面板关闭时不采样。", "Refresh interval while the performance panel is open; no sampling when closed.", "1", gPerf, gPerfEn);
            sample.Min = 1; sample.Max = 10;
            list.Add(sample);
            list.Add(A(Logging, "perf.open", "uetovs.diagnostics.performancePanel", "打开性能面板", "Open performance panel", null, null, gPerf, gPerfEn));

            // ================= 外观 > 字体（the rest of the page is the custom appearance panel）
            UEToVS.Host.Logic.Fonts.FontSettingsSchema.AddDescriptors(list, Appearance);

            // v0.7.1: page and card of every row (one table, HostSettingsPages); unknown keys there fail the unit tests.
            HostSettingsPages.Apply(list);

            // Short button verbs (the card already shows the full title on the left).
            foreach (var d in list)
            {
                if (d.Kind != SettingKind.Action || !ActionLabels.TryGetValue(d.Key, out var label)) continue;
                d.ActionLabelZh = label.Zh;
                d.ActionLabelEn = label.En;
            }
            return list;
        }

        private static readonly Dictionary<string, (string Zh, string En)> ActionLabels = new Dictionary<string, (string Zh, string En)>(StringComparer.Ordinal)
        {
            ["engine.restart"] = ("重启", "Restart"),
            ["intelliSense.toggle"] = ("执行", "Run"),
            ["vs.restoreAll"] = ("恢复", "Restore"),
            ["update.check"] = ("检查", "Check"),
            ["mcp.writeConfig"] = ("写入", "Write"),
            ["indexing.rebuildProject"] = ("重建", "Rebuild"),
            ["indexing.rebuildEngine"] = ("重建", "Rebuild"),
            ["indexing.rebuildBlueprint"] = ("重建", "Rebuild"),
            ["coloring.importVa"] = ("导入…", "Import…"),
            ["coloring.reset"] = ("恢复", "Reset"),
            ["log.openFolder"] = ("打开", "Open"),
            ["log.openCurrent"] = ("打开", "Open"),
            ["diagnostics.createPackage"] = ("生成", "Create"),
            ["diagnostics.componentGallery"] = ("打开", "Open"),
            ["perf.open"] = ("打开", "Open"),
            // ---- v0.4.5 heavy features
            ["inspection.run"] = ("运行", "Run"),
            ["inspection.results"] = ("打开", "Open"),
            ["upgrade.run"] = ("运行", "Run"),
        };

        /// <summary>HostSettings ⇄ invariant string values, keyed by descriptor key.</summary>
        public static class Codec
        {
            private static string B(bool v) => v ? "true" : "false";
            private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
            private static bool PB(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            private static int PI(string v, int fallback) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : fallback;
            private static T PE<T>(string v, T fallback) where T : struct => Enum.TryParse(v, true, out T e) && Enum.IsDefined(typeof(T), e) ? e : fallback;

            public static Dictionary<string, string> ToValues(HostSettings s)
            {
                var hidden = ToolbarButtons.ParseHidden(s.HiddenToolbarButtons);
                var d = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["engine.path"] = s.EnginePathOverride ?? "",
                    ["engine.unrealEngineRoot"] = s.UnrealEngineRootOverride ?? "",
                    ["engine.requestTimeoutSeconds"] = I(s.EngineRequestTimeoutSeconds),
                    ["intelliSense.mode"] = s.IntelliSenseTakeoverMode.ToString(),
                    ["intelliSense.keepVsErrors"] = B(s.KeepVsErrorChecking),
                    ["vsFeatures.semanticIndex"] = B(s.TurnOffVsSemanticIndex),
                    ["vsFeatures.blueprints"] = B(s.TurnOffVsBlueprints),
                    ["vsFeatures.uhtAnalysis"] = B(s.TurnOffVsUhtAnalysis),
                    ["vsFeatures.codeLens"] = B(s.TurnOffVsCodeLens),
                    ["vsFeatures.adjacentSymbols"] = B(s.TurnOffVsAdjacentSymbols),
                    ["vsFeatures.ueLogAutoShow"] = B(s.TurnOffVsUeLogAutoShow),
                    ["vsFeatures.solutionExplorerSymbols"] = B(s.TurnOffVsSolutionExplorerSymbols),
                    ["toolbar.show"] = B(s.ShowToolbar),
                    ["keyboard.doubleShift"] = B(s.DoubleShiftEnabled),
                    ["keyboard.doubleShiftIntervalMs"] = I(s.DoubleShiftIntervalMs),
                    ["keyboard.doubleShiftLeftOnly"] = B(s.DoubleShiftLeftOnly),
                    ["unreal.checkIncludesOnSave"] = B(s.CheckIncludesOnSave),
                    ["mcp.enable"] = B(s.EnableMcpServer),
                    ["mcp.debugControl"] = B(s.EnableDebugControl),
                    ["mcp.debugKeepFocus"] = B(s.DebugKeepFocus),
                    ["link.ueLiveCodingManual"] = B(s.UeLiveCodingManualStartup),
                    ["indexing.cppThreads"] = I(s.CppIndexThreads),
                    ["indexing.blueprintThreads"] = I(s.BlueprintIndexThreads),
                    ["indexing.duringBuild"] = s.IndexDuringBuild.ToString(),
                    ["indexing.includeEngine"] = B(s.IndexEngineSource),
                    ["indexing.engineIndexPath"] = s.EngineIndexPath ?? "",
                    ["indexing.shareEngineIndex"] = B(s.ShareEngineIndex),
                    ["nav.ctrlClick"] = B(s.CtrlClickNavigation),
                    ["nav.highlightReferences"] = B(s.ReferenceHighlighting),
                    ["nav.highlightReadColor"] = s.ReferenceHighlightReadColor ?? "",
                    ["nav.highlightWriteColor"] = s.ReferenceHighlightWriteColor ?? "",
                    ["nav.navigationBar"] = B(s.NavigationBar),
                    ["nav.syncSolutionExplorer"] = B(s.SyncSolutionExplorer),
                    ["nav.mouseButtons"] = B(s.MouseButtonsNavigate),
                    ["nav.lineThreshold"] = I(s.NavigationLineThreshold),
                    ["nav.historyCapacity"] = I(s.NavigationHistoryCapacity),
                    ["search.maxResultsPerCategory"] = I(s.SearchMaxResultsPerCategory),
                    ["log.level"] = s.LogLevel.ToString(),
                    ["log.retentionDays"] = I(s.LogRetentionDays),
                    ["log.maxTotalMB"] = I(s.LogMaxTotalMB),
                    ["perf.sampleSeconds"] = I(s.PerformanceSampleSeconds),
                    ["completion.enabled"] = B(s.CompletionEnabled),
                    ["completion.scope"] = s.CompletionScope.ToString(),
                    ["completion.onIdentifier"] = B(s.CompletionOnIdentifier),
                    ["completion.caseMode"] = s.CompletionCaseMode.ToString(),
                    ["completion.tolerant"] = B(s.CompletionTolerant),
                    ["completion.accessorFix"] = B(s.CompletionAccessorFix),
                    ["completion.maxResults"] = I(s.CompletionMaxResults),
                    ["quickInfo.enabled"] = B(s.QuickInfoEnabled),
                    ["quickInfo.reflectionTags"] = B(s.QuickInfoReflectionTags),
                    ["signatureHelp.enabled"] = B(s.SignatureHelpEnabled),
                    ["coloring.enabled"] = B(s.SemanticColoring),
                    ["coloring.overrides"] = s.SemanticColorOverrides ?? "",
                    ["coloring.marginLines"] = I(s.SemanticColoringMarginLines),
                    ["diagnostics.enabled"] = B(s.DiagnosticsEnabled),
                    ["snippets.enabled"] = B(s.SnippetsEnabled),
                    ["postfix.enabled"] = B(s.PostfixTemplatesEnabled),
                    ["inlay.parameterNames"] = B(s.InlayParameterNames),
                    ["inlay.autoTypes"] = B(s.InlayAutoTypes),
                    ["inlay.codeVision"] = B(s.InlayCodeVision),
                    // ---- v0.4.5 构建结果 / 错误列表
                    ["build.squiggles"] = B(s.BuildErrorSquiggles),
                    ["errorList.enabled"] = B(s.ErrorListEnabled),
                    ["errorList.includeBuild"] = B(s.ErrorListIncludeBuild),
                    // ---- v0.4.5 heavy features
                    ["templates.enabled"] = B(s.TemplateInstantiationEnabled),
                    ["templates.cacheCapacity"] = I(s.TemplateCacheCapacity),
                    ["inspection.enabled"] = B(s.ProjectInspectionEnabled),
                    ["inspection.includePlugins"] = B(s.InspectionIncludePlugins),
                    ["upgrade.enabled"] = B(s.UpgradeAssistantEnabled),
                };
                foreach (var b in ToolbarButtons.All)
                    if (b.Key != "settings") d["toolbar.button." + b.Key] = B(!hidden.Contains(b.Key));
                UEToVS.Host.Logic.Fonts.FontSettingsSchema.ToValues(s, d);
                UEToVS.Host.Logic.Translation.TranslationSettingsSchema.ToValues(s, d);
                EditorFeatureSettings.ToValues(s, d); // v0.4.5 编辑体验
                FileEncodingSettings.ToValues(s, d); // v0.9.13 文件编码
                UEToVS.Host.Logic.Horde.HordeSettingsSchema.ToValues(s, d); // v0.5.4 联合编译
                return d;
            }

            /// <summary>Indexing part pushed to the engine (0 threads = engine default = max(1, cores / 4)).</summary>
            public static UEToVS.Contracts.IndexingSettings ToIndexing(HostSettings s) => new UEToVS.Contracts.IndexingSettings
            {
                CppThreads = Math.Max(0, s.CppIndexThreads),
                AssetThreads = Math.Max(0, s.BlueprintIndexThreads),
                DuringBuild = (int)s.IndexDuringBuild,
                IncludeEngine = s.IndexEngineSource,
                SharedEngineCache = s.ShareEngineIndex,
                EngineCacheDirectory = string.IsNullOrWhiteSpace(s.EngineIndexPath) ? null : s.EngineIndexPath,
                // ---- v0.4.5 heavy features
                ProjectInspectionEnabled = s.ProjectInspectionEnabled,
                UpgradeAssistantEnabled = s.UpgradeAssistantEnabled,
                TemplateInstantiationEnabled = s.TemplateInstantiationEnabled,
                TemplateCacheCapacity = Math.Max(0, s.TemplateCacheCapacity),
            };

            /// <summary>
            /// Applies <paramref name="values"/> onto a copy of <paramref name="baseline"/>; values are normalized through the
            /// schema first (bad values fall back to the default and are reported in <paramref name="problems"/>).
            /// Unknown keys are ignored (forward compatible).
            /// </summary>
            public static HostSettings FromValues(IReadOnlyDictionary<string, string> values, IReadOnlyList<SettingDescriptor> schema, HostSettings baseline, List<string> problems = null)
            {
                var s = (baseline ?? new HostSettings()).Clone();
                if (values == null) return s;
                var hidden = ToolbarButtons.ParseHidden(s.HiddenToolbarButtons);
                foreach (var d in schema)
                {
                    if (!d.HasValue || !values.TryGetValue(d.Key, out var raw) || raw == null) continue;
                    var v = SettingsModel.Normalize(d, raw, out bool ok);
                    if (!ok) problems?.Add($"{d.Key}: '{raw}' → '{v}'");
                    switch (d.Key)
                    {
                        case "engine.path": s.EnginePathOverride = v; break;
                        case "engine.unrealEngineRoot": s.UnrealEngineRootOverride = v; break;
                        case "engine.requestTimeoutSeconds": s.EngineRequestTimeoutSeconds = PI(v, 30); break;
                        case "intelliSense.mode": s.IntelliSenseTakeoverMode = PE(v, IntelliSenseTakeoverMode.UnrealSolutionOnly); break;
                        case "intelliSense.keepVsErrors": s.KeepVsErrorChecking = PB(v); break;
                        case "vsFeatures.semanticIndex": s.TurnOffVsSemanticIndex = PB(v); break;
                        case "vsFeatures.blueprints": s.TurnOffVsBlueprints = PB(v); break;
                        case "vsFeatures.uhtAnalysis": s.TurnOffVsUhtAnalysis = PB(v); break;
                        case "vsFeatures.codeLens": s.TurnOffVsCodeLens = PB(v); break;
                        case "vsFeatures.adjacentSymbols": s.TurnOffVsAdjacentSymbols = PB(v); break;
                        case "vsFeatures.ueLogAutoShow": s.TurnOffVsUeLogAutoShow = PB(v); break;
                        case "vsFeatures.solutionExplorerSymbols": s.TurnOffVsSolutionExplorerSymbols = PB(v); break;
                        case "toolbar.show": s.ShowToolbar = PB(v); break;
                        case "keyboard.doubleShift": s.DoubleShiftEnabled = PB(v); break;
                        case "keyboard.doubleShiftIntervalMs": s.DoubleShiftIntervalMs = PI(v, 300); break;
                        case "keyboard.doubleShiftLeftOnly": s.DoubleShiftLeftOnly = PB(v); break;
                        case "unreal.checkIncludesOnSave": s.CheckIncludesOnSave = PB(v); break;
                        case "mcp.enable": s.EnableMcpServer = PB(v); break;
                        case "mcp.debugControl": s.EnableDebugControl = PB(v); break;
                        case "mcp.debugKeepFocus": s.DebugKeepFocus = PB(v); break;
                        case "link.ueLiveCodingManual": s.UeLiveCodingManualStartup = PB(v); break;
                        case "indexing.cppThreads": s.CppIndexThreads = PI(v, 0); break;
                        case "indexing.blueprintThreads": s.BlueprintIndexThreads = PI(v, 0); break;
                        case "indexing.duringBuild": s.IndexDuringBuild = PE(v, IndexDuringBuild.SingleThread); break;
                        case "indexing.includeEngine": s.IndexEngineSource = PB(v); break;
                        case "indexing.engineIndexPath": s.EngineIndexPath = v; break;
                        case "indexing.shareEngineIndex": s.ShareEngineIndex = PB(v); break;
                        case "nav.ctrlClick": s.CtrlClickNavigation = PB(v); break;
                        case "nav.highlightReferences": s.ReferenceHighlighting = PB(v); break;
                        case "nav.highlightReadColor": s.ReferenceHighlightReadColor = v; break;
                        case "nav.highlightWriteColor": s.ReferenceHighlightWriteColor = v; break;
                        case "nav.navigationBar": s.NavigationBar = PB(v); break;
                        case "nav.syncSolutionExplorer": s.SyncSolutionExplorer = PB(v); break;
                        case "nav.mouseButtons": s.MouseButtonsNavigate = PB(v); break;
                        case "nav.lineThreshold": s.NavigationLineThreshold = PI(v, 15); break;
                        case "nav.historyCapacity": s.NavigationHistoryCapacity = PI(v, 100); break;
                        case "search.maxResultsPerCategory": s.SearchMaxResultsPerCategory = PI(v, 60); break;
                        case "log.level": s.LogLevel = PE(v, LogLevel.Info); break;
                        case "log.retentionDays": s.LogRetentionDays = PI(v, 7); break;
                        case "log.maxTotalMB": s.LogMaxTotalMB = PI(v, 200); break;
                        case "perf.sampleSeconds": s.PerformanceSampleSeconds = PI(v, 1); break;
                        case "completion.enabled": s.CompletionEnabled = PB(v); break;
                        case "completion.scope": s.CompletionScope = PE(v, CompletionScope.Auto); break;
                        case "completion.onIdentifier": s.CompletionOnIdentifier = PB(v); break;
                        case "completion.caseMode": s.CompletionCaseMode = PE(v, CompletionCaseMode.Prefer); break;
                        case "completion.tolerant": s.CompletionTolerant = PB(v); break;
                        case "completion.accessorFix": s.CompletionAccessorFix = PB(v); break;
                        case "completion.maxResults": s.CompletionMaxResults = PI(v, 500); break;
                        case "quickInfo.enabled": s.QuickInfoEnabled = PB(v); break;
                        case "quickInfo.reflectionTags": s.QuickInfoReflectionTags = PB(v); break;
                        case "signatureHelp.enabled": s.SignatureHelpEnabled = PB(v); break;
                        case "coloring.enabled": s.SemanticColoring = PB(v); break;
                        case "coloring.overrides": s.SemanticColorOverrides = v; break;
                        case "coloring.marginLines": s.SemanticColoringMarginLines = PI(v, 50); break;
                        case "diagnostics.enabled": s.DiagnosticsEnabled = PB(v); break;
                        case "snippets.enabled": s.SnippetsEnabled = PB(v); break;
                        case "postfix.enabled": s.PostfixTemplatesEnabled = PB(v); break;
                        case "inlay.parameterNames": s.InlayParameterNames = PB(v); break;
                        case "inlay.autoTypes": s.InlayAutoTypes = PB(v); break;
                        case "inlay.codeVision": s.InlayCodeVision = PB(v); break;
                        // ---- v0.4.5 构建结果 / 错误列表
                        case "build.squiggles": s.BuildErrorSquiggles = PB(v); break;
                        case "errorList.enabled": s.ErrorListEnabled = PB(v); break;
                        case "errorList.includeBuild": s.ErrorListIncludeBuild = PB(v); break;
                        // ---- v0.4.5 heavy features
                        case "templates.enabled": s.TemplateInstantiationEnabled = PB(v); break;
                        case "templates.cacheCapacity": s.TemplateCacheCapacity = PI(v, 2048); break;
                        case "inspection.enabled": s.ProjectInspectionEnabled = PB(v); break;
                        case "inspection.includePlugins": s.InspectionIncludePlugins = PB(v); break;
                        case "upgrade.enabled": s.UpgradeAssistantEnabled = PB(v); break;
                        default:
                            if (UEToVS.Host.Logic.Fonts.FontSettingsSchema.TrySet(s, d.Key, v)) break;
                            if (UEToVS.Host.Logic.Translation.TranslationSettingsSchema.TrySet(s, d.Key, v)) break;
                            if (EditorFeatureSettings.TrySet(s, d.Key, v)) break; // v0.4.5 编辑体验
                            if (FileEncodingSettings.TrySet(s, d.Key, v)) break; // v0.9.13 文件编码
                            if (UEToVS.Host.Logic.Horde.HordeSettingsSchema.TrySet(s, d.Key, v)) break; // v0.5.4 联合编译
                            if (d.Key.StartsWith("toolbar.button.", StringComparison.Ordinal))
                            {
                                string key = d.Key.Substring("toolbar.button.".Length);
                                if (PB(v)) hidden.Remove(key); else hidden.Add(key);
                            }
                            break;
                    }
                }
                s.HiddenToolbarButtons = ToolbarButtons.FormatHidden(hidden);
                return s;
            }
        }
    }
}
