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
        public static string NoMatches(string q) => Loc.F("没有与“{0}”匹配的设置", "No settings match “{0}”", q);
        public static string Unsaved => Loc.T("有未应用的修改", "Unapplied changes");
        public static string Saved => Loc.T("已应用", "Applied");
        public static string Browse => Loc.T("浏览…", "Browse…");
        public static string Run => Loc.T("执行", "Run");
        public static string RestartHint => Loc.T("重启 VS 后生效", "Takes effect after restarting VS");
        public static string ActionUnavailable => Loc.T("该功能尚未就绪（由其他模块提供）。", "Not available yet (provided by another module).");
        public static string Done => Loc.T("已完成", "Done");
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
        public static string LuaIndexDisabled => Loc.T("未设置 Lua 脚本路径", "No Lua script folder set");
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
            "每次打开解决方案时状态栏的“正在从 X.uproject 中扫描 Blueprint”来自 VS 自带的 Unreal Engine 集成，不是 UEToVS（UEToVS 只列出资产文件名，不读取 .uasset 内容）。" +
            "关闭方法：VS 菜单 项目 > Configure Tools for Unreal Engine，在打开的页面里关掉“Blueprint Support”，然后重启 VS。" +
            "关掉后 VS 的 Code Lens 蓝图引用也会停用；资产搜索、跳转到资产仍由 UEToVS 提供。微软没有公开这个开关的存储位置，所以 UEToVS 不自动修改它。",
            "The status bar message “Scanning Blueprints from X.uproject” on every solution load comes from VS's own Unreal Engine integration, not from UEToVS (UEToVS only lists asset file names and never reads .uasset contents). " +
            "To turn it off: in VS choose Project > Configure Tools for Unreal Engine, switch off “Blueprint Support” on that page, then restart VS. " +
            "This also turns off VS's Code Lens Blueprint references; asset search and go-to-asset keep working through UEToVS. Microsoft does not document where this switch is stored, so UEToVS does not change it for you.");

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
