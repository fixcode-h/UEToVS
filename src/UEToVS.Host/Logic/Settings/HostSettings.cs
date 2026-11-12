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

        public HostSettings Clone() => (HostSettings)MemberwiseClone();
    }
}
