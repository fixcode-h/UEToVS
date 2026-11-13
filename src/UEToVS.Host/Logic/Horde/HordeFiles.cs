using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Horde
{
    /// <summary>Where UBT reads BuildConfiguration.xml from (lowest precedence first) and where the Horde Agent keeps Agent.json.</summary>
    public static class HordeLocations
    {
        public enum Level
        {
            Engine = 0,
            UserAppData = 1,
            Documents = 2,
            Project = 3,
        }

        public sealed class ConfigFile
        {
            public ConfigFile(Level level, string path)
            {
                FileLevel = level;
                Path = path;
            }

            public Level FileLevel { get; }
            public string Path { get; }
        }

        /// <summary>
        /// The BuildConfiguration.xml files UBT reads, lowest precedence first (a later file overrides an earlier one).
        /// Null roots are skipped.
        /// </summary>
        public static List<ConfigFile> BuildConfigurationFiles(string engineRoot, string projectDirectory, string appData, string documents)
        {
            var list = new List<ConfigFile>();
            if (!string.IsNullOrEmpty(engineRoot)) list.Add(new ConfigFile(Level.Engine, Path.Combine(engineRoot, "Engine", "Saved", "UnrealBuildTool", "BuildConfiguration.xml")));
            if (!string.IsNullOrEmpty(appData)) list.Add(new ConfigFile(Level.UserAppData, Path.Combine(appData, "Unreal Engine", "UnrealBuildTool", "BuildConfiguration.xml")));
            if (!string.IsNullOrEmpty(documents)) list.Add(new ConfigFile(Level.Documents, Path.Combine(documents, "Unreal Engine", "UnrealBuildTool", "BuildConfiguration.xml")));
            if (!string.IsNullOrEmpty(projectDirectory)) list.Add(new ConfigFile(Level.Project, Path.Combine(projectDirectory, "Saved", "UnrealBuildTool", "BuildConfiguration.xml")));
            return list;
        }

        /// <summary>The file UEToVS writes for a target (null when the project is unknown and the target is the project).</summary>
        public static string TargetFile(HordeConfigTarget target, string projectDirectory, string appData)
        {
            if (target == HordeConfigTarget.User)
                return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "Unreal Engine", "UnrealBuildTool", "BuildConfiguration.xml");
            return string.IsNullOrEmpty(projectDirectory) ? null : Path.Combine(projectDirectory, "Saved", "UnrealBuildTool", "BuildConfiguration.xml");
        }

        /// <summary>C:\ProgramData\Epic\Horde\Agent\Agent.json (settings applied on top of the agent's appsettings.json).</summary>
        public static string AgentJson(string programData) =>
            string.IsNullOrEmpty(programData) ? null : Path.Combine(programData, "Epic", "Horde", "Agent", "Agent.json");

        /// <summary>Agent executables looked for under the install folder (zip install, MSI install).</summary>
        public static string FindAgentExecutable(string installPath)
        {
            if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) return null;
            foreach (var name in new[] { "HordeAgent.exe", "HordeAgent.dll" })
            {
                string direct = Path.Combine(installPath, name);
                if (File.Exists(direct)) return direct;
                foreach (var sub in new[] { "Agent", "HordeAgent" })
                {
                    string p = Path.Combine(installPath, sub, name);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }
    }

    /// <summary>Engine version from Engine/Build/Build.version.</summary>
    public static class EngineVersionReader
    {
        public static Version Read(string engineRoot)
        {
            if (string.IsNullOrEmpty(engineRoot)) return null;
            string path = Path.Combine(engineRoot, "Engine", "Build", "Build.version");
            if (!File.Exists(path)) return null;
            try { return Parse(File.ReadAllText(path)); }
            catch (Exception) { return null; }
        }

        public static Version Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var o = JObject.Parse(json);
            int major = (int?)o["MajorVersion"] ?? -1;
            int minor = (int?)o["MinorVersion"] ?? -1;
            int patch = (int?)o["PatchVersion"] ?? 0;
            return major < 0 || minor < 0 ? null : new Version(major, minor, Math.Max(0, patch));
        }
    }

    /// <summary>One value as UBT sees it after merging the BuildConfiguration.xml files, and the file it came from.</summary>
    public sealed class EffectiveValue
    {
        public string Value { get; set; }
        public string SourceFile { get; set; }
    }

    /// <summary>Merges the BuildConfiguration.xml files (a later file overrides an earlier one).</summary>
    public static class EffectiveBuildConfiguration
    {
        public static Dictionary<string, EffectiveValue> Read(IEnumerable<HordeLocations.ConfigFile> files, IEnumerable<(string Section, string Key)> keys, Action<string, Exception> onError = null)
        {
            var result = new Dictionary<string, EffectiveValue>(StringComparer.Ordinal);
            var keyList = keys.ToList();
            foreach (var f in files)
            {
                if (!File.Exists(f.Path)) continue;
                BuildConfigurationXml xml;
                try { xml = BuildConfigurationXml.Load(f.Path); }
                catch (Exception ex) { onError?.Invoke(f.Path, ex); continue; }
                foreach (var (section, key) in keyList)
                {
                    string v = xml.Get(section, key);
                    if (v != null) result[section + "/" + key] = new EffectiveValue { Value = v, SourceFile = f.Path };
                }
            }
            return result;
        }
    }

    /// <summary>Reads and changes <c>Horde.computePort</c> in the Horde Agent's Agent.json (0 = no incoming compute work).</summary>
    public static class AgentJsonConfig
    {
        public const string ComputePort = "computePort";

        private static JProperty HordeSection(JObject root) =>
            root.Properties().FirstOrDefault(p => string.Equals(p.Name, "Horde", StringComparison.OrdinalIgnoreCase));

        private static JProperty Find(JObject section, string name) =>
            section?.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The configured computePort as text ("0", "7000"), or null when Agent.json does not set it.</summary>
        public static string ReadComputePort(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var root = JObject.Parse(json);
            var section = HordeSection(root)?.Value as JObject;
            var p = Find(section, ComputePort);
            return p == null || p.Value.Type == JTokenType.Null ? null : Convert.ToString(((JValue)p.Value).Value, CultureInfo.InvariantCulture);
        }

        /// <summary>Agent.json text with computePort set to <paramref name="port"/> (null removes it). Other settings are kept.</summary>
        public static string WithComputePort(string json, string port)
        {
            var root = string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json);
            var sectionProp = HordeSection(root);
            if (sectionProp == null || !(sectionProp.Value is JObject))
            {
                if (port == null) return root.ToString(Formatting.Indented);
                var created = new JObject();
                if (sectionProp == null) root.Add("Horde", created);
                else sectionProp.Value = created;
                sectionProp = HordeSection(root);
            }
            var section = (JObject)sectionProp.Value;
            var p = Find(section, ComputePort);
            if (port == null) p?.Remove();
            else
            {
                JToken value = int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? new JValue(n) : new JValue(port);
                if (p == null) section.Add(ComputePort, value);
                else p.Value = value;
            }
            return root.ToString(Formatting.Indented);
        }

        public static bool IsBlocked(string port) => port == "0";
    }

    /// <summary>
    /// What UEToVS changed, so "restore" (and turning a setting back to "UE default") puts back the earlier values: for
    /// each written BuildConfiguration.xml the value every key had before UEToVS first wrote it (null = absent), and the
    /// Agent.json computePort before UEToVS first changed it. Stored at %LocalAppData%\UEToVS\horde-backup.json (the
    /// files are machine-wide, not per solution).
    /// </summary>
    public sealed class HordeBackupManifest
    {
        public const string AbsentMarker = "<absent>";

        [JsonProperty("files")]
        public Dictionary<string, Dictionary<string, string>> Files { get; set; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>computePort before UEToVS changed it (<see cref="AbsentMarker"/> = not set); null = never changed.</summary>
        [JsonProperty("agentComputePort")]
        public string AgentComputePort { get; set; }

        [JsonProperty("agentFile")]
        public string AgentFile { get; set; }

        public static HordeBackupManifest Load(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    var m = JsonConvert.DeserializeObject<HordeBackupManifest>(File.ReadAllText(path));
                    if (m != null)
                    {
                        m.Files = new Dictionary<string, Dictionary<string, string>>(m.Files ?? new Dictionary<string, Dictionary<string, string>>(), StringComparer.OrdinalIgnoreCase);
                        return m;
                    }
                }
            }
            catch (Exception)
            {
                // a broken manifest only loses the "before" values; the files themselves are not touched
            }
            return new HordeBackupManifest();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public bool IsEmpty => AgentComputePort == null && Files.All(f => f.Value.Count == 0);
    }

    /// <summary>Result of writing one BuildConfiguration.xml.</summary>
    public sealed class BuildConfigurationWriteResult
    {
        public string Path { get; set; }
        public List<string> Written { get; } = new List<string>();
        public List<string> Restored { get; } = new List<string>();
        public bool FileChanged { get; set; }
    }

    /// <summary>Applies a <see cref="HordeConfigPlan"/> to a BuildConfiguration.xml and records the earlier values in the manifest.</summary>
    public static class BuildConfigurationWriter
    {
        public static BuildConfigurationWriteResult Apply(string path, HordeConfigPlan plan, HordeBackupManifest manifest)
        {
            var result = new BuildConfigurationWriteResult { Path = path };
            bool existed = File.Exists(path);
            var xml = BuildConfigurationXml.Load(path);
            if (!manifest.Files.TryGetValue(path, out var originals))
                manifest.Files[path] = originals = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var e in plan.Entries)
            {
                if (e.Value != null)
                {
                    if (!originals.ContainsKey(e.Id)) originals[e.Id] = xml.Get(e.Section, e.Key) ?? HordeBackupManifest.AbsentMarker;
                    xml.Set(e.Section, e.Key, e.Value);
                    result.Written.Add(e.Id);
                }
                else if (originals.TryGetValue(e.Id, out var before))
                {
                    xml.Set(e.Section, e.Key, before == HordeBackupManifest.AbsentMarker ? null : before);
                    originals.Remove(e.Id);
                    result.Restored.Add(e.Id);
                }
            }
            if (originals.Count == 0) manifest.Files.Remove(path);
            if (xml.Changed && (existed || result.Written.Count > 0))
            {
                xml.Save(path);
                result.FileChanged = true;
            }
            return result;
        }

        /// <summary>Puts back every key of every file in the manifest (the "恢复到修改前" button).</summary>
        public static List<BuildConfigurationWriteResult> RestoreAll(HordeBackupManifest manifest)
        {
            var empty = HordeConfigPlan.Build(null, 1);
            var results = new List<BuildConfigurationWriteResult>();
            foreach (var path in manifest.Files.Keys.ToList())
                results.Add(Apply(path, empty, manifest));
            return results;
        }
    }
}
