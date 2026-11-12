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

        // ---- 导航（M2 预留：高亮 / 导航栏 / Ctrl+点击）----
        public bool HighlightReferences { get; set; } = true;

        public bool ShowNavigationBar { get; set; } = true;

        public bool CtrlClickGoToDefinition { get; set; } = true;

        // ---- 工具栏 ----
        public bool ShowToolbar { get; set; } = true;

        /// <summary>Comma separated toolbar button keys that are hidden (see ToolbarButtons). Empty = all visible.</summary>
        public string HiddenToolbarButtons { get; set; } = string.Empty;

        // ---- 键盘 ----
        /// <summary>Press Shift twice to open Search Everywhere (Rider style).</summary>
        public bool DoubleShiftSearchEverywhere { get; set; } = true;

        // ---- 性能 ----
        /// <summary>Performance panel sampling interval in seconds.</summary>
        public int PerformanceSampleSeconds { get; set; } = 1;

        public HostSettings Clone() => (HostSettings)MemberwiseClone();
    }
}
