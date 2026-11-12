using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>User-visible strings of the M4 refactoring features (Alt+Enter, code generation, Add Include, Rename). Both languages.</summary>
    public static class RefactorStrings
    {
        // ---- commands / action titles ----
        public static string CmdQuickActions => Loc.T("上下文操作（Alt+Enter）", "Context Actions (Alt+Enter)");
        public static string CmdCreateImplementation => Loc.T("生成实现", "Create Implementation");
        public static string CmdCreateDeclaration => Loc.T("生成声明", "Create Declaration");
        public static string CmdImplementVirtual => Loc.T("实现虚函数 / 重写…", "Implement Virtual Methods / Override...");
        public static string CmdAddInclude => Loc.T("添加 #include", "Add #include");
        public static string CmdRename => Loc.T("重命名…", "Rename...");

        public static readonly string[] KwQuickActions = { "alt+enter", "quick actions", "context actions", "light bulb", "fix", "上下文", "快速操作", "灯泡", "修复" };
        public static readonly string[] KwCreateImplementation = { "create implementation", "definition", "body", "_implementation", "_validate", "生成实现", "函数体", "定义" };
        public static readonly string[] KwCreateDeclaration = { "create declaration", "declare", "header", "生成声明", "声明", "头文件" };
        public static readonly string[] KwImplementVirtual = { "override", "implement virtual", "virtual", "super", "重写", "覆盖", "虚函数" };
        public static readonly string[] KwAddInclude = { "include", "add include", "header", "#include", "包含", "头文件" };
        public static readonly string[] KwRename = { "rename", "refactor", "core redirects", "重命名", "改名", "重构" };

        // ---- menu items (with arguments) ----
        public static string CreateImplementationOf(string name) => Loc.T($"生成 {name} 的实现", $"Create implementation of {name}");
        public static string CreateUeImplementationOf(string name, string suffixes) => Loc.T($"生成 {name} 的 {suffixes}", $"Create {suffixes} for {name}");
        public static string CreateDeclarationOf(string name) => Loc.T($"在头文件中生成 {name} 的声明", $"Create declaration of {name} in the header");
        public static string ImplementVirtualIn(string cls) => Loc.T($"实现 {cls} 的虚函数 / 重写…", $"Implement virtual methods / override in {cls}...");
        public static string AddIncludeFor(string include) => Loc.T($"添加 #include \"{include}\"", $"Add #include \"{include}\"");
        public static string RenameSymbol(string name) => Loc.T($"重命名 {name}…", $"Rename {name}...");

        public static string GroupGenerate => Loc.T("生成", "Generate");
        public static string GroupRefactor => Loc.T("重构", "Refactor");
        public static string GroupFix => Loc.T("修复", "Fix");

        // ---- status bar ----
        public static string NoActions => Loc.T("UEToVS：光标处没有可用的上下文操作", "UEToVS: no context actions at the caret");
        public static string NotActive => Loc.T("UEToVS：重构需要先由 UEToVS 接管 C++ IntelliSense", "UEToVS: refactorings need UEToVS to take over C++ IntelliSense");
        public static string NotProjectFile => Loc.T("UEToVS：引擎 / 第三方文件不会被修改", "UEToVS: engine / third-party files are not modified");
        public static string NoFunctionAtCaret => Loc.T("UEToVS：光标不在函数声明或定义上", "UEToVS: the caret is not on a function declaration or definition");
        public static string NoCounterpartFile => Loc.T("UEToVS：找不到对应的源文件 / 头文件", "UEToVS: the matching source / header file was not found");
        public static string ClassBodyNotFound(string cls) => Loc.T($"UEToVS：在头文件中找不到类 {cls} 的类体", $"UEToVS: the body of class {cls} was not found in the header");
        public static string Created(string what, string file) => Loc.T($"UEToVS：已生成 {what}（{file}）", $"UEToVS: created {what} ({file})");
        public static string AlreadyExists(string what) => Loc.T($"UEToVS：{what} 已存在", $"UEToVS: {what} already exists");
        public static string NoClassAtCaret => Loc.T("UEToVS：光标不在类中", "UEToVS: the caret is not inside a class");
        public static string NoOverridable(string cls) => Loc.T($"UEToVS：{cls} 没有可重写的虚函数", $"UEToVS: {cls} has no virtual methods left to override");
        public static string Overridden(int count, string cls) => Loc.T($"UEToVS：已在 {cls} 中重写 {count} 个函数", $"UEToVS: overrode {count} method(s) in {cls}");
        public static string IncludeAdded(string include) => Loc.T($"UEToVS：已添加 #include \"{include}\"", $"UEToVS: added #include \"{include}\"");
        public static string IncludeAlreadyThere(string include) => Loc.T($"UEToVS：#include \"{include}\" 已存在", $"UEToVS: #include \"{include}\" is already there");
        public static string EditFailed => Loc.T("UEToVS：修改文件失败（详见日志）", "UEToVS: editing the files failed (see the log)");
        public static string Searching(string name) => Loc.T($"UEToVS：正在查找 {name} 的全部引用…", $"UEToVS: finding all references of {name}...");
        public static string RenameNothing => Loc.T("UEToVS：没有可重命名的位置", "UEToVS: nothing to rename");
        public static string Renamed(string oldName, string newName, int edits, int files) =>
            Loc.T($"UEToVS：已将 {oldName} 重命名为 {newName}（{edits} 处，{files} 个文件）", $"UEToVS: renamed {oldName} to {newName} ({edits} edits in {files} files)");
        public static string RenameSkipped(int count) => Loc.T($"UEToVS：{count} 处因文件内容已变化而跳过", $"UEToVS: {count} location(s) skipped because the file changed");
        public static string ReferencesTruncated => Loc.T("引用结果被截断，可能有遗漏", "The reference results were truncated; some may be missing");

        // ---- dialogs ----
        public static string OverrideDialogTitle(string cls) => Loc.T($"重写 {cls} 的虚函数", $"Override Virtual Methods in {cls}");
        public static string OverrideDialogDescription => Loc.T("勾选要重写的函数（空格切换）。声明加在类中，带 Super:: 调用的实现加在源文件中。",
            "Check the methods to override (Space toggles). Declarations go into the class, implementations calling Super:: into the source file.");
        public static string OverrideButton => Loc.T("重写", "Override");
        public static string RenameDialogTitle(string name) => Loc.T($"重命名 {name}", $"Rename {name}");
        public static string RenameDialogDescription(int project, int engine) =>
            Loc.T($"项目中 {project} 处将被修改；引擎 / 第三方文件中的 {engine} 处只列出，不修改。取消勾选可跳过某处。",
                  $"{project} location(s) in the project will change; {engine} in engine / third-party files are listed but not changed. Uncheck to skip one.");
        public static string RenameButton => Loc.T("重命名", "Rename");
        public static string NewNameLabel => Loc.T("新名称", "New name");
        public static string InvalidIdentifier => Loc.T("不是合法的 C++ 标识符", "Not a valid C++ identifier");
        public static string NameUnchanged => Loc.T("名称未改变", "The name did not change");
        public static string WriteCoreRedirects => Loc.T("写入 Core Redirects（Config/DefaultEngine.ini）", "Write Core Redirects (Config/DefaultEngine.ini)");
        public static string WriteCoreRedirectsTip => Loc.T("让已保存的资产、蓝图引用在改名后仍能加载", "Keeps saved assets and Blueprint references loading after the rename");
        public static string RenameFiles(string oldFile, string newFile) => Loc.T($"同时重命名文件 {oldFile} → {newFile} 并更新 #include", $"Also rename the files {oldFile} → {newFile} and update #includes");
        public static string EngineReadOnly => Loc.T("引擎（只读）", "Engine (read-only)");
        public static string ProjectGroup => Loc.T("项目", "Project");
        public static string RedirectsWritten(string ini) => Loc.T($"已写入 Core Redirects：{ini}", $"Core Redirects written: {ini}");
        public static string FilesRenamedRegenerate => Loc.T("文件已改名，请重新生成项目文件（Generate Project Files）", "Files were renamed; regenerate the project files (Generate Project Files)");
    }
}
