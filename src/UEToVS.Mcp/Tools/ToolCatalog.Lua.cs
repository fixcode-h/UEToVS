using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Mcp.Tools
{
    /// <summary>
    /// v0.9.0 移除了 Lua 支持：Lua 全部归 IntelliJ IDEA 的 EmmyLua 插件（IDE 自带 MCP 服务器里的 <c>lua_code</c> 工具）。
    /// 旧工具名 search_lua / search_lua_files / get_lua_outline 保留一版：仍在 tools/list 里（说明写明已移除），
    /// 调用时不管参数是什么都返回 <c>tool_removed</c> 和「改用哪个」，免得 Agent 和用户提示词里写死的旧名字悄悄失效。
    /// 下一个 minor 版本删掉这个文件和 ToolGuides / ToolTitles 里对应的条目。
    /// </summary>
    public sealed partial class ToolCatalog
    {
        /// <summary>调用已移除的 Lua 工具时返回的说明（中文，原样给用户和 Agent 看）。</summary>
        public const string LuaRemovedMessage =
            "UEToVS 不再提供 Lua 功能，改用 IntelliJ IDEA 的 EmmyLua 插件的 Lua 查找工具（IDE 自带 MCP 服务器里的 lua_code 工具）。";

        /// <summary>具体某个旧工具的报错：<see cref="LuaRemovedMessage"/> 再点明对应的 action。</summary>
        public static string LuaRemovedMessageFor(string replacement) =>
            "UEToVS 不再提供 Lua 功能，改用 IntelliJ IDEA 的 EmmyLua 插件的 Lua 查找工具（IDE 自带 MCP 服务器里的 lua_code 工具，这里对应 " + replacement + "）。";

        private void RegisterRemovedLuaTools()
        {
            Register(RemovedTool("search_lua", "Search Lua symbols (removed)", "lua_code action=search"));
            Register(RemovedTool("search_lua_files", "Search Lua script files (removed)", "lua_code action=files"));
            Register(RemovedTool("get_lua_outline", "Lua file outline (removed)", "lua_code action=outline"));
        }

        /// <summary>已移除的工具：不收参数（调用时在参数校验之前就返回 tool_removed），说明里写改用什么。</summary>
        private static ToolDefinition RemovedTool(string name, string title, string replacement)
        {
            var t = Define(name, title, ToolGuides.Description(name), Schema(""),
                (a, ct) => Task.FromResult<ToolOutput>(null));
            t.RemovedMessage = LuaRemovedMessageFor(replacement);
            return t;
        }
    }
}
