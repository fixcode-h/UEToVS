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

    /// <summary>
    /// One UEToVS command: VSCT id, canonical DTE name, the action it dispatches to, titles, and the default shortcut in
    /// each keymap preset. <see cref="Reserved"/> commands have a reserved VSCT id but are implemented by other modules
    /// (M2 navigation, M1 search …); until their action is registered they show as disabled.
    /// </summary>
    public sealed class CommandInfo
    {
        public int Id { get; set; }
        /// <summary>Canonical name as DTE sees it, e.g. "UEToVS.GoToDefinition" (VSCT CanonicalName).</summary>
        public string Name { get; set; }
        public string ActionId { get; set; }
        public string TitleZh { get; set; }
        public string TitleEn { get; set; }
        /// <summary>"navigation" | "search" | "tools" | "settings" | "diagnostics" — groups the keymap page.</summary>
        public string Group { get; set; }
        public CommandScope Scope { get; set; }
        /// <summary>Visual Assist style default (also what the VSCT KeyBinding declares). VS key syntax; null = none.</summary>
        public string VaKeys { get; set; }
        public string RiderKeys { get; set; }
        public string VsKeys { get; set; }
        public bool Reserved { get; set; }
        /// <summary>Shown in the top-level Extensions &gt; UEToVS menu (everything else: Find Action / settings window).</summary>
        public bool InMenu { get; set; }

        public string Title(bool english) => english ? TitleEn : TitleZh;

        public string KeysFor(KeymapPresetId preset)
        {
            switch (preset)
            {
                case KeymapPresetId.Rider: return RiderKeys;
                case KeymapPresetId.VisualStudio: return VsKeys;
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
    /// The single list of UEToVS commands (ids must match UEToVSHostPackage.vsct; a unit test checks the VSCT key
    /// bindings against <see cref="CommandInfo.VaKeys"/>). Id ranges: 0x01xx existing, 0x02xx M2 navigation,
    /// 0x03xx M1 features, 0x04xx settings / windows / toolbar.
    /// </summary>
    public static class CommandCatalog
    {
        // ---- existing (0x01xx)
        public const int OpenLogFolder = 0x0100;
        public const int OpenCurrentLog = 0x0101;
        public const int CreateDiagnostics = 0x0102;
        public const int ShowPerformancePanel = 0x0104;
        public const int RestartEngine = 0x0110;
        public const int ToggleIntelliSense = 0x0120;
        public const int ShowComponentGallery = 0x0130;
        public const int Settings = 0x0140;
        public const int FindAction = 0x0150;

        // ---- M2 navigation (0x0200–0x02FF), implemented by the navigation module
        public const int GoToDefinition = 0x0200;
        public const int GoToImplementation = 0x0201;
        public const int FindReferences = 0x0202;
        public const int TypeHierarchy = 0x0203;
        public const int NavigateBack = 0x0204;
        public const int NavigateForward = 0x0205;
        public const int FileMembers = 0x0206;
        public const int ToggleHeaderSource = 0x0207;

        // ---- M1 features (0x0300–0x03FF)
        public const int SearchEverywhere = 0x0300;
        public const int OpenFile = 0x0301;
        public const int SearchSymbol = 0x0302;
        public const int ToggleBookmark = 0x0303;
        public const int ShowBookmarks = 0x0304;
        public const int RecentLocations = 0x0305;

        // ---- settings / windows (0x0400–0x04FF)
        public const int Toolbar = 0x0400;
        public const int ToolbarGroup = 0x0401;
        public const int MenuNavigationGroup = 0x0402;
        /// <summary>Toolbar-only buttons (separate ids so each can be hidden from the settings window).</summary>
        public const int TbSettings = 0x0480;
        public const int TbSearchEverywhere = 0x0481;
        public const int TbBack = 0x0482;
        public const int TbForward = 0x0483;
        public const int TbGoToDefinition = 0x0484;
        public const int TbFindReferences = 0x0485;
        public const int TbPerformance = 0x0486;

        public static class ActionIds
        {
            public const string GoToDefinition = "uetovs.nav.goToDefinition";
            public const string GoToImplementation = "uetovs.nav.goToImplementation";
            public const string FindReferences = "uetovs.nav.findReferences";
            public const string TypeHierarchy = "uetovs.nav.typeHierarchy";
            public const string NavigateBack = "uetovs.nav.back";
            public const string NavigateForward = "uetovs.nav.forward";
            public const string FileMembers = "uetovs.nav.fileMembers";
            public const string ToggleHeaderSource = "uetovs.nav.toggleHeaderSource";
            public const string SearchEverywhere = "uetovs.search.everywhere";
            public const string OpenFile = "uetovs.search.openFile";
            public const string SearchSymbol = "uetovs.search.symbol";
            public const string ToggleBookmark = "uetovs.bookmarks.toggle";
            public const string ShowBookmarks = "uetovs.bookmarks.show";
            public const string RecentLocations = "uetovs.history.recentLocations";
        }

        private static CommandInfo C(int id, string name, string action, string zh, string en, string group, CommandScope scope,
                                     string va, string rider, string vs, bool reserved = false, bool inMenu = false) =>
            new CommandInfo
            {
                Id = id, Name = "UEToVS." + name, ActionId = action, TitleZh = zh, TitleEn = en, Group = group, Scope = scope,
                VaKeys = va, RiderKeys = rider, VsKeys = vs, Reserved = reserved, InMenu = inMenu,
            };

        public static IReadOnlyList<CommandInfo> All { get; } = new[]
        {
            // navigation (M2) — VA keys as the user asked: Alt+G / Alt+Shift+G / Shift+Alt+F / Alt+M / Alt+O / Alt+←/→ / Ctrl+Alt+H
            C(GoToDefinition, "GoToDefinition", ActionIds.GoToDefinition, "转到定义", "Go to Definition", "navigation", CommandScope.TextEditor,
              "Alt+G", "Ctrl+B", "F12", reserved: true, inMenu: true),
            C(GoToImplementation, "GoToImplementation", ActionIds.GoToImplementation, "转到实现", "Go to Implementation", "navigation", CommandScope.TextEditor,
              "Shift+Alt+G", "Ctrl+Alt+B", "Ctrl+F12", reserved: true, inMenu: true),
            C(FindReferences, "FindReferences", ActionIds.FindReferences, "查找引用", "Find References", "navigation", CommandScope.TextEditor,
              "Shift+Alt+F", "Alt+F7", "Shift+F12", reserved: true, inMenu: true),
            C(TypeHierarchy, "TypeHierarchy", ActionIds.TypeHierarchy, "类型层次", "Type Hierarchy", "navigation", CommandScope.TextEditor,
              "Ctrl+Alt+H", "Ctrl+H", null, reserved: true, inMenu: true),
            C(NavigateBack, "NavigateBack", ActionIds.NavigateBack, "后退", "Navigate Back", "navigation", CommandScope.Global,
              "Alt+Left Arrow", "Ctrl+Alt+Left Arrow", "Ctrl+-", reserved: true, inMenu: true),
            C(NavigateForward, "NavigateForward", ActionIds.NavigateForward, "前进", "Navigate Forward", "navigation", CommandScope.Global,
              "Alt+Right Arrow", "Ctrl+Alt+Right Arrow", "Ctrl+Shift+-", reserved: true, inMenu: true),
            C(FileMembers, "FileMembers", ActionIds.FileMembers, "文件成员", "File Members", "navigation", CommandScope.TextEditor,
              "Alt+M", "Ctrl+F12", null, reserved: true, inMenu: true),
            C(ToggleHeaderSource, "ToggleHeaderSource", ActionIds.ToggleHeaderSource, "头文件/源文件切换", "Toggle Header/Source", "navigation", CommandScope.TextEditor,
              "Alt+O", "Ctrl+Alt+Home", "Ctrl+K, Ctrl+O", reserved: true, inMenu: true),

            // search (M1); Search Everywhere is also "double Shift" (DoubleShiftDetector), which VSCT cannot express
            C(SearchEverywhere, "SearchEverywhere", ActionIds.SearchEverywhere, "Search Everywhere", "Search Everywhere", "search", CommandScope.Global,
              null, null, null, reserved: true, inMenu: true),
            C(OpenFile, "OpenFile", ActionIds.OpenFile, "打开文件", "Open File in Solution", "search", CommandScope.Global,
              "Shift+Alt+O", "Ctrl+Shift+N", "Ctrl+Shift+T", reserved: true),
            C(SearchSymbol, "SearchSymbol", ActionIds.SearchSymbol, "搜索符号", "Find Symbol", "search", CommandScope.Global,
              "Shift+Alt+S", "Ctrl+Shift+Alt+N", "Ctrl+1, Ctrl+S", reserved: true),
            C(FindAction, "FindAction", "uetovs.findAction", "查找操作", "Find Action", "search", CommandScope.Global,
              "Ctrl+Shift+Alt+A", "Ctrl+Shift+A", null, inMenu: true),
            C(ToggleBookmark, "ToggleBookmark", ActionIds.ToggleBookmark, "切换书签", "Toggle Bookmark", "search", CommandScope.TextEditor,
              null, "F11", "Ctrl+K, Ctrl+K", reserved: true),
            C(ShowBookmarks, "ShowBookmarks", ActionIds.ShowBookmarks, "书签列表", "Bookmarks", "search", CommandScope.Global,
              null, "Shift+F11", "Ctrl+K, Ctrl+W", reserved: true),
            C(RecentLocations, "RecentLocations", ActionIds.RecentLocations, "最近位置", "Recent Locations", "search", CommandScope.Global,
              null, "Ctrl+Shift+E", null, reserved: true),

            // tools / settings / diagnostics
            C(ShowPerformancePanel, "PerformancePanel", "uetovs.diagnostics.performancePanel", "性能面板", "Performance Panel", "tools", CommandScope.Global,
              null, null, null, inMenu: true),
            C(Settings, "Settings", "uetovs.settings.open", "设置…", "Settings...", "settings", CommandScope.Global,
              null, "Ctrl+Alt+S", null, inMenu: true),
            C(RestartEngine, "RestartEngine", "uetovs.engine.restart", "重启引擎", "Restart Engine", "tools", CommandScope.Global, null, null, null),
            C(ToggleIntelliSense, "ToggleIntelliSense", "uetovs.intellisense.takeOver", "接管/恢复 VS C++ IntelliSense", "Take Over / Restore C++ IntelliSense", "settings", CommandScope.Global, null, null, null),
            C(OpenLogFolder, "OpenLogFolder", "uetovs.diagnostics.openLogFolder", "打开日志文件夹", "Open Log Folder", "diagnostics", CommandScope.Global, null, null, null),
            C(OpenCurrentLog, "OpenCurrentLog", "uetovs.diagnostics.openCurrentLog", "打开当前日志", "Open Current Log", "diagnostics", CommandScope.Global, null, null, null),
            C(CreateDiagnostics, "CreateDiagnosticsPackage", "uetovs.diagnostics.createPackage", "生成诊断包", "Create Diagnostics Package", "diagnostics", CommandScope.Global, null, null, null),
            C(ShowComponentGallery, "ShowComponentGallery", "uetovs.ui.componentGallery", "组件画廊", "Component Gallery", "diagnostics", CommandScope.Global, null, null, null),
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
            new ToolbarButtonInfo("settings", CommandCatalog.TbSettings, "uetovs.settings.open", "UEToVS 设置", "UEToVS Settings"),
            new ToolbarButtonInfo("searchEverywhere", CommandCatalog.TbSearchEverywhere, CommandCatalog.ActionIds.SearchEverywhere, "Search Everywhere", "Search Everywhere"),
            new ToolbarButtonInfo("back", CommandCatalog.TbBack, CommandCatalog.ActionIds.NavigateBack, "后退", "Navigate Back"),
            new ToolbarButtonInfo("forward", CommandCatalog.TbForward, CommandCatalog.ActionIds.NavigateForward, "前进", "Navigate Forward"),
            new ToolbarButtonInfo("goToDefinition", CommandCatalog.TbGoToDefinition, CommandCatalog.ActionIds.GoToDefinition, "转到定义", "Go to Definition"),
            new ToolbarButtonInfo("findReferences", CommandCatalog.TbFindReferences, CommandCatalog.ActionIds.FindReferences, "查找引用", "Find References"),
            new ToolbarButtonInfo("performance", CommandCatalog.TbPerformance, "uetovs.diagnostics.performancePanel", "性能面板", "Performance Panel"),
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
