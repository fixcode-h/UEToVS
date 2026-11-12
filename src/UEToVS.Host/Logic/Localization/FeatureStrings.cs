using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>
    /// User-visible strings of the M1 features ported from the prototype (Search Everywhere, navigation history, recent
    /// files/locations, bookmarks, related files, Build.cs dependency fix, UE editor link). Same rules as
    /// <see cref="Strings"/>: Chinese + English via <see cref="Loc.T"/>; kept in a separate file to ease merging.
    /// </summary>
    public static class FeatureStrings
    {
        // ---- commands / actions ----
        public static string CmdSearchEverywhere => Loc.T("全局搜索 (Search Everywhere)", "Search Everywhere");
        public static string CmdGoToType => Loc.T("转到类型…", "Go to Type...");
        public static string CmdGoToSymbol => Loc.T("转到符号…", "Go to Symbol...");
        public static string CmdGoToFile => Loc.T("转到文件…", "Go to File...");
        public static string CmdGoToAsset => Loc.T("转到资产（蓝图）…", "Go to Asset (Blueprint)...");
        public static string CmdNavigateBack => Loc.T("后退", "Navigate Back");
        public static string CmdNavigateForward => Loc.T("前进", "Navigate Forward");
        public static string CmdRecentFiles => Loc.T("最近的文件…", "Recent Files...");
        public static string CmdRecentLocations => Loc.T("最近的位置…", "Recent Locations...");
        public static string CmdLastEditLocation => Loc.T("上一个编辑位置", "Last Edit Location");
        public static string CmdNextEditLocation => Loc.T("下一个编辑位置", "Next Edit Location");
        public static string CmdFileStructure => Loc.T("文件结构…", "File Structure...");
        public static string CmdNavigateTo => Loc.T("跳转到…", "Navigate To...");
        public static string CmdToggleHeaderSource => Loc.T("切换头文件/源文件", "Toggle Header/Source");
        public static string CmdGoToRelated => Loc.T("相关文件和函数…", "Go to Related...");
        public static string CmdOpenBuildCs => Loc.T("打开所属模块的 Build.cs", "Open Module Build.cs");
        public static string CmdAddModuleDependency => Loc.T("为 #include 添加模块依赖", "Add Module Dependency for Include");
        public static string CmdToggleBookmark => Loc.T("切换书签", "Toggle Bookmark");
        public static string CmdShowBookmarks => Loc.T("书签列表…", "Show Bookmarks...");
        public static string CmdSetBookmark(int d) => Loc.T($"设置书签 {d}", $"Set Bookmark {d}");
        public static string CmdGoToBookmark(int d) => Loc.T($"转到书签 {d}", $"Go to Bookmark {d}");
        public static string CmdLiveCoding => Loc.T("Live Coding 编译（UE 编辑器）", "Live Coding (Unreal Editor)");
        public static string CmdGenerateProjectFiles => Loc.T("生成工程文件", "Generate Project Files");
        public static string CmdInstallEditorPlugin => Loc.T("安装 UE 编辑器插件 UEToVSLink 到当前项目", "Install the UEToVSLink Editor Plugin into the Project");
        public static string CmdOpenAssetInEditor => Loc.T("在 UE 编辑器中打开资产", "Open Asset in Unreal Editor");

        public static readonly string[] KwSearch = { "search", "everywhere", "搜索", "全局", "goto", "go to", "find", "查找", "double shift", "双击 shift" };
        public static readonly string[] KwNavigation = { "navigate", "导航", "back", "forward", "后退", "前进", "history", "历史", "jump", "跳转" };
        public static readonly string[] KwRecent = { "recent", "最近", "files", "文件", "locations", "位置", "edit", "编辑" };
        public static readonly string[] KwRelated = { "related", "相关", "header", "source", "头文件", "源文件", "toggle", "切换", ".h", ".cpp", "_implementation" };
        public static readonly string[] KwBookmark = { "bookmark", "书签", "mark", "标记" };
        public static readonly string[] KwBuildCs = { "build.cs", "module", "模块", "依赖", "dependency", "include", "包含" };
        public static readonly string[] KwUnreal = { "unreal", "ue", "editor", "编辑器", "live coding", "热重载", "hot reload", "ubt", "工程文件", "project files", "asset", "资产", "blueprint", "蓝图", "plugin", "插件" };

        // ---- Search Everywhere ----
        public static string TabAll => Loc.T("全部", "All");
        public static string TabTypes => Loc.T("类型", "Types");
        public static string TabSymbols => Loc.T("符号", "Symbols");
        public static string TabFiles => Loc.T("文件", "Files");
        public static string TabAssets => Loc.T("资产（蓝图）", "Assets");
        public static string TabActions => Loc.T("操作", "Actions");
        public static string FilterNonProject => Loc.T("包含非项目项", "Include non-project items");
        public static string FilterReflected => Loc.T("仅 UE 反射", "UE reflected only");
        public static string SearchPlaceholder => Loc.T("类型、符号、文件、资产或操作 · /types /files 前缀 · 文件名:行号",
                                                       "Types, symbols, files, assets or actions · /types /files prefixes · file:line");
        public static string SearchEmptyHint => Loc.T("输入以搜索；双击 Shift 切换“包含非项目项”", "Type to search; double Shift toggles non-project items");
        public static string AssetsEmptyHint => Loc.T("输入资产名称（需要索引完成蓝图扫描）", "Type an asset name (needs the Blueprint scan)");
        public static string RecentFilesGroup => Loc.T("最近打开的文件", "Recently opened files");
        public static string Edited => Loc.T("已编辑", "edited");
        public static string SwitchToTab(string tab) => Loc.T("切换到 " + tab, "Switch to " + tab);
        public static string MoreIn(string tab) => Loc.T("更多" + tab + "…", "More " + tab + "...");
        public static string IndexNotReady => Loc.T("正在建立索引，结果可能不完整", "Indexing; results may be incomplete");
        public static string IndexingProgress(int done, int total) => Loc.T($"正在建立索引 {done}/{total}，结果可能不完整", $"Indexing {done}/{total}; results may be incomplete");
        public static string ShowingNonProject => Loc.T("项目中没有匹配项，已显示非项目项", "Nothing in the project; showing non-project items");
        public static string MoreNonProject(int n) => Loc.T($"非项目项中还有 {n:N0} 个匹配 · 双击 Shift 显示", $"{n:N0} more matches outside the project · double Shift to show");
        public static string EngineNotReady => Loc.T("引擎未连接：只能搜索操作和最近文件", "Engine not connected: only actions and recent files are searchable");
        public static string SearchFailed(string message) => Loc.T("搜索出错：" + message, "Search failed: " + message);
        public static string OriginPlugin => Loc.T("插件", "plugin");
        public static string OriginEngine => Loc.T("引擎", "engine");
        public static string OriginEnginePlugin => Loc.T("引擎插件", "engine plugin");
        public static string OriginThirdParty => Loc.T("第三方", "third party");
        public static string ResultsCaption => Loc.T("UEToVS 搜索结果", "UEToVS Search Results");
        public static string ResultsSummary(string query, int count) => Loc.T($"“{query}” — {count} 项", $"\"{query}\" — {count} items");

        // ---- navigation ----
        public static string NoBack => Loc.T("没有可以后退的位置", "Nothing to go back to");
        public static string NoForward => Loc.T("没有可以前进的位置", "Nothing to go forward to");
        public static string NoPreviousEdit => Loc.T("没有更早的编辑位置", "No earlier edit location");
        public static string NoNextEdit => Loc.T("没有更晚的编辑位置", "No later edit location");
        public static string FileNotFound(string path) => Loc.T("文件不存在：" + path, "File not found: " + path);
        public static string NoActiveDocument => Loc.T("没有打开的文件", "No active document");
        public static string RecentFilesTitle => Loc.T("最近的文件（再按一次：仅已编辑）", "Recent Files (press again: edited only)");
        public static string RecentEditedFilesTitle => Loc.T("最近编辑的文件", "Recently Edited Files");
        public static string RecentLocationsTitle => Loc.T("最近的位置（再按一次：仅编辑位置）", "Recent Locations (press again: edits only)");
        public static string RecentEditLocationsTitle => Loc.T("最近编辑的位置", "Recent Edit Locations");
        public static string EditMark => Loc.T("编辑", "edit");
        public static string FileStructureTitle(string file) => Loc.T("文件结构 — " + file, "File Structure — " + file);
        public static string NothingFound => Loc.T("没有找到", "Nothing found");
        public static string EngineUnavailable => Loc.T("索引引擎未就绪", "The index engine is not ready");
        public static string NavigateToTitle(string word) => string.IsNullOrEmpty(word) ? Loc.T("跳转到", "Navigate To") : Loc.T("跳转到 — " + word, "Navigate To — " + word);
        public static string NavDeclaration => Loc.T("声明", "Declaration");
        public static string NavImplementation => Loc.T("实现", "Implementation");
        public static string NavDefinition => Loc.T("定义", "Definition");
        public static string NavBase => Loc.T("基类符号", "Base Symbols");
        public static string NavUsages => Loc.T("用法", "Usages");
        public static string NavCallHierarchy => Loc.T("调用层次", "Call Hierarchy");
        public static string NavRelated => Loc.T("相关项：头文件/源文件、_Implementation、_Validate", "Related: header/source, _Implementation, _Validate");
        public static string NavSyncSolutionExplorer => Loc.T("在解决方案资源管理器中定位", "Locate in Solution Explorer");
        public static string NavOpenFolder => Loc.T("在文件资源管理器中打开所在文件夹", "Open Containing Folder");
        public static string NavSearchWord(string word) => Loc.T($"在全局搜索中搜索“{word}”", $"Search Everywhere for \"{word}\"");
        public static string CommandUnavailable(string cmd) => Loc.T("命令当前不可用：" + cmd, "Command not available: " + cmd);

        // ---- related ----
        public static string RelatedTitle => Loc.T("相关项", "Related");
        public static string NoRelated => Loc.T("没有找到相关的文件或函数", "No related file or function found");
        public static string NoCounterpart => Loc.T("没有找到对应的头文件/源文件", "No matching header/source file found");
        public static string DefinitionLabel => Loc.T("定义", "definition");
        public static string DeclarationLabel => Loc.T("声明", "declaration");
        public static string GeneratedHeader => Loc.T("生成的头文件", "generated header");
        public static string NoBuildCs(string file) => Loc.T("没有找到 " + file + " 所属模块的 Build.cs", "No Build.cs found for " + file);

        // ---- bookmarks ----
        public static string BookmarksTitle => Loc.T("书签（Delete 删除）", "Bookmarks (Delete removes)");
        public static string BookmarkAdded => Loc.T("已添加书签", "Bookmark added");
        public static string BookmarkRemoved => Loc.T("已移除书签", "Bookmark removed");
        public static string BookmarkDigitSet(int d) => Loc.T($"书签 {d} 已设置", $"Bookmark {d} set");
        public static string BookmarkDigitRemoved(int d) => Loc.T($"书签 {d} 已移除", $"Bookmark {d} removed");
        public static string BookmarkDigitMissing(int d) => Loc.T($"没有书签 {d}（Ctrl+Shift+{d} 设置）", $"No bookmark {d} (Ctrl+Shift+{d} sets it)");
        public static string NoBookmarks => Loc.T("还没有书签（F11 添加）", "No bookmarks yet (F11 adds one)");

        // ---- Build.cs dependencies ----
        public static string DependenciesComplete => Loc.T("模块依赖完整，无需修改 Build.cs", "Module dependencies are complete; Build.cs unchanged");
        public static string AddDependencyTitle => Loc.T("添加模块依赖（头文件里用到的放 Public，只在 .cpp 用到的放 Private）",
                                                         "Add module dependency (Public when used in headers, Private when only in .cpp)");
        public static string AddTo(string modules, string list) => Loc.T($"把 {modules} 加入 {list}", $"Add {modules} to {list}");
        public static string AddToShort(string list) => Loc.T("添加到 " + list, "Add to " + list);
        public static string Ignore => Loc.T("忽略", "Ignore");
        public static string MissingDependencyBanner(string file, string includes, string modules, string owner) =>
            Loc.T($"{file} 包含的 {includes} 属于模块 {modules}，但 {owner}.Build.cs 没有依赖它（编译或链接会失败）。",
                  $"{file} includes {includes} from module {modules}, but {owner}.Build.cs does not depend on it (compile or link will fail).");
        public static string DependencyAdded(string modules, string file) => Loc.T($"已把 {modules} 加入 {file}，需要重新生成工程文件或直接编译",
                                                                                   $"Added {modules} to {file}; regenerate project files or build");
        public static string DependencyEditFailed => Loc.T("修改 Build.cs 失败，详见日志", "Editing Build.cs failed; see the log");

        // ---- Unreal tools / editor link ----
        public static string NoProjectOrEngine => Loc.T("没有找到 .uproject 或引擎目录", "No .uproject or engine folder found");
        public static string NoUbt => Loc.T("没有找到 UnrealBuildTool", "UnrealBuildTool not found");
        public static string GeneratingProjectFiles => Loc.T("正在生成工程文件…", "Generating project files...");
        public static string ProjectFilesGenerated => Loc.T("工程文件已生成，VS 会提示重新加载解决方案", "Project files generated; VS will offer to reload the solution");
        public static string ProjectFilesFailed => Loc.T("生成工程文件失败，详见输出窗口", "Generating project files failed; see the Output window");
        public static string LiveCodingRequesting => Loc.T("正在请求编辑器执行 Live Coding…", "Asking the editor to run Live Coding...");
        public static string LiveCodingStarted(string msg) => Loc.T("Live Coding 已开始" + (string.IsNullOrEmpty(msg) ? "" : "：" + msg), "Live Coding started" + (string.IsNullOrEmpty(msg) ? "" : ": " + msg));
        public static string LiveCodingFailed(string msg) => Loc.T("Live Coding 失败：" + msg, "Live Coding failed: " + msg);
        public static string EditorNotConnected => Loc.T("连不上 UE 编辑器。请先打开 Unreal Editor 并启用 UEToVSLink 插件", "Cannot reach the Unreal Editor. Open it and enable the UEToVSLink plugin");
        public static string AssetOpened(string name) => Loc.T("已在编辑器中打开 " + name, "Opened " + name + " in the editor");
        public static string AssetOpenFailed(string msg) => Loc.T("编辑器无法打开资产：" + msg, "The editor could not open the asset: " + msg);
        public static string AssetPathCopied(string path) => Loc.T("编辑器未连接，已复制资产路径 " + path, "Editor not connected; copied the asset path " + path);
        public static string NoUProject => Loc.T("当前解决方案没有 .uproject", "The solution has no .uproject");
        public static string PluginSourceMissing(string path) => Loc.T("扩展里没有找到插件源码 " + path, "Plugin source not found in the extension: " + path);
        public static string PluginInstalled => Loc.T("插件已复制到 Plugins/UEToVSLink，重新生成工程文件并编译编辑器后生效", "Plugin copied to Plugins/UEToVSLink; regenerate project files and build the editor");
        public static string PluginInstallFailed => Loc.T("复制插件失败，详见日志", "Copying the plugin failed; see the log");
    }
}
