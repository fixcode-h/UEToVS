using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// shareable project-scope .mcp.json (version-control root, else the solution folder) for Claude Code. All file work runs off the UI thread.
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

            // the shared .mcp.json written earlier by the command: keep its command path valid across extension updates
            // (the VSIX folder changes with each version). Only touched when it already has a uetovs entry.
            var shared = McpConfigTarget.SharedProject(ResolveSharedConfigDir(solutionDir));
            if (File.Exists(shared.Path))
            {
                string current = McpConfig.ExistingCommand(File.ReadAllText(shared.Path), shared.AllKeys);
                if (current != null && !string.Equals(current, exe, StringComparison.OrdinalIgnoreCase))
                    LogResult(McpConfig.ApplyToFile(shared, entry), shared.Path, "shared-refresh");
            }
        });

        /// <summary>Command: write/merge the shared .mcp.json (version-control root, else solution folder) and tell the user what happened.</summary>
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
            var (target, r) = await Task.Run(() =>
            {
                string dir = ResolveSharedConfigDir(solutionDir);
                var t = McpConfigTarget.SharedProject(dir);
                var result = McpConfig.ApplyToFile(t, McpConfig.BuildEntry(exe, solutionDir));
                LogResult(result, t.Path, "shared");
                if (result.Outcome != McpMergeOutcome.Invalid) CleanUpLegacy(solutionDir, t.Path);
                return (t, result);
            }).ConfigureAwait(false);
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

        /// <summary>
        /// Where the shared .mcp.json goes: the version-control root above the solution (Git/SVN/Mercurial/Plastic marker or
        /// P4CONFIG file; else the Perforce client root from p4 when the solution lies in it), otherwise the solution folder
        /// (the UE project root). Agents such as Claude Code are usually started at the repository root.
        /// </summary>
        internal static string ResolveSharedConfigDir(string solutionDir)
        {
            string root = VcsRoot.Find(solutionDir) ?? PerforceClientRoot(solutionDir);
            string dir = root != null && VcsRoot.IsSameOrUnder(solutionDir, root) ? root : solutionDir;
            L.Info("mcp.config.dir", null, ("solution", solutionDir), ("dir", dir), ("vcs", root ?? "none"));
            return dir;
        }

        /// <summary>"p4 -ztag -F %clientRoot% info" run in the solution folder (3 s limit); null when p4 is missing or the folder is not in the client.</summary>
        private static string PerforceClientRoot(string solutionDir)
        {
            try
            {
                var psi = new ProcessStartInfo("p4", "-ztag -F %clientRoot% info")
                {
                    WorkingDirectory = solutionDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                };
                psi.EnvironmentVariables["PWD"] = solutionDir; // p4 prefers PWD over the process cwd when set
                var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) output.Enqueue(e.Data.Trim()); };
                    p.ErrorDataReceived += (_, e) => { };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.StandardInput.Close();
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        L.Info("mcp.config.p4", "p4 info timed out");
                        return null;
                    }
                    p.WaitForExit(); // flushes the redirected output
                    string line = output.FirstOrDefault();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("*", StringComparison.Ordinal) || !Path.IsPathRooted(line)) return null;
                    return VcsRoot.IsSameOrUnder(solutionDir, line) ? line : null;
                }
            }
            catch (Exception ex) // p4 not installed (Win32Exception) or anything else: no Perforce root
            {
                L.Info("mcp.config.p4", "p4 not available", ("error", ex.Message));
                return null;
            }
        }

        /// <summary>The shared file moved away from &lt;solution&gt;\.mcp.json: remove the uetovs entries earlier versions wrote there (the file too if nothing else is left).</summary>
        private static void CleanUpLegacy(string solutionDir, string sharedPath)
        {
            var legacy = McpConfigTarget.LegacySolutionRoot(solutionDir);
            if (string.Equals(Path.GetFullPath(legacy.Path), Path.GetFullPath(sharedPath), StringComparison.OrdinalIgnoreCase) || !File.Exists(legacy.Path)) return;
            try
            {
                var r = McpConfig.ApplyToFile(legacy, null, remove: true, deleteIfEmpty: true);
                LogResult(r, legacy.Path, "legacy-cleanup");
            }
            catch (Exception ex)
            {
                L.Warn("mcp.config.legacy_cleanup_failed", "Could not clean up the old .mcp.json", ("file", legacy.Path), ("error", ex.Message));
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
