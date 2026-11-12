using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>v0.4.5 UE editor link texts: UE log window, automation test runner, Live Coding errors, UEToVSLink card.</summary>
    public static class LinkStrings
    {
        // ---- commands
        public static string CmdLogWindow => Loc.T("UE 日志窗口", "Unreal Log Window");
        public static string CmdTestRunner => Loc.T("UE 自动化测试", "Unreal Automation Tests");
        public static readonly string[] KwLog = { "log", "日志", "output log", "UE_LOG", "PIE", "unreal", "编辑器" };
        public static readonly string[] KwTests = { "test", "测试", "automation", "自动化", "spec", "RunTests", "unreal" };

        // ---- connection
        public static string StateConnected => Loc.T("已连接 UE 编辑器", "Connected to the Unreal Editor");
        public static string StateWaiting => Loc.T("等待 UE 编辑器（未运行或未启用 UEToVSLink）", "Waiting for the Unreal Editor (not running or UEToVSLink not enabled)");
        public static string StateConnecting => Loc.T("正在连接 UE 编辑器…", "Connecting to the Unreal Editor…");
        public static string StateTooOld => Loc.T("编辑器里的 UEToVSLink 版本太旧，不支持日志 / 测试，请更新插件", "The UEToVSLink plugin in the editor is too old for logs / tests; update it");
        public static string StateIdle => Loc.T("未连接", "Not connected");
        public static string UpdatePlugin => Loc.T("更新插件", "Update plugin");
        public static string InstallPlugin => Loc.T("安装插件", "Install plugin");

        // ---- log window
        public static string LogWindowTitle => Loc.T("UE 日志", "Unreal Log");
        public static string LogSearch => Loc.T("搜索日志（多个词同时匹配）", "Search the log (all words must match)");
        public static string AllCategories => Loc.T("全部分类", "All categories");
        public static string LevelError => Loc.T("错误", "Error");
        public static string LevelWarning => Loc.T("警告", "Warning");
        public static string LevelDisplay => "Display";
        public static string LevelLog => "Log";
        public static string LevelVerbose => "Verbose";
        public static string AutoScroll => Loc.T("自动滚动", "Auto-scroll");
        public static string Clear => Loc.T("清空", "Clear");
        public static string CopyLine => Loc.T("复制", "Copy");
        public static string LogEmpty => Loc.T("还没有日志", "No log lines yet");
        public static string LogEmptyHint => Loc.T("在 UE 编辑器里启用 UEToVSLink 插件后，编辑器和 PIE 的日志会实时显示在这里。", "With the UEToVSLink plugin enabled in the Unreal Editor, editor and PIE log lines show up here live.");
        public static string LogNoMatch => Loc.T("没有符合过滤条件的日志", "No log lines match the filter");
        public static string HintJump => Loc.T("跳到源码", "Go to source");
        public static string LogCount(int shown, int total) => Loc.F("{0} / {1} 行", "{0} / {1} lines", shown, total);
        public static string LogPaused(long pending) => Loc.F("已暂停（{0} 行新日志）", "Paused ({0} new lines)", pending);
        public static string LogDropped(long dropped) => Loc.F("编辑器限流丢弃了 {0} 行", "{0} lines dropped by the editor's rate limit", dropped);
        public static string SourceSearching => Loc.T("正在查找输出这行日志的 UE_LOG…", "Looking for the UE_LOG that wrote this line…");
        public static string SourceNotFound(string category) => Loc.F("没有找到能精确匹配这行日志的 UE_LOG（分类 {0}）", "No UE_LOG matches this line exactly (category {0})", category);
        public static string SourceNoSites(string category) => Loc.F("项目源码里没有使用分类 {0} 的 UE_LOG", "No UE_LOG with category {0} in the project sources", category);
        public static string SourceAmbiguous => Loc.T("只有 \"%s\" 这类没有固定文字的格式能匹配，无法确定是哪一处", "Only formats without fixed text (\"%s\") match; cannot tell which one");
        public static string SourcePickTitle => Loc.T("选择 UE_LOG", "Choose the UE_LOG");
        public static string EngineUnavailable => Loc.T("UEToVS 引擎未就绪，稍后再试", "The UEToVS engine is not ready; try again shortly");

        // ---- test runner
        public static string TestsWindowTitle => Loc.T("UE 自动化测试", "Unreal Automation Tests");
        public static string TestsSearch => Loc.T("过滤测试（名称或类名）", "Filter tests (name or class)");
        public static string RunSelected => Loc.T("运行选中", "Run selected");
        public static string RunFailed => Loc.T("重跑失败的", "Rerun failed");
        public static string Refresh => Loc.T("刷新", "Refresh");
        public static string GoToSource => Loc.T("转到源码", "Go to source");
        public static string TestsScanning => Loc.T("正在扫描项目源码里的测试…", "Scanning the project sources for tests…");
        public static string TestsNone => Loc.T("项目源码里没有找到自动化测试", "No automation tests found in the project sources");
        public static string TestsNoneHint => Loc.T("识别 IMPLEMENT_SIMPLE_AUTOMATION_TEST / IMPLEMENT_COMPLEX_AUTOMATION_TEST / BEGIN_DEFINE_SPEC / DEFINE_SPEC。", "Recognizes IMPLEMENT_SIMPLE_AUTOMATION_TEST / IMPLEMENT_COMPLEX_AUTOMATION_TEST / BEGIN_DEFINE_SPEC / DEFINE_SPEC.");
        public static string TestsFound(int tests, int files) => Loc.F("{0} 个测试（扫描了 {1} 个文件）", "{0} tests ({1} files scanned)", tests, files);
        public static string TestsEditorOffline => Loc.T("UE 编辑器没有连接：请先启动编辑器并启用 UEToVSLink 插件，再运行测试", "The Unreal Editor is not connected: start it with the UEToVSLink plugin enabled, then run the tests");
        public static string TestsPluginTooOld => Loc.T("编辑器里的 UEToVSLink 不支持运行测试（版本太旧），请更新插件后重启编辑器", "The UEToVSLink plugin in the editor cannot run tests (too old); update it and restart the editor");
        public static string TestsBusy => Loc.T("编辑器正在运行另一批测试", "The editor is already running tests");
        public static string TestsRunning(string filter) => Loc.F("正在运行 {0}…", "Running {0}…", filter);
        public static string TestsRunFailed(string message) => Loc.F("编辑器拒绝运行测试：{0}", "The editor refused to run the tests: {0}", message);
        public static string TestsSummary(int passed, int failed, int total) => Loc.F("通过 {0} · 失败 {1} · 共 {2}", "Passed {0} · Failed {1} · Total {2}", passed, failed, total);
        public static string TestsRunDone(int passed, int failed) => Loc.F("测试完成：通过 {0}，失败 {1}", "Tests finished: {0} passed, {1} failed", passed, failed);
        public static string TestsRunMessage(string message) => Loc.F("测试结束：{0}", "Tests finished: {0}", message);
        public static string TestsTimeout => Loc.T("等待测试结果超时（编辑器没有回报结束）", "Timed out waiting for test results (the editor did not report the end)");
        public static string TestsDetailsTitle => Loc.T("详情", "Details");
        public static string TestsNoDetails => Loc.T("选中一个测试查看结果和日志；双击或 Enter 运行，F4 转到源码", "Select a test to see its result and log; double-click or Enter runs it, F4 goes to the source");
        public static string StateNotRun => Loc.T("未运行", "Not run");
        public static string StateQueued => Loc.T("排队", "Queued");
        public static string StateRunning => Loc.T("运行中", "Running");
        public static string StatePassed => Loc.T("通过", "Passed");
        public static string StateFailed => Loc.T("失败", "Failed");
        public static string StateSkipped => Loc.T("跳过", "Skipped");
        public static string Duration(double ms) => ms >= 1000 ? (ms / 1000).ToString("0.0#") + " s" : ms.ToString("0") + " ms";
        public static string ErrorsHeader => Loc.T("错误", "Errors");
        public static string WarningsHeader => Loc.T("警告", "Warnings");
        public static string LogsHeader => Loc.T("日志", "Log");

        // ---- Live Coding
        public static string LiveCodingCompiling => Loc.T("Live Coding 正在编译…", "Live Coding is compiling…");
        public static string LiveCodingSucceeded => Loc.T("Live Coding 编译成功", "Live Coding succeeded");
        public static string LiveCodingNoChanges => Loc.T("Live Coding：没有需要编译的改动", "Live Coding: nothing to compile");
        public static string LiveCodingFailedErrors(int errors) => Loc.F("Live Coding 编译失败：{0} 个错误", "Live Coding failed: {0} errors", errors);
        public static string LiveCodingFailed => Loc.T("Live Coding 编译失败（详见 Live Coding 控制台）", "Live Coding failed (see the Live Coding console)");
        public static string LiveCodingFirstError => Loc.T("转到第一个错误", "Go to first error");
        public static string LiveCodingShowOutput => Loc.T("查看输出", "Show output");

        // ---- related items (log categories)
        public static string CategoryDeclaration => Loc.T("声明", "Declaration");
        public static string CategoryDefinition => Loc.T("定义", "Definition");
        public static string CategoryUsage => Loc.T("使用", "Usage");

        // ---- UEToVSLink card (settings)
        public static string PluginCardTitle => "UEToVSLink";
        public static string PluginNotInstalled => Loc.T("未安装", "Not installed");
        public static string PluginNoProject => Loc.T("没有 .uproject（引擎解决方案），无法安装到项目", "No .uproject (engine solution): cannot install into a project");
        public static string PluginInstalledAt(string where, string version) => Loc.F("已安装 {1}（{0}）", "Installed {1} ({0})", where, version);
        public static string PluginLocationProject => Loc.T("项目 Plugins", "project Plugins");
        public static string PluginLocationEngine => Loc.T("引擎 Plugins", "engine Plugins");
        public static string PluginLocationMarketplace => Loc.T("引擎 Plugins/Marketplace", "engine Plugins/Marketplace");
        public static string PluginBundled(string version) => Loc.F("扩展自带版本 {0}", "Bundled with the extension: {0}", version);
        public static string PluginUpdateAvailable => Loc.T("有更新：安装的版本比扩展自带的旧", "Update available: the installed version is older than the bundled one");
        public static string PluginNewer => Loc.T("安装的版本比扩展自带的新", "The installed version is newer than the bundled one");
        public static string PluginEditorOnline(string info) => Loc.F("编辑器在线（{0}）", "Editor online ({0})", info);
        public static string PluginEditorOffline => Loc.T("编辑器不在线", "Editor offline");
        public static string PluginInstall => Loc.T("安装", "Install");
        public static string PluginUpdate => Loc.T("更新", "Update");
        public static string PluginUninstall => Loc.T("卸载", "Uninstall");
        public static string PluginUninstallConfirm(string dir) => Loc.F("删除 UEToVSLink 插件目录（含 Binaries / Intermediate）并从 .uproject 移除它？\n\n{0}", "Delete the UEToVSLink plugin folder (including Binaries / Intermediate) and remove it from the .uproject?\n\n{0}", dir);
        public static string PluginLocked(string file) => Loc.F("插件文件正在被占用（{0}）：请先关闭 UE 编辑器", "A plugin file is in use ({0}): close the Unreal Editor first", file);
        public static string PluginInstalledDone(string target) => Loc.F("UEToVSLink 已安装到 {0}，重新打开 UE 编辑器后生效（首次会编译插件）", "UEToVSLink installed to {0}; reopen the Unreal Editor to load it (it compiles the plugin the first time)", target);
        public static string PluginUninstalledDone => Loc.T("UEToVSLink 已卸载", "UEToVSLink uninstalled");
        public static string PluginOperationFailed(string detail) => Loc.F("操作失败：{0}", "Operation failed: {0}", detail);
        public static string PluginMissingSource(string dir) => Loc.F("扩展里缺少插件源码：{0}", "The extension is missing the plugin sources: {0}", dir);
    }
}
