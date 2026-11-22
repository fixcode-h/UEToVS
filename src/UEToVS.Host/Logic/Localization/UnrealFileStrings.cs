using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>v0.10 着色器、配置文件和模块依赖图的界面文本（中英）。</summary>
    public static class UnrealFileStrings
    {
        public static bool English => Loc.IsEnglish;

        // ---- 着色器
        public static string ShaderDefinitionNotFound(string word) => Loc.T("在本文件和 #include 链上没找到 " + word + " 的定义", "No definition of " + word + " in this file or its #include chain");
        public static string ShaderDefinitionsTitle(string word) => Loc.T("“" + word + "”的定义", "Definitions of \"" + word + "\"");
        public static string ShaderSearching => Loc.T("正在沿 #include 查找定义…", "Searching the #include chain…");
        public static string ShaderIncludeVirtualRoot => Loc.T("UE 着色器虚拟目录", "UE shader virtual directory");
        public static string ShaderIncludeFolder => Loc.T("文件夹", "Folder");
        public static string ShaderIncludeFile => Loc.T("着色器头文件", "Shader header");
        public static string ShaderPluginDir => Loc.T("插件着色器目录", "Plugin shader directory");
        public static string ShaderInFile(string file, int line) => Loc.T(file + " 第 " + line + " 行", file + " line " + line);
        public static string ShaderFileStructureEmpty => Loc.T("这个着色器文件里没找到函数、结构体或宏", "No functions, structs or macros found in this shader file");

        // ---- ini / 描述文件
        public static string IniUnknownKey(string key, string cls) =>
            Loc.T("“" + key + "”不是 " + cls + " 的 config 属性（类里没有带 Config 的同名 UPROPERTY；如果是代码里用 GConfig 手动读的，可以忽略）",
                  "\"" + key + "\" is not a config property of " + cls + " (no UPROPERTY with Config of that name; ignore this if the code reads it through GConfig)");
        public static string IniUnknownClass(string module, string cls) =>
            Loc.T("项目模块 " + module + " 里没找到类 U" + cls + " / A" + cls, "No class U" + cls + " / A" + cls + " in the project module " + module);
        public static string IniConfigClass => Loc.T("config 类", "config class");
        public static string IniSection => Loc.T("ini 节", "ini section");
        public static string IniValueOf(string type) => Loc.T("类型：" + type, "Type: " + type);
        public static string IniDeclaredIn(string container) => Loc.T("声明于 " + container, "Declared in " + container);
        public static string DescriptorModuleMissing(string module) =>
            Loc.T("找不到模块 " + module + " 的 " + module + ".Build.cs（Source/" + module + "/ 下或索引里都没有）",
                  "No " + module + ".Build.cs for module " + module + " (neither under Source/" + module + "/ nor in the index)");
        public static string DescriptorKey => Loc.T("描述文件字段", "Descriptor field");
        public static string DescriptorPlugin => Loc.T("插件", "Plugin");

        // ---- 模块依赖图
        public static string CmdModuleGraph => Loc.T("模块依赖图…", "Module Dependency Graph...");
        public static string GraphTitle(string scope) => Loc.T("模块依赖图 — " + scope, "Module Dependency Graph — " + scope);
        public static string GraphWholeProject => Loc.T("全项目", "Whole project");
        public static string GraphCurrentModule => Loc.T("当前模块", "Current module");
        public static string GraphTabTree => Loc.T("依赖树", "Dependency tree");
        public static string GraphTabGraph => Loc.T("图", "Graph");
        public static string GraphTabProblems => Loc.T("问题", "Problems");
        public static string GraphScanning(int done, int total) => Loc.T("正在检查 include（" + done + " / " + total + " 个模块）…", "Checking includes (" + done + " / " + total + " modules)…");
        public static string GraphLoading => Loc.T("正在读取 Build.cs…", "Reading Build.cs files…");
        public static string GraphSummary(int modules, int cycles, int problems) =>
            Loc.T(modules + " 个项目模块 · " + cycles + " 个循环依赖 · " + problems + " 个问题", modules + " project modules · " + cycles + " cycles · " + problems + " problems");
        public static string GraphNoModules => Loc.T("没找到项目模块（项目的 Source 和 Plugins 下没有 Build.cs）", "No project modules (no Build.cs under the project's Source and Plugins)");
        public static string GraphPublic => Loc.T("Public", "Public");
        public static string GraphPrivate => Loc.T("Private", "Private");
        public static string GraphInCycle => Loc.T("循环", "Cycle");
        public static string GraphEngine => Loc.T("引擎", "Engine");
        public static string GraphUnresolved => Loc.T("未找到 Build.cs", "No Build.cs");
        public static string GraphLegend => Loc.T("实线 = Public 依赖，虚线 = Private 依赖，红色 = 循环依赖；只画项目模块，双击方块打开 Build.cs",
                                                  "Solid = Public, dashed = Private, red = circular; project modules only, double-click a box to open its Build.cs");
        public static string GraphNoProblems => Loc.T("没发现问题", "No problems found");
        public static string GraphCycleKind => Loc.T("循环依赖", "Circular dependency");
        public static string GraphPrivateInPublicKind => Loc.T("Public 头用了 Private 依赖", "Private dependency in a Public header");
        public static string GraphUnusedKind => Loc.T("可能未用到（不一定准）", "Possibly unused (may be wrong)");
        public static string GraphRefresh => Loc.T("刷新", "Refresh");
        public static string GraphNoUnreal => Loc.T("没有打开 UE 项目", "No Unreal project is open");
    }
}
