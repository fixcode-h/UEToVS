using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>User-visible strings of the MCP integration (kept apart from <see cref="Strings"/> to keep merges simple).</summary>
    public static class McpStrings
    {
        public static string CmdWriteMcpConfig => Loc.T("写入 .mcp.json 到解决方案目录", "Write .mcp.json to Solution Folder");

        public static readonly string[] KwMcp = { "mcp", "agent", "ai", "copilot", "claude", "cursor", ".mcp.json", "智能体", "代理", "配置" };

        public static string SettingEnableMcp => Loc.T("启用 MCP 服务（给 AI Agent 用）", "Enable MCP server (for AI agents)");

        public static string SettingEnableMcpDescription => Loc.T(
            "打开 UE 解决方案时在 .vs\\mcp.json 注册 uetovs MCP 服务器，VS Copilot Agent 模式自动发现；Agent 通过它直接查询 UEToVS 索引（符号、文件、蓝图资产、引用、继承）。关闭则移除该注册。",
            "Registers the uetovs MCP server in .vs\\mcp.json when a UE solution opens, so Visual Studio's Copilot agent mode discovers it; agents then query the UEToVS index (symbols, files, Blueprint assets, references, hierarchy) directly. Turning it off removes the registration.");

        public static string NoSolution => Loc.T("请先打开 Unreal Engine 解决方案。", "Open an Unreal Engine solution first.");

        public static string NotInstalled(string path) => Loc.F("未找到 MCP 服务程序：{0}（请重新安装扩展）。", "MCP server not found: {0} (reinstall the extension).", path);

        public static string Written(string path, int otherServers) => otherServers > 0
            ? Loc.F("已写入 {0}（保留了其他 {1} 个服务器）。VS、Claude Code、Cursor、VS Code 均可读取。", "Wrote {0} (kept {1} other server(s)). Visual Studio, Claude Code, Cursor and VS Code read it.", path, otherServers)
            : Loc.F("已写入 {0}。VS、Claude Code、Cursor、VS Code 均可读取。", "Wrote {0}. Visual Studio, Claude Code, Cursor and VS Code read it.", path);

        public static string AlreadyUpToDate(string path) => Loc.F("{0} 已是最新。", "{0} is already up to date.", path);

        public static string InvalidFile(string path, string error) =>
            Loc.F("{0} 不是有效的 JSON，未修改：{1}", "{0} is not valid JSON and was left unchanged: {1}", path, error);
    }
}
