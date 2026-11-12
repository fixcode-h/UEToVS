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

        public bool IntelliSenseTakeover { get; set; } = true; // ADR-08: on by default since M3 (UEToVS provides completion, Quick Info, parameter info, colouring)

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

        // ---- IntelliSense（M3）----
        /// <summary>UEToVS completion list in C++ editors.</summary>
        public bool CompletionEnabled { get; set; } = true;

        /// <summary>Which items UEToVS contributes: Auto = UE enhancements only until VS's IntelliSense is taken over, then everything.</summary>
        public CompletionScope CompletionScope { get; set; } = CompletionScope.Auto;

        /// <summary>Open the list as soon as an identifier is typed (VA style); off = only '.', '-&gt;', '::', #include, specifiers, Ctrl+Space.</summary>
        public bool CompletionOnIdentifier { get; set; } = true;

        /// <summary>VA '.' ↔ '-&gt;' correction applied when a member completion is committed.</summary>
        public bool CompletionAccessorFix { get; set; } = true;

        /// <summary>Items requested from the engine per completion (it re-queries as the prefix grows when there are more).</summary>
        public int CompletionMaxResults { get; set; } = 500;

        /// <summary>Hover / Ctrl+K,I Quick Info (declaration, docs, UE reflection, macro expansion).</summary>
        public bool QuickInfoEnabled { get; set; } = true;

        /// <summary>Parameter info on '(' / ',' and Ctrl+Shift+Space.</summary>
        public bool SignatureHelpEnabled { get; set; } = true;

        /// <summary>VA-style semantic coloring (types, functions, variables, macros, enum members …; §7.7).</summary>
        public bool SemanticColoring { get; set; } = true;

        /// <summary>Per-kind colour overrides, e.g. "Class=#4EC9B0; Functions=#DCDCAA" (kinds or plural groups; empty = theme defaults / Fonts and Colors).</summary>
        public string SemanticColorOverrides { get; set; } = string.Empty;

        /// <summary>Lines above / below the viewport classified ahead of scrolling.</summary>
        public int SemanticColoringMarginLines { get; set; } = 50;

        public HostSettings Clone() => (HostSettings)MemberwiseClone();
    }
}
