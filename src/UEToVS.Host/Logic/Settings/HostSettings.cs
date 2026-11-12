using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Logic.Settings
{
    public enum IntelliSenseTakeoverMode
    {
        /// <summary>VS's C++ IntelliSense stays off while UEToVS is installed and enabled (default, §7.5).</summary>
        Global = 0,

        /// <summary>Off while a UE solution is open; restored when it closes or a non-UE solution is opened.</summary>
        UnrealSolutionOnly = 1,
    }

    /// <summary>What background indexing does while VS/UBT builds (maps to IndexingSettings.DuringBuild).</summary>
    public enum IndexDuringBuild
    {
        Pause = 0,
        SingleThread = 1,
        FullSpeed = 2,
    }

    /// <summary>
    /// Effective host settings. Layers (§10.3): built-in defaults (the initializers below) &lt; user settings (Tools &gt;
    /// Options &gt; UEToVS, persisted in the VS settings store) &lt; solution settings (&lt;solution&gt;/.uetovs/settings.json,
    /// read-only for now, see <see cref="SettingsMerger"/>).
    /// </summary>
    public sealed class HostSettings
    {
        // ---- 常规 ----
        public LogLevel LogLevel { get; set; } = LogLevel.Info;

        /// <summary>Full path of UEToVS.Engine.exe (or its folder) to use instead of the bundled one. Empty = bundled.</summary>
        public string EnginePathOverride { get; set; } = string.Empty;

        /// <summary>Unreal Engine root override passed to the engine in the handshake. Empty = auto (EngineAssociation).</summary>
        public string UnrealEngineRootOverride { get; set; } = string.Empty;

        public int EngineRequestTimeoutSeconds { get; set; } = 30;

        public bool IntelliSenseTakeover { get; set; } = false; // ADR-08: default on only once our own IntelliSense (M3) is complete

        public IntelliSenseTakeoverMode IntelliSenseTakeoverMode { get; set; } = IntelliSenseTakeoverMode.Global;

        public bool DisableDatabase { get; set; } = true;

        /// <summary>Turn off VS's C++ background code analysis while a UE solution is open (off by default; independent of the takeover).</summary>
        public bool DisableBackgroundCodeAnalysis { get; set; } = false;

        // ---- 日志 ----
        public int LogRetentionDays { get; set; } = 7;

        public int LogMaxTotalMB { get; set; } = 200;

        // ---- M1 原型功能（Search Everywhere / 导航 / 书签 / Build.cs / UE 编辑器联动）----

        /// <summary>Double Shift anywhere in VS opens Search Everywhere.</summary>
        public bool DoubleShiftEnabled { get; set; } = true;

        /// <summary>Maximum interval between the two Shift presses (ms, ≥ 150).</summary>
        public int DoubleShiftIntervalMs { get; set; } = 300;

        public bool DoubleShiftLeftOnly { get; set; } = false;

        /// <summary>Results per category on a single tab (the "All" tab shows 8 per group).</summary>
        public int SearchMaxResultsPerCategory { get; set; } = 60;

        /// <summary>Mouse XButton1/XButton2 navigate back/forward through UEToVS's history.</summary>
        public bool MouseButtonsNavigate { get; set; } = true;

        /// <summary>A caret move within one file is recorded in Back history only beyond this many lines.</summary>
        public int NavigationLineThreshold { get; set; } = 15;

        /// <summary>Back history capacity.</summary>
        public int NavigationHistoryCapacity { get; set; } = 100;

        /// <summary>On save of a .h/.cpp: check #include modules against Build.cs and offer to add missing dependencies.</summary>
        public bool CheckIncludesOnSave { get; set; } = true;

        /// <summary>UEToVSLink editor plugin TCP port; 0 = read &lt;Project&gt;/Saved/UEToVSLink/port.txt.</summary>
        public int EditorLinkPort { get; set; } = 0;

        // ---- 导航（M2）----
        /// <summary>Ctrl+left-click on an identifier = go to definition (hover with Ctrl underlines it).</summary>
        public bool CtrlClickNavigation { get; set; } = true;

        /// <summary>Highlight the other occurrences of the symbol under the caret (C++ only).</summary>
        public bool ReferenceHighlighting { get; set; } = true;

        /// <summary>"#RRGGBB" / "#AARRGGBB" background of read occurrences; empty = theme default (§7.7).</summary>
        public string ReferenceHighlightReadColor { get; set; } = string.Empty;

        /// <summary>"#RRGGBB" / "#AARRGGBB" background of write occurrences; empty = theme default (§7.7).</summary>
        public string ReferenceHighlightWriteColor { get; set; } = string.Empty;

        /// <summary>Navigation bar (current type / current function) at the top of C++ editors.</summary>
        public bool NavigationBar { get; set; } = true;
        // ---- MCP（给 AI Agent 用） ----

        /// <summary>
        /// 启用 MCP 服务：打开 UE 解决方案时把 uetovs MCP 服务器登记到 &lt;解决方案&gt;\.vs\mcp.json（VS Copilot Agent 自动发现），
        /// 关闭时移除。文案见 McpStrings.SettingEnableMcp / SettingEnableMcpDescription；docs/design/MCP.md。
        /// </summary>
        public bool EnableMcpServer { get; set; } = true;

        // ---- 索引（设置窗口；0 = 默认 = max(1, 逻辑核心数 / 4)）----
        public int CppIndexThreads { get; set; } = 0;

        public int BlueprintIndexThreads { get; set; } = 0;

        public IndexDuringBuild IndexDuringBuild { get; set; } = IndexDuringBuild.SingleThread;

        public bool IndexEngineSource { get; set; } = true;

        /// <summary>
        /// Share the engine index between projects. Off (default): the engine index lives in the solution's
        /// .vs\UEToVS\Index; on: in <see cref="EngineIndexPath"/> (empty = %LocalAppData%\UEToVS\EngineIndex\&lt;engine version&gt;).
        /// </summary>
        public bool ShareEngineIndex { get; set; } = false;

        /// <summary>Shared engine index folder, used only when <see cref="ShareEngineIndex"/> is on. Empty = default location.</summary>
        public string EngineIndexPath { get; set; } = string.Empty;

        // ---- 工具栏 ----
        public bool ShowToolbar { get; set; } = true;

        /// <summary>Comma separated toolbar button keys that are hidden (see ToolbarButtons). Empty = all visible.</summary>
        public string HiddenToolbarButtons { get; set; } = string.Empty;

        // ---- 性能 ----
        /// <summary>Performance panel sampling interval in seconds.</summary>
        public int PerformanceSampleSeconds { get; set; } = 1;

        public HostSettings Clone() => (HostSettings)MemberwiseClone();
    }
}
