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
        /// <summary>One sentence on what the command does (Find Action rows, keymap page). Written so the title + this line need no manual.</summary>
        public string DescriptionZh { get; set; }
        public string DescriptionEn { get; set; }
        /// <summary>
        /// One of <see cref="CommandCatalog.Groups"/> ("search" | "navigation" | "history" | "bookmarks" | "refactor" | "generate" |
        /// "editing" | "vcs" | "unreal" | "build" | "tools" | "diagnostics"): the same categories as the Extensions &gt; UEToVS submenus,
        /// used by the keymap page and Find Action.
        /// </summary>
        public string Group { get; set; }
        public CommandScope Scope { get; set; }
        /// <summary>Visual Assist style default (also what the VSCT KeyBinding declares). VS key syntax. Every command has one (tools use the "Ctrl+Shift+Alt+U, x" chords).</summary>
        public string VaKeys { get; set; }
        public string RiderKeys { get; set; }
        public string VsKeys { get; set; }

        /// <summary>
        /// Earlier <see cref="VaKeys"/> defaults. A command still bound to exactly one of these was given it by UEToVS, not
        /// chosen by the user, so the first-run step moves it to the new default (<see cref="DefaultKeymapPlanner"/>).
        /// </summary>
        public string[] FormerVaKeys { get; set; }

        public CommandOwner Owner { get; set; }

        public string Title(bool english) => english ? TitleEn : TitleZh;

        public string Description(bool english) => english ? DescriptionEn : DescriptionZh;

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
        /// <summary>注释翻译 (Translation/TranslationActions.cs): editor context menu.</summary>
        public const int TranslateSelection = 0x0165;
        // ---- v0.4.5 构建结果 / 错误列表 / 新建 UE 类 (0x0170–0x017F)
        public const int NewUeClass = 0x0170;
        public const int NextBuildError = 0x0171;
        public const int ClearBuildErrors = 0x0172;
        // ---- v0.4.5 编辑体验 (0x0180–0x019F): Editing/EditorFeatureActions.cs
        public const int SurroundWith = 0x0180;
        public const int ToggleInlayHints = 0x0181;
        public const int ShowHashtags = 0x0182;
        public const int PasteFromHistory = 0x0183;
        /// <summary>v0.9.4 引擎代码只读：当前引擎文件 启用编辑 / 恢复只读（Editing/EngineReadOnlyFeature.cs）。</summary>
        public const int ToggleEngineReadOnly = 0x0184;
        // ---- v0.9.12 写代码辅助 (0x01C0–0x01CF): Editing/SpecifierEditorFeature.cs
        /// <summary>UPROPERTY / UFUNCTION / UCLASS / USTRUCT 说明符编辑器。</summary>
        public const int EditSpecifiers = 0x01C0;
        // ---- v0.4.5 heavy features (0x01A0–0x01AF): Inspection/HeavyFeatureActions.cs
        public const int RunProjectInspection = 0x01A0;
        public const int ShowInspectionResults = 0x01A1;
        public const int RunUpgradeAssistant = 0x01A2;
        public const int ShowUpgradeResults = 0x01A3;
        public const int CancelHeavyJob = 0x01A4;
        // ---- v0.10 模块依赖图 (0x01D0–0x01DF): Unreal/ModuleGraphFeature.cs
        public const int ShowModuleGraph = 0x01D0;
        // ---- 版本信息（Git / Perforce 只读，0x01B0–0x01BF）：Vcs/VcsActions.cs
        public const int ToggleInlineBlame = 0x01B0;
        public const int ToggleBlameGutter = 0x01B1;
        public const int NextChange = 0x01B2;
        public const int PreviousChange = 0x01B3;
        public const int ShowChangeAtCaret = 0x01B4;
        public const int CompareWithBase = 0x01B5;
        public const int SelectionHistory = 0x01B6;
        public const int FunctionHistory = 0x01B7;

        // ---- M2 navigation (0x0200–0x02FF): Navigation/NavigationActions.cs
        public const int GoToDefinition = 0x0200;
        public const int GoToImplementation = 0x0201;
        public const int FindReferences = 0x0202;
        public const int TypeHierarchy = 0x0203;
        public const int ListMembersInFile = 0x0204;
        public const int ListTypeMembers = 0x0205;
        // ---- v0.4.5 navigation gaps (0x0210–0x022F): Navigation/CallHierarchyFeature.cs
        public const int CallHierarchy = 0x0210;

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
        public const int GenerateProjectFiles = 0x0361;

        // ---- settings / windows / toolbar (0x0400–0x04FF)
        public const int Toolbar = 0x0400;
        /// <summary>Toolbar-only buttons (separate ids so each can be hidden from the settings window).</summary>
        public const int TbSettings = 0x0480;
        public const int TbSearchEverywhere = 0x0481;
        // 0x0482 / 0x0483 were the toolbar Back / Forward buttons (removed in v0.4.5: VS has its own); ids stay reserved.
        public const int TbGoToDefinition = 0x0484;
        public const int TbFindReferences = 0x0485;
        public const int TbPerformance = 0x0486;
        // ---- v0.4.5 构建结果 / 新建 UE 类
        public const int TbGenerateProjectFiles = 0x0487;

        // ---- M4 refactoring (0x0700–0x07FF): Refactoring/RefactoringFeature.cs
        public const int QuickActions = 0x0700;
        public const int Rename = 0x0701;
        public const int ImplementVirtual = 0x0702;
        // ---- v0.4.5 refactoring batch 2 (0x0710–0x073F): Refactoring/Batch2/*
        public const int ChangeSignature = 0x0710;
        public const int MoveImplementation = 0x0711;
        public const int AddForwardDeclaration = 0x0712;
        public const int IntroduceVariable = 0x0713;
        public const int ExtractMethod = 0x0714;
        public const int EncapsulateField = 0x0715;
        public const int GenerateGetterSetter = 0x0716;

        /// <summary>Action ids of the M1/M2 commands (mirrors NavigationActionIds / M1ActionIds, which are host-internal).</summary>
        public static class ActionIds
        {
            public const string GoToDefinition = "uetovs.navigation.goToDefinition";
            public const string GoToImplementation = "uetovs.navigation.goToImplementation";
            public const string FindReferences = "uetovs.navigation.findReferences";
            public const string TypeHierarchy = "uetovs.navigation.typeHierarchy";
            public const string ListMembersInFile = "uetovs.navigation.listMembersInFile";
            public const string ListTypeMembers = "uetovs.navigation.listTypeMembers";
            // ---- v0.4.5 navigation gaps
            public const string CallHierarchy = "uetovs.navigation.callHierarchy";
            public const string PeekDefinition = "uetovs.navigation.peekDefinition";
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
            public const string GenerateProjectFiles = "uetovs.unreal.generateProjectFiles";

            // host (mirror of Shell/ActionIds)
            public const string FindAction = "uetovs.findAction";
            public const string Settings = "uetovs.settings.open";
            public const string ShowPerformancePanel = "uetovs.diagnostics.performancePanel";

            // M4 refactoring (mirror of Logic/Refactoring/RefactorActionIds)
            public const string QuickActions = "uetovs.refactor.quickActions";
            public const string Rename = "uetovs.refactor.rename";
            public const string ImplementVirtual = "uetovs.refactor.implementVirtual";

            // ---- v0.4.5 构建结果 / 错误列表 / 新建 UE 类 (Build/BuildFeature.cs)
            public const string NewUeClass = "uetovs.unreal.newClass";
            public const string NextBuildError = "uetovs.build.nextError";
            public const string ClearBuildErrors = "uetovs.build.clearErrors";
            // ---- v0.4.5 refactoring batch 2 (mirror of RefactorActionIds)
            public const string ChangeSignature = "uetovs.refactor.changeSignature";
            public const string MoveImplementation = "uetovs.refactor.moveImplementation";
            public const string AddForwardDeclaration = "uetovs.refactor.addForwardDeclaration";
            public const string IntroduceVariable = "uetovs.refactor.introduceVariable";
            public const string ExtractMethod = "uetovs.refactor.extractMethod";
            public const string EncapsulateField = "uetovs.refactor.encapsulateField";
            public const string GenerateGetterSetter = "uetovs.generate.getterSetter";
            // ---- v0.5.12 灯泡菜单（mirror of Shell/ActionIds）
            public const string GenerateConstructor = "uetovs.generate.constructor";
            public const string GenerateReplication = "uetovs.generate.replication";
            public const string GenerateRpc = "uetovs.generate.rpc";
            public const string TranslateSelection = "uetovs.translate.selection";
            // ---- 版本信息（mirror of Logic/Localization/VcsStrings.VcsIds）
            public const string VcsToggleInlineBlame = "uetovs.vcs.toggleInlineBlame";
            public const string VcsToggleBlameGutter = "uetovs.vcs.toggleBlameGutter";
            public const string VcsNextChange = "uetovs.vcs.nextChange";
            public const string VcsPreviousChange = "uetovs.vcs.previousChange";
            public const string VcsShowChange = "uetovs.vcs.showChange";
            public const string VcsCompareWithBase = "uetovs.vcs.compareWithBase";
            public const string VcsSelectionHistory = "uetovs.vcs.selectionHistory";
            public const string VcsFunctionHistory = "uetovs.vcs.functionHistory";
        }

        private static CommandInfo C(CommandOwner owner, int id, string name, string action, string zh, string en, string group, CommandScope scope,
                                     string va, string rider, string vs, string descZh, string descEn) =>
            new CommandInfo
            {
                Owner = owner, Id = id, Name = "UEToVS." + name, ActionId = action, TitleZh = zh, TitleEn = en, Group = group, Scope = scope,
                VaKeys = va, RiderKeys = rider, VsKeys = vs, DescriptionZh = descZh, DescriptionEn = descEn,
            };

        /// <summary>Marks the earlier defaults of a command whose default changed (see <see cref="CommandInfo.FormerVaKeys"/>).</summary>
        private static CommandInfo Former(CommandInfo c, params string[] keys)
        {
            c.FormerVaKeys = keys;
            return c;
        }

        private const CommandOwner H = CommandOwner.Host, M2 = CommandOwner.Navigation, M1 = CommandOwner.Features;
        private const CommandOwner M4 = CommandOwner.Refactoring;
        private const CommandScope Ed = CommandScope.TextEditor, Gl = CommandScope.Global;

        public static IReadOnlyList<CommandInfo> All { get; } = new[]
        {
            // Order = menu order (Extensions > UEToVS); the keymap page and Find Action group by Group in this order.
            // ---- search
            C(H, FindAction, "FindAction", ActionIds.FindAction, "查找操作…", "Find Action...", "search", Gl, "Ctrl+Shift+Alt+A", "Ctrl+Shift+A", null,
              "输入名字搜索 UEToVS 的任意命令并执行", "Search every UEToVS command by name and run it"),
            // Search Everywhere is also "double Shift" (M1 InputMonitor), which VSCT cannot express
            C(M1, SearchEverywhere, "SearchEverywhere", ActionIds.SearchEverywhere, "全局搜索…", "Search Everywhere...", "search", Gl, "Ctrl+T", null, null,
              "在类型、符号、文件和蓝图资产里一起搜索（也可以连按两次 Shift）", "Search types, symbols, files and Blueprint assets at once (or press Shift twice)"),
            C(M1, GoToType, "GoToType", ActionIds.GoToType, "转到类型…", "Go to Type...", "search", Gl, "Shift+Alt+T", "Ctrl+N", null,
              "按名字找类、结构体或枚举并跳过去", "Find a class, struct or enum by name and jump to it"),
            C(M1, GoToSymbol, "GoToSymbol", ActionIds.GoToSymbol, "转到符号…", "Go to Symbol...", "search", Gl, "Shift+Alt+S", "Ctrl+Shift+Alt+N", null,
              "按名字找函数、变量、宏等任意符号并跳过去", "Find any function, variable, macro or other symbol by name and jump to it"),
            C(M1, GoToFile, "GoToFile", ActionIds.GoToFile, "转到文件…", "Go to File...", "search", Gl, "Shift+Alt+O", "Ctrl+Shift+N", null,
              "按文件名打开项目或引擎里的文件", "Open a project or engine file by name"),
            C(M1, GoToAsset, "GoToAsset", ActionIds.GoToAsset, "转到蓝图资产…", "Go to Blueprint Asset...", "search", Gl, "Shift+Alt+B", null, null,
              "按名字找蓝图等 .uasset 资产，复制它的对象路径", "Find a Blueprint or other .uasset by name and copy its object path"),

            // ---- code navigation (M2 / M1, text editor). VA keys = the VSCT defaults
            C(M2, GoToDefinition, "GoToDefinition", ActionIds.GoToDefinition, "转到定义 / 声明", "Go to Definition / Declaration", "navigation", Ed, "Alt+G", "Ctrl+B", "F12",
              "跳到光标处符号的定义；已经在定义上时跳到声明（Ctrl+点击同效）", "Jump to the definition of the symbol at the caret; on the definition, jump to the declaration (same as Ctrl+Click)"),
            C(M2, GoToImplementation, "GoToImplementation", ActionIds.GoToImplementation, "转到实现", "Go to Implementation", "navigation", Ed, "Shift+Alt+G", "Ctrl+Alt+B", "Ctrl+F12",
              "跳到函数在源文件里的实现；有多个时（如虚函数的重写）列出来选", "Jump to the function's implementation; several (e.g. overrides) are listed to choose from"),
            C(M2, FindReferences, "FindReferences", ActionIds.FindReferences, "查找所有引用", "Find All References", "navigation", Ed, "Shift+Alt+F", "Alt+F7", "Shift+F12",
              "列出光标处符号在项目里被用到的所有地方", "List every place in the project that uses the symbol at the caret"),
            // VS: View Call Hierarchy = Ctrl+K, Ctrl+T
            C(M2, CallHierarchy, "CallHierarchy", ActionIds.CallHierarchy, "调用层次", "Call Hierarchy", "navigation", Ed, "Ctrl+Shift+Alt+U, H", "Ctrl+Alt+H", "Ctrl+K, Ctrl+T",
              "查看谁调用了这个函数，以及它又调用了谁", "See who calls this function and what it calls"),
            C(M2, TypeHierarchy, "TypeHierarchy", ActionIds.TypeHierarchy, "类型层次", "Type Hierarchy", "navigation", Ed, "Shift+Alt+H", "Ctrl+H", null,
              "查看光标处类的基类和所有子类", "See the base classes and all subclasses of the class at the caret"),
            C(M2, ListMembersInFile, "ListMembersInFile", ActionIds.ListMembersInFile, "当前文件的成员…", "Members of This File...", "navigation", Ed, "Alt+M", null, null,
              "弹出当前文件里的类、函数和变量列表，选中即跳转", "Pop up the classes, functions and variables of this file and jump to one"),
            C(M2, ListTypeMembers, "ListTypeMembers", ActionIds.ListTypeMembers, "类的成员（含继承）…", "Class Members (incl. Inherited)...", "navigation", Ed, "Shift+Alt+M", null, null,
              "列出光标处类的全部成员，包括从基类继承来的", "List every member of the class at the caret, including inherited ones"),
            C(M1, FileStructure, "FileStructure", ActionIds.FileStructure, "文件结构…", "File Structure...", "navigation", Gl, "Ctrl+Shift+Alt+M", "Ctrl+F12", null,
              "按类和区域分层显示当前文件的结构，可输入过滤", "Show the current file as a tree of classes and members, with filtering"),
            C(M1, NavigateTo, "NavigateTo", ActionIds.NavigateTo, "跳转到…", "Navigate To...", "navigation", Gl, "Shift+Alt+N", null, null,
              "列出从光标处能去的所有地方：定义、实现、引用、基类、相关文件等", "List everywhere you can go from the caret: definition, implementation, usages, base types, related files..."),
            C(M1, ToggleHeaderSource, "ToggleHeaderSource", ActionIds.ToggleHeaderSource, "切换头文件 / 源文件", "Switch Header / Source", "navigation", Gl, "Alt+O", "Ctrl+Alt+Home", "Ctrl+K, Ctrl+O",
              "在 .h 和对应的 .cpp 之间来回切换（认得 Public / Private 目录）", "Switch between the .h and its .cpp (aware of Public / Private folders)"),
            C(M1, GoToRelated, "GoToRelated", ActionIds.GoToRelated, "相关文件和函数…", "Related Files and Functions...", "navigation", Gl, "Shift+Alt+R", null, null,
              "列出与当前位置相关的内容：头/源文件、.generated.h、RPC 的 _Implementation / _Validate、Build.cs", "List what belongs together: header / source, .generated.h, RPC _Implementation / _Validate, Build.cs"),

            // ---- navigation history (M1, global)
            C(M1, NavigateBack, "NavigateBack", ActionIds.NavigateBack, "后退", "Back", "history", Gl, "Alt+Left Arrow", "Ctrl+Alt+Left Arrow", "Ctrl+-",
              "回到跳转前的位置", "Go back to where you were before the last jump"),
            C(M1, NavigateForward, "NavigateForward", ActionIds.NavigateForward, "前进", "Forward", "history", Gl, "Alt+Right Arrow", "Ctrl+Alt+Right Arrow", "Ctrl+Shift+-",
              "撤销一次后退", "Undo the last Back"),
            C(M1, RecentFiles, "RecentFiles", ActionIds.RecentFiles, "最近的文件…", "Recent Files...", "history", Gl, "Shift+Alt+E", "Ctrl+E", null,
              "列出最近打开过的文件", "List the files you opened recently"),
            C(M1, RecentLocations, "RecentLocations", ActionIds.RecentLocations, "最近的位置…", "Recent Locations...", "history", Gl, "Shift+Alt+L", "Ctrl+Shift+E", null,
              "列出最近停留过的代码位置，带代码预览", "List the code locations you visited recently, with a preview"),
            C(M1, LastEditLocation, "LastEditLocation", ActionIds.LastEditLocation, "上一个修改位置", "Previous Edit Location", "history", Gl, "Ctrl+Shift+Bkspce", "Ctrl+Shift+Bkspce", null,
              "跳回最近改过代码的地方，连按继续往前", "Jump back to where you last edited; repeat to go further back"),
            C(M1, NextEditLocation, "NextEditLocation", ActionIds.NextEditLocation, "下一个修改位置", "Next Edit Location", "history", Gl, "Ctrl+Shift+Alt+Bkspce", null, null,
              "在修改位置之间往回走（和“上一个修改位置”相反）", "Move forward through edit locations (the reverse of Previous Edit Location)"),

            // ---- bookmarks (M1 / host)
            C(M1, ToggleBookmark, "ToggleBookmark", ActionIds.ToggleBookmark, "添加 / 删除书签", "Toggle Bookmark", "bookmarks", Gl, "Ctrl+F2", "F11", null,
              "在当前行加书签，已经有书签就删掉", "Add a bookmark on the current line, or remove the one there"),
            C(M1, ShowBookmarks, "ShowBookmarks", ActionIds.ShowBookmarks, "书签列表…", "Bookmark List...", "bookmarks", Gl, "Ctrl+Shift+F2", "Shift+F11", null,
              "列出所有书签，选中即跳转", "List all bookmarks and jump to one"),
            C(H, ShowHashtags, "ShowHashtags", "uetovs.edit.hashtags", "#tag 注释列表…", "#tag Comments...", "bookmarks", Gl, "Ctrl+Shift+Alt+U, 3", null, null,
              "按标签列出已打开文件注释里写的 #todo、#bug 等，选中即跳转", "List the #todo, #bug... tags written in comments of open files, grouped by tag"),

            // ---- refactor (M4, text editor). Context actions (falls back to VS's quick actions when UEToVS IntelliSense is off).
            // v0.7.7: Shift+Alt+Q (user request) instead of Alt+Enter; the Rider preset keeps Rider's own Alt+Enter.
            Former(C(M4, QuickActions, "QuickActions", ActionIds.QuickActions, "上下文操作…", "Context Actions...", "refactor", Ed, "Shift+Alt+Q", "Alt+Enter", null,
              "列出光标处能做的修复、生成和重构", "List the fixes, generators and refactorings available at the caret"), "Alt+Enter"),
            C(M4, Rename, "Rename", ActionIds.Rename, "重命名…", "Rename...", "refactor", Ed, "Ctrl+R, Ctrl+R", "Shift+F6", "Ctrl+R, Ctrl+R",
              "重命名光标处的符号，并改掉所有引用", "Rename the symbol at the caret and update every reference"),
            // Ctrl+R chords; UEToVS wins over VS's own Refactor.* bindings and falls back to them outside UE C++
            C(M4, ChangeSignature, "ChangeSignature", ActionIds.ChangeSignature, "更改函数签名…", "Change Signature...", "refactor", Ed, "Ctrl+R, Ctrl+O", "Ctrl+F6", null,
              "增加、删除或调整参数，同时改声明、定义和所有调用处", "Add, remove or reorder parameters and update the declaration, definition and callers"),
            C(M4, ExtractMethod, "ExtractMethod", ActionIds.ExtractMethod, "提取为函数…", "Extract Method...", "refactor", Ed, "Ctrl+R, Ctrl+M", "Ctrl+Alt+M", null,
              "把选中的代码移到一个新函数里，原处改成调用", "Move the selected code into a new function and call it instead"),
            C(M4, IntroduceVariable, "IntroduceVariable", ActionIds.IntroduceVariable, "提取为变量…", "Introduce Variable...", "refactor", Ed, "Ctrl+R, Ctrl+V", "Ctrl+Alt+V", null,
              "把选中的表达式存进一个新的局部变量", "Store the selected expression in a new local variable"),
            C(M4, EncapsulateField, "EncapsulateField", ActionIds.EncapsulateField, "封装字段…", "Encapsulate Field...", "refactor", Ed, "Ctrl+R, Ctrl+E", null, null,
              "为成员变量生成 Get / Set 函数（可加 BlueprintGetter / Setter）", "Generate Get / Set functions for a member variable (optionally BlueprintGetter / Setter)"),
            C(M4, MoveImplementation, "MoveImplementation", ActionIds.MoveImplementation, "移动函数实现（头文件 ↔ 源文件）", "Move Implementation (Header ↔ Source)", "refactor", Ed, "Ctrl+R, Ctrl+I", "F6", null,
              "把函数体从 .h 移到 .cpp，或从 .cpp 移回 .h", "Move a function body from the .h to the .cpp, or back"),
            C(M4, AddForwardDeclaration, "AddForwardDeclaration", ActionIds.AddForwardDeclaration, "添加前置声明", "Add Forward Declaration", "refactor", Ed, "Ctrl+R, Ctrl+F", null, null,
              "在头文件里为光标处的类型加 class X;，代替 #include", "Declare the type at the caret with class X; in the header instead of an #include"),
            C(M4, ImplementVirtual, "ImplementVirtual", ActionIds.ImplementVirtual, "实现 / 重写虚函数…", "Implement / Override Virtual Functions...", "refactor", Ed, "Ctrl+Shift+Alt+U, V", "Ctrl+O", null,
              "勾选基类的虚函数，生成重写的声明和实现", "Pick base-class virtual functions and generate their override declarations and bodies"),

            // ---- generate code
            C(H, GenerateConstructor, "GenerateConstructor", "uetovs.generate.constructor", "生成构造函数", "Generate Constructor", "generate", Gl, "Ctrl+Shift+Alt+U, K", null, null,
              "为光标处的类生成构造函数声明，并在 .cpp 里加空实现", "Declare a constructor for the class at the caret and add an empty body in the .cpp"),
            C(M4, GenerateGetterSetter, "GenerateGetterSetter", ActionIds.GenerateGetterSetter, "生成 Getter / Setter…", "Generate Getter / Setter...", "generate", Ed, "Ctrl+R, Ctrl+P", null, null,
              "为选中的成员变量生成 Get / Set 函数", "Generate Get / Set functions for the selected member variables"),
            C(H, GenerateReplication, "GenerateReplication", "uetovs.generate.replication", "生成属性同步（GetLifetimeReplicatedProps）", "Generate Replication (GetLifetimeReplicatedProps)", "generate", Gl, "Ctrl+Shift+Alt+U, Y", null, null,
              "为标了 Replicated / ReplicatedUsing 的属性生成 DOREPLIFETIME 注册", "Register the Replicated / ReplicatedUsing properties with DOREPLIFETIME"),
            C(H, GenerateRpc, "GenerateRpc", "uetovs.generate.rpc", "生成 RPC 函数体（_Implementation / _Validate）", "Generate RPC Bodies (_Implementation / _Validate)", "generate", Gl, "Ctrl+Shift+Alt+U, W", null, null,
              "为 Server / Client / NetMulticast 函数生成 _Implementation 和 _Validate", "Generate _Implementation and _Validate for Server / Client / NetMulticast functions"),
            C(H, SurroundWith, "SurroundWith", "uetovs.edit.surroundWith", "用代码块包围…", "Surround With...", "generate", Ed, "Ctrl+Shift+Alt+U, 1", "Ctrl+Alt+J", null,
              "用 if、for、#if WITH_EDITOR 等模板包住选中的代码", "Wrap the selected code in if, for, #if WITH_EDITOR or another template"),

            // ---- editing
            C(H, ExpandSelection, "ExpandSelection", "uetovs.edit.expandSelection", "扩大选区", "Expand Selection", "editing", Ed, "Shift+Alt+W", null, null,
              "按语法一层层扩大选中范围：单词 → 表达式 → 语句 → 代码块", "Grow the selection by syntax: word → expression → statement → block"),
            C(H, ShrinkSelection, "ShrinkSelection", "uetovs.edit.shrinkSelection", "缩小选区", "Shrink Selection", "editing", Ed, "Ctrl+Shift+Alt+W", null, null,
              "撤回一次扩大选区", "Undo the last Expand Selection"),
            C(H, PasteFromHistory, "PasteFromHistory", "uetovs.edit.pasteFromHistory", "从剪贴板历史粘贴…", "Paste from Clipboard History...", "editing", Ed, "Ctrl+Shift+V", "Ctrl+Shift+V", null,
              "从最近复制过的内容里挑一条粘贴", "Pick one of the things you copied recently and paste it"),
            C(H, ToggleInlayHints, "ToggleInlayHints", "uetovs.edit.toggleInlayHints", "显示 / 隐藏内嵌提示", "Show / Hide Inlay Hints", "editing", Ed, "Ctrl+Shift+Alt+U, 2", null, null,
              "在代码里显示或隐藏参数名、推导类型等灰色小字", "Show or hide the grey parameter-name and type hints inside the code"),
            C(H, ToggleEngineReadOnly, "ToggleEngineReadOnly", "uetovs.edit.toggleEngineReadOnly", "引擎文件：启用编辑 / 恢复只读", "Engine File: Enable Editing / Make Read-Only", "editing", Ed, "Ctrl+Shift+Alt+U, E", null, null,
              "引擎目录下的文件默认只读；对当前文件启用编辑，再按一次恢复只读", "Engine files open read-only; enable editing for the current file, press again to lock it"),
            C(H, EditSpecifiers, "EditSpecifiers", "uetovs.edit.specifiers", "编辑反射说明符…", "Edit Reflection Specifiers...", "editing", Ed, "Ctrl+Shift+Alt+U, Shift+S", null, null,
              "光标在 UPROPERTY / UFUNCTION / UCLASS / USTRUCT 上时，用勾选方式编辑说明符和 meta（带说明和冲突提示）", "With the caret on a UPROPERTY / UFUNCTION / UCLASS / USTRUCT, edit its specifiers and meta by checking boxes (with descriptions and conflict hints)"),
            C(H, TranslateSelection, "TranslateSelection", "uetovs.translate.selection", "翻译选中内容", "Translate Selection", "editing", Gl, "Ctrl+Shift+Alt+U, T", null, null,
              "翻译选中的注释或文字，结果在弹窗里显示", "Translate the selected comment or text and show the result in a popup"),

            // ---- 版本信息（Git / Perforce 只读；VS 自带的提交、分支、追溯不动）
            C(H, ToggleInlineBlame, "ToggleInlineBlame", ActionIds.VcsToggleInlineBlame, "显示 / 隐藏行尾追溯", "Show / Hide Inline Blame", "vcs", Ed, "Ctrl+Shift+Alt+U, J", null, null,
              "在光标所在行行尾显示或隐藏“作者 · 多久前 · 提交说明”", "Show or hide \"author · when · commit summary\" at the end of the caret line"),
            C(H, ToggleBlameGutter, "ToggleBlameGutter", ActionIds.VcsToggleBlameGutter, "注解", "Annotate", "vcs", Ed, "Ctrl+Shift+Alt+U, O", null, null,
              "在行号左边显示或隐藏每行的日期和作者（越新底色越蓝），点一行看提交详情", "Show or hide each line's date and author left of the line numbers (bluer for newer); click a line for the commit"),
            C(H, NextChange, "NextChange", ActionIds.VcsNextChange, "下一处改动", "Next Change", "vcs", Ed, "Ctrl+Shift+Alt+U, Down Arrow", "Ctrl+Shift+Alt+Down Arrow", null,
              "跳到下一块和 HEAD（Perforce 为 #have）不同的代码", "Jump to the next block that differs from HEAD (Perforce: #have)"),
            C(H, PreviousChange, "PreviousChange", ActionIds.VcsPreviousChange, "上一处改动", "Previous Change", "vcs", Ed, "Ctrl+Shift+Alt+U, Up Arrow", "Ctrl+Shift+Alt+Up Arrow", null,
              "跳到上一块和 HEAD（Perforce 为 #have）不同的代码", "Jump to the previous block that differs from HEAD (Perforce: #have)"),
            C(H, ShowChangeAtCaret, "ShowChangeAtCaret", ActionIds.VcsShowChange, "查看光标处的改动…", "Show Change at Caret...", "vcs", Ed, "Ctrl+Shift+Alt+U, Z", null, null,
              "弹出光标所在改动块的旧内容，可回滚这一块或复制旧内容（和点色条一样）", "Show the old text of the change at the caret to roll it back or copy it (same as clicking the bar)"),
            C(H, CompareWithBase, "CompareWithBase", ActionIds.VcsCompareWithBase, "和 HEAD / #have 比较", "Compare with HEAD / #have", "vcs", Ed, "Ctrl+Shift+Alt+U, 0", null, null,
              "在 VS 的比较窗口里打开当前文件和 HEAD（Perforce 为 #have）版本的差异", "Open VS's diff window between the current file and HEAD (Perforce: #have)"),
            C(H, SelectionHistory, "SelectionHistory", ActionIds.VcsSelectionHistory, "查看选中部分的历史…", "Show History for Selection...", "vcs", Ed, "Ctrl+Shift+Alt+U, 4", null, null,
              "列出改过选中这几行的提交（git log -L），选一个看那次的 diff", "List the commits that changed the selected lines (git log -L) and open one's diff"),
            C(H, FunctionHistory, "FunctionHistory", ActionIds.VcsFunctionHistory, "查看此函数的历史…", "Show History for Function...", "vcs", Ed, "Ctrl+Shift+Alt+U, 5", null, null,
              "列出改过光标所在函数的提交，函数范围来自 UEToVS 的代码索引", "List the commits that changed the function at the caret (range from the UEToVS index)"),

            // ---- Unreal Engine
            C(M1, GenerateProjectFiles, "GenerateProjectFiles", ActionIds.GenerateProjectFiles, "生成 VS 项目文件", "Generate Visual Studio Project Files", "unreal", Gl, "Ctrl+Shift+Alt+U, G", null, null,
              "调用 UBT 重新生成 .sln 和项目文件（增删源文件或模块后用）", "Run UBT to regenerate the .sln and project files (after adding or removing files or modules)"),
            C(H, NewUeClass, "NewUeClass", ActionIds.NewUeClass, "新建 UE 类…", "New Unreal Class...", "unreal", Gl, "Ctrl+Shift+Alt+U, N", null, null,
              "选父类和模块，生成带 UCLASS 等宏的 .h / .cpp", "Pick a parent class and module and create the .h / .cpp with UCLASS and friends"),
            C(M1, OpenBuildCs, "OpenBuildCs", ActionIds.OpenBuildCs, "打开所属模块的 Build.cs", "Open Module's Build.cs", "unreal", Gl, "Ctrl+Shift+Alt+U, B", null, null,
              "打开当前文件所在模块的 .Build.cs", "Open the .Build.cs of the module the current file belongs to"),
            C(M1, AddModuleDependency, "AddModuleDependency", ActionIds.AddModuleDependency, "为 #include 添加模块依赖", "Add Module Dependency for #include", "unreal", Gl, "Ctrl+Shift+Alt+U, A", null, null,
              "把光标处 #include 的头文件所在模块加进 Build.cs 的依赖", "Add the module of the #include at the caret to the dependencies in Build.cs"),
            C(H, ShowModuleGraph, "ShowModuleGraph", "uetovs.unreal.moduleGraph", "模块依赖图…", "Module Dependency Graph...", "unreal", Gl, "Ctrl+Shift+Alt+U, Shift+B", null, null,
              "查看当前模块或全项目的 Build.cs 依赖（树和图），标出循环依赖、Public 头用到 Private 依赖、可能没用到的依赖",
              "See the Build.cs dependencies of the current module or the whole project (tree and graph), with cycles, Private dependencies used by Public headers and possibly unused ones"),
            // heavy features (each behind a plugin switch in Settings > Inspections / IntelliSense)
            C(H, RunProjectInspection, "RunProjectInspection", "uetovs.inspection.run", "检查整个项目（UE 规则）", "Inspect Whole Project (UE Rules)", "unreal", Gl, "Ctrl+Shift+Alt+U, 6", null, null,
              "在后台按 UE 编码规则检查整个项目，项目大时要几分钟", "Check the whole project against the UE rules in the background (minutes on big projects)"),
            C(H, ShowInspectionResults, "ShowInspectionResults", "uetovs.inspection.show", "查看项目检查结果", "Show Project Inspection Results", "unreal", Gl, "Ctrl+Shift+Alt+U, 7", null, null,
              "打开上一次全项目检查的结果", "Open the results of the last whole-project inspection"),
            C(H, RunUpgradeAssistant, "RunUpgradeAssistant", "uetovs.upgrade.run", "扫描弃用 API（升级引擎前）", "Scan for Deprecated APIs (Engine Upgrade)", "unreal", Gl, "Ctrl+Shift+Alt+U, 8", null, null,
              "找出项目里用到的已弃用引擎 API，方便升级引擎版本", "Find the deprecated engine APIs the project uses, before upgrading the engine"),
            C(H, ShowUpgradeResults, "ShowUpgradeResults", "uetovs.upgrade.show", "查看弃用 API 扫描结果", "Show Deprecated API Scan Results", "unreal", Gl, "Ctrl+Shift+Alt+U, 9", null, null,
              "打开上一次弃用 API 扫描的结果", "Open the results of the last deprecated-API scan"),
            C(H, CancelHeavyJob, "CancelHeavyJob", "uetovs.heavy.cancel", "停止正在运行的检查 / 扫描", "Stop the Running Inspection / Scan", "unreal", Gl, "Ctrl+Shift+Alt+U, U", null, null,
              "停止后台的全项目检查或弃用 API 扫描", "Stop the background project inspection or deprecated-API scan"),

            // ---- build
            C(H, NextBuildError, "NextBuildError", ActionIds.NextBuildError, "跳到下一个编译错误", "Go to Next Build Error", "build", Gl, "Ctrl+Shift+Alt+U, Q", null, null,
              "按顺序跳到上次编译报错的代码行", "Jump to the next line that failed in the last build"),
            C(H, ClearBuildErrors, "ClearBuildErrors", ActionIds.ClearBuildErrors, "清除编译错误标记", "Clear Build Error Marks", "build", Gl, "Ctrl+Shift+Alt+U, X", null, null,
              "去掉编辑器里上次编译留下的错误标记", "Remove the error marks the last build left in the editor"),

            // ---- tools (in the menu: performance panel + settings; the rest are buttons in the settings window and Find Action entries)
            C(H, ShowPerformancePanel, "PerformancePanel", ActionIds.ShowPerformancePanel, "性能面板", "Performance Panel", "tools", Gl, "Ctrl+Shift+Alt+U, P", null, null,
              "查看 UEToVS 的内存、CPU、索引进度和各项操作的耗时", "See UEToVS's memory, CPU, indexing progress and operation timings"),
            C(H, Settings, "Settings", ActionIds.Settings, "设置…", "Settings...", "tools", Gl, "Ctrl+Shift+Alt+U, S", "Ctrl+Alt+S", null,
              "打开 UEToVS 设置窗口（所有选项都在这里）", "Open the UEToVS settings window (every option lives here)"),
            C(H, ToggleIntelliSense, "ToggleIntelliSense", "uetovs.intellisense.takeOver", "接管 / 恢复 VS 的 C++ IntelliSense", "Take Over / Restore VS C++ IntelliSense", "tools", Gl, "Ctrl+Shift+Alt+U, I", null, null,
              "关掉 VS 自带的 C++ IntelliSense 改用 UEToVS，或者恢复成 VS 原来的", "Turn VS's own C++ IntelliSense off in favour of UEToVS, or bring it back"),
            C(H, RestartEngine, "RestartEngine", "uetovs.engine.restart", "重启 UEToVS 代码引擎", "Restart UEToVS Code Engine", "tools", Gl, "Ctrl+Shift+Alt+U, R", null, null,
              "重启后台的代码分析进程，补全或跳转卡住时用", "Restart the background code-analysis process when completion or navigation hangs"),
            C(H, WriteMcpConfig, "WriteMcpConfig", "uetovs.mcp.writeConfig", "为 Agent 写入 .mcp.json", "Write .mcp.json for Agents", "tools", Gl, "Ctrl+Shift+Alt+U, M", null, null,
              "在项目根目录写 .mcp.json，让 Claude Code 等 Agent 能用 UEToVS 的代码索引", "Write .mcp.json at the project root so Claude Code and other agents can use the UEToVS index"),

            // ---- diagnostics
            C(H, OpenLogFolder, "OpenLogFolder", "uetovs.diagnostics.openLogFolder", "打开日志文件夹", "Open Log Folder", "diagnostics", Gl, "Ctrl+Shift+Alt+U, F", null, null,
              "在资源管理器里打开 .vs\\UEToVS\\Logs", "Open .vs\\UEToVS\\Logs in Explorer"),
            C(H, OpenCurrentLog, "OpenCurrentLog", "uetovs.diagnostics.openCurrentLog", "打开当前日志", "Open Current Log", "diagnostics", Gl, "Ctrl+Shift+Alt+U, L", null, null,
              "在 VS 里打开这次运行正在写的日志文件", "Open the log file of this session in VS"),
            C(H, CreateDiagnostics, "CreateDiagnosticsPackage", "uetovs.diagnostics.createPackage", "生成诊断包", "Create Diagnostics Package", "diagnostics", Gl, "Ctrl+Shift+Alt+U, D", null, null,
              "把日志、设置和环境信息打成 zip，反馈问题时附上", "Zip the logs, settings and environment info to attach to a bug report"),
            C(H, ShowComponentGallery, "ShowComponentGallery", "uetovs.ui.componentGallery", "界面组件预览", "UI Component Gallery", "diagnostics", Gl, "Ctrl+Shift+Alt+U, C", null, null,
              "预览 UEToVS 的所有界面组件（开发和调样式用）", "Preview every UEToVS UI component (for development and styling)"),
        };

        public static CommandInfo ById(int id)
        {
            foreach (var c in All) if (c.Id == id) return c;
            return null;
        }

        /// <summary>
        /// Command of <paramref name="actionId"/>, so Find Action shows the menu's title and description. The IntelliSense toggle is
        /// left out: it dispatches to two actions (take over / restore) that keep their own titles.
        /// </summary>
        public static CommandInfo ByActionId(string actionId)
        {
            if (string.IsNullOrEmpty(actionId)) return null;
            foreach (var c in All)
                if (c.Id != ToggleIntelliSense && string.Equals(c.ActionId, actionId, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        /// <summary>Groups in menu order (Extensions &gt; UEToVS submenus, keymap page sections).</summary>
        public static IReadOnlyList<string> Groups { get; } = new[]
        {
            "search", "navigation", "history", "bookmarks", "refactor", "generate", "editing", "vcs", "unreal", "build", "tools", "diagnostics",
        };

        /// <summary>Display name of a group; the same words as the submenu in the VSCT.</summary>
        public static string GroupTitle(string group, bool english)
        {
            switch (group)
            {
                case "search": return english ? "Search" : "搜索";
                case "navigation": return english ? "Code Navigation" : "代码导航";
                case "history": return english ? "Navigation History" : "导航历史";
                case "bookmarks": return english ? "Bookmarks" : "书签";
                case "refactor": return english ? "Refactor" : "重构";
                case "generate": return english ? "Generate Code" : "生成代码";
                case "editing": return english ? "Editing" : "编辑";
                case "vcs": return english ? "Version Control" : "版本管理";
                case "unreal": return "Unreal Engine";
                case "build": return english ? "Build" : "编译";
                case "tools": return english ? "Tools" : "工具";
                case "diagnostics": return english ? "Diagnostics" : "诊断";
                default: return group;
            }
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
            new ToolbarButtonInfo("searchEverywhere", CommandCatalog.TbSearchEverywhere, CommandCatalog.ActionIds.SearchEverywhere, "全局搜索", "Search Everywhere"),
            new ToolbarButtonInfo("goToDefinition", CommandCatalog.TbGoToDefinition, CommandCatalog.ActionIds.GoToDefinition, "转到定义", "Go to Definition"),
            new ToolbarButtonInfo("findReferences", CommandCatalog.TbFindReferences, CommandCatalog.ActionIds.FindReferences, "查找所有引用", "Find All References"),
            // ---- v0.4.5 构建结果 / 新建 UE 类
            new ToolbarButtonInfo("generateProjectFiles", CommandCatalog.TbGenerateProjectFiles, CommandCatalog.ActionIds.GenerateProjectFiles, "生成 VS 项目文件", "Generate Visual Studio Project Files"),
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
