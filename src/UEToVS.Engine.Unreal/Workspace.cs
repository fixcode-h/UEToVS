using System;
using System.Collections.Generic;
using System.IO;
using UEToVS.Contracts;

namespace UEToVS.Engine.Unreal
{
    public sealed class WorkspaceOptions
    {
        /// <summary>Folder holding the .sln; the project is searched in it and its parents.</summary>
        public string SolutionDir { get; set; }

        /// <summary>Explicit .uproject (or a folder containing one). Wins over the search.</summary>
        public string ProjectFile { get; set; }

        /// <summary>Explicit engine root ("UE_5.4" or "UE_5.4/Engine").</summary>
        public string EngineRootOverride { get; set; }

        /// <summary>False = project only (no engine source, plugins, content). Used by tests and quick CLI runs.</summary>
        public bool IncludeEngine { get; set; } = true;

        /// <summary>Index engine plugins that the project does not enable (normally off: they are noise).</summary>
        public bool IncludeDisabledEnginePlugins { get; set; }
    }

    public sealed class PluginInfo
    {
        public string Name { get; set; }
        public string DescriptorPath { get; set; }
        public string Directory { get; set; }
        public ItemOrigin Origin { get; set; }
        public bool Enabled { get; set; }

        /// <summary>Why the plugin is enabled or not: default, uproject, dependency, disabled-by-default, uproject-disabled.</summary>
        public string EnabledReason { get; set; }

        public UnrealDescriptor Descriptor { get; set; }
        public bool HasContent => System.IO.Directory.Exists(System.IO.Path.Combine(Directory, "Content"));
    }

    public enum RootKind : byte
    {
        Source = 0,
        Config = 1,
        Shaders = 2,
        Content = 3,
    }

    /// <summary>A directory tree to list, with how its items are classified.</summary>
    public sealed class IndexRoot
    {
        public IndexRoot(string path, ItemOrigin origin, RootKind kind, string contentMount = null, string plugin = null)
        {
            Path = System.IO.Path.GetFullPath(path);
            Origin = origin;
            Kind = kind;
            ContentMount = contentMount;
            Plugin = plugin;
        }

        public string Path { get; }
        public ItemOrigin Origin { get; }
        public RootKind Kind { get; }

        /// <summary>UE mount point for content roots, e.g. "/Game", "/Engine" or "/MyPlugin".</summary>
        public string ContentMount { get; }

        public string Plugin { get; }
        public bool IsProject => Origin == ItemOrigin.Project || Origin == ItemOrigin.ProjectPlugin;

        public override string ToString() => $"{Kind} {Origin} {Path}";
    }

    /// <summary>The project, its engine and the folders that make up both (§6.1 工程模型).</summary>
    public sealed class Workspace
    {
        public string SolutionDir { get; set; }
        public string ProjectFile { get; set; }
        public string ProjectRoot { get; set; }
        public string ProjectName { get; set; }
        public UnrealDescriptor Project { get; set; }
        public string EngineRoot { get; set; }

        /// <summary>How the engine root was found (override, source-tree, registry-builds, launcher, ...).</summary>
        public string EngineRootMethod { get; set; }

        public UnrealVersion EngineVersion { get; set; }
        public string EngineBuildId { get; set; }
        public string EngineAssociation { get; set; }

        public List<PluginInfo> Plugins { get; } = new List<PluginInfo>();
        public List<IndexRoot> SourceRoots { get; } = new List<IndexRoot>();
        public List<IndexRoot> ConfigRoots { get; } = new List<IndexRoot>();
        public List<IndexRoot> ContentRoots { get; } = new List<IndexRoot>();

        /// <summary>Descriptor files (.uproject, enabled .uplugin) listed as files.</summary>
        public List<(string Path, ItemOrigin Origin)> DescriptorFiles { get; } = new List<(string, ItemOrigin)>();

        /// <summary>Non-fatal problems found during discovery (bad descriptors, missing engine...).</summary>
        public List<string> Warnings { get; } = new List<string>();

        public string EngineDir => EngineRoot == null ? null : Path.Combine(EngineRoot, "Engine");

        /// <summary>Roots to list: project first, then engine (indexing order, §6 "project files first").</summary>
        public IEnumerable<IndexRoot> AllListingRoots()
        {
            foreach (var r in SourceRoots) if (r.IsProject) yield return r;
            foreach (var r in ConfigRoots) if (r.IsProject) yield return r;
            foreach (var r in SourceRoots) if (!r.IsProject) yield return r;
            foreach (var r in ConfigRoots) if (!r.IsProject) yield return r;
        }

        public OriginClassifier CreateClassifier() => new OriginClassifier(ProjectRoot, EngineRoot);
    }

    /// <summary>
    /// Classifies a path: Project/Plugins/** → ProjectPlugin, Project/** → Project, Engine/** with a "ThirdParty"
    /// segment → ThirdParty, Engine/Plugins/** → EnginePlugin, Engine/** → Engine. The project is checked first so a
    /// project inside a source engine tree is still "Project". Project-local ThirdParty folders stay Project/ProjectPlugin
    /// (they are the user's code). Returns null for paths outside the workspace.
    /// </summary>
    public sealed class OriginClassifier
    {
        private readonly string _projectRoot;
        private readonly string _projectPlugins;
        private readonly string _engineDir;
        private readonly string _enginePlugins;

        public OriginClassifier(string projectRoot, string engineRoot)
        {
            _projectRoot = Norm(projectRoot);
            _projectPlugins = _projectRoot == null ? null : _projectRoot + "Plugins" + Path.DirectorySeparatorChar;
            _engineDir = engineRoot == null ? null : Norm(Path.Combine(engineRoot, "Engine"));
            _enginePlugins = _engineDir == null ? null : _engineDir + "Plugins" + Path.DirectorySeparatorChar;
        }

        public ItemOrigin? Classify(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p;
            try { p = Path.GetFullPath(path); }
            catch (Exception) { return null; }
            if (_projectRoot != null && p.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase))
                return _projectPlugins != null && p.StartsWith(_projectPlugins, StringComparison.OrdinalIgnoreCase) ? ItemOrigin.ProjectPlugin : ItemOrigin.Project;
            if (_engineDir != null && p.StartsWith(_engineDir, StringComparison.OrdinalIgnoreCase))
            {
                if (HasSegment(p, _engineDir.Length, "ThirdParty")) return ItemOrigin.ThirdParty;
                return p.StartsWith(_enginePlugins, StringComparison.OrdinalIgnoreCase) ? ItemOrigin.EnginePlugin : ItemOrigin.Engine;
            }
            return null;
        }

        internal static bool HasSegment(string path, int start, string segment)
        {
            int i = start;
            while (i < path.Length)
            {
                int next = path.IndexOfAny(Seps, i);
                int end = next < 0 ? path.Length : next;
                if (end - i == segment.Length && string.Compare(path, i, segment, 0, segment.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return next >= 0; // a directory segment, not the file name
                if (next < 0) break;
                i = next + 1;
            }
            return false;
        }

        private static readonly char[] Seps = { '/', '\\' };

        private static string Norm(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            string full = Path.GetFullPath(dir).TrimEnd('/', '\\');
            return full + Path.DirectorySeparatorChar;
        }
    }
}
