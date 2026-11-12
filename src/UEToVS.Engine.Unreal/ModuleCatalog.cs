using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Unreal;

namespace UEToVS.Engine.Unreal
{
    /// <summary>A UE module (directory containing Foo.Build.cs).</summary>
    public sealed class ModuleInfo
    {
        public string Name { get; set; }
        public string BuildCsPath { get; set; }
        public string Directory { get; set; }

        /// <summary>Runtime / Editor / Developer / Program / External / UncookedOnly / ... (descriptor or inferred).</summary>
        public string Type { get; set; }

        public ItemOrigin Origin { get; set; }
        public string Plugin { get; set; }
        public IReadOnlyCollection<string> PublicDependencies { get; set; } = Array.Empty<string>();
        public IReadOnlyCollection<string> PrivateDependencies { get; set; } = Array.Empty<string>();

        /// <summary>Public, Classes and Internal folders that exist (what dependents can include).</summary>
        public List<string> PublicIncludeDirs { get; } = new List<string>();

        /// <summary>The module folder and its Private folder.</summary>
        public List<string> PrivateIncludeDirs { get; } = new List<string>();

        /// <summary>Set when the Build.cs could not be read.</summary>
        public string Error { get; set; }
    }

    /// <summary>All modules of a workspace; maps files to their module.</summary>
    public sealed class ModuleCatalog
    {
        private readonly Dictionary<string, ModuleInfo> _byName = new Dictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModuleInfo> _byDir = new Dictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<ModuleInfo> All => _byName.Values;
        public int Count => _byName.Count;

        public ModuleInfo Get(string name) => name != null && _byName.TryGetValue(name, out var m) ? m : null;

        /// <summary>Owning module of a file: the nearest ancestor folder that holds a Build.cs.</summary>
        public ModuleInfo FindForFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
            catch (Exception) { return null; }
            for (; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (_byDir.TryGetValue(dir, out var m)) return m;
            return null;
        }

        /// <summary>Builds the catalog from the Build.cs files a listing found; parses them in parallel.</summary>
        public static ModuleCatalog Build(Workspace ws, IEnumerable<FoundBuildCs> buildFiles)
        {
            var declaredTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (ws.Project != null)
                foreach (var m in ws.Project.Modules) declaredTypes[m.Name] = m.Type;
            foreach (var p in ws.Plugins)
                foreach (var m in p.Descriptor.Modules) declaredTypes.TryAdd(m.Name, m.Type);

            var infos = new ConcurrentBag<ModuleInfo>();
            Parallel.ForEach(buildFiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, b =>
            {
                var info = new ModuleInfo
                {
                    Name = b.Module,
                    BuildCsPath = b.Path,
                    Directory = Path.GetDirectoryName(b.Path),
                    Origin = b.Origin,
                    Plugin = b.Plugin,
                };
                string text = null;
                try
                {
                    text = File.ReadAllText(b.Path);
                    var rules = new BuildCsFile(b.Module, text);
                    info.PublicDependencies = rules.PublicDependencies.ToArray();
                    info.PrivateDependencies = rules.PrivateDependencies.ToArray();
                }
                catch (Exception ex)
                {
                    info.Error = ex.GetType().Name + ": " + ex.Message;
                }
                info.Type = declaredTypes.TryGetValue(b.Module, out var t) && !string.IsNullOrEmpty(t) ? t : InferType(ws, info.Directory, text);
                foreach (var sub in new[] { "Public", "Classes", "Internal" })
                {
                    string d = Path.Combine(info.Directory, sub);
                    if (System.IO.Directory.Exists(d)) info.PublicIncludeDirs.Add(d);
                }
                info.PrivateIncludeDirs.Add(info.Directory);
                string priv = Path.Combine(info.Directory, "Private");
                if (System.IO.Directory.Exists(priv)) info.PrivateIncludeDirs.Add(priv);
                infos.Add(info);
            });

            var catalog = new ModuleCatalog();
            foreach (var info in infos.OrderBy(i => i.Origin).ThenBy(i => i.BuildCsPath, StringComparer.OrdinalIgnoreCase))
            {
                // First wins by name (project before engine), but every directory maps to its own rules file.
                catalog._byName.TryAdd(info.Name, info);
                catalog._byDir[info.Directory] = info;
            }
            return catalog;
        }

        private static string InferType(Workspace ws, string moduleDir, string buildCsText)
        {
            if (buildCsText != null && buildCsText.Contains("ModuleType.External", StringComparison.Ordinal)) return "External";
            if (ws.EngineDir != null)
            {
                string src = Path.Combine(ws.EngineDir, "Source") + Path.DirectorySeparatorChar;
                if (moduleDir.StartsWith(src, StringComparison.OrdinalIgnoreCase))
                {
                    string first = moduleDir.Substring(src.Length).Split('/', '\\')[0];
                    switch (first.ToLowerInvariant())
                    {
                        case "runtime": return "Runtime";
                        case "editor": return "Editor";
                        case "developer": return "Developer";
                        case "programs": return "Program";
                        case "thirdparty": return "External";
                    }
                }
            }
            return "Runtime";
        }
    }
}
