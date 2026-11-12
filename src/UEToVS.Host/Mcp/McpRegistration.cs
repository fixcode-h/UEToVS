using System;
using System.IO;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Mcp;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Mcp
{
    /// <summary>
    /// Makes the bundled MCP server (Engine\Mcp\UEToVS.Mcp.exe) discoverable by AI agents. Visual Studio has no extension
    /// API for contributing MCP servers (docs/design/MCP.md §2): it reads MCP configuration files. So, while "Enable MCP
    /// server" is on, the host keeps the "uetovs" entry of &lt;solution&gt;\.vs\mcp.json (VS's per-user, per-solution file)
    /// current on every UE solution open; turning the setting off removes it. The "Write .mcp.json" command writes the
    /// shareable &lt;solution&gt;\.mcp.json for Claude Code / Cursor / VS Code as well. All file work runs off the UI thread.
    /// </summary>
    internal static class McpRegistration
    {
        private static readonly Logger L = Log.For("Mcp");

        public static string ExecutablePath => McpConfig.ResolveExecutable(HostPackage.ExtensionDirectory);

        /// <summary>Solution opened or setting changed: register (or unregister) in .vs\mcp.json; refresh a stale .mcp.json entry.</summary>
        public static Task SyncAsync(string solutionDir, HostSettings settings) => Task.Run(() =>
        {
            if (string.IsNullOrEmpty(solutionDir)) return;
            string exe = ExecutablePath;
            var vsTarget = McpConfigTarget.VisualStudioUser(solutionDir);
            if (!settings.EnableMcpServer)
            {
                var removed = McpConfig.ApplyToFile(vsTarget, null, remove: true);
                L.Info("mcp.register.disabled", null, ("file", vsTarget.Path), ("outcome", removed.Outcome.ToString()));
                return;
            }
            if (!File.Exists(exe))
            {
                L.Warn("mcp.register.missing_exe", "Bundled MCP server not found; not registering", ("exe", exe));
                return;
            }
            var entry = McpConfig.BuildEntry(exe, solutionDir);
            var r = McpConfig.ApplyToFile(vsTarget, entry);
            LogResult(r, vsTarget.Path, "vs");

            // <solution>\.mcp.json written earlier by the command: keep its command path valid across extension updates
            // (the VSIX folder changes with each version). Only touched when it already has a uetovs entry.
            var rootTarget = McpConfigTarget.SolutionRoot(solutionDir);
            if (File.Exists(rootTarget.Path))
            {
                string current = McpConfig.ExistingCommand(File.ReadAllText(rootTarget.Path), rootTarget.RootKeys);
                if (current != null && !string.Equals(current, exe, StringComparison.OrdinalIgnoreCase))
                    LogResult(McpConfig.ApplyToFile(rootTarget, entry), rootTarget.Path, "solution-refresh");
            }
        });

        /// <summary>Command: write/merge &lt;solution&gt;\.mcp.json and tell the user what happened.</summary>
        public static async Task WriteSolutionConfigAsync(string solutionDir)
        {
            if (string.IsNullOrEmpty(solutionDir))
            {
                VsStatusBar.SetText(McpStrings.NoSolution);
                return;
            }
            string exe = ExecutablePath;
            if (!File.Exists(exe))
            {
                L.Warn("mcp.config.missing_exe", "Bundled MCP server not found", ("exe", exe));
                VsStatusBar.SetText(McpStrings.NotInstalled(exe));
                return;
            }
            var target = McpConfigTarget.SolutionRoot(solutionDir);
            var r = await Task.Run(() => McpConfig.ApplyToFile(target, McpConfig.BuildEntry(exe, solutionDir))).ConfigureAwait(false);
            LogResult(r, target.Path, "solution");
            switch (r.Outcome)
            {
                case McpMergeOutcome.Invalid:
                    Banners.Show("mcp.config", McpStrings.InvalidFile(target.Path, r.Error), Integration.BannerKind.Warning);
                    break;
                case McpMergeOutcome.Unchanged:
                    VsStatusBar.SetText(McpStrings.AlreadyUpToDate(target.Path));
                    break;
                default:
                    VsStatusBar.SetText(McpStrings.Written(target.Path, r.OtherServers));
                    break;
            }
        }

        private static void LogResult(McpMergeResult r, string path, string which)
        {
            if (r.Outcome == McpMergeOutcome.Invalid)
                L.Warn("mcp.config.invalid", "MCP config file is not valid JSON; left unchanged", ("file", path), ("which", which), ("error", r.Error));
            else
                L.Info("mcp.config.written", null, ("file", path), ("which", which), ("outcome", r.Outcome.ToString()), ("otherServers", r.OtherServers));
        }
    }
}
