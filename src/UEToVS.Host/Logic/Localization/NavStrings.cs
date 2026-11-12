using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>
    /// User-visible strings of the M2 navigation features (go to definition, find references, type hierarchy, reference
    /// highlighting, navigation bar, member lists). Kept apart from <see cref="Strings"/> so parallel work merges cleanly.
    /// Pure; covered by the localization tests (both languages, distinct).
    /// </summary>
    public static class NavStrings
    {
        // ---- commands (menu text; the VSCT holds the English default) ----
        public static string CmdGoToDefinition => Loc.T("转到定义 / 声明", "Go to Definition / Declaration");
        public static string CmdGoToImplementation => Loc.T("转到实现", "Go to Implementation");
        public static string CmdFindReferences => Loc.T("查找引用", "Find References");
        public static string CmdTypeHierarchy => Loc.T("类型层次", "Type Hierarchy");
        public static string CmdListMembersInFile => Loc.T("文件成员列表", "List Members in File");
        public static string CmdListTypeMembers => Loc.T("类型成员（含继承）", "Type Members (with Inherited)");
        public static string CmdToggleReferenceHighlight => Loc.T("开关引用高亮", "Toggle Reference Highlighting");
        public static string CmdToggleNavigationBar => Loc.T("开关导航栏", "Toggle Navigation Bar");

        public static readonly string[] KwGoToDefinition = { "go to definition", "declaration", "goto", "jump", "alt+g", "f12", "转到", "定义", "声明", "跳转" };
        public static readonly string[] KwGoToImplementation = { "implementation", "override", "derived", "_implementation", "实现", "重写", "派生" };
        public static readonly string[] KwFindReferences = { "find references", "usages", "find usages", "引用", "查找引用", "使用" };
        public static readonly string[] KwTypeHierarchy = { "hierarchy", "base", "derived", "inheritance", "层次", "基类", "派生", "继承" };
        public static readonly string[] KwMembers = { "members", "methods", "file structure", "alt+m", "成员", "方法", "文件结构", "列表" };
        public static readonly string[] KwHighlight = { "highlight", "references", "高亮", "引用高亮" };
        public static readonly string[] KwNavigationBar = { "navigation bar", "breadcrumb", "导航栏", "作用域" };

        // ---- status bar: why nothing happened ----
        public static string NotOnIdentifier => Loc.T("UEToVS：光标不在标识符上", "UEToVS: the caret is not on an identifier");
        public static string NoActiveCppEditor => Loc.T("UEToVS：请在 C++ 编辑器中使用此命令", "UEToVS: use this command in a C++ editor");
        public static string EngineUnavailable => Loc.T("UEToVS：引擎不可用，无法解析符号", "UEToVS: the engine is unavailable; symbols cannot be resolved");
        public static string EngineTimeout => Loc.T("UEToVS：引擎响应超时（详见日志）", "UEToVS: the engine did not answer in time (see the log)");
        public static string EngineFailed => Loc.T("UEToVS：引擎请求失败（详见日志）", "UEToVS: the engine request failed (see the log)");
        public static string IndexNotReady => Loc.T("（索引尚未完成，结果可能不完整）", " (indexing is not finished; results may be incomplete)");
        public static string NoDefinition(string word) => Loc.F("UEToVS：找不到“{0}”的定义", "UEToVS: no definition found for '{0}'", word);
        public static string NoImplementation(string word) => Loc.F("UEToVS：找不到“{0}”的实现或派生类型", "UEToVS: no implementation or derived type found for '{0}'", word);
        public static string NoReferences(string word) => Loc.F("UEToVS：没有找到“{0}”的引用", "UEToVS: no references to '{0}' found", word);
        public static string NoTypeAtCaret => Loc.T("UEToVS：光标处不是类型（也不在类成员中）", "UEToVS: no type at the caret (and not inside a class member)");
        public static string NoMembers => Loc.T("UEToVS：没有可列出的成员", "UEToVS: no members to list");
        public static string JumpFailed(string file) => Loc.F("UEToVS：无法打开 {0}", "UEToVS: cannot open {0}", file);
        public static string Jumped(string title, string where) => Loc.F("UEToVS：{0} → {1}", "UEToVS: {0} → {1}", title, where);
        public static string ReasonSuffix(string reason) => Loc.F("（{0}）", " ({0})", reason);

        /// <summary>Engine reason codes (NavigationResponse.Reason) → short localized explanation; unknown codes as-is.</summary>
        public static string ReasonText(string reason)
        {
            switch (reason)
            {
                case null:
                case "": return null;
                case "member-of-enclosing": return Loc.T("当前类的成员", "member of the enclosing class");
                case "qualified": return Loc.T("按限定名解析", "resolved by qualified name");
                case "super": return Loc.T("Super:: 基类成员", "Super:: base class member");
                case "include": return Loc.T("#include 文件", "#include file");
                case "name-only": return Loc.T("仅按名称匹配", "matched by name only");
                case "local": return Loc.T("局部变量 / 参数", "local variable / parameter");
                case "not-found": return Loc.T("索引中没有该名称", "name not in the index");
                case "no-word": return Loc.T("光标不在标识符上", "the caret is not on an identifier");
                case "keyword": return Loc.T("关键字", "keyword");
                case "unsupported-file": return Loc.T("不支持的文件类型", "unsupported file type");
                case "index-not-ready": return Loc.T("索引尚未就绪", "index not ready");
                default: return reason;
            }
        }

        // ---- chooser / badges ----
        public static string ChooseDefinition(string word) => Loc.F("“{0}”的多个目标", "Targets of '{0}'", word);
        public static string ChooseImplementation(string word) => Loc.F("“{0}”的实现", "Implementations of '{0}'", word);
        public static string RoleDeclaration => Loc.T("声明", "Declaration");
        public static string RoleDefinition => Loc.T("定义", "Definition");
        public static string RoleOverride => Loc.T("重写", "Override");
        public static string RoleUeCounterpart => Loc.T("UE 生成", "UE generated");
        public static string RoleDerived => Loc.T("派生", "Derived");
        public static string OriginProject => Loc.T("项目", "Project");
        public static string OriginProjectPlugin => Loc.T("项目插件", "Project plugin");
        public static string OriginEngine => Loc.T("引擎", "Engine");
        public static string OriginEnginePlugin => Loc.T("引擎插件", "Engine plugin");
        public static string OriginThirdParty => Loc.T("第三方", "Third party");
        public static string ConfidenceExact => Loc.T("精确", "Exact");
        public static string ConfidenceScoped => Loc.T("按作用域", "Scoped");
        public static string ConfidenceNameOnly => Loc.T("仅名称", "Name only");
        public static string ConfidenceNone => Loc.T("未解析", "Unresolved");

        // ---- references ----
        public static string KindRead => Loc.T("读", "Read");
        public static string KindWrite => Loc.T("写", "Write");
        public static string KindCall => Loc.T("调用", "Call");
        public static string KindTypeUse => Loc.T("类型", "Type");
        public static string KindDeclaration => Loc.T("声明", "Decl");
        public static string KindDefinition => Loc.T("定义", "Def");
        public static string ReferencesTitle(string symbol) => Loc.F("“{0}”的引用", "References of '{0}'", symbol);
        public static string ReferencesSearching(string symbol) => Loc.F("正在查找“{0}”的引用…", "Finding references of '{0}'…", symbol);
        public static string ReferencesSummary(int hits, int files, double ms, bool truncated) =>
            Loc.F("{0:N0} 处引用 · {1:N0} 个文件 · {2:N0} ms{3}", "{0:N0} references · {1:N0} files · {2:N0} ms{3}", hits, files, ms,
                  truncated ? Loc.T(" · 结果已截断", " · truncated") : "");
        public static string ReferencesHidden(int hidden) => Loc.F("（已隐藏 {0:N0} 处）", " ({0:N0} hidden)", hidden);
        public static string ReferencesRerun => Loc.T("重新搜索", "Search again");
        public static string ReferencesWritesOnly => Loc.T("只看写入", "Writes only");
        public static string ReferencesIncludeEngine => Loc.T("包含引擎代码", "Include engine code");
        public static string ReferencesShowUncertain => Loc.T("显示不确定的结果", "Show uncertain matches");
        public static string ReferencesUncertainTip => Loc.T("仅按名称匹配、可能不是同一个符号的结果显示为灰色", "Matches found by name only (possibly a different symbol) are grayed out");
        public static string ReferencesFailed => Loc.T("查找引用失败（详见日志）。", "Find references failed (see the log).");

        // ---- type hierarchy ----
        public static string HierarchyTitle(string type) => Loc.F("“{0}”的类型层次", "Type hierarchy of '{0}'", type);
        public static string HierarchyBases => Loc.T("基类", "Base types");
        public static string HierarchyDerived => Loc.T("派生类", "Derived types");
        public static string HierarchyUnresolved => Loc.T("未在索引中（模板参数或外部类型）", "Not in the index (template argument or external type)");
        public static string HierarchyTruncated => Loc.T("…（已截断）", "… (truncated)");
        public static string HierarchySummary(int nodes, double ms) => Loc.F("{0:N0} 个类型 · {1:N0} ms", "{0:N0} types · {1:N0} ms", nodes, ms);

        // ---- navigation bar / member lists ----
        public static string NavBarGlobalScope => Loc.T("（全局作用域）", "(global scope)");
        public static string NavBarNoFunction => Loc.T("（无函数）", "(no function)");
        public static string NavBarTypesTitle => Loc.T("文件中的类型", "Types in file");
        public static string NavBarMembersTitle => Loc.T("成员", "Members");
        public static string NavBarTypesTip => Loc.T("当前类型（点击选择文件中的类型）", "Current type (click to pick a type in this file)");
        public static string NavBarMembersTip => Loc.T("当前函数（点击选择成员）", "Current function (click to pick a member)");
        public static string MembersInFileTitle(string file) => Loc.F("{0} 中的声明", "Declarations in {0}", file);
        public static string TypeMembersTitle(string type) => Loc.F("{0} 的成员（含继承）", "Members of {0} (with inherited)", type);
        public static string InheritedFrom(string type) => Loc.F("继承自 {0}", "from {0}", type);
        public static string Truncated => Loc.T("（结果已截断）", "(truncated)");
    }
}
