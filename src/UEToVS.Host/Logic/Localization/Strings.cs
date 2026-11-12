using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>
    /// Every user-visible string of the host (banners, status bar, commands, options pages, tool windows, messages),
    /// Chinese + English via <see cref="Loc.T"/>. The language follows VS's UI language (English VS → English, otherwise
    /// Chinese), set once at package initialization. Log events/messages are NOT localized (they stay English).
    /// Pure: no VS types, covered by tests (both languages non-empty and distinct where expected).
    /// </summary>
    public static class Strings
    {
        // ---- product / windows ----
        public static string ProductName => "UEToVS";
        public static string PerformanceWindowCaption => Loc.T("UEToVS 性能", "UEToVS Performance");
        public static string OutputPaneName => "UEToVS";

        // ---- commands (menu text; VSCT holds the English default, set at runtime via TextChanges) ----
        public static string CmdRestartEngine => Loc.T("重启引擎", "Restart Engine");
        public static string CmdTakeOverIntelliSense => Loc.T("接管 VS C++ IntelliSense", "Take Over C++ IntelliSense");
        public static string CmdRestoreIntelliSense => Loc.T("恢复 VS 自带 C++ IntelliSense", "Restore Visual Studio C++ IntelliSense");
        public static string CmdOpenLogFolder => Loc.T("打开日志文件夹", "Open Log Folder");
        public static string CmdOpenCurrentLog => Loc.T("打开当前日志", "Open Current Log");
        public static string CmdCreateDiagnostics => Loc.T("生成诊断包", "Create Diagnostics Package");
        public static string CmdPerformancePanel => Loc.T("性能面板", "Performance Panel");
        public static string CmdComponentGallery => Loc.T("组件画廊", "Component Gallery");
        public static string CmdSettings => Loc.T("设置…", "Settings...");
        public static string CmdFindAction => Loc.T("查找操作…", "Find Action...");

        // ---- action keywords (searchable in both languages regardless of UI language) ----
        public static readonly string[] KwLogs = { "log", "logs", "日志", "folder", "文件夹" };
        public static readonly string[] KwDiagnostics = { "diagnostics", "zip", "诊断", "诊断包", "report", "bug" };
        public static readonly string[] KwEngine = { "engine", "引擎", "restart", "重启", "index", "索引" };
        public static readonly string[] KwIntelliSense = { "intellisense", "c++", "感知", "接管", "恢复", "restore", "take over", "vs" };
        public static readonly string[] KwPerformance = { "performance", "性能", "memory", "内存", "cpu", "latency", "延迟" };
        public static readonly string[] KwGallery = { "gallery", "components", "组件", "画廊", "ui" };
        public static readonly string[] KwSettings = { "settings", "options", "设置", "选项", "preferences" };
        public static readonly string[] KwFindAction = { "find action", "actions", "command", "palette", "查找", "操作", "命令" };

        // ---- Find Action palette ----
        public static string PaletteTabActions => Loc.T("操作", "Actions");
        public static string PalettePlaceholder => Loc.T("输入操作名称、关键字或 id", "Type an action name, keyword or id");
        public static string PaletteEmptyHint => Loc.T("输入以查找 UEToVS 操作", "Type to find a UEToVS action");

        public static string CategoryName(Logic.Actions.ActionCategory category)
        {
            switch (category)
            {
                case Logic.Actions.ActionCategory.Navigation: return Loc.T("导航", "Navigation");
                case Logic.Actions.ActionCategory.Refactoring: return Loc.T("重构", "Refactoring");
                case Logic.Actions.ActionCategory.Generate: return Loc.T("生成", "Generate");
                case Logic.Actions.ActionCategory.Fix: return Loc.T("修复", "Fix");
                case Logic.Actions.ActionCategory.Unreal: return Loc.T("Unreal", "Unreal Engine");
                case Logic.Actions.ActionCategory.Settings: return Loc.T("设置", "Settings");
                case Logic.Actions.ActionCategory.Diagnostics: return Loc.T("诊断", "Diagnostics");
                default: return category.ToString();
            }
        }

        // ---- IntelliSense takeover ----
        public static string BannerTakenOver => Loc.T("UEToVS 已接管 C++ 感知，已关闭 VS 自带 IntelliSense（原设置已备份）。",
                                                      "UEToVS has taken over C++ code insight and turned off Visual Studio's own IntelliSense (original settings backed up).");
        public static string Restore => Loc.T("恢复", "Restore");
        public static string Settings => Loc.T("设置", "Settings");
        public static string OpenLog => Loc.T("打开日志", "Open Log");
        public static string DontShowAgain => Loc.T("不再提示", "Don't show again");
        public static string RestartEngine => Loc.T("重启引擎", "Restart Engine");

        public static string BannerBackupUnreadable(string path) => Loc.F("UEToVS 无法读取 IntelliSense 备份文件，未修改 VS 设置。请检查 {0}（详见日志）。",
                                                                          "UEToVS cannot read the IntelliSense backup; VS settings were not changed. Check {0} (see the log).", path);
        public static string BannerBackupWriteFailed => Loc.T("UEToVS 无法写入 IntelliSense 备份，未修改 VS 设置（详见日志）。",
                                                              "UEToVS could not write the IntelliSense backup; VS settings were not changed (see the log).");
        public static string BannerUnknownVs => Loc.T("UEToVS 未能识别此 VS 版本的 C++ IntelliSense 设置，未做修改（详见日志 intellisense.properties）。",
                                                      "UEToVS does not recognize the C++ IntelliSense settings of this VS version; nothing was changed (see intellisense.properties in the log).");
        public static string BannerBackupCorruptRestore => Loc.T("IntelliSense 备份文件损坏，无法自动恢复。请在 工具 > 选项 > 文本编辑器 > C/C++ > 高级 中手动恢复（见 README）。",
                                                                 "The IntelliSense backup is corrupt and cannot be restored automatically. Restore manually in Tools > Options > Text Editor > C/C++ > Advanced (see README).");
        public static string BannerNothingToRestore => Loc.T("没有需要恢复的设置：UEToVS 未修改过 VS 的 C++ IntelliSense 设置。",
                                                             "Nothing to restore: UEToVS has not changed Visual Studio's C++ IntelliSense settings.");
        public static string BannerRestored => Loc.T("已恢复 VS 自带 C++ IntelliSense 设置。", "Visual Studio's C++ IntelliSense settings have been restored.");

        public static string BannerRestoredPartially(int failed) => Loc.F("已恢复部分设置，{0} 项失败（备份已保留，可再次执行恢复；详见日志）。",
                                                                         "Settings partially restored; {0} failed (the backup is kept, run Restore again; see the log).", failed);
        public static string BannerVisualAssist => Loc.T("检测到 Visual Assist。两套 C++ 感知同时启用会互相抢占补全与快捷键，建议二选一：使用 UEToVS 时请在 VA 中关闭其 C++ 感知（UEToVS 不会修改 VA 的设置）。",
                                                         "Visual Assist detected. Two C++ code-insight engines compete for completion and shortcuts; choose one. To use UEToVS, turn off VA's C++ features in VA (UEToVS does not change VA's settings).");

        // ---- engine ----
        public static string EngineStartFailed(string error) => Loc.F("无法启动 UEToVS 引擎进程：{0}", "Cannot start the UEToVS engine process: {0}", error);
        public static string EngineIncompatible => Loc.T("UEToVS 引擎版本与扩展不匹配，请重新安装 UEToVS。", "The UEToVS engine does not match the extension version. Please reinstall UEToVS.");
        public static string EngineRuntimeMissing => Loc.T("UEToVS 引擎需要 .NET 8 运行时，请安装后执行“重启引擎”：https://dotnet.microsoft.com/download/dotnet/8.0",
                                                           "The UEToVS engine needs the .NET 8 runtime. Install it, then run Restart Engine: https://dotnet.microsoft.com/download/dotnet/8.0");
        public static string EngineRestartExhausted(int minutes, int max) => Loc.F("UEToVS 引擎在 {0} 分钟内崩溃超过 {1} 次，已停止自动重启，功能已降级。",
                                                                                 "The UEToVS engine crashed more than {1} times in {0} minutes; automatic restart stopped and features are degraded.", minutes, max);
        public static string StatusEngineStarting => Loc.T("UEToVS：正在启动引擎…", "UEToVS: starting engine...");
        public static string StatusEngineUnavailable => Loc.T("UEToVS：引擎不可用，功能已降级", "UEToVS: engine unavailable, features degraded");
        public static string StatusEngineNotInstalled => Loc.T("UEToVS：未找到引擎，功能已降级", "UEToVS: engine not found, features degraded");
        public static string StatusEngineRestarting => Loc.T("UEToVS：引擎重启中…", "UEToVS: restarting engine...");
        public static string StatusIndexing(int done, int total) => Loc.F("UEToVS：正在索引 {0:N0}/{1:N0}", "UEToVS: indexing {0:N0}/{1:N0}", done, total);
        public static string StatusIndexPhase(string phase) => Loc.F("UEToVS：{0}", "UEToVS: {0}", phase);
        public static string StatusIndexReady(int symbols, int files) => Loc.F("UEToVS：索引就绪，{0:N0} 个符号 / {1:N0} 个文件", "UEToVS: index ready, {0:N0} symbols / {1:N0} files", symbols, files);
        public static string StatusIndexFailed => Loc.T("UEToVS：索引失败（详见日志）", "UEToVS: indexing failed (see the log)");

        public static string PhaseName(int phase)
        {
            switch (phase)
            {
                case 1: return Loc.T("正在识别工程", "Discovering project");
                case 2: return Loc.T("正在列出文件", "Listing files");
                case 3: return Loc.T("正在读取缓存", "Loading cache");
                case 4: return Loc.T("正在解析", "Parsing");
                case 5: return Loc.T("正在合并索引", "Merging index");
                case 6: return Loc.T("就绪", "Ready");
                case 7: return Loc.T("失败", "Failed");
                default: return Loc.T("未开始", "Not started");
            }
        }

        // ---- diagnostics / logs ----
        public static string NoLogYet => Loc.T("尚未生成日志。", "No log file yet.");
        public static string DiagnosticsCreated(string path) => Loc.F("诊断包已生成：{0}", "Diagnostics package created: {0}", path);
        public static string DiagnosticsFailed => Loc.T("生成诊断包失败（详见日志）。", "Creating the diagnostics package failed (see the log).");
        public static string NoUnrealSolution => Loc.T("当前不是 Unreal Engine 解决方案，UEToVS 未启动引擎。", "The open solution is not an Unreal Engine solution; the UEToVS engine is not running.");
        public static string GalleryNotAvailable => Loc.T("组件画廊不可用（UI 组件库未能加载，详见日志）。", "The component gallery is unavailable (the UI component library failed to load; see the log).");
        public static string ActionFailed(string title) => Loc.F("“{0}”执行失败（详见日志）。", "\"{0}\" failed (see the log).", title);

        // ---- options pages (page names are fixed English in ProvideOptionPage; content is localized) ----
        public static string CatLogging => Loc.T("日志", "Logging");
        public static string CatEngine => Loc.T("引擎", "Engine");
        public static string CatCppInsight => Loc.T("C++ 感知", "C++ Code Insight");

        public static string OptLogLevel => Loc.T("日志级别", "Log level");
        public static string OptLogLevelDesc => Loc.T("Error / Warn / Info / Debug / Trace。应用后立即对 VS 宿主和引擎进程生效。Debug 以上仅用于临时排查。",
                                                      "Error / Warn / Info / Debug / Trace. Applies immediately to the VS host and the engine process. Use Debug and above only for troubleshooting.");
        public static string OptEnginePath => Loc.T("引擎路径覆盖（可选）", "Engine path override (optional)");
        public static string OptEnginePathDesc => Loc.T("UEToVS.Engine.exe 的完整路径或所在文件夹，用于调试自己构建的引擎。留空使用扩展自带的 Engine\\UEToVS.Engine.exe。",
                                                        "Full path of UEToVS.Engine.exe or its folder, to debug your own engine build. Empty = the bundled Engine\\UEToVS.Engine.exe.");
        public static string OptUeRoot => Loc.T("Unreal Engine 根目录（可选）", "Unreal Engine root (optional)");
        public static string OptUeRootDesc => Loc.T("留空时由引擎根据 .uproject 的 EngineAssociation 自动查找。", "Empty = resolved by the engine from the .uproject EngineAssociation.");
        public static string OptTimeout => Loc.T("请求超时（秒）", "Request timeout (seconds)");
        public static string OptTimeoutDesc => Loc.T("单个引擎请求的最长等待时间。", "Maximum time to wait for a single engine request.");
        public static string OptTakeover => Loc.T("接管 VS 自带 C++ IntelliSense", "Take over Visual Studio's C++ IntelliSense");
        public static string OptTakeoverDesc => Loc.T("打开 UE 解决方案时关闭 VS 自带的 C++ IntelliSense（禁用 IntelliSense、自动快速信息、成员列表、错误波浪线、语义着色、外部依赖项等），由 UEToVS 提供 C++ 感知。修改前会备份原值，可随时用“恢复 VS 自带 C++ IntelliSense”还原。",
                                                      "When a UE solution opens, turn off Visual Studio's own C++ IntelliSense (IntelliSense, auto Quick Info, member list, squiggles, semantic colorization, external dependencies...) and let UEToVS provide C++ code insight. Original values are backed up; use \"Restore Visual Studio C++ IntelliSense\" at any time.");
        public static string OptTakeoverMode => Loc.T("接管范围", "Takeover scope");
        public static string OptTakeoverModeDesc => Loc.T("Global：插件启用期间始终关闭（默认）。UnrealSolutionOnly：仅在打开 UE 解决方案时关闭，关闭解决方案后自动恢复。",
                                                          "Global: off while UEToVS is enabled (default). UnrealSolutionOnly: off only while a UE solution is open, restored when it closes.");
        public static string OptDisableDb => Loc.T("同时禁用浏览数据库（Disable Database）", "Also disable the browsing database (Disable Database)");
        public static string OptDisableDbDesc => Loc.T("UE 工程上 .vc.db 占用大量磁盘与内存，UEToVS 自有索引覆盖其功能。依赖 VS“类视图”的用户可关闭此项。",
                                                       "On UE projects the .vc.db uses a lot of disk and memory and UEToVS's index covers it. Turn this off if you rely on VS Class View.");
        public static string OptDisableBgAnalysis => Loc.T("关闭 VS C++ 后台代码分析", "Turn off Visual Studio's C++ background code analysis");
        public static string OptDisableBgAnalysisDesc => Loc.T("打开 UE 解决方案时开启 VS 的“禁用后台代码分析”。VS 每次打开或保存 .cpp 都会用编译器完整分析整个编译单元，UE 源文件往往要几十秒（任务中心里的“C++ 后台 Code Analysis”）。修改前会备份原值，关闭此项即恢复。不影响 IntelliSense 接管，也不影响手动“分析 > 运行代码分析”。",
                                                             "When a UE solution opens, turn on Visual Studio's \"Disable Background Code Analysis\". On every open or save VS compiles the whole translation unit for analysis, which takes tens of seconds on UE sources (\"C++ Background Code Analysis\" in the task center). The original value is backed up and restored when you turn this off. Independent of the IntelliSense takeover; Analyze > Run Code Analysis still works.");

        public static string AppearancePlaceholder => Loc.T("外观设置（界面风格、字号、密度、圆角、强调色、徽章颜色等，见设计文档 §8.2.1）将由 UI 组件库提供。",
                                                            "Appearance settings (style, font scale, density, corner radius, accent and badge colors; design doc §8.2.1) are provided by the UI component library.");
        public static string AppearanceLoadFailed => Loc.T("外观设置加载失败，详见日志。", "Appearance settings failed to load; see the log.");
        public static string LoggingIntro => Loc.T("日志保存在 <解决方案目录>\\.vs\\UEToVS\\Logs\\（JSON Lines，host-*.log 为 VS 进程，engine-*.log 为引擎进程）。",
                                                   "Logs are stored in <solution dir>\\.vs\\UEToVS\\Logs\\ (JSON Lines; host-*.log = VS process, engine-*.log = engine process).");
        public static string LoggingCurrent(string dir) => Loc.F("当前：{0}", "Current: {0}", dir ?? Loc.T("（尚未打开 UE 解决方案）", "(no UE solution open yet)"));
        public static string LoggingRetentionDays => Loc.T("保留天数", "Days to keep");
        public static string LoggingMaxTotal => Loc.T("最大总大小 (MB)", "Maximum total size (MB)");

        // ---- performance panel text fallback (PerformanceToolWindow.PlaceholderContent) ----
        public static string PerfSampling => Loc.T("正在采样…", "Sampling...");
        public static string PerfPlaceholderTitle => Loc.T("UEToVS 性能（纯文本视图：UI 组件库的性能面板不可用，详见日志）",
                                                           "UEToVS Performance (text view: the UI library's performance panel is unavailable, see the log)");
        public static string PerfHostHeader => Loc.T("[VS 进程 devenv]", "[VS process devenv]");
        public static string PerfHostMemory(string priv, string delta, string ws) => Loc.F("  私有内存 {0}（自加载以来 {1}）  工作集 {2}", "  Private {0} (since load {1})  Working set {2}", priv, delta, ws);
        public static string PerfHostHeap(string heap, string gc, int threads) => Loc.F("  托管堆 {0}  GC {1}  线程 {2}", "  Managed heap {0}  GC {1}  Threads {2}", heap, gc, threads);
        public static string PerfEngineHeader(string state, string error) =>
            Loc.F("[引擎] 状态：{0}{1}", "[Engine] State: {0}{1}", state, string.IsNullOrEmpty(error) ? "" : Loc.F("（{0}）", " ({0})", error));
        public static string PerfEngineProcess(int pid, double uptimeSec, double cpu, double rttMs) =>
            Loc.F("  PID {0}  运行 {1:F0} s  CPU {2:F1}%  往返 {3:F1} ms", "  PID {0}  Uptime {1:F0} s  CPU {2:F1}%  Round trip {3:F1} ms", pid, uptimeSec, cpu, rttMs);
        public static string PerfEngineMemory(string priv, string ws, string heap, string disk) =>
            Loc.F("  私有内存 {0}  工作集 {1}  托管堆 {2}  索引磁盘 {3}", "  Private {0}  Working set {1}  Managed heap {2}  Index on disk {3}", priv, ws, heap, disk);
        public static string PerfIndex(string phase, int parsed, int toParse, int symbols) =>
            Loc.F("  索引：{0}  {1}/{2} 文件  {3:N0} 符号", "  Index: {0}  {1}/{2} files  {3:N0} symbols", phase, parsed, toParse, symbols);
        public static string PerfQueues(int interactive, int background, bool paused) =>
            Loc.F("  队列：交互 {0}  后台 {1}{2}", "  Queues: interactive {0}  background {1}{2}", interactive, background, paused ? Loc.T("（已暂停）", " (paused)") : "");
        public static string PerfPhasesHeader => Loc.T("  索引阶段：", "  Index phases:");
        public static string PerfIndexPartsHeader => Loc.T("  索引内存构成：", "  Index memory:");
        public static string PerfEngineOps => Loc.T("引擎操作延迟", "Engine operation latency");
        public static string PerfHostOps => Loc.T("VS 侧操作延迟", "VS-side operation latency");
        public static string PerfOpsColumns => Loc.T("次数 / 错误 / p50 / p95 / max (ms)", "count / errors / p50 / p95 / max (ms)");
        public static string PerfBudget(double ms) => Loc.F("预算 {0:F0}", "budget {0:F0}", ms);
        public static string PerfEngineNotStarted => Loc.T("引擎未启动", "Engine not started");
        public static string PerfEngineState(string state) => Loc.F("引擎状态：{0}", "Engine state: {0}", state);

        // ---- performance panel (UEToVS.UI PerformancePanel; mapping in Logic/Performance/PerformanceViewMapper.cs) ----
        /// <summary>EngineClient state name ("Connected", "Unavailable", …, "None") → localized; unknown names as-is.</summary>
        public static string EngineStateName(string state)
        {
            switch (state)
            {
                case "None": return Loc.T("未启动", "Not started");
                case "Stopped": return Loc.T("已停止", "Stopped");
                case "Starting": return Loc.T("正在启动", "Starting");
                case "Connected": return Loc.T("已连接", "Connected");
                case "Restarting": return Loc.T("正在重启", "Restarting");
                case "Unavailable": return Loc.T("不可用", "Unavailable");
                case "Incompatible": return Loc.T("版本不匹配", "Incompatible");
                case "NotInstalled": return Loc.T("未安装", "Not installed");
                default: return string.IsNullOrEmpty(state) ? Loc.T("未知", "Unknown") : state;
            }
        }

        public static string PerfViewEngineUnavailable(string state, string error) =>
            string.IsNullOrEmpty(error)
                ? Loc.F("引擎不可用（{0}），以下仅显示 VS 侧数据。", "Engine unavailable ({0}); only VS-side data is shown.", state)
                : Loc.F("引擎不可用（{0}）：{1}。以下仅显示 VS 侧数据。", "Engine unavailable ({0}): {1}. Only VS-side data is shown.", state, error);
        public static string PerfViewEngineMemoryDetail(string workingSet, string heap) => Loc.F("工作集 {0} · 托管堆 {1}", "working set {0} · heap {1}", workingSet, heap);
        public static string PerfViewHostMemoryDetail(string delta, string heap) => Loc.F("自加载 {0} · 托管堆 {1}", "since load {0} · heap {1}", delta, heap);
        public static string PerfViewIndexProgress(int parsed, int toParse, int symbols) =>
            Loc.F("{0:N0}/{1:N0} 文件 · {2:N0} 符号", "{0:N0}/{1:N0} files · {2:N0} symbols", parsed, toParse, symbols);
        public static string PerfViewIndexSummary(int symbols, int files) => Loc.F("{0:N0} 符号 · {1:N0} 文件", "{0:N0} symbols · {1:N0} files", symbols, files);
        public static string PerfViewQueues(int interactive, int background, bool paused) =>
            Loc.F("排队：交互 {0} · 后台 {1}{2}", "Queued: interactive {0} · background {1}{2}", interactive, background, paused ? Loc.T("（后台索引已暂停）", " (background indexing paused)") : "");
        public static string PerfViewOpEngine(string name) => Loc.F("引擎 · {0}", "Engine · {0}", name);
        public static string PerfViewOpHost(string name) => Loc.F("VS · {0}", "VS · {0}", name);
    }
}
