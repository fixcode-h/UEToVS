using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Logic.Settings
{
    public sealed class SettingsMergeResult
    {
        public HostSettings Effective { get; set; }

        /// <summary>Keys overridden by the solution file, with the value applied (for the session header).</summary>
        public Dictionary<string, object> SolutionOverrides { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Problems found in the solution file (unknown keys, bad values, disallowed keys). Logged as Warn.</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Keys in sections other modules own (e.g. "naming", "inspections"); passed through, not interpreted here.</summary>
        public List<string> ForeignKeys { get; } = new List<string>();

        /// <summary>Path of the solution file that was read, or null.</summary>
        public string SolutionFile { get; set; }
    }

    /// <summary>
    /// Merges the team-shared &lt;solution&gt;/.uetovs/settings.json on top of the user's settings (§10.3). The file is
    /// read-only for UEToVS (never written). Keys may be nested objects or dotted names:
    /// <code>{ "log": { "level": "Debug" }, "intelliSense.mode": "UnrealSolutionOnly" }</code>
    /// Security: keys that would make VS start a different executable (engine.path) are refused from solution files.
    /// </summary>
    public static class SettingsMerger
    {
        public const string FolderName = ".uetovs";
        public const string FileName = "settings.json";

        /// <summary>Sections owned by the host; unknown keys inside them produce a warning.</summary>
        private static readonly string[] HostSections = { "log", "engine", "intelliSense" };

        /// <summary>Keys a solution file may not set.</summary>
        private static readonly HashSet<string> Refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "engine.path" };

        public static string SolutionSettingsPath(string solutionDir) =>
            string.IsNullOrEmpty(solutionDir) ? null : Path.Combine(solutionDir, FolderName, FileName);

        /// <summary>Reads &lt;solutionDir&gt;/.uetovs/settings.json if it exists and merges it. Never throws.</summary>
        public static SettingsMergeResult MergeFromSolution(HostSettings user, string solutionDir)
        {
            string path = SolutionSettingsPath(solutionDir);
            string text = null;
            var pre = new List<string>();
            if (path != null)
            {
                try
                {
                    if (File.Exists(path)) text = File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    pre.Add($"cannot read {path}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            var r = Merge(user, text);
            if (text != null) r.SolutionFile = path;
            r.Warnings.InsertRange(0, pre);
            return r;
        }

        /// <summary>Merges <paramref name="solutionJson"/> (may be null/empty) over a copy of <paramref name="user"/>.</summary>
        public static SettingsMergeResult Merge(HostSettings user, string solutionJson)
        {
            var result = new SettingsMergeResult { Effective = (user ?? new HostSettings()).Clone() };
            if (string.IsNullOrWhiteSpace(solutionJson)) return result;

            JObject root;
            try
            {
                root = JObject.Parse(solutionJson);
            }
            catch (JsonException ex)
            {
                result.Warnings.Add("settings.json is not valid JSON: " + ex.Message);
                return result;
            }

            var flat = new List<KeyValuePair<string, JToken>>();
            Flatten(root, null, flat);
            foreach (var kv in flat)
            {
                string key = kv.Key;
                string section = key.Split('.')[0];
                bool ours = Array.Exists(HostSections, s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase));
                if (!ours)
                {
                    result.ForeignKeys.Add(key);
                    continue;
                }
                if (Refused.Contains(key))
                {
                    result.Warnings.Add($"'{key}' is not allowed in solution settings (user setting only)");
                    continue;
                }
                try
                {
                    if (!Apply(result.Effective, key, kv.Value, out object applied))
                    {
                        result.Warnings.Add($"unknown setting '{key}'");
                        continue;
                    }
                    result.SolutionOverrides[key] = applied;
                }
                catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is InvalidCastException || ex is OverflowException || ex is JsonException)
                {
                    result.Warnings.Add($"invalid value for '{key}': {kv.Value.ToString(Formatting.None)}");
                }
            }
            return result;
        }

        private static void Flatten(JToken token, string prefix, List<KeyValuePair<string, JToken>> output)
        {
            if (token is JObject obj)
            {
                foreach (var p in obj.Properties())
                {
                    string key = prefix == null ? p.Name : prefix + "." + p.Name;
                    Flatten(p.Value, key, output);
                }
            }
            else if (prefix != null)
            {
                output.Add(new KeyValuePair<string, JToken>(prefix, token));
            }
        }

        private static bool Apply(HostSettings s, string key, JToken v, out object applied)
        {
            switch (key.ToLowerInvariant())
            {
                case "log.level":
                    if (!Log.TryParseLevel(v.Value<string>(), out var level) || !Enum.IsDefined(typeof(LogLevel), level))
                        throw new FormatException();
                    s.LogLevel = level;
                    applied = level.ToString();
                    return true;
                case "log.retentiondays":
                    s.LogRetentionDays = Positive(v.Value<int>());
                    applied = s.LogRetentionDays;
                    return true;
                case "log.maxtotalmb":
                    s.LogMaxTotalMB = Positive(v.Value<int>());
                    applied = s.LogMaxTotalMB;
                    return true;
                case "engine.unrealengineroot":
                    s.UnrealEngineRootOverride = v.Value<string>() ?? string.Empty;
                    applied = s.UnrealEngineRootOverride;
                    return true;
                case "engine.requesttimeoutseconds":
                    s.EngineRequestTimeoutSeconds = Positive(v.Value<int>());
                    applied = s.EngineRequestTimeoutSeconds;
                    return true;
                case "intellisense.takeover":
                    s.IntelliSenseTakeover = v.Value<bool>();
                    applied = s.IntelliSenseTakeover;
                    return true;
                case "intellisense.mode":
                    s.IntelliSenseTakeoverMode = (IntelliSenseTakeoverMode)Enum.Parse(typeof(IntelliSenseTakeoverMode), v.Value<string>(), true);
                    if (!Enum.IsDefined(typeof(IntelliSenseTakeoverMode), s.IntelliSenseTakeoverMode)) throw new FormatException();
                    applied = s.IntelliSenseTakeoverMode.ToString();
                    return true;
                case "intellisense.disabledatabase":
                    s.DisableDatabase = v.Value<bool>();
                    applied = s.DisableDatabase;
                    return true;
                case "intellisense.disablebackgroundcodeanalysis":
                    s.DisableBackgroundCodeAnalysis = v.Value<bool>();
                    applied = s.DisableBackgroundCodeAnalysis;
                    return true;
                case "vs.blueprintsupport":
                    s.VsBlueprintSupport = v.Value<bool>();
                    applied = s.VsBlueprintSupport;
                    return true;
                default:
                    applied = null;
                    return false;
            }
        }

        private static int Positive(int value)
        {
            if (value <= 0) throw new ArgumentException("must be > 0");
            return value;
        }
    }
}
