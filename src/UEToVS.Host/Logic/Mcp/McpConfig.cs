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
        {
            Path = path;
            RootKeys = rootKeys;
        }

        public string Path { get; }

        /// <summary>"servers" (Visual Studio, VS Code .vscode/mcp.json) and/or "mcpServers" (Claude Code, Cursor, VS Code workspace-root .mcp.json).</summary>
        public IReadOnlyList<string> RootKeys { get; }

        /// <summary>
        /// &lt;solution&gt;\.vs\mcp.json: Visual Studio's per-user, per-solution MCP file (not source-controlled). The host keeps
        /// the "uetovs" entry here up to date automatically while "Enable MCP server" is on — this is how VS discovers it.
        /// </summary>
        public static McpConfigTarget VisualStudioUser(string solutionDir) =>
            new McpConfigTarget(System.IO.Path.Combine(solutionDir, ".vs", "mcp.json"), McpConfig.VsRootKey);

        /// <summary>
        /// &lt;solution&gt;\.mcp.json: read by Visual Studio ("servers"), Claude Code and VS Code's workspace-root config ("mcpServers").
        /// Written on request (command), with both keys so one file serves every agent.
        /// </summary>
        public static McpConfigTarget SolutionRoot(string solutionDir) =>
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

        /// <summary>{"type":"stdio","command":exe,"args":["--solution-dir",dir]}.</summary>
        public static JObject BuildEntry(string exePath, string solutionDir) => new JObject
        {
            ["type"] = "stdio",
            ["command"] = exePath,
            ["args"] = new JArray("--solution-dir", solutionDir),
        };

        /// <summary>
        /// Adds or refreshes the uetovs entry under every root key of <paramref name="rootKeys"/>. In an existing entry,
        /// "type" and "command" are replaced and the "--solution-dir" value is updated; any other args, "env" and other
        /// fields the user added are kept.
        /// </summary>
        public static McpMergeResult Merge(string existingJson, IReadOnlyList<string> rootKeys, JObject entry)
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
            // update "--x value" pairs we own, keep everything else in the user's order
            for (int i = 0; i + 1 < wanted.Count; i += 2)
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

        /// <summary>Reads, merges (or removes) and writes <paramref name="target"/> atomically. Returns the outcome; throws on I/O errors.</summary>
        public static McpMergeResult ApplyToFile(McpConfigTarget target, JObject entry, bool remove = false)
        {
            string existing = File.Exists(target.Path) ? File.ReadAllText(target.Path) : null;
            if (remove && existing == null) return new McpMergeResult { Outcome = McpMergeOutcome.Unchanged };
            var result = remove ? Remove(existing, target.RootKeys) : Merge(existing, target.RootKeys, entry);
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
