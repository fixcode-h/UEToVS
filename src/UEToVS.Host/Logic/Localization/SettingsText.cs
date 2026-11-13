using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>User-visible strings of the settings window, keymap page, toolbar and utility windows (v0.3.0).</summary>
    public static class SettingsText
    {
        public static string WindowTitle => Loc.T("UEToVS 设置", "UEToVS Settings");
        public static string SearchPlaceholder => Loc.T("搜索设置", "Search settings");
        public static string Apply => Loc.T("应用", "Apply");
        public static string Ok => Loc.T("确定", "OK");
        public static string Cancel => Loc.T("取消", "Cancel");
        public static string ResetDefaults => Loc.T("恢复默认", "Restore defaults");
        public static string ResetConfirm => Loc.T("已把本页恢复为默认值（点击“应用”或“确定”后生效）。", "This page was reset to defaults (takes effect on Apply / OK).");
        public static string ResetPage => Loc.T("恢复本页默认", "Reset this page");
        public static string PendingChanges(int n) => Loc.F("● {0} 项未应用", "● {0} unapplied", n);

        /// <summary>One-line description under each settings page title.</summary>
        public static string PageDescription(string categoryId) => categoryId switch
        {
            "general" => Loc.T("引擎、IntelliSense 接管、工具栏和快捷操作。", "Engine, IntelliSense takeover, toolbar and shortcuts."),
            "appearance" => Loc.T("颜色、圆角、密度和字体。修改即时预览，点击“应用”后保存。", "Colors, corners, density and fonts. Changes preview live and are saved on Apply."),
            "indexing" => Loc.T("C++、蓝图和 Lua 索引的范围、线程数和存放位置。", "Scope, threads and location of the C++, Blueprint and Lua indexes."),
            "keymap" => Loc.T("UEToVS 命令的快捷键；和 VS 冲突时 UEToVS 优先（原绑定已备份）。", "Shortcuts of UEToVS commands; UEToVS wins conflicts with VS (originals backed up)."),
            "navigation" => Loc.T("跳转、搜索和引用高亮。", "Go to, search and reference highlighting."),
            "intellisense" => Loc.T("补全、悬停信息和语义着色。", "Completion, Quick Info and semantic coloring."),
            "agent" => Loc.T("配置 OpenAI 兼容（Chat Completions）的 API 和密钥，从接口获取模型；勾选的模型会出现在 VS 自带的 Copilot 聊天里。另有给 AI Agent 用的 MCP 服务。", "OpenAI-compatible (Chat Completions) APIs and keys; enabled models show up in Visual Studio's Copilot Chat. Also the MCP server for AI agents."),
            "logging" => Loc.T("日志级别、保留时间和位置（JSON 行格式，方便分析问题）。", "Log level, retention and location (JSON lines, easy to analyse)."),
            "performance" => Loc.T("性能面板和采样。", "Performance panel and sampling."),
            "updates" => Loc.T("查看当前版本，检查并安装 GitHub 上的新版本。", "Current version; check for and install new releases from GitHub."),
            _ => null,
        };
        public static string NoMatches(string q) => Loc.F("没有与“{0}”匹配的设置", "No settings match “{0}”", q);
        public static string Unsaved => Loc.T("有未应用的修改", "Unapplied changes");
        public static string Saved => Loc.T("已应用", "Applied");
        public static string Browse => Loc.T("浏览…", "Browse…");
        public static string Run => Loc.T("执行", "Run");
        public static string RestartHint => Loc.T("重启 VS 后生效", "Takes effect after restarting VS");
        public static string ActionUnavailable => Loc.T("该功能尚未就绪（由其他模块提供）。", "Not available yet (provided by another module).");
        public static string Done => Loc.T("已完成", "Done");
        public static string CmdRestoreVsDefaults => Loc.T("恢复 VS 原样（禁用 / 卸载前）", "Restore Visual Studio (before disabling / uninstalling)");
        public static string RestoreVsDone(int keymapCommands) => Loc.T(
            $"已恢复：C++ IntelliSense、浏览数据库、语义索引、后台代码分析、蓝图支持、VS 字体，以及 {keymapCommands} 个命令的快捷键。接管范围已设为“关闭”，现在可以禁用或卸载 UEToVS。",
            $"Restored: C++ IntelliSense, browse database, semantic index, background code analysis, Blueprint support, VS fonts and the shortcuts of {keymapCommands} commands. The takeover scope is now Off; UEToVS can be disabled or uninstalled.");
        public static string RestoreVsPartly(string failed) => Loc.T(
            $"部分设置没能恢复：{failed}。详见日志 vs.restoreAll。", $"Some settings could not be restored: {failed}. See the log (vs.restoreAll).");
        public static string Failed => Loc.T("失败，详见日志", "Failed — see the log");
        public static string Requested => Loc.T("已请求，引擎正在后台处理", "Requested; the engine works on it in the background");
        public static string EngineNotConnected => Loc.T("引擎未连接（需要先打开 UE 解决方案）", "Engine not connected (open a UE solution first)");
        public static string OptionsMovedIntro => Loc.T(
            "UEToVS 的所有设置已移到独立的“UEToVS 设置”窗口（扩展 > UEToVS > 设置…，或工具栏最左侧的 UEToVS 图标）。",
            "All UEToVS settings moved to the separate “UEToVS Settings” window (Extensions > UEToVS > Settings…, or the UEToVS icon at the left of the UEToVS toolbar).");
        public static string OpenUEToVSSettings => Loc.T("打开 UEToVS 设置", "Open UEToVS Settings");

        // ---- info rows
        public static string IndexStatusNone => Loc.T("没有打开 UE 解决方案", "No UE solution open");
        public static string IndexStatus(string phase, int parsed, int toParse, int symbols, int assets) =>
            Loc.F("{0} · {1:N0}/{2:N0} 文件 · {3:N0} 符号 · {4:N0} 资产", "{0} · {1:N0}/{2:N0} files · {3:N0} symbols · {4:N0} assets", phase, parsed, toParse, symbols, assets);
        public static string LuaIndexDisabled => Loc.T("未设置 Lua 脚本路径，Content/Script 下也没有 .lua 文件", "No Lua script folder set, and no .lua files under Content/Script");
        public static string LuaDefaultFolderNote => Loc.T("（未设置路径，自动使用 Content/Script）", " (no folder set: Content/Script is used automatically)");
        public static string LuaIndexStatus(string state, int files, int symbols, string roots) =>
            Loc.F("{0} · {1:N0} 个 Lua 文件 · {2:N0} 符号 · {3}", "{0} · {1:N0} Lua files · {2:N0} symbols · {3}", state, files, symbols, roots);
        public static string LuaStateName(int state) => state switch
        {
            1 => Loc.T("正在索引", "Indexing"),
            2 => Loc.T("就绪", "Ready"),
            3 => Loc.T("失败", "Failed"),
            _ => Loc.T("未启用", "Off"),
        };
        public static string IndexThreadsEffective(int cpp, int bp, int cores) =>
            Loc.F("当前生效：C++ {0} 线程，蓝图 {1} 线程（本机 {2} 个逻辑核心）", "In effect: C++ {0} threads, Blueprint {1} threads ({2} logical cores)", cpp, bp, cores);
        public static string IntelliSenseTakenOver => Loc.T("已接管（VS 自带 C++ IntelliSense 已关闭，原设置已备份）", "Taken over (VS C++ IntelliSense off; originals backed up)");
        public static string IntelliSenseNotTakenOver => Loc.T("未接管（VS 自带 C++ IntelliSense 正常工作）", "Not taken over (VS C++ IntelliSense active)");
        public static string LogDirectory(string dir) => dir ?? Loc.T("（尚未打开 UE 解决方案）", "(no UE solution open yet)");
        public static string VsUnrealIntegration => Loc.T(
            "状态栏的“正在从 X.uproject 中扫描 Blueprint”、UHT 代码分析和全代码库语义索引都来自 VS 自带的功能，不是 UEToVS；UEToVS 只列出资产文件名，不读取 .uasset。" +
            "接管时（接管范围不是“关闭”）下面勾选的项会被关掉：修改前备份，取消勾选或范围选“关闭”即恢复。其中几项保存在 VS 18 的 settings.json 里，部分要下次启动 VS 才生效。",
            "The status bar message “Scanning Blueprints from X.uproject”, UHT code analysis and the whole-codebase semantic index all come from VS itself, not from UEToVS; UEToVS only lists asset file names and never reads .uasset files. " +
            "While taken over (scope not “Off”) the items ticked below are turned off: backed up first, and unticking them or scope “Off” restores them. Some live in VS 18's settings.json and some take effect from the next VS start.");
        public static string VsSettingsJsonFound(string path) => Loc.F("VS 设置文件：{0}", "VS settings file: {0}", path);
        public static string VsSettingsJsonMissing(string path) => Loc.F(
            "没有找到 VS 设置文件（{0}），只保存在 settings.json 里的项无法关闭（VS 2022 及更早版本没有这个文件）。",
            "VS settings file not found ({0}); items stored only in settings.json cannot be turned off (VS 2022 and earlier have no such file).", path);
        // ---- 检查更新 (Logic/Update/UpdateState.cs renders these)
        public static string UpdateButtonCheck => Loc.T("检查", "Check");
        public static string UpdateButtonChecking => Loc.T("检查中…", "Checking…");
        public static string UpdateButtonInstall => Loc.T("下载并安装", "Download and install");
        public static string UpdateButtonRetry => Loc.T("重试", "Retry");
        public static string UpdateReleasePage => Loc.T("发布页", "Release page");
        public static string UpdateNotesTitle(string v) => Loc.F("{0} 更新说明", "What's new in {0}", v);
        public static string UpdateNotesBeforeCheck => Loc.T("检查更新后，这里显示最新版本的更新说明。", "Check for updates to see the latest release notes here.");
        public static string UpdateNotesEmpty => Loc.T("这个版本没有附带更新说明。", "This release has no notes.");
        public static string UpdateCurrentVersion(string v) => Loc.F("当前版本 {0}", "Current version {0}", v);
        public static string UpdateChecking => Loc.T("正在连接 GitHub…", "Contacting GitHub…");
        public static string UpdateUpToDate(string v) => Loc.F("已是最新版本（{0}）", "You have the latest version ({0})", v);
        public static string UpdateAvailable(string latest, string installed, string size) => Loc.F(
            "发现新版本 {0}（当前 {1}），安装包 {2}", "{0} is available (installed: {1}), download {2}", latest, installed, size);
        public static string UpdateDownloading(string v, string progress) => Loc.F("正在下载 {0}：{1}", "Downloading {0}: {1}", v, progress);
        public static string UpdateDownloadCanceled => Loc.T("已取消下载。", "Download canceled.");
        public static string UpdateInstallerStarted(string v) => Loc.F(
            "已下载 {0} 并启动安装程序：请关闭 Visual Studio，安装完成后重新打开。", "Downloaded {0} and started the installer: close Visual Studio, then reopen it after the install finishes.", v);
        public static string UpdateCheckFailed(string why) => Loc.F("检查更新失败：{0}", "Update check failed: {0}", why);
        public static string UpdateDownloadFailedWith(string why) => Loc.F(
            "下载失败：{0}。可以重试，或点“发布页”手动下载。", "Download failed: {0}. Retry, or use “Release page” to download it by hand.", why);
        public static string UpdateDownloadIncomplete => Loc.T("下载的安装包不完整", "the downloaded package is incomplete");
        public static string UpdateDownloadStalled(int seconds) => Loc.F("{0} 秒没有收到数据", "no data for {0} seconds", seconds);
        public static string UpdateConnectTimeout => Loc.T("连接 GitHub 超时，请检查网络或代理", "timed out contacting GitHub; check your network or proxy");
        public static string UpdateRateLimited => Loc.T("GitHub 接口访问次数超限（每小时 60 次），请稍后再试", "GitHub API rate limit reached (60 per hour); try again later");
        public static string UpdateNoRelease => Loc.T("GitHub 上还没有发布版本", "no release has been published on GitHub");
        public static string UpdateNoVsix => Loc.T("最新发布里没有 VSIX 安装包", "the latest release has no VSIX package");
        public static string UpdateHttpStatus(int code) => Loc.F("GitHub 返回 HTTP {0}", "GitHub answered HTTP {0}", code);
        public static string UpdateNetwork(string message) => Loc.F("无法连接 GitHub（{0}）", "cannot reach GitHub ({0})", message);

        // ---- keymap page
        public static string KeymapIntro => Loc.T(
            "UEToVS 命令的快捷键。与 VS 自带快捷键冲突时以 UEToVS 为准（自动移除 VS 命令上的相同按键）。修改会写入 VS 的键盘设置；每次修改前自动备份，可一键还原。",
            "Shortcuts of the UEToVS commands. When a key is also used by a VS command, UEToVS wins (the key is removed from the VS command). Changes go into VS's keyboard settings; every change is backed up first and can be restored.");
        public static string KeymapPreset => Loc.T("套用预设", "Apply preset");
        public static string KeymapPresetVa => Loc.T("Visual Assist", "Visual Assist");
        public static string KeymapPresetRider => Loc.T("Rider", "Rider");
        public static string KeymapPresetVs => Loc.T("VS 默认", "VS default");
        public static string KeymapColumnCommand => Loc.T("命令", "Command");
        public static string KeymapColumnShortcut => Loc.T("快捷键", "Shortcut");
        public static string KeymapPressKeys => Loc.T("按下新的快捷键…（Esc 取消，Backspace 清除）", "Press the new shortcut… (Esc cancels, Backspace clears)");
        public static string KeymapNone => Loc.T("（无）", "(none)");
        public static string KeymapReserved => Loc.T("（不可用）", "(unavailable)");
        public static string KeymapRestore => Loc.T("还原上次备份", "Restore last backup");
        public static string KeymapRemoveConflicts => Loc.T("解除冲突（移除其他命令的相同快捷键）", "Resolve conflicts (remove the same keys from other commands)");
        public static string KeymapLoading => Loc.T("正在读取 VS 键盘设置…", "Reading VS keyboard settings…");
        public static string KeymapNoConflicts => Loc.T("没有冲突", "No conflicts");
        public static string KeymapConflicts(int n) => Loc.F("{0} 个冲突", "{0} conflicts", n);
        public static string KeymapConflictLine(string ours, string keys, string other) =>
            Loc.F("{0}：{1} 也绑定在 {2}", "{0}: {1} is also bound to {2}", ours, keys, other);
        public static string KeymapApplied(int n, string backup) => Loc.F("已修改 {0} 个命令（备份：{1}）", "Changed {0} commands (backup: {1})", n, backup);
        public static string KeymapNoBackup => Loc.T("没有可还原的备份", "No backup to restore");
        public static string KeymapDoubleShift => Loc.T("Search Everywhere 还可以双击 Shift 打开（见“常规 > 键盘”）。", "Search Everywhere also opens with a double Shift (see General > Keyboard).");
        public static string KeymapInvalid => Loc.T("无效的快捷键", "Invalid shortcut");

        // ---- windows
        public static string PerformanceWindowTitle => Loc.T("UEToVS 性能", "UEToVS Performance");

        // ---- v0.3.1: page errors, index status card, auto numbers
        public static string PageFailed(string error) => string.IsNullOrWhiteSpace(error)
            ? Loc.T("这个页面加载失败。", "This page failed to load.")
            : Loc.F("这个页面加载失败：{0}", "This page failed to load: {0}", error.Trim());
        public static string Retry => Loc.T("重试", "Retry");
        public static string ViewLog => Loc.T("查看日志", "View log");
        public static string Rebuild => Loc.T("重建", "Rebuild");
        public static string IndexFailedTitle => Loc.T("索引失败", "Indexing failed");
        public static string IndexNoReason => Loc.T("引擎没有给出原因，请查看日志。", "The engine gave no reason; see the log.");
        public static string IndexEngineUnavailable(string state) => Loc.F("引擎{0}，索引不可用", "Engine {0}; indexing is unavailable", state);
        public static string IndexCounts(int parsed, int toParse, int symbols, int assets) =>
            Loc.F("{0:N0} / {1:N0} 文件 · {2:N0} 符号 · {3:N0} 资产", "{0:N0} / {1:N0} files · {2:N0} symbols · {3:N0} assets", parsed, toParse, symbols, assets);
        public static string Auto => Loc.T("自动", "Auto");
    }
}
