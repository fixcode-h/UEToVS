using System.Collections.Generic;

namespace UEToVS.Mcp.Tools
{
    /// <summary>
    /// Chinese display names of the MCP tools. The protocol name (search_symbols) never changes — agents call it — and the
    /// English display name is <see cref="ToolDefinition.Title"/>. Both go out in tools/list as <c>_meta.titles</c>
    /// (<c>{"zh": "...", "en": "..."}</c>): clients that show tool calls to people (Agent Panel) pick the one matching
    /// their own UI language, so the language follows the client, not Visual Studio. Every tool must have an entry
    /// (ProtocolTests checks it).
    /// </summary>
    public static class ToolTitles
    {
        private static readonly Dictionary<string, string> Zh = new Dictionary<string, string>
        {
            ["search_symbols"] = "搜索 C++ 符号",
            ["search_files"] = "搜索文件",
            ["search_assets"] = "搜索 UE 资产",
            ["get_file_outline"] = "文件大纲",
            ["go_to_definition"] = "跳转到定义",
            ["find_references"] = "查找引用",
            ["get_type_hierarchy"] = "类型继承关系",
            ["get_members"] = "类型成员",
            ["index_status"] = "索引状态",
            // v0.9.0 已移除（旧名字保留一版，调用报 tool_removed）
            ["search_lua"] = "搜索 Lua 符号（已移除）",
            ["search_lua_files"] = "搜索 Lua 文件（已移除）",
            ["get_lua_outline"] = "Lua 文件大纲（已移除）",
            ["find_blueprint_usages"] = "查找蓝图用法和派生蓝图",
            ["get_reflection_info"] = "UE 反射信息",
            ["get_module_info"] = "UE 模块信息",
            ["suggest_include"] = "建议 #include 和模块依赖",
            ["search_config"] = "搜索配置、CVar 和 GameplayTag",
            ["find_delegate_bindings"] = "查找委托绑定",
            ["get_build_errors"] = "上次编译的错误",
            ["get_diagnostics"] = "UE 规则诊断",
            ["rename_symbol"] = "重命名 C++ 符号",
            ["run_project_inspection"] = "全项目检查",
            ["debug_session"] = "VS 调试：启停与单步",
            ["debug_breakpoints"] = "VS 断点",
            ["debug_inspect"] = "VS 调试：栈和变量",
        };

        /// <summary>Chinese display name of a tool; null when it has none.</summary>
        public static string Chinese(string name) => name != null && Zh.TryGetValue(name, out var s) ? s : null;
    }
}
