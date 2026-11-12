using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>v0.4.5 构建结果 / 错误列表 / 新建 UE 类 / 生成 VS 项目文件 strings (bilingual, follow the VS UI language).</summary>
    public static class BuildStrings
    {
        // ---- commands
        public static string CmdNewUeClass => Loc.T("新建 UE 类…", "New Unreal Class...");
        public static string CmdNextBuildError => Loc.T("下一个编译错误", "Next Build Error");
        public static string CmdClearBuildErrors => Loc.T("清除编译错误标记", "Clear Build Error Marks");
        public static string CmdGenerateProjectFiles => Loc.T("生成 Visual Studio 项目文件", "Generate Visual Studio Project Files");

        public static readonly string[] KwNewClass = { "new class", "新建类", "class", "类", "uclass", "actor", "component", "组件", "wizard", "向导", "add", "添加" };
        public static readonly string[] KwBuildErrors = { "build", "生成", "编译", "error", "错误", "warning", "警告", "compile", "ubt", "msvc", "squiggle", "波浪线" };

        // ---- build results
        public static string BuildErrorsFound(int errors, int warnings) =>
            Loc.T($"UEToVS：本次生成 {errors} 个错误，{warnings} 个警告（Ctrl+Shift+Alt+U, Q 跳到下一个）",
                  $"UEToVS: build finished with {errors} error(s), {warnings} warning(s) (Ctrl+Shift+Alt+U, Q for the next one)");
        public static string NoBuildErrors => Loc.T("上次生成没有可定位的编译错误或警告", "The last build has no located errors or warnings");
        public static string BuildErrorsCleared => Loc.T("已清除编译错误标记", "Build error marks cleared");
        public static string NextBuildError(int index, int count, string message) =>
            Loc.T($"编译错误 {index}/{count}：{message}", $"Build error {index}/{count}: {message}");
        public static string CompilerErrorFormat => Loc.T("UEToVS 编译错误", "UEToVS Compiler Error");
        public static string CompilerWarningFormat => Loc.T("UEToVS 编译警告", "UEToVS Compiler Warning");
        public static string CompilerTooltip(string tool, string code, string message) =>
            (string.IsNullOrEmpty(tool) ? "" : "[" + tool + "] ") + (string.IsNullOrEmpty(code) ? "" : code + ": ") + message +
            Loc.T("\n（上次生成的结果；下次生成开始时清除）", "\n(from the last build; cleared when the next build starts)");

        // ---- generate project files
        public static string UbtAlreadyRunning => Loc.T("正在生成 Visual Studio 项目文件，请等待完成", "Generating Visual Studio project files; wait for it to finish");
        public static string UbtProgress => Loc.T("正在生成 Visual Studio 项目文件…", "Generating Visual Studio project files...");
        public static string UbtPaneHeader(string uproject) => Loc.T($"==== 生成 Visual Studio 项目文件：{uproject} ====", $"==== Generate Visual Studio project files: {uproject} ====");
        public static string UbtPaneDone(double seconds) => Loc.T($"==== 完成（{seconds:F1} 秒） ====", $"==== Done ({seconds:F1} s) ====");
        public static string UbtPaneFailed(int code) => Loc.T($"==== 失败：退出码 {code} ====", $"==== Failed: exit code {code} ====");
        public static string UbtPaneLastErrors => Loc.T("最后的错误输出：", "Last error output:");

        // ---- New UE class dialog
        public static string NewClassTitle => Loc.T("新建 UE 类", "New Unreal Class");
        public static string NewClassCreate => Loc.T("创建", "Create");
        public static string NewClassDescription => Loc.T("选择父类，填写类名；创建后会重新生成 Visual Studio 项目文件并打开新文件。",
                                                         "Pick a parent class and a name; Visual Studio project files are regenerated and the new files opened afterwards.");
        public static string ParentClass => Loc.T("父类", "Parent class");
        public static string ParentSearchPlaceholder => Loc.T("搜索父类（常用父类在上方）", "Search parent classes (common ones first)");
        public static string CommonParentGroup => Loc.T("常用", "Common");
        public static string IndexParentGroup => Loc.T("索引中的类", "Classes in the index");
        public static string NoParentRow => Loc.T("无父类（普通 C++ 类）", "No parent (plain C++ class)");
        public static string ClassName => Loc.T("类名", "Class name");
        public static string ClassNamePlaceholder => Loc.T("例如 MyActor（自动加 A/U/F 前缀）", "e.g. MyActor (A/U/F prefix added)");
        public static string Module => Loc.T("模块", "Module");
        public static string Visibility => Loc.T("位置", "Location");
        public static string VisibilityPublic => Loc.T("Public", "Public");
        public static string VisibilityPrivate => Loc.T("Private", "Private");
        public static string VisibilitySame => Loc.T("模块根目录（无 Public 文件夹）", "Module folder (no Public folder)");
        public static string Subfolder => Loc.T("子文件夹（可选）", "Subfolder (optional)");
        public static string BeginPlayTick => Loc.T("生成 BeginPlay / Tick", "Add BeginPlay / Tick");
        public static string NoModules => Loc.T("在项目中没有找到 *.Build.cs 模块", "No *.Build.cs module found in the project");
        public static string FileExists(string path) => Loc.T($"文件已存在：{path}", $"File already exists: {path}");
        public static string Created(string name) => Loc.T($"已创建 {name}，正在生成 Visual Studio 项目文件…", $"Created {name}; generating Visual Studio project files...");
        public static string CreateFailed(string message) => Loc.T($"创建失败：{message}", $"Create failed: {message}");
        public static string WillCreate(string header, string source) => Loc.T($"将创建：\n{header}\n{source}", $"Will create:\n{header}\n{source}");

        public static string NameError(string code)
        {
            switch (code)
            {
                case "empty": return Loc.T("请输入类名", "Enter a class name");
                case "invalid": return Loc.T("类名只能包含字母、数字和下划线，且不能以数字开头", "Letters, digits and underscores only; must not start with a digit");
                case "too-long": return Loc.T("类名太长", "The class name is too long");
                case "same-as-parent": return Loc.T("类名不能与父类相同", "The class name must differ from the parent");
                default: return code;
            }
        }
    }
}
