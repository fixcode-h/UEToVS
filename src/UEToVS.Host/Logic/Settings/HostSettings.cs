using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Logic.Settings
{
    public enum IntelliSenseTakeoverMode
    {
        /// <summary>VS's C++ IntelliSense stays off while UEToVS is installed and enabled (default, §7.5).</summary>
        Global = 0,

        /// <summary>Off while a UE solution is open; restored when it closes or a non-UE solution is opened.</summary>
        UnrealSolutionOnly = 1,

        /// <summary>v0.5.1: never taken over; VS's own C++ IntelliSense, browse database, background code analysis and Blueprint scan stay as they are.</summary>
        Off = 2,
    }

    /// <summary>What background indexing does while VS/UBT builds (maps to IndexingSettings.DuringBuild).</summary>
    public enum IndexDuringBuild
    {
        Pause = 0,
        SingleThread = 1,
        FullSpeed = 2,
    }

    /// <summary>v0.4.5: what Ctrl+R, Ctrl+R does in a C++ editor.</summary>
    public enum RenameMode
    {
        /// <summary>Edit every occurrence in the current file in place; Enter opens the project-wide preview, Esc cancels.</summary>
        Inline = 0,

        /// <summary>The preview dialog right away (v0.4.2 behaviour).</summary>
        Dialog = 1,
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

        /// <summary>
        /// Takeover scope (default: UE solutions only). v0.5.1: <see cref="IntelliSenseTakeoverMode.Off"/> replaces the old
        /// separate "take over" switch, and the VS features below follow the scope instead of having switches of their own.
        /// </summary>
        public IntelliSenseTakeoverMode IntelliSenseTakeoverMode { get; set; } = IntelliSenseTakeoverMode.UnrealSolutionOnly;

        /// <summary>
        /// Whether VS's C++ IntelliSense is taken over at all (scope is not Off). Setting false (legacy "intelliSense.takeover"
        /// value) switches the scope to Off; setting true when it is Off switches it back to the default scope.
        /// </summary>
        public bool IntelliSenseTakeover
        {
            get => IntelliSenseTakeoverMode != IntelliSenseTakeoverMode.Off;
            set
            {
                if (!value) IntelliSenseTakeoverMode = IntelliSenseTakeoverMode.Off;
                else if (IntelliSenseTakeoverMode == IntelliSenseTakeoverMode.Off) IntelliSenseTakeoverMode = IntelliSenseTakeoverMode.UnrealSolutionOnly;
            }
        }

        /// <summary>VS's C++ browse database (.vc.db) is switched off with the takeover (follows the scope since v0.5.1).</summary>
        public bool DisableDatabase => IntelliSenseTakeover;

        /// <summary>VS's C++ background code analysis is switched off while a UE solution is open whenever the takeover is on (follows the scope since v0.5.1).</summary>
        public bool DisableBackgroundCodeAnalysis => IntelliSenseTakeover;

        /// <summary>VS's own Unreal Engine "Blueprint Support" (scans every Blueprint on each solution load): switched off (backed up) whenever the takeover is on (follows the scope since v0.5.1).</summary>
        public bool VsBlueprintSupport => !(IntelliSenseTakeover && TurnOffVsBlueprints);

        // v0.5.6: VS Unreal / C++ background features with switches of their own (Logic/IntelliSense/VsUnrealFeatures.cs).
        // true = turned off (backed up first) while the takeover is active; false = left as the user has it.

        /// <summary>VS 18's whole-codebase C++ semantic index and its CodeLens references.</summary>
        public bool TurnOffVsSemanticIndex { get; set; } = true;

        /// <summary>VS's Unreal Engine "enhanced Blueprint support" (Blueprint scan on every solution load).</summary>
        public bool TurnOffVsBlueprints { get; set; } = true;

        /// <summary>VS's Unreal Header Tool code analysis.</summary>
        public bool TurnOffVsUhtAnalysis { get; set; } = true;

        /// <summary>VS's CodeLens (all languages; empty once the semantic index and Blueprint support are off).</summary>
        public bool TurnOffVsCodeLens { get; set; } = true;

        /// <summary>VS debugger "Always include symbols next to modules" (12 GB while debugging the UE editor). Off by default (v0.5.9).</summary>
        public bool TurnOffVsAdjacentSymbols { get; set; } = true;

        /// <summary>Ids (<see cref="UEToVS.Host.Logic.IntelliSense.VsUnrealFeatures"/>) of the VS features to turn off.</summary>
        public System.Collections.Generic.ISet<string> TurnOffVsFeatures()
        {
            var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            if (TurnOffVsSemanticIndex) set.Add(UEToVS.Host.Logic.IntelliSense.VsUnrealFeatures.SemanticIndex);
            if (TurnOffVsBlueprints) set.Add(UEToVS.Host.Logic.IntelliSense.VsUnrealFeatures.Blueprints);
            if (TurnOffVsUhtAnalysis) set.Add(UEToVS.Host.Logic.IntelliSense.VsUnrealFeatures.UhtAnalysis);
            if (TurnOffVsCodeLens) set.Add(UEToVS.Host.Logic.IntelliSense.VsUnrealFeatures.CodeLens);
            if (TurnOffVsAdjacentSymbols) set.Add(UEToVS.Host.Logic.IntelliSense.VsUnrealFeatures.AdjacentSymbols);
            return set;
        }

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

        /// <summary>
        /// UELink push-stream TCP port; 0 = read the card &lt;Project&gt;/Saved/UELink/instances/&lt;PID&gt;.json (old
        /// UEToVSLink: &lt;Project&gt;/Saved/UEToVSLink/port.txt).
        /// </summary>
        public int EditorLinkPort { get; set; } = 0;

        /// <summary>
        /// Where to look for UELink updates: empty = latest release of fixcode-h/UELink on GitHub; an http(s) URL = a
        /// mirror's manifest.json (or the folder holding it); a local folder holding manifest.json and the zip (offline).
        /// </summary>
        public string UELinkSource { get; set; } = "";

        // ---- v0.4.5 UE editor link (log window, test runner, Live Coding errors)
        /// <summary>UE log window ring buffer size (lines); older lines are dropped.</summary>
        public int LinkLogMaxLines { get; set; } = 20000;

        /// <summary>Keep receiving editor log lines after the log window is closed (it was opened once this session).</summary>
        public bool LinkLogKeepReceiving { get; set; } = true;

        /// <summary>
        /// v0.5.1: UEToVS's Live Coding features (the Live Coding command, error mapping). Off by default: a Live Coding
        /// compile starts UE's LiveCodingConsole, which can hold several GB of memory.
        /// </summary>
        public bool LinkLiveCodingEnabled { get; set; } = false;

        /// <summary>
        /// v0.5.11: set the UE project's Live Coding startup to "Manual" (EditorPerProjectUserSettings.ini, backed up) so the
        /// editor no longer starts LiveCodingConsole at launch; it starts on the first Live Coding compile instead.
        /// </summary>
        public bool UeLiveCodingManualStartup { get; set; } = true;

        /// <summary>Collect Live Coding compiler output from the editor and report its errors in VS (only with <see cref="LinkLiveCodingEnabled"/>).</summary>
        public bool LinkLiveCodingErrors { get; set; } = true;

        /// <summary>Live Coding errors are collected: the feature is on and its error mapping too.</summary>
        public bool LiveCodingErrorsActive => LinkLiveCodingEnabled && LinkLiveCodingErrors;

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

        /// <summary>
        /// 打开 / 切换到 C++ 文件时在解决方案资源管理器中展开并选中它（类似 Rider 的 Autoscroll from Source，只对 C++ 文件，不抢焦点）。
        /// </summary>
        public bool SyncSolutionExplorer { get; set; } = true;
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

        /// <summary>
        /// Lua script folders to index, separated by ';' (absolute, or relative to the project folder, e.g. Content/Script).
        /// Empty = no Lua index. Pushed to the engine as <c>IndexingSettings.LuaScriptPaths</c>.
        /// </summary>
        public string LuaScriptPaths { get; set; } = string.Empty;

        // ---- 工具栏 ----
        public bool ShowToolbar { get; set; } = true;

        /// <summary>Comma separated toolbar button keys that are hidden (see ToolbarButtons). Empty = all visible.</summary>
        public string HiddenToolbarButtons { get; set; } = string.Empty;

        // ---- 性能 ----
        /// <summary>Performance panel sampling interval in seconds.</summary>
        public int PerformanceSampleSeconds { get; set; } = 1;

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

        // ---- 注释翻译（代码感知 > 注释翻译；Logic/Translation/TranslationSettingsSchema）----
        /// <summary>Translate doc comments in Quick Info (async, cached; the original shows first).</summary>
        public bool CommentTranslation { get; set; } = true;

        public UEToVS.Host.Logic.Translation.TranslationDisplay TranslationDisplay { get; set; } = UEToVS.Host.Logic.Translation.TranslationDisplay.Bilingual;

        public UEToVS.Host.Logic.Translation.TranslationProviderKind TranslationProvider { get; set; } = UEToVS.Host.Logic.Translation.TranslationProviderKind.Google;

        /// <summary>"auto" (UI language) or zh-CN / zh-TW / en / ja / ko.</summary>
        public string TranslationTarget { get; set; } = UEToVS.Host.Logic.Translation.TranslationSettingsSchema.AutoTarget;

        /// <summary>Google endpoint (mirror); empty = https://translate.googleapis.com.</summary>
        public string TranslationGoogleUrl { get; set; } = string.Empty;

        /// <summary>Parameter info on '(' / ',' and Ctrl+Shift+Space.</summary>
        public bool SignatureHelpEnabled { get; set; } = true;

        /// <summary>VA-style semantic coloring (types, functions, variables, macros, enum members …; §7.7).</summary>
        public bool SemanticColoring { get; set; } = true;

        /// <summary>Per-kind colour overrides, e.g. "Class=#4EC9B0; Functions=#DCDCAA" (kinds or plural groups; empty = theme defaults / Fonts and Colors).</summary>
        public string SemanticColorOverrides { get; set; } = string.Empty;

        /// <summary>Lines above / below the viewport classified ahead of scrolling.</summary>
        public int SemanticColoringMarginLines { get; set; } = 50;

        /// <summary>High-confidence squiggles (unknown members, UHT rules, missing Build.cs dependencies) once VS IntelliSense is taken over.</summary>
        public bool DiagnosticsEnabled { get; set; } = true;

        /// <summary>UE live templates (uclass, uprop, ufunc, uelog …): in the completion list at line start, and abbreviation + Tab.</summary>
        public bool SnippetsEnabled { get; set; } = true;

        /// <summary>Postfix templates after '.' (expr.if, expr.notnull, expr.cast, expr.isvalid, expr.for …).</summary>
        public bool PostfixTemplatesEnabled { get; set; } = true;

        /// <summary>Inlay hints: parameter names before literal / nullptr arguments.</summary>
        public bool InlayParameterNames { get; set; } = true;

        /// <summary>Inlay hints: deduced type after <c>auto</c> variables.</summary>
        public bool InlayAutoTypes { get; set; }

        // ---- v0.4.5 编辑体验（rows: Logic/Settings/EditorFeatureSettings.cs）----
        /// <summary>Surround With (Ctrl+Shift+Alt+U, 1 / Rider Ctrl+Alt+J).</summary>
        public bool SurroundWithEnabled { get; set; } = true;

        /// <summary>Deprecated API squiggles / strikethrough / Quick Info note / Alt+Enter fix in the current file (UE0011).</summary>
        public bool DeprecationHints { get; set; } = true;

        /// <summary>UE naming convention suggestions for types and bool members declared in the current file (UE0012).</summary>
        public bool NamingConventionCheck { get; set; } = false;

        /// <summary>Deploy UEToVS.natstepfilter to the user's Visualizers folder (removed when off).</summary>
        public bool DebugStepFilter { get; set; } = true;

        /// <summary>Ctrl+R, Ctrl+R: edit the name in place (Inline) or open the preview dialog right away (Dialog).</summary>
        public RenameMode RenameMode { get; set; } = RenameMode.Inline;

        /// <summary>Remember copied / cut text for Ctrl+Shift+V (paste from history).</summary>
        public bool ClipboardHistoryEnabled { get; set; } = true;

        /// <summary>Entries kept in the clipboard history.</summary>
        public int ClipboardHistorySize { get; set; } = 16;

        /// <summary>#tag bookmarks: collect #tags written in comments of the open files.</summary>
        public bool HashtagsEnabled { get; set; } = true;

        // ---- 字体（设置 > 外观 > 字体；值见 UEToVS.UI.Logic.FontChoice，VS 类别见 Logic/Fonts/VsFontCategories）----
        /// <summary>英文字体: Latin part of the UEToVS UI font ("bundled:IBM Plex Sans", "vs" or an installed family).</summary>
        public string UiLatinFont { get; set; } = UEToVS.UI.Logic.FontSettings.DefaultUiLatin.ToString();

        /// <summary>中文字体: CJK part of the UEToVS UI font.</summary>
        public string UiCjkFont { get; set; } = UEToVS.UI.Logic.FontSettings.DefaultUiCjk.ToString();

        /// <summary>代码字体: code previews, and the VS categories set to "同代码字体".</summary>
        public string CodeFont { get; set; } = UEToVS.UI.Logic.FontSettings.DefaultCode.ToString();

        /// <summary>自定义字体: installed family names (comma separated) put before 英文字体 / 中文字体 in the UI font; empty = none.</summary>
        public string CustomFont { get; set; } = string.Empty;

        /// <summary>由 UEToVS 管理 VS 字体: write the VS font categories below (backed up first; off restores them).</summary>
        public bool ManageVsFonts { get; set; } = true;

        /// <summary>Per VS category family/size, only the ones that differ from the defaults (Logic/Fonts/VsFontSettingsText).</summary>
        public string VsFonts { get; set; } = string.Empty;
        // ---- v0.4.5 构建结果 / 错误列表
        /// <summary>Squiggles for the last build's compiler errors / warnings (independent of the IntelliSense takeover).</summary>
        public bool BuildErrorSquiggles { get; set; } = true;

        /// <summary>UEToVS diagnostics (UE rule squiggles of open files) in the VS Error List, source "UEToVS".</summary>
        public bool ErrorListEnabled { get; set; } = true;

        /// <summary>Also list the parsed build errors under "UEToVS" (VS already lists most of them under "Build").</summary>
        public bool ErrorListIncludeBuild { get; set; }
        // ---- v0.4.5 heavy features（插件开关：默认开；关闭后不允许运行，后台也不跑）----
        /// <summary>Whole-project UE rule inspection (command, settings button, MCP run_project_inspection).</summary>
        public bool ProjectInspectionEnabled { get; set; } = true;

        /// <summary>Whole-project inspection also covers project plugins.</summary>
        public bool InspectionIncludePlugins { get; set; } = true; // v0.5.3: on by default

        /// <summary>Engine upgrade assistant (deprecated API references, batch replacement).</summary>
        public bool UpgradeAssistantEnabled { get; set; } = true;

        /// <summary>Generic template instantiation in IntelliSense; off = the built-in UE template rules only.</summary>
        public bool TemplateInstantiationEnabled { get; set; } = true;

        /// <summary>LRU capacity of instantiated (template, arguments) bindings in the engine.</summary>
        public int TemplateCacheCapacity { get; set; } = 2048;

        // ---- v0.5.4 联合编译（Horde / UBA；rows: Logic/Horde/HordeSettingsSchema.cs）----
        /// <summary>Write the UBA / Horde keys to BuildConfiguration.xml (off = put back what was there before UEToVS).</summary>
        public bool HordeEnabled { get; set; }

        /// <summary>Where Horde (the agent) is installed; default C:\Horde.</summary>
        public string HordeInstallPath { get; set; } = UEToVS.Host.Logic.Horde.HordeSettingsSchema.DefaultInstallPath;

        /// <summary>Which BuildConfiguration.xml is written: the engine's Saved folder (default), the user's AppData one, or the project's.</summary>
        public UEToVS.Host.Logic.Horde.HordeConfigTarget HordeTarget { get; set; } = UEToVS.Host.Logic.Horde.HordeConfigTarget.Engine;

        /// <summary>Horde/Server; empty = keep what the other config files say.</summary>
        public string HordeServer { get; set; } = string.Empty;

        /// <summary>Horde/Pool; empty = not written.</summary>
        public string HordePool { get; set; } = string.Empty;

        /// <summary>Horde/MaxCores (remote cores borrowed at most); 0 = not written.</summary>
        public int HordeMaxCores { get; set; }

        /// <summary>BuildConfiguration/MaxParallelActions while Horde is on; 0 = logical cores / 4.</summary>
        public int HordeLocalCores { get; set; }

        /// <summary>Whether other people's builds may use this machine's Horde Agent (Agent.json computePort).</summary>
        public UEToVS.Host.Logic.Horde.HordeAgentPolicy HordeAgentPolicy { get; set; } = UEToVS.Host.Logic.Horde.HordeAgentPolicy.Block;

        /// <summary>Tri-state UBA switches: "default" (not written) / "true" / "false".</summary>
        public string HordeLinkRemote { get; set; } = UEToVS.Host.Logic.Horde.HordeSettingsSchema.TriDefault;
        public string HordePrintSummary { get; set; } = "true";
        public string HordeForceRemote { get; set; } = UEToVS.Host.Logic.Horde.HordeSettingsSchema.TriDefault;
        public string HordeDisableRemote { get; set; } = UEToVS.Host.Logic.Horde.HordeSettingsSchema.TriDefault;
        public string HordeAllowRetry { get; set; } = UEToVS.Host.Logic.Horde.HordeSettingsSchema.TriDefault;
        public string HordeLaunchVisualizer { get; set; } = UEToVS.Host.Logic.Horde.HordeSettingsSchema.TriDefault;
        public string HordeStoreObjCompressed { get; set; } = UEToVS.Host.Logic.Horde.HordeSettingsSchema.TriDefault;

        /// <summary>Advanced text values; empty = not written.</summary>
        public string HordeCluster { get; set; } = string.Empty;
        public string HordeZone { get; set; } = string.Empty;
        public string HordeEncryption { get; set; } = string.Empty;
        public string HordeConnectionMode { get; set; } = string.Empty;
        public string HordeRootDir { get; set; } = string.Empty;

        /// <summary>UnrealBuildAccelerator/StoreCapacityGb; 0 = not written.</summary>
        public int HordeStoreCapacityGb { get; set; }

        public HostSettings Clone() => (HostSettings)MemberwiseClone();
    }
}
