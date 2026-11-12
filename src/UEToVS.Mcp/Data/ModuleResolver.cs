using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Unreal;
using UEToVS.Engine.Unreal;

namespace UEToVS.Mcp.Data
{
    /// <summary>A UE module as the MCP tools report it (from its Build.cs and the project/plugin descriptors).</summary>
    public sealed class UeModule
    {
        public string Name { get; set; }
        public string BuildCsPath { get; set; }
        public string Directory { get; set; }

        /// <summary>Runtime / Editor / Developer / UncookedOnly / Program / External ... (descriptor, else inferred from the folder).</summary>
        public string Type { get; set; }

        /// <summary>Where <see cref="Type"/> came from: uproject, uplugin, path, buildcs.</summary>
        public string TypeSource { get; set; }

        public ItemOrigin? Origin { get; set; }
        public string Plugin { get; set; }
        public BuildCsFile Rules { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Module lookups for get_module_info / suggest_include: file → owning Build.cs (walk up the folders), module name →
    /// Build.cs (engine file search when the engine runs, else one indexed file of that module from the on-disk index),
    /// Build.cs parsing (<see cref="BuildCsFile"/>, cached by time stamp) and UE-style include paths. Reads a few small
    /// files per call; never lists directories beyond the ancestors of one file. Thread-safe.
    /// </summary>
    public sealed class ModuleResolver
    {
        private static readonly Logger L = Log.For("Mcp.Modules");

        private readonly CodeIndex _index;
        private readonly object _gate = new object();
        private readonly ModuleLocator _locator = ModuleLocator.ForFileSystem();
        private readonly Dictionary<string, (DateTime Time, UeModule Module)> _byBuildCs = new Dictionary<string, (DateTime, UeModule)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _buildCsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private (DateTime Manifest, Dictionary<string, string> Map) _samples;

        public ModuleResolver(CodeIndex index) => _index = index;

        /// <summary>The Build.cs owning <paramref name="filePath"/>, or null.</summary>
        public string BuildCsForFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            lock (_gate) return _locator.FindBuildCs(Path.GetFullPath(filePath));
        }

        public UeModule ForFile(string filePath)
        {
            string cs = BuildCsForFile(filePath);
            return cs == null ? null : Load(cs);
        }

        /// <summary>Module by name: engine file search first (live), else a file of the module from the on-disk index.</summary>
        public async Task<(UeModule Module, string Source)> ForNameAsync(string module, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(module)) return (null, null);
            lock (_gate)
                if (_buildCsByName.TryGetValue(module, out var known) && File.Exists(known)) return (Load(known), Sources.Index);

            string fileName = module + ".Build.cs";
            string source = null, path = null;
            if (_index.Mode != DataSourceMode.IndexOnly)
            {
                try
                {
                    var resp = await _index.SearchAsync(new SearchRequest
                    {
                        Pattern = fileName,
                        IncludeFiles = true,
                        IncludeTypes = false,
                        IncludeSymbols = false,
                        IncludeNonProject = true,
                        MaxResultsPerCategory = 20,
                    }, ct).ConfigureAwait(false);
                    if (resp.Source == Sources.Engine)
                    {
                        source = Sources.Engine;
                        path = resp.Value.Hits.Where(h => h.File != null && string.Equals(h.File.Name, fileName, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(h => h.File.Origin).Select(h => h.File.Path).FirstOrDefault();
                    }
                }
                catch (ToolException)
                {
                    // no index on disk: the sample lookup below reports nothing either
                }
            }
            if (path == null)
            {
                var sample = SampleFile(module, ct);
                if (sample != null)
                {
                    source ??= Sources.Index;
                    string cs = BuildCsForFile(sample);
                    if (cs != null && string.Equals(BuildCsFile.ModuleNameFromPath(cs), module, StringComparison.OrdinalIgnoreCase)) path = cs;
                }
            }
            if (path == null) return (null, source ?? Sources.Index);
            lock (_gate) _buildCsByName[module] = path;
            return (Load(path), source);
        }

        private string SampleFile(string module, CancellationToken ct)
        {
            if (!_index.Offline.IsAvailable) return null;
            var manifest = _index.Offline.ManifestTimeUtc;
            Dictionary<string, string> map;
            lock (_gate) map = _samples.Map != null && _samples.Manifest == manifest ? _samples.Map : null;
            if (map == null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                map = _index.Offline.Use(s => s.ModuleSampleFiles(ct), new Dictionary<string, string>());
                lock (_gate) _samples = (manifest, map);
                L.Info("mcp.modules.samples", null, ("modules", map.Count), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            }
            return map.TryGetValue(module, out var p) ? p : null;
        }

        /// <summary>Parses a Build.cs (cached until its time stamp changes) and works out type, origin and plugin.</summary>
        public UeModule Load(string buildCsPath)
        {
            DateTime time;
            try { time = File.GetLastWriteTimeUtc(buildCsPath); }
            catch (Exception) { time = DateTime.MinValue; }
            lock (_gate)
                if (_byBuildCs.TryGetValue(buildCsPath, out var c) && c.Time == time) return c.Module;

            string name = BuildCsFile.ModuleNameFromPath(buildCsPath);
            var m = new UeModule { Name = name, BuildCsPath = buildCsPath, Directory = Path.GetDirectoryName(buildCsPath) };
            string text = null;
            try
            {
                text = File.ReadAllText(buildCsPath);
                m.Rules = new BuildCsFile(name, text);
            }
            catch (Exception ex)
            {
                m.Error = ex.GetType().Name + ": " + ex.Message;
                m.Rules = new BuildCsFile(name, string.Empty);
            }
            var ws = _index.Offline.Workspace();
            m.Origin = ws?.CreateClassifier().Classify(buildCsPath);
            ResolveType(m, ws, text);
            lock (_gate) _byBuildCs[buildCsPath] = (time, m);
            return m;
        }

        private static void ResolveType(UeModule m, Workspace ws, string buildCsText)
        {
            // plugin module: the nearest .uplugin above the module folder (a few levels at most)
            var dir = Directory.GetParent(m.Directory);
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                string[] plugins;
                try { plugins = Directory.GetFiles(dir.FullName, "*.uplugin"); }
                catch (Exception) { plugins = Array.Empty<string>(); }
                if (plugins.Length == 0) continue;
                var d = UnrealDescriptor.Load(plugins[0]);
                m.Plugin = Path.GetFileNameWithoutExtension(plugins[0]);
                var md = d.Modules.FirstOrDefault(x => string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase));
                if (md?.Type != null)
                {
                    m.Type = md.Type;
                    m.TypeSource = "uplugin";
                    return;
                }
                break;
            }
            var pm = ws?.Project?.Modules.FirstOrDefault(x => string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase));
            if (pm?.Type != null)
            {
                m.Type = pm.Type;
                m.TypeSource = "uproject";
                return;
            }
            if (buildCsText != null && buildCsText.IndexOf("ModuleType.External", StringComparison.Ordinal) >= 0)
            {
                m.Type = "External";
                m.TypeSource = "buildcs";
                return;
            }
            string norm = m.Directory.Replace('\\', '/');
            int src = norm.IndexOf("/Engine/Source/", StringComparison.OrdinalIgnoreCase);
            if (src >= 0)
            {
                string first = norm.Substring(src + "/Engine/Source/".Length).Split('/')[0];
                m.Type = first.ToLowerInvariant() switch
                {
                    "editor" => "Editor",
                    "developer" => "Developer",
                    "programs" => "Program",
                    "thirdparty" => "External",
                    _ => "Runtime",
                };
                m.TypeSource = "path";
                return;
            }
            m.Type = (m.Name ?? string.Empty).EndsWith("Editor", StringComparison.OrdinalIgnoreCase) ? "Editor" : "Runtime";
            m.TypeSource = "name";
        }

