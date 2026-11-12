using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>
    /// v0.4.5 "navigation gaps" (call hierarchy, peek definition, GENERATED_BODY preview, what VS loses after the
    /// IntelliSense takeover). Own file so parallel work merges cleanly; covered by the localization tests.
    /// </summary>
    public static class NavGapStrings
    {
        // ---- call hierarchy
        public static string CmdCallHierarchy => Loc.T("调用层次", "Call Hierarchy");
        public static string CmdPeekDefinition => Loc.T("速览定义", "Peek Definition");

        public static readonly string[] KwCallHierarchy = { "call hierarchy", "callers", "callees", "incoming", "outgoing", "who calls", "调用层次", "调用者", "被调用", "谁调用" };
        public static readonly string[] KwPeekDefinition = { "peek", "peek definition", "alt+f12", "速览", "定义", "预览" };

        public static string CallHierarchyTitle(string name) => Loc.F("调用层次：{0}", "Call Hierarchy: {0}", name);
        public static string Callers => Loc.T("调用者", "Callers");
        public static string Callees => Loc.T("被调用者", "Callees");
        public static string IncludeEngine => Loc.T("包含引擎代码", "Include engine code");
        public static string ShowUncertain => Loc.T("显示不确定的调用", "Show uncertain calls");
        public static string ShowUncertainTip => Loc.T("仅名字匹配、无法确定是否是这个函数的调用（例如对象类型未知）。", "Calls matched by name only; the engine could not tell whether they call this function (e.g. unknown object type).");
        public static string FilterPlaceholder => Loc.T("输入过滤…", "Type to filter…");
        public static string Loading => Loc.T("正在加载…", "Loading…");
        public static string NoCallers => Loc.T("没有找到调用者", "No callers found");
        public static string NoCallees => Loc.T("没有找到被调用的函数", "No calls found");
        public static string Recursive => Loc.T("递归调用", "recursive");
        public static string Truncated => Loc.T("… 结果过多，已截断", "… too many results (truncated)");
        public static string FileScope => Loc.T("（文件作用域）", "(file scope)");
        public static string Binding => Loc.T("委托绑定", "Delegate binding");
        public static string BindingVia(string method) => Loc.F("委托绑定 · {0}", "Delegate binding · {0}", method);
        public static string Sites(int n) => Loc.F("{0} 处", "{0} sites", n);
        public static string Unresolved => Loc.T("未解析", "unresolved");
        public static string NoFunctionAtCaret => Loc.T("UEToVS：光标处不是函数（也不在函数体内）", "UEToVS: no function at the caret (and not inside a function body)");
        public static string NoBody => Loc.T("没有找到函数体（只有声明）", "No function body found (declaration only)");
        public static string LevelFailed => Loc.T("加载失败（详见日志）", "Failed to load (see the log)");

        public static string Summary(int items, int sites, int bindings, double engineMs, bool truncated) =>
            Loc.F("{0} 个函数 · {1} 处{2} · 引擎 {3} ms{4}", "{0} functions · {1} sites{2} · engine {3} ms{4}", items, sites,
                bindings > 0 ? Loc.F("（{0} 处委托绑定）", " ({0} delegate bindings)", bindings) : string.Empty,
                engineMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture), truncated ? Loc.T(" · 已截断", " · truncated") : string.Empty);

        // ---- peek definition
        public static string PeekNoDefinition(string word) => Loc.F("UEToVS：找不到“{0}”的定义，无法速览", "UEToVS: no definition of '{0}' to peek", word);
        public static string PeekRoleDefinition => Loc.T("定义", "Definition");
        public static string PeekRoleDeclaration => Loc.T("声明", "Declaration");

        // ---- GENERATED_BODY() preview (Quick Info)
        public static string GeneratedCodeTitle => Loc.T("UHT 生成代码", "UHT generated code");
        public static string GeneratedCodeFrom(string file) => Loc.F("来自 {0}", "From {0}", file);
        public static string GeneratedCodeNotBuilt => Loc.T("还没有 UHT 生成的代码：请先编译一次（生成 Intermediate 下的 .generated.h）。", "No UHT output yet: build once to generate the .generated.h under Intermediate.");
        public static string GeneratedCodeStale => Loc.T(".generated.h 中没有这一行的宏，可能已过期：请重新编译。", "The .generated.h has no macro for this line (it may be stale): build again.");

        // ---- features after the takeover (settings window table)
        public static string TakeoverTableTitle => Loc.T("接管后 VS 自带功能", "VS features after the takeover");
        public static string TakeoverTableDescription => Loc.T("接管后 VS 的 C++ IntelliSense 和浏览数据库关闭，依赖它们的 VS 功能会失效；下表列出 UEToVS 的替代命令和默认快捷键。",
            "With VS's C++ IntelliSense and browse database off, the VS features built on them stop working; the table lists the UEToVS replacements and their default keys.");
        public static string TakeoverColumnFeature => Loc.T("VS 功能", "VS feature");
        public static string TakeoverColumnState => Loc.T("接管后", "After takeover");
        public static string TakeoverColumnReplacement => Loc.T("UEToVS 替代", "UEToVS replacement");
        public static string TakeoverUnavailable => Loc.T("不可用", "Unavailable");
        public static string TakeoverReplaced => Loc.T("由 UEToVS 提供", "Provided by UEToVS");
        public static string TakeoverAvailable => Loc.T("可用", "Available");
        public static string TakeoverConfirm => Loc.T("需在 VS 中确认", "To be confirmed in VS");
        public static string TakeoverNone => Loc.T("暂无", "None yet");
    }
}
