using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Mcp
{
    /// <summary>Which MCP configuration file and which root key(s) it uses.</summary>
    public sealed class McpConfigTarget
    {
        public McpConfigTarget(string path, params string[] rootKeys)
            : this(path, rootKeys, Array.Empty<string>())
        {
        }

        public McpConfigTarget(string path, IReadOnlyList<string> rootKeys, IReadOnlyList<string> dropKeys)
        {
            Path = path;
            RootKeys = rootKeys;
            DropKeys = dropKeys ?? Array.Empty<string>();
        }

        public string Path { get; }

        /// <summary>"servers" (Visual Studio .vs\mcp.json, VS Code .vscode/mcp.json) and/or "mcpServers" (Claude Code .mcp.json, Cursor .cursor/mcp.json).</summary>
        public IReadOnlyList<string> RootKeys { get; }

        /// <summary>Root keys whose uetovs entry is removed when writing (written by older versions, no longer wanted here).</summary>
        public IReadOnlyList<string> DropKeys { get; }

        /// <summary>Every key that may hold a uetovs entry in this file.</summary>
        public IReadOnlyList<string> AllKeys => RootKeys.Concat(DropKeys).ToArray();

        /// <summary>
        /// &lt;solution&gt;\.vs\mcp.json: Visual Studio's per-user, per-solution MCP file (not source-controlled). The host keeps
        /// the "uetovs" entry here up to date automatically while "Enable MCP server" is on — this is how VS discovers it.
        /// </summary>
        public static McpConfigTarget VisualStudioUser(string solutionDir) =>
            new McpConfigTarget(System.IO.Path.Combine(solutionDir, ".vs", "mcp.json"), McpConfig.VsRootKey);

        /// <summary>
        /// &lt;dir&gt;\.mcp.json, the shareable project-scope file Claude Code reads ("mcpServers") from the folder it starts in.
        /// &lt;dir&gt; is the version-control root above the solution, else the solution folder (McpRegistration resolves it).
        /// Only "mcpServers" is written: Visual Studio also reads &lt;solution&gt;\.mcp.json's "servers" and already gets
        /// uetovs from .vs\mcp.json, so a "servers" copy here would register the same server twice. A "servers" uetovs
        /// entry written by earlier versions is removed.
        /// </summary>
        public static McpConfigTarget SharedProject(string dir) =>
            new McpConfigTarget(System.IO.Path.Combine(dir, ".mcp.json"), new[] { McpConfig.PortableRootKey }, new[] { McpConfig.VsRootKey });

        /// <summary>&lt;solution&gt;\.mcp.json as earlier versions wrote it (both keys): only used to clean it up after the shared file moved.</summary>
        public static McpConfigTarget LegacySolutionRoot(string solutionDir) =>
            new McpConfigTarget(System.IO.Path.Combine(solutionDir, ".mcp.json"), McpConfig.VsRootKey, McpConfig.PortableRootKey);
    }

    public enum McpMergeOutcome
    {
        Created,
        Updated,
        Unchanged,
        Removed,

        /// <summary>The existing file is not valid JSON (or its root is not an object): left untouched.</summary>
        Invalid,
    }

    public sealed class McpMergeResult
    {
        public McpMergeOutcome Outcome { get; set; }
        public string Json { get; set; }
        public string Error { get; set; }

        /// <summary>Other servers already in the file (kept as they were).</summary>
        public int OtherServers { get; set; }
    }

    /// <summary>
    /// Builds and merges the "uetovs" MCP server entry into MCP configuration files. Pure (strings in, strings out) apart
    /// from <see cref="ApplyToFile"/>. Merging never drops other servers or unknown keys; an invalid file is never overwritten.
    /// </summary>
    public static class McpConfig
    {
        public const string ServerName = "uetovs";
        public const string VsRootKey = "servers";
        public const string PortableRootKey = "mcpServers";
        public const string ExeName = "UEToVS.Mcp.exe";

        /// <summary>&lt;extension dir&gt;\Engine\Mcp\UEToVS.Mcp.exe (own folder: its dependencies never clash with the engine's).</summary>
        public static string ResolveExecutable(string extensionDir) =>
            System.IO.Path.Combine(extensionDir ?? string.Empty, "Engine", "Mcp", ExeName);

        /// <summary>{"type":"stdio","command":exe,"args":["--solution-dir",dir]}: machine-specific, used for VS's per-user .vs\mcp.json.</summary>
        public static JObject BuildEntry(string exePath, string solutionDir) => new JObject
        {
            ["type"] = "stdio",
            ["command"] = exePath,
            ["args"] = new JArray("--solution-dir", solutionDir),
        };

        // ------------------------------------------------------------------ portable entry (shared .mcp.json)

        /// <summary>Launcher script the extension keeps at a fixed per-user location (same path on every machine).</summary>
        public const string LauncherFileName = "uetovs-mcp.cmd";

        /// <summary>The launcher path as written into shared files; cmd.exe expands %LOCALAPPDATA% itself, so no client has to.</summary>
        public const string PortableLauncherPath = @"%LOCALAPPDATA%\UEToVS\Mcp\" + LauncherFileName;

        /// <summary>&lt;LocalAppData&gt;\UEToVS\Mcp\uetovs-mcp.cmd on this machine.</summary>
        public static string ResolveLauncherPath(string localAppData) =>
            System.IO.Path.Combine(localAppData ?? string.Empty, "UEToVS", "Mcp", LauncherFileName);

        /// <summary>
        /// Entry for the shareable .mcp.json, with no absolute path of this machine: <c>cmd /c %LOCALAPPDATA%\UEToVS\Mcp\uetovs-mcp.cmd</c>
        /// runs the launcher that every colleague's UEToVS keeps current (cmd expands the variable, so it works whether or
        /// not the agent expands environment variables), and the solution folder is relative to the folder holding the
        /// .mcp.json (agents start there; UEToVS.Mcp resolves it against its working directory).
        /// </summary>
        public static JObject BuildPortableEntry(string relativeSolutionDir) => new JObject
        {
            ["type"] = "stdio",
            ["command"] = "cmd",
            ["args"] = new JArray("/c", PortableLauncherPath, "--solution-dir", string.IsNullOrEmpty(relativeSolutionDir) ? "." : relativeSolutionDir),
        };

        /// <summary>
        /// <paramref name="solutionDir"/> relative to <paramref name="configDir"/> ("." when equal, forward slashes), or the
        /// absolute path when it is not inside it (then the entry is necessarily machine-specific).
        /// </summary>
        public static string RelativeSolutionDir(string configDir, string solutionDir)
        {
            string c = NormDir(configDir), s = NormDir(solutionDir);
            if (string.Equals(c, s, StringComparison.OrdinalIgnoreCase)) return ".";
            if (c.Length > 0 && s.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase)) return s.Substring(c.Length + 1).Replace('\\', '/');
            return solutionDir;
        }

        private static string NormDir(string p) => (p ?? string.Empty).Replace('/', '\\').TrimEnd('\\');

        /// <summary>True for the command of the portable launcher form.</summary>
        public static bool IsPortableCommand(string command) =>
            string.Equals(command, "cmd", StringComparison.OrdinalIgnoreCase) || string.Equals(command, "cmd.exe", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The launcher: runs the UEToVS.Mcp.exe of the installed extension (path recorded when VS last opened a UE solution);
        /// when that folder is gone (extension updated or removed meanwhile) it takes a per-user VSIX install found under
        /// %LOCALAPPDATA%\Microsoft\VisualStudio. Writes nothing to stdout (the MCP protocol uses it); passes arguments through.
        /// </summary>
        public static string BuildLauncherScript(string exePath)
        {
            string exe = ExeName;
            return string.Join("\r\n", new[]
            {
                "@echo off",
                "rem Generated by UEToVS (Visual Studio extension): starts its MCP server for AI agents. Rewritten whenever VS opens a UE solution.",
                "setlocal",
                "set \"UETOVS_MCP=" + exePath + "\"",
                "if not exist \"%UETOVS_MCP%\" call :find",
                "if not exist \"%UETOVS_MCP%\" goto :missing",
                "\"%UETOVS_MCP%\" %*",
                "exit /b %ERRORLEVEL%",
                ":find",
                "for /d %%V in (\"%LOCALAPPDATA%\\Microsoft\\VisualStudio\\*\") do for /d %%E in (\"%%V\\Extensions\\*\") do if exist \"%%E\\Engine\\Mcp\\" + exe +
                "\" set \"UETOVS_MCP=%%E\\Engine\\Mcp\\" + exe + "\"",
                "exit /b 0",
                ":missing",
                "echo UEToVS MCP server not found: install the UEToVS extension and open the solution in Visual Studio once. 1>&2",
                "exit /b 1",
                "",
            });
        }

        /// <summary>Writes the launcher when its content changed (atomic). Returns true when the file was (re)written.</summary>
        public static bool WriteLauncher(string launcherPath, string exePath)
        {
            string text = BuildLauncherScript(exePath);
            if (File.Exists(launcherPath) && File.ReadAllText(launcherPath) == text) return false;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(launcherPath));
            string tmp = launcherPath + ".tmp";
            File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            if (File.Exists(launcherPath)) File.Replace(tmp, launcherPath, null);
            else File.Move(tmp, launcherPath);
            return true;
        }

        /// <summary>
        /// Adds or refreshes the uetovs entry under every root key of <paramref name="rootKeys"/>. In an existing entry,
        /// "type" and "command" are replaced and the "--solution-dir" value is updated; any other args, "env" and other
        /// fields the user added are kept.
        /// </summary>
        public static McpMergeResult Merge(string existingJson, IReadOnlyList<string> rootKeys, JObject entry, IReadOnlyList<string> dropKeys = null)
        {
            bool created = string.IsNullOrWhiteSpace(existingJson);
            JObject root;
            if (created) root = new JObject();
            else if (!TryParse(existingJson, out root, out string error))
                return new McpMergeResult { Outcome = McpMergeOutcome.Invalid, Error = error, Json = existingJson };

            bool changed = false;
            int others = 0;
            foreach (var key in rootKeys)
            {
                if (!(root[key] is JObject servers))
                {
                    if (root[key] != null && root[key].Type != JTokenType.Null)
                        return new McpMergeResult { Outcome = McpMergeOutcome.Invalid, Error = $"\"{key}\" is not an object", Json = existingJson };
                    servers = new JObject();
                    root[key] = servers;
                    changed = true;
                }
                others = Math.Max(others, servers.Properties().Count(p => p.Name != ServerName));
                if (servers[ServerName] is JObject current)
                {
                    var merged = MergeEntry(current, entry);
                    if (!JToken.DeepEquals(current, merged))
                    {
                        servers[ServerName] = merged;
                        changed = true;
                    }
                }
                else
                {
                    servers[ServerName] = entry.DeepClone();
                    changed = true;
                }
            }
            foreach (var key in dropKeys ?? Array.Empty<string>())
            {
                if (rootKeys.Contains(key) || !(root[key] is JObject servers) || !servers.Remove(ServerName)) continue;
                changed = true;
                if (!servers.HasValues) root.Remove(key);
            }
            return new McpMergeResult
            {
                Outcome = created ? McpMergeOutcome.Created : changed ? McpMergeOutcome.Updated : McpMergeOutcome.Unchanged,
                Json = changed || created ? root.ToString(Formatting.Indented) + Environment.NewLine : existingJson,
                OtherServers = others,
            };
        }

        private static JObject MergeEntry(JObject current, JObject entry)
        {
            var merged = (JObject)current.DeepClone();
            merged["type"] = entry["type"]?.DeepClone();
            merged["command"] = entry["command"]?.DeepClone();
            var wanted = (entry["args"] as JArray)?.Select(t => (string)t).ToList() ?? new List<string>();
            var have = (merged["args"] as JArray)?.Select(t => t.Type == JTokenType.String ? (string)t : t.ToString()).ToList();
            if (have == null)
            {
                merged["args"] = new JArray(wanted.Cast<object>().ToArray());
                return merged;
            }
            // positional args before the first "--flag" (the portable form's "/c <launcher>") belong to the command: replaced as
            // a whole; an old "/c <launcher>" prefix is dropped when the entry goes back to a direct exe
            int lead = wanted.FindIndex(a => a.StartsWith("--", StringComparison.Ordinal));
            if (lead < 0) lead = wanted.Count;
            if (have.Count >= 2 && string.Equals(have[0], "/c", StringComparison.OrdinalIgnoreCase)) have.RemoveRange(0, 2);
            have.InsertRange(0, wanted.Take(lead));
            // update "--x value" pairs we own, keep everything else in the user's order
            for (int i = lead; i + 1 < wanted.Count; i += 2)
            {
                string flag = wanted[i], value = wanted[i + 1];
                int at = have.IndexOf(flag);
                if (at >= 0 && at + 1 < have.Count) have[at + 1] = value;
                else if (at >= 0) have.Add(value);
                else
                {
                    have.Add(flag);
                    have.Add(value);
                }
            }
            merged["args"] = new JArray(have.Cast<object>().ToArray());
            return merged;
        }

        /// <summary>Removes the uetovs entry (setting turned off). Empty root objects are removed too; nothing else changes.</summary>
        public static McpMergeResult Remove(string existingJson, IReadOnlyList<string> rootKeys)
        {
            if (string.IsNullOrWhiteSpace(existingJson)) return new McpMergeResult { Outcome = McpMergeOutcome.Unchanged, Json = existingJson };
            if (!TryParse(existingJson, out var root, out string error))
                return new McpMergeResult { Outcome = McpMergeOutcome.Invalid, Error = error, Json = existingJson };
            bool changed = false;
            foreach (var key in rootKeys)
            {
                if (root[key] is JObject servers && servers.Remove(ServerName))
                {
                    changed = true;
                    if (!servers.HasValues) root.Remove(key);
                }
            }
            return new McpMergeResult
            {
                Outcome = changed ? McpMergeOutcome.Removed : McpMergeOutcome.Unchanged,
                Json = changed ? root.ToString(Formatting.Indented) + Environment.NewLine : existingJson,
            };
        }

        /// <summary>The command path of an existing uetovs entry (first root key that has one), or null.</summary>
        public static string ExistingCommand(string json, IReadOnlyList<string> rootKeys)
        {
            if (string.IsNullOrWhiteSpace(json) || !TryParse(json, out var root, out _)) return null;
            foreach (var key in rootKeys)
                if (root[key]?[ServerName]?["command"] is JValue v && v.Type == JTokenType.String) return (string)v;
            return null;
        }

        private static bool TryParse(string json, out JObject root, out string error)
        {
            root = null;
            error = null;
            try
            {
                var token = JToken.Parse(json, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore, DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace });
                root = token as JObject;
                if (root == null) error = "root is not a JSON object";
                return root != null;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads, merges (or removes) and writes <paramref name="target"/> atomically. Returns the outcome; throws on I/O errors.
        /// With <paramref name="deleteIfEmpty"/>, a removal that leaves an empty object deletes the file instead.
        /// </summary>
        public static McpMergeResult ApplyToFile(McpConfigTarget target, JObject entry, bool remove = false, bool deleteIfEmpty = false)
        {
            string existing = File.Exists(target.Path) ? File.ReadAllText(target.Path) : null;
            if (remove && existing == null) return new McpMergeResult { Outcome = McpMergeOutcome.Unchanged };
            var result = remove ? Remove(existing, target.AllKeys) : Merge(existing, target.RootKeys, entry, target.DropKeys);
            if (remove && deleteIfEmpty && result.Outcome == McpMergeOutcome.Removed && TryParse(result.Json, out var left, out _) && !left.HasValues)
            {
                File.Delete(target.Path);
                return result;
            }
            if (result.Outcome == McpMergeOutcome.Created || result.Outcome == McpMergeOutcome.Updated || result.Outcome == McpMergeOutcome.Removed)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target.Path));
                string tmp = target.Path + ".tmp";
                File.WriteAllText(tmp, result.Json, new System.Text.UTF8Encoding(false));
                if (File.Exists(target.Path)) File.Replace(tmp, target.Path, null);
                else File.Move(tmp, target.Path);
            }
            return result;
        }
    }
}
