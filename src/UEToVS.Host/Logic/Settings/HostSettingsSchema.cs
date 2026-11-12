using System;
using System.Collections.Generic;
using System.Globalization;
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
        public const string Appearance = "appearance";
        public const string Indexing = "indexing";
        public const string Keymap = "keymap";
        public const string Navigation = "navigation";
        public const string Logging = "logging";
        public const string Performance = "performance";

        /// <summary>Action ids only the settings window resolves (not registered in the ActionRegistry).</summary>
        public static class WindowActions
        {
            public const string RebuildCppIndex = "settings.indexing.rebuildCpp";
            public const string RebuildBlueprintIndex = "settings.indexing.rebuildBlueprint";
            public const string ToggleIntelliSense = "settings.intelliSense.toggle";
            public const string IndexStatus = "settings.indexing.status";
            public const string IntelliSenseStatus = "settings.intelliSense.status";
            public const string LogDirectory = "settings.logging.directory";
            public const string VsUnrealIntegration = "settings.general.vsUnrealIntegration";
        }

        public static IReadOnlyList<SettingCategory> Categories { get; } = new[]
        {
            new SettingCategory(General, "常规", "General"),
            new SettingCategory(Appearance, "外观", "Appearance"),
            new SettingCategory(Indexing, "索引", "Indexing"),
            new SettingCategory(Keymap, "快捷键", "Keyboard"),
            new SettingCategory(Navigation, "导航", "Navigation"),
            new SettingCategory(Logging, "日志与诊断", "Logs & Diagnostics"),
            new SettingCategory(Performance, "性能", "Performance"),
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

        /// <summary>The schema for a machine with <paramref name="logicalCores"/> cores (thread ranges and "auto" labels depend on it).</summary>
        public static List<SettingDescriptor> Build(int logicalCores)
        {
            int cores = Math.Max(1, logicalCores);
            int auto = DefaultIndexThreads(cores);
            var list = new List<SettingDescriptor>();

            // ================= 常规
            const string gEngine = "引擎", gEngineEn = "Engine";
            list.Add(D(General, "engine.path", SettingKind.Path, "引擎程序路径", "Engine executable",
                "UEToVS.Engine.exe 或其所在文件夹；留空使用扩展自带的引擎。", "UEToVS.Engine.exe or its folder; empty = the bundled engine.", "", gEngine, gEngineEn));
            list.Add(D(General, "engine.unrealEngineRoot", SettingKind.Path, "Unreal Engine 根目录", "Unreal Engine root",
                "留空则按 .uproject 的 EngineAssociation 自动查找。", "Empty = found from the .uproject EngineAssociation.", "", gEngine, gEngineEn));
            var timeout = D(General, "engine.requestTimeoutSeconds", SettingKind.Int, "请求超时（秒）", "Request timeout (s)",
                "单个引擎请求的最长等待时间。", "Longest wait for one engine request.", "30", gEngine, gEngineEn);
            timeout.Min = 1; timeout.Max = 600;
            list.Add(timeout);
            list.Add(A(General, "engine.restart", "uetovs.engine.restart", "重启引擎", "Restart engine",
                "重新启动 UEToVS 引擎进程（重置崩溃计数）。", "Restarts the UEToVS engine process (resets the crash budget).", gEngine, gEngineEn));

            const string gCpp = "C++ IntelliSense", gCppEn = "C++ IntelliSense";
            list.Add(A(General, "intelliSense.status", WindowActions.IntelliSenseStatus, "当前状态", "Current state", null, null, gCpp, gCppEn, SettingKind.Info));
            list.Add(D(General, "intelliSense.takeover", SettingKind.Bool, "接管 VS C++ IntelliSense", "Take over VS C++ IntelliSense",
                "关闭 VS 自带的 C++ IntelliSense（修改前备份，可随时恢复），由 UEToVS 提供。", "Turns VS's own C++ IntelliSense off (backed up first, restorable); UEToVS provides it instead.",
                "false", gCpp, gCppEn));
            var mode = D(General, "intelliSense.mode", SettingKind.Choice, "接管范围", "Takeover scope",
                "全局：安装期间一直接管；仅 UE 解决方案：打开 UE 解决方案时接管，关闭时恢复。", "Global: while installed; UE solutions only: restored when the UE solution closes.",
                nameof(IntelliSenseTakeoverMode.Global), gCpp, gCppEn);
            mode.Options = new[]
            {
                new SettingOption(nameof(IntelliSenseTakeoverMode.Global), "全局", "Global"),
                new SettingOption(nameof(IntelliSenseTakeoverMode.UnrealSolutionOnly), "仅 UE 解决方案", "UE solutions only"),
            };
            list.Add(mode);
            list.Add(D(General, "intelliSense.disableDatabase", SettingKind.Bool, "同时关闭 VS 的 C++ 数据库", "Also disable VS's C++ database",
                "接管时关闭浏览数据库（.vc.db），节省大量磁盘和 CPU。", "Disables the browse database (.vc.db) while taken over; saves lots of disk and CPU.", "true", gCpp, gCppEn));
            list.Add(D(General, "intelliSense.disableBackgroundCodeAnalysis", SettingKind.Bool, "关闭 VS 后台代码分析", "Turn off VS background code analysis",
                "打开 UE 解决方案时关闭 VS 的 C++ 后台代码分析（修改前备份，可恢复）。", "Turns VS's C++ background code analysis off for UE solutions (backed up, restorable).", "false", gCpp, gCppEn));
            list.Add(A(General, "intelliSense.toggle", WindowActions.ToggleIntelliSense, "立即接管 / 恢复", "Take over / restore now",
                "根据当前状态接管或恢复 VS 自带的 C++ IntelliSense。", "Takes over or restores VS's own C++ IntelliSense, depending on the current state.", gCpp, gCppEn));
            list.Add(A(General, "vs.unrealIntegration", WindowActions.VsUnrealIntegration, "VS 自带的 Unreal Engine 集成", "VS's built-in Unreal Engine integration",
                null, null, gCpp, gCppEn, SettingKind.Info));

            const string gTb = "工具栏", gTbEn = "Toolbar";
            list.Add(D(General, "toolbar.show", SettingKind.Bool, "显示 UEToVS 工具栏", "Show the UEToVS toolbar",
                "VS 顶部的 UEToVS 快捷按钮栏。", "The UEToVS quick-access toolbar at the top of VS.", "true", gTb, gTbEn));
            foreach (var b in ToolbarButtons.All)
            {
                if (b.Key == "settings") continue;
                list.Add(D(General, "toolbar.button." + b.Key, SettingKind.Bool, "按钮：" + b.TitleZh, "Button: " + b.TitleEn, null, null, "true", gTb, gTbEn));
            }

            const string gKb = "键盘", gKbEn = "Keyboard";
            list.Add(D(General, "keyboard.doubleShift", SettingKind.Bool, "双击 Shift 打开 Search Everywhere", "Double Shift opens Search Everywhere",
                "像 Rider 一样快速按两次 Shift。", "Press Shift twice quickly, like Rider.", "true", gKb, gKbEn));

            // ================= 索引
            const string gIdx = "索引", gIdxEn = "Indexing";
            list.Add(A(Indexing, "indexing.status", WindowActions.IndexStatus, "当前索引状态", "Index status", null, null, gIdx, gIdxEn, SettingKind.Info));
            var cpp = D(Indexing, "indexing.cppThreads", SettingKind.Int, "C++ 索引线程数", "C++ indexing threads",
                $"并发解析 C++ 的线程数。0 = 自动（逻辑核心数的 1/4，本机 {auto}）。", $"Threads parsing C++ concurrently. 0 = auto (a quarter of the logical cores, {auto} here).", "0", gIdx, gIdxEn);
            cpp.Min = 0; cpp.Max = cores; cpp.AutoLabelZh = $"自动（{auto}）"; cpp.AutoLabelEn = $"Auto ({auto})"; cpp.Keywords = "cores 核心 并发 concurrency";
            list.Add(cpp);
            var bp = D(Indexing, "indexing.blueprintThreads", SettingKind.Int, "蓝图/资产索引线程数", "Blueprint / asset indexing threads",
                $"并发扫描 .uasset/.umap 的线程数。0 = 自动（本机 {auto}）。", $"Threads scanning .uasset/.umap concurrently. 0 = auto ({auto} here).", "0", gIdx, gIdxEn);
            bp.Min = 0; bp.Max = cores; bp.AutoLabelZh = cpp.AutoLabelZh; bp.AutoLabelEn = cpp.AutoLabelEn; bp.Keywords = "cores 核心 blueprint 蓝图 asset 资产";
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
            const string gRb = "重建", gRbEn = "Rebuild";
            list.Add(A(Indexing, "indexing.rebuildCpp", WindowActions.RebuildCppIndex, "重建 C++ 索引", "Rebuild C++ index",
                "丢弃 C++ 索引并在后台重新解析。", "Drops the C++ index and re-parses in the background.", gRb, gRbEn));
            list.Add(A(Indexing, "indexing.rebuildBlueprint", WindowActions.RebuildBlueprintIndex, "重建蓝图索引", "Rebuild Blueprint index",
                "丢弃蓝图/资产索引并重新扫描。", "Drops the Blueprint / asset index and re-scans.", gRb, gRbEn));

            // ================= 导航（M2 预留）
            const string gNav = "编辑器", gNavEn = "Editor";
            list.Add(D(Navigation, "nav.highlightReferences", SettingKind.Bool, "高亮光标处符号的引用", "Highlight references of the symbol at the caret",
                null, null, "true", gNav, gNavEn));
            list.Add(D(Navigation, "nav.navigationBar", SettingKind.Bool, "显示导航栏", "Show the navigation bar",
                "编辑器顶部的 类型 / 成员 导航栏。", "Type / member bar at the top of the editor.", "true", gNav, gNavEn));
            list.Add(D(Navigation, "nav.ctrlClick", SettingKind.Bool, "Ctrl+单击 转到定义", "Ctrl+Click goes to definition",
                "按住 Ctrl 单击符号跳转（Alt+← / Alt+→ 后退/前进）。", "Ctrl+click a symbol to jump (Alt+← / Alt+→ to go back/forward).", "true", gNav, gNavEn));

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
            var days = D(Logging, "log.retentionDays", SettingKind.Int, "保留天数", "Keep logs for (days)", null, null, "7", gLog, gLogEn);
            days.Min = 1; days.Max = 365;
            list.Add(days);
            var mb = D(Logging, "log.maxTotalMB", SettingKind.Int, "日志总大小上限（MB）", "Total log size limit (MB)", null, null, "200", gLog, gLogEn);
            mb.Min = 10; mb.Max = 10000;
            list.Add(mb);
            list.Add(A(Logging, "log.directory", WindowActions.LogDirectory, "日志目录", "Log folder", null, null, gLog, gLogEn, SettingKind.Info));
            list.Add(A(Logging, "log.openFolder", "uetovs.diagnostics.openLogFolder", "打开日志文件夹", "Open log folder", null, null, gLog, gLogEn));
            list.Add(A(Logging, "log.openCurrent", "uetovs.diagnostics.openCurrentLog", "打开当前日志", "Open current log", null, null, gLog, gLogEn));
            const string gDiag = "诊断", gDiagEn = "Diagnostics";
            list.Add(A(Logging, "diagnostics.createPackage", "uetovs.diagnostics.createPackage", "生成诊断包", "Create diagnostics package",
                "打包日志、脱敏设置、索引清单与环境信息，便于反馈问题。", "Zips logs, sanitized settings, index manifest and environment info for bug reports.", gDiag, gDiagEn));
            list.Add(A(Logging, "diagnostics.componentGallery", "uetovs.ui.componentGallery", "组件画廊", "Component gallery",
                "查看 UEToVS 界面组件（开发与截图用）。", "Shows UEToVS UI components (development / screenshots).", gDiag, gDiagEn));

            // ================= 性能
            const string gPerf = "性能面板", gPerfEn = "Performance panel";
            var sample = D(Performance, "perf.sampleSeconds", SettingKind.Int, "采样间隔（秒）", "Sampling interval (s)",
                "性能面板打开时的刷新间隔；面板关闭时不采样。", "Refresh interval while the performance panel is open; no sampling when closed.", "1", gPerf, gPerfEn);
            sample.Min = 1; sample.Max = 10;
            list.Add(sample);
            list.Add(A(Performance, "perf.open", "uetovs.diagnostics.performancePanel", "打开性能面板", "Open performance panel", null, null, gPerf, gPerfEn));

            return list;
        }

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
                    ["intelliSense.takeover"] = B(s.IntelliSenseTakeover),
                    ["intelliSense.mode"] = s.IntelliSenseTakeoverMode.ToString(),
                    ["intelliSense.disableDatabase"] = B(s.DisableDatabase),
                    ["intelliSense.disableBackgroundCodeAnalysis"] = B(s.DisableBackgroundCodeAnalysis),
                    ["toolbar.show"] = B(s.ShowToolbar),
                    ["keyboard.doubleShift"] = B(s.DoubleShiftSearchEverywhere),
                    ["indexing.cppThreads"] = I(s.CppIndexThreads),
                    ["indexing.blueprintThreads"] = I(s.BlueprintIndexThreads),
                    ["indexing.duringBuild"] = s.IndexDuringBuild.ToString(),
                    ["indexing.includeEngine"] = B(s.IndexEngineSource),
                    ["nav.highlightReferences"] = B(s.HighlightReferences),
                    ["nav.navigationBar"] = B(s.ShowNavigationBar),
                    ["nav.ctrlClick"] = B(s.CtrlClickGoToDefinition),
                    ["log.level"] = s.LogLevel.ToString(),
                    ["log.retentionDays"] = I(s.LogRetentionDays),
                    ["log.maxTotalMB"] = I(s.LogMaxTotalMB),
                    ["perf.sampleSeconds"] = I(s.PerformanceSampleSeconds),
                };
                foreach (var b in ToolbarButtons.All)
                    if (b.Key != "settings") d["toolbar.button." + b.Key] = B(!hidden.Contains(b.Key));
                return d;
            }

            /// <summary>Indexing part pushed to the engine (0 threads = engine default = max(1, cores / 4)).</summary>
            public static UEToVS.Contracts.IndexingSettings ToIndexing(HostSettings s) => new UEToVS.Contracts.IndexingSettings
            {
                CppThreads = Math.Max(0, s.CppIndexThreads),
                AssetThreads = Math.Max(0, s.BlueprintIndexThreads),
                DuringBuild = (int)s.IndexDuringBuild,
                IncludeEngine = s.IndexEngineSource,
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
                        case "intelliSense.takeover": s.IntelliSenseTakeover = PB(v); break;
                        case "intelliSense.mode": s.IntelliSenseTakeoverMode = PE(v, IntelliSenseTakeoverMode.Global); break;
                        case "intelliSense.disableDatabase": s.DisableDatabase = PB(v); break;
                        case "intelliSense.disableBackgroundCodeAnalysis": s.DisableBackgroundCodeAnalysis = PB(v); break;
                        case "toolbar.show": s.ShowToolbar = PB(v); break;
                        case "keyboard.doubleShift": s.DoubleShiftSearchEverywhere = PB(v); break;
                        case "indexing.cppThreads": s.CppIndexThreads = PI(v, 0); break;
                        case "indexing.blueprintThreads": s.BlueprintIndexThreads = PI(v, 0); break;
                        case "indexing.duringBuild": s.IndexDuringBuild = PE(v, IndexDuringBuild.SingleThread); break;
                        case "indexing.includeEngine": s.IndexEngineSource = PB(v); break;
                        case "nav.highlightReferences": s.HighlightReferences = PB(v); break;
                        case "nav.navigationBar": s.ShowNavigationBar = PB(v); break;
                        case "nav.ctrlClick": s.CtrlClickGoToDefinition = PB(v); break;
                        case "log.level": s.LogLevel = PE(v, LogLevel.Info); break;
                        case "log.retentionDays": s.LogRetentionDays = PI(v, 7); break;
                        case "log.maxTotalMB": s.LogMaxTotalMB = PI(v, 200); break;
                        case "perf.sampleSeconds": s.PerformanceSampleSeconds = PI(v, 1); break;
                        default:
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
