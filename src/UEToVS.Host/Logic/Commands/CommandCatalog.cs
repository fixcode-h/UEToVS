using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Commands
{
    /// <summary>Where a shortcut applies (DTE binding scope).</summary>
    public enum CommandScope
    {
        Global = 0,
        TextEditor = 1,
    }

    /// <summary>Which module implements a command (and therefore binds it to its OleMenuCommand).</summary>
    public enum CommandOwner
    {
        /// <summary>Host commands, bound by CommandRouter from this catalog.</summary>
        Host = 0,
        /// <summary>M2 navigation (Navigation/NavigationActions.cs, 0x0200–0x02FF).</summary>
        Navigation = 1,
        /// <summary>M1 features (Features/M1Feature.cs, 0x0300–0x03FF).</summary>
        Features = 2,
        /// <summary>M4 refactoring (Refactoring/RefactoringFeature.cs, 0x0700–0x07FF).</summary>
        Refactoring = 3,
    }

    /// <summary>
    /// One UEToVS command: VSCT id, canonical DTE name, the action it dispatches to, titles, and the default shortcut in
    /// each keymap preset. The catalog is the single source for the keymap page (presets, conflicts) and the VSCT tests;
    /// the commands of M1/M2 are bound by their modules (<see cref="Owner"/>), the host ones by CommandRouter.
    /// </summary>
    public sealed class CommandInfo
    {
        public int Id { get; set; }
        /// <summary>
        /// VSCT CanonicalName, e.g. "UEToVS.GoToDefinition". Also the key of presets / backups / conflicts. NOT the DTE
        /// command name: VS may prefix it with the parent menu path (e.g. "Extensions.UEToVS..."), so DTE lookups go by guid + <see cref="Id"/>.
        /// </summary>
        public string Name { get; set; }
        public string ActionId { get; set; }
        public string TitleZh { get; set; }
        public string TitleEn { get; set; }
        /// <summary>"navigation" | "search" | "related" | "bookmarks" | "unreal" | "tools" | "settings" | "diagnostics" | "refactor" — groups the keymap page.</summary>
        public string Group { get; set; }
        public CommandScope Scope { get; set; }
        /// <summary>Visual Assist style default (also what the VSCT KeyBinding declares). VS key syntax. Every command has one (tools use the "Ctrl+Shift+Alt+U, x" chords).</summary>
        public string VaKeys { get; set; }
        public string RiderKeys { get; set; }
        public string VsKeys { get; set; }
        public CommandOwner Owner { get; set; }

        public string Title(bool english) => english ? TitleEn : TitleZh;

        /// <summary>
        /// Keys of <paramref name="preset"/>; a command without an equivalent in Rider / VS falls back to its Visual Assist
        /// default, so every UEToVS command has a shortcut under every preset.
        /// </summary>
        public string KeysFor(KeymapPresetId preset)
        {
            switch (preset)
            {
                case KeymapPresetId.Rider: return RiderKeys ?? VaKeys;
                case KeymapPresetId.VisualStudio: return VsKeys ?? VaKeys;
                default: return VaKeys;
            }
        }

        public string DteScope => Scope == CommandScope.TextEditor ? "Text Editor" : "Global";
    }

    public enum KeymapPresetId
    {
        /// <summary>Visual Assist style (UEToVS default; matches the VSCT key bindings).</summary>
        VisualAssist = 0,
        Rider = 1,
        /// <summary>VS defaults: UEToVS commands get the keys of VS's own equivalents (or none).</summary>
        VisualStudio = 2,
    }

    /// <summary>
    /// The list of UEToVS commands shown on the keymap page (ids must match UEToVSHostPackage.vsct; unit tests check ids,
    /// canonical names and that the VSCT key bindings equal <see cref="CommandInfo.VaKeys"/>). Id ranges: 0x01xx host (+ 0x05xx MCP),
    /// 0x02xx M2 navigation, 0x03xx M1 features, 0x04xx settings / windows / toolbar. The 20 bookmark digit commands
    /// (0x0340–0x0359) are left out of the page (bind them in Tools &gt; Options &gt; Keyboard).
    /// </summary>
    public static class CommandCatalog
    {
        // ---- host (0x01xx)
        public const int OpenLogFolder = 0x0100;
        public const int OpenCurrentLog = 0x0101;
        public const int CreateDiagnostics = 0x0102;
        public const int ShowPerformancePanel = 0x0104;
        public const int RestartEngine = 0x0110;
        public const int ToggleIntelliSense = 0x0120;
        public const int ShowComponentGallery = 0x0130;
        public const int Settings = 0x0140;
        public const int FindAction = 0x0150;
        /// <summary>MCP (0x0500–0x05FF).</summary>
        public const int WriteMcpConfig = 0x0500;
        /// <summary>v0.4.2 Generate (Generate/GenerateActions.cs).</summary>
        public const int GenerateConstructor = 0x0160;
        public const int GenerateReplication = 0x0161;
        public const int GenerateRpc = 0x0162;
        /// <summary>v0.4.2 Smart Select (Editing/SmartSelectActions.cs).</summary>
        public const int ExpandSelection = 0x0163;
        public const int ShrinkSelection = 0x0164;
        // ---- v0.4.5 编辑体验 (0x0180–0x019F): Editing/EditorFeatureActions.cs
        public const int SurroundWith = 0x0180;
        public const int ToggleInlayHints = 0x0181;
        public const int ShowHashtags = 0x0182;
        public const int PasteFromHistory = 0x0183;

        // ---- M2 navigation (0x0200–0x02FF): Navigation/NavigationActions.cs
        public const int GoToDefinition = 0x0200;
        public const int GoToImplementation = 0x0201;
        public const int FindReferences = 0x0202;
        public const int TypeHierarchy = 0x0203;
        public const int ListMembersInFile = 0x0204;
        public const int ListTypeMembers = 0x0205;

        // ---- M1 features (0x0300–0x03FF): Features/M1Feature.cs
        public const int SearchEverywhere = 0x0300;
        public const int GoToType = 0x0301;
        public const int GoToSymbol = 0x0302;
        public const int GoToFile = 0x0303;
        public const int GoToAsset = 0x0304;
        public const int NavigateBack = 0x0310;
        public const int NavigateForward = 0x0311;
        public const int RecentFiles = 0x0312;
        public const int RecentLocations = 0x0313;
        public const int LastEditLocation = 0x0314;
        public const int NextEditLocation = 0x0315;
        public const int FileStructure = 0x0316;
        public const int NavigateTo = 0x0317;
        public const int ToggleHeaderSource = 0x0320;
        public const int GoToRelated = 0x0321;
        public const int OpenBuildCs = 0x0322;
        public const int AddModuleDependency = 0x0323;
        public const int ToggleBookmark = 0x0330;
        public const int ShowBookmarks = 0x0331;
        public const int LiveCoding = 0x0360;
        public const int GenerateProjectFiles = 0x0361;
        public const int InstallEditorPlugin = 0x0362;

        // ---- settings / windows / toolbar (0x0400–0x04FF)
        public const int Toolbar = 0x0400;
        /// <summary>Toolbar-only buttons (separate ids so each can be hidden from the settings window).</summary>
        public const int TbSettings = 0x0480;
        public const int TbSearchEverywhere = 0x0481;
        // 0x0482 / 0x0483 were the toolbar Back / Forward buttons (removed in v0.4.5: VS has its own); ids stay reserved.
        public const int TbGoToDefinition = 0x0484;
        public const int TbFindReferences = 0x0485;
        public const int TbPerformance = 0x0486;

        // ---- M4 refactoring (0x0700–0x07FF): Refactoring/RefactoringFeature.cs
        public const int QuickActions = 0x0700;
        public const int Rename = 0x0701;
        public const int ImplementVirtual = 0x0702;

        /// <summary>Action ids of the M1/M2 commands (mirrors NavigationActionIds / M1ActionIds, which are host-internal).</summary>
        public static class ActionIds
        {
            public const string GoToDefinition = "uetovs.navigation.goToDefinition";
            public const string GoToImplementation = "uetovs.navigation.goToImplementation";
            public const string FindReferences = "uetovs.navigation.findReferences";
            public const string TypeHierarchy = "uetovs.navigation.typeHierarchy";
            public const string ListMembersInFile = "uetovs.navigation.listMembersInFile";
            public const string ListTypeMembers = "uetovs.navigation.listTypeMembers";
            public const string SearchEverywhere = "uetovs.search.everywhere";
            public const string GoToType = "uetovs.search.type";
            public const string GoToSymbol = "uetovs.search.symbol";
            public const string GoToFile = "uetovs.search.file";
            public const string GoToAsset = "uetovs.search.asset";
            public const string NavigateBack = "uetovs.navigation.back";
            public const string NavigateForward = "uetovs.navigation.forward";
            public const string RecentFiles = "uetovs.navigation.recentFiles";
            public const string RecentLocations = "uetovs.navigation.recentLocations";
            public const string LastEditLocation = "uetovs.navigation.lastEdit";
            public const string NextEditLocation = "uetovs.navigation.nextEdit";
            public const string FileStructure = "uetovs.navigation.fileStructure";
            public const string NavigateTo = "uetovs.navigation.navigateTo";
            public const string ToggleHeaderSource = "uetovs.related.toggleHeaderSource";
            public const string GoToRelated = "uetovs.related.goTo";
            public const string OpenBuildCs = "uetovs.unreal.openBuildCs";
            public const string AddModuleDependency = "uetovs.unreal.addModuleDependency";
            public const string ToggleBookmark = "uetovs.bookmarks.toggle";
            public const string ShowBookmarks = "uetovs.bookmarks.list";
            public const string LiveCoding = "uetovs.unreal.liveCoding";
            public const string GenerateProjectFiles = "uetovs.unreal.generateProjectFiles";
            public const string InstallEditorPlugin = "uetovs.unreal.installEditorPlugin";

            // host (mirror of Shell/ActionIds)
            public const string FindAction = "uetovs.findAction";
            public const string Settings = "uetovs.settings.open";
            public const string ShowPerformancePanel = "uetovs.diagnostics.performancePanel";

            // M4 refactoring (mirror of Logic/Refactoring/RefactorActionIds)
            public const string QuickActions = "uetovs.refactor.quickActions";
            public const string Rename = "uetovs.refactor.rename";
            public const string ImplementVirtual = "uetovs.refactor.implementVirtual";
        }

        private static CommandInfo C(CommandOwner owner, int id, string name, string action, string zh, string en, string group, CommandScope scope,
                                     string va, string rider, string vs) =>
            new CommandInfo
            {
                Owner = owner, Id = id, Name = "UEToVS." + name, ActionId = action, TitleZh = zh, TitleEn = en, Group = group, Scope = scope,
                VaKeys = va, RiderKeys = rider, VsKeys = vs,
            };

        private const CommandOwner H = CommandOwner.Host, M2 = CommandOwner.Navigation, M1 = CommandOwner.Features;
        private const CommandOwner M4 = CommandOwner.Refactoring;
        private const CommandScope Ed = CommandScope.TextEditor, Gl = CommandScope.Global;

        public static IReadOnlyList<CommandInfo> All { get; } = new[]
        {
            // navigation (M2, text editor). VA keys = the VSCT defaults: Alt+G / Shift+Alt+G / Shift+Alt+F / Shift+Alt+H / Alt+M / Shift+Alt+M
            C(M2, GoToDefinition, "GoToDefinition", ActionIds.GoToDefinition, "转到定义 / 声明", "Go to Definition / Declaration", "navigation", Ed, "Alt+G", "Ctrl+B", "F12"),
            C(M2, GoToImplementation, "GoToImplementation", ActionIds.GoToImplementation, "转到实现", "Go to Implementation", "navigation", Ed, "Shift+Alt+G", "Ctrl+Alt+B", "Ctrl+F12"),
            C(M2, FindReferences, "FindReferences", ActionIds.FindReferences, "查找引用", "Find References", "navigation", Ed, "Shift+Alt+F", "Alt+F7", "Shift+F12"),
            C(M2, TypeHierarchy, "TypeHierarchy", ActionIds.TypeHierarchy, "类型层次", "Type Hierarchy", "navigation", Ed, "Shift+Alt+H", "Ctrl+H", null),
            C(M2, ListMembersInFile, "ListMembersInFile", ActionIds.ListMembersInFile, "文件成员", "List Members in File", "navigation", Ed, "Alt+M", null, null),
            C(M2, ListTypeMembers, "ListTypeMembers", ActionIds.ListTypeMembers, "类型成员（含继承）", "Type Members (with Inherited)", "navigation", Ed, "Shift+Alt+M", null, null),
            // navigation history (M1, global)
            C(M1, NavigateBack, "NavigateBack", ActionIds.NavigateBack, "后退", "Navigate Back", "navigation", Gl, "Alt+Left Arrow", "Ctrl+Alt+Left Arrow", "Ctrl+-"),
            C(M1, NavigateForward, "NavigateForward", ActionIds.NavigateForward, "前进", "Navigate Forward", "navigation", Gl, "Alt+Right Arrow", "Ctrl+Alt+Right Arrow", "Ctrl+Shift+-"),
            C(M1, RecentFiles, "RecentFiles", ActionIds.RecentFiles, "最近文件", "Recent Files", "navigation", Gl, "Shift+Alt+E", "Ctrl+E", null),
            C(M1, RecentLocations, "RecentLocations", ActionIds.RecentLocations, "最近位置", "Recent Locations", "navigation", Gl, "Shift+Alt+L", "Ctrl+Shift+E", null),
            C(M1, LastEditLocation, "LastEditLocation", ActionIds.LastEditLocation, "上一个编辑位置", "Last Edit Location", "navigation", Gl, "Ctrl+Shift+Bkspce", "Ctrl+Shift+Bkspce", null),
            C(M1, NextEditLocation, "NextEditLocation", ActionIds.NextEditLocation, "下一个编辑位置", "Next Edit Location", "navigation", Gl, "Ctrl+Shift+Alt+Bkspce", null, null),
            C(M1, FileStructure, "FileStructure", ActionIds.FileStructure, "文件结构", "File Structure", "navigation", Gl, "Ctrl+Shift+Alt+M", "Ctrl+F12", null),
            C(M1, NavigateTo, "NavigateTo", ActionIds.NavigateTo, "导航到…", "Navigate To", "navigation", Gl, "Shift+Alt+N", null, null),

            // search (M1); Search Everywhere is also "double Shift" (M1 InputMonitor), which VSCT cannot express
            C(H, FindAction, "FindAction", ActionIds.FindAction, "查找操作", "Find Action", "search", Gl, "Ctrl+Shift+Alt+A", "Ctrl+Shift+A", null),
            C(M1, SearchEverywhere, "SearchEverywhere", ActionIds.SearchEverywhere, "Search Everywhere", "Search Everywhere", "search", Gl, "Ctrl+T", null, null),
            C(M1, GoToType, "GoToType", ActionIds.GoToType, "转到类型", "Go to Type", "search", Gl, "Shift+Alt+T", "Ctrl+N", null),
            C(M1, GoToSymbol, "GoToSymbol", ActionIds.GoToSymbol, "转到符号", "Go to Symbol", "search", Gl, "Shift+Alt+S", "Ctrl+Shift+Alt+N", null),
            C(M1, GoToFile, "GoToFile", ActionIds.GoToFile, "转到文件", "Go to File", "search", Gl, "Shift+Alt+O", "Ctrl+Shift+N", null),
            C(M1, GoToAsset, "GoToAsset", ActionIds.GoToAsset, "转到资产（蓝图）", "Go to Asset (Blueprint)", "search", Gl, "Shift+Alt+B", null, null),

            // related (M1)
            C(M1, ToggleHeaderSource, "ToggleHeaderSource", ActionIds.ToggleHeaderSource, "头文件/源文件切换", "Toggle Header/Source", "related", Gl, "Alt+O", "Ctrl+Alt+Home", "Ctrl+K, Ctrl+O"),
            C(M1, GoToRelated, "GoToRelated", ActionIds.GoToRelated, "转到相关文件", "Go to Related", "related", Gl, "Shift+Alt+R", null, null),
            C(M1, OpenBuildCs, "OpenBuildCs", ActionIds.OpenBuildCs, "打开模块 Build.cs", "Open Module Build.cs", "related", Gl, "Ctrl+Shift+Alt+U, B", null, null),
            C(M1, AddModuleDependency, "AddModuleDependency", ActionIds.AddModuleDependency, "为 include 添加模块依赖", "Add Module Dependency for Include", "related", Gl, "Ctrl+Shift+Alt+U, A", null, null),

            // bookmarks (M1)
            C(M1, ToggleBookmark, "ToggleBookmark", ActionIds.ToggleBookmark, "切换书签", "Toggle Bookmark", "bookmarks", Gl, "Ctrl+F2", "F11", null),
            C(M1, ShowBookmarks, "ShowBookmarks", ActionIds.ShowBookmarks, "书签列表", "Show Bookmarks", "bookmarks", Gl, "Ctrl+Shift+F2", "Shift+F11", null),

            // Unreal (M1)
            C(M1, LiveCoding, "LiveCoding", ActionIds.LiveCoding, "Live Coding（UE 编辑器）", "Live Coding (Unreal Editor)", "unreal", Gl, "Ctrl+Alt+F11", "Ctrl+Alt+F11", "Ctrl+Alt+F11"),
            C(M1, GenerateProjectFiles, "GenerateProjectFiles", ActionIds.GenerateProjectFiles, "生成项目文件", "Generate Project Files", "unreal", Gl, "Ctrl+Shift+Alt+U, G", null, null),
            C(M1, InstallEditorPlugin, "InstallEditorPlugin", ActionIds.InstallEditorPlugin, "安装 UEToVSLink 编辑器插件", "Install UEToVSLink Editor Plugin", "unreal", Gl, "Ctrl+Shift+Alt+U, E", null, null),

            // tools / settings / diagnostics (host)
            C(H, ShowPerformancePanel, "PerformancePanel", ActionIds.ShowPerformancePanel, "性能面板", "Performance Panel", "tools", Gl, "Ctrl+Shift+Alt+U, P", null, null),
            C(H, Settings, "Settings", ActionIds.Settings, "设置…", "Settings...", "settings", Gl, "Ctrl+Shift+Alt+U, S", "Ctrl+Alt+S", null),
            C(H, RestartEngine, "RestartEngine", "uetovs.engine.restart", "重启引擎", "Restart Engine", "tools", Gl, "Ctrl+Shift+Alt+U, R", null, null),
            C(H, ToggleIntelliSense, "ToggleIntelliSense", "uetovs.intellisense.takeOver", "接管/恢复 VS C++ IntelliSense", "Take Over / Restore C++ IntelliSense", "settings", Gl, "Ctrl+Shift+Alt+U, I", null, null),
            C(H, OpenLogFolder, "OpenLogFolder", "uetovs.diagnostics.openLogFolder", "打开日志文件夹", "Open Log Folder", "diagnostics", Gl, "Ctrl+Shift+Alt+U, F", null, null),
            C(H, OpenCurrentLog, "OpenCurrentLog", "uetovs.diagnostics.openCurrentLog", "打开当前日志", "Open Current Log", "diagnostics", Gl, "Ctrl+Shift+Alt+U, L", null, null),
            C(H, CreateDiagnostics, "CreateDiagnosticsPackage", "uetovs.diagnostics.createPackage", "生成诊断包", "Create Diagnostics Package", "diagnostics", Gl, "Ctrl+Shift+Alt+U, D", null, null),
            C(H, ShowComponentGallery, "ShowComponentGallery", "uetovs.ui.componentGallery", "组件画廊", "Component Gallery", "diagnostics", Gl, "Ctrl+Shift+Alt+U, C", null, null),
            C(H, WriteMcpConfig, "WriteMcpConfig", "uetovs.mcp.writeConfig", "写入 .mcp.json 到项目根目录", "Write .mcp.json to Project Root", "tools", Gl, "Ctrl+Shift+Alt+U, M", null, null),

            // M4 refactoring (text editor). Alt+Enter: UEToVS context actions (falls back to VS's quick actions when UEToVS IntelliSense is off).
            C(M4, QuickActions, "QuickActions", ActionIds.QuickActions, "上下文操作（Alt+Enter）", "Context Actions (Alt+Enter)", "refactor", Ed, "Alt+Enter", "Alt+Enter", "Alt+Enter"),
            C(M4, Rename, "Rename", ActionIds.Rename, "重命名…", "Rename...", "refactor", Ed, "Ctrl+R, Ctrl+R", "Shift+F6", "Ctrl+R, Ctrl+R"),
            C(M4, ImplementVirtual, "ImplementVirtual", ActionIds.ImplementVirtual, "实现虚函数 / 重写…", "Implement Virtual Methods / Override...", "refactor", Ed, "Ctrl+Shift+Alt+U, V", "Ctrl+O", null),
            // generate (v0.4.2, host; later also offered by the Alt+Enter hub through the same action ids)
            C(H, GenerateConstructor, "GenerateConstructor", "uetovs.generate.constructor", "生成构造函数", "Generate Constructor", "unreal", Gl, "Ctrl+Shift+Alt+U, K", null, null),
            C(H, GenerateReplication, "GenerateReplication", "uetovs.generate.replication", "生成 GetLifetimeReplicatedProps", "Generate GetLifetimeReplicatedProps", "unreal", Gl, "Ctrl+Shift+Alt+U, Y", null, null),
            C(H, GenerateRpc, "GenerateRpc", "uetovs.generate.rpc", "生成 RPC（_Implementation / _Validate）", "Generate RPC (Implementation / Validate)", "unreal", Gl, "Ctrl+Shift+Alt+U, W", null, null),
            C(H, ExpandSelection, "ExpandSelection", "uetovs.edit.expandSelection", "扩大选区", "Extend Selection", "navigation", Ed, "Shift+Alt+W", null, null),
            C(H, ShrinkSelection, "ShrinkSelection", "uetovs.edit.shrinkSelection", "缩小选区", "Shrink Selection", "navigation", Ed, "Ctrl+Shift+Alt+W", null, null),
            // ---- v0.4.5 编辑体验 (host; Editing/EditorFeatureActions.cs)
            C(H, SurroundWith, "SurroundWith", "uetovs.edit.surroundWith", "Surround With…", "Surround With...", "refactor", Ed, "Ctrl+Shift+Alt+U, 1", "Ctrl+Alt+J", null),
            C(H, ToggleInlayHints, "ToggleInlayHints", "uetovs.edit.toggleInlayHints", "显示 / 隐藏内嵌提示", "Toggle Inlay Hints", "tools", Ed, "Ctrl+Shift+Alt+U, 2", null, null),
            C(H, ShowHashtags, "ShowHashtags", "uetovs.edit.hashtags", "#tag 书签", "#tag Bookmarks", "bookmarks", Gl, "Ctrl+Shift+Alt+U, 3", null, null),
            C(H, PasteFromHistory, "PasteFromHistory", "uetovs.edit.pasteFromHistory", "从剪贴板历史粘贴…", "Paste from Clipboard History...", "tools", Ed, "Ctrl+Shift+V", "Ctrl+Shift+V", null),
        };

        public static CommandInfo ById(int id)
        {
            foreach (var c in All) if (c.Id == id) return c;
            return null;
        }

        public static CommandInfo ByName(string name)
        {
            foreach (var c in All) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
    }

    /// <summary>One button of the UEToVS toolbar (configurable in the settings window).</summary>
    public sealed class ToolbarButtonInfo
    {
        public ToolbarButtonInfo(string key, int commandId, string actionId, string zh, string en)
        {
            Key = key;
            CommandId = commandId;
            ActionId = actionId;
            TitleZh = zh;
            TitleEn = en;
        }

        public string Key { get; }
        public int CommandId { get; }
        public string ActionId { get; }
        public string TitleZh { get; }
        public string TitleEn { get; }
    }

    public static class ToolbarButtons
    {
        /// <summary>Left to right; "settings" is the brand-icon button that opens the settings window.</summary>
        public static IReadOnlyList<ToolbarButtonInfo> All { get; } = new[]
        {
            new ToolbarButtonInfo("settings", CommandCatalog.TbSettings, CommandCatalog.ActionIds.Settings, "UEToVS 设置", "UEToVS Settings"),
            new ToolbarButtonInfo("searchEverywhere", CommandCatalog.TbSearchEverywhere, CommandCatalog.ActionIds.SearchEverywhere, "Search Everywhere", "Search Everywhere"),
            new ToolbarButtonInfo("goToDefinition", CommandCatalog.TbGoToDefinition, CommandCatalog.ActionIds.GoToDefinition, "转到定义", "Go to Definition"),
            new ToolbarButtonInfo("findReferences", CommandCatalog.TbFindReferences, CommandCatalog.ActionIds.FindReferences, "查找引用", "Find References"),
            new ToolbarButtonInfo("performance", CommandCatalog.TbPerformance, CommandCatalog.ActionIds.ShowPerformancePanel, "性能面板", "Performance Panel"),
        };

        public static HashSet<string> ParseHidden(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return set;
            foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(part.Trim());
            return set;
        }

        public static string FormatHidden(IEnumerable<string> keys)
        {
            var list = new List<string>();
            foreach (var b in All)
                foreach (var k in keys)
                    if (string.Equals(b.Key, k, StringComparison.OrdinalIgnoreCase)) { list.Add(b.Key); break; }
            return string.Join(",", list);
        }

        /// <summary>The settings (brand) button can't be hidden: it is the toolbar's way into the settings window.</summary>
        public static bool IsVisible(string key, bool showToolbar, HashSet<string> hidden) =>
            showToolbar && (string.Equals(key, "settings", StringComparison.OrdinalIgnoreCase) || hidden == null || !hidden.Contains(key));
    }
}