        /// <summary>
        /// How UE code includes <paramref name="headerPath"/>: the path below the module's Public / Classes / Internal /
        /// Private folder (UBT adds those to the include path), else relative to the module folder.
        /// </summary>
        public static string IncludePath(string headerPath, UeModule module)
        {
            string p = headerPath.Replace('\\', '/');
            foreach (var marker in new[] { "/Public/", "/Classes/", "/Internal/", "/Private/" })
            {
                int i = p.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (module == null || i >= module.Directory.Replace('\\', '/').Length)) return p.Substring(i + marker.Length);
            }
            if (module != null)
            {
                string dir = module.Directory.Replace('\\', '/').TrimEnd('/') + "/";
                if (p.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return p.Substring(dir.Length);
            }
            return Path.GetFileName(p);
        }

        /// <summary>Private headers cannot be included from other modules.</summary>
        public static bool IsPrivateHeader(string headerPath)
        {
            string p = headerPath.Replace('\\', '/');
            return p.IndexOf("/Private/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Modules whose headers <paramref name="from"/> may include: itself, its dependencies and, transitively, their public
        /// dependencies (UBT's rule). Build.cs files are resolved by name (at most <paramref name="maxModules"/>).
        /// </summary>
        public async Task<HashSet<string>> VisibleModulesAsync(UeModule from, CancellationToken ct, int maxModules = 200)
        {
            var rules = new Dictionary<string, BuildCsFile>(StringComparer.OrdinalIgnoreCase) { [from.Name] = from.Rules };
            var queue = new Queue<string>(from.Rules.DirectlyVisibleModules());
            while (queue.Count > 0 && rules.Count < maxModules)
            {
                ct.ThrowIfCancellationRequested();
                string m = queue.Dequeue();
                if (rules.ContainsKey(m)) continue;
                var (mod, _) = await ForNameAsync(m, ct).ConfigureAwait(false);
                rules[m] = mod?.Rules;
                if (mod?.Rules == null) continue;
                foreach (var d in mod.Rules.PublicDependencies.Concat(mod.Rules.PublicIncludePathModules)) queue.Enqueue(d);
            }
            return BuildCsFile.VisibleModules(from.Name, n => rules.TryGetValue(n, out var r) ? r : null);
        }
    }
}
