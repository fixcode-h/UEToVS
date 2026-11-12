using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Contracts;

namespace UEToVS.Engine.Unreal
{
    /// <summary>Builds a <see cref="Workspace"/> from the solution directory and optional overrides.</summary>
    public static class WorkspaceDiscovery
    {
        /// <summary>
        /// Fast part (no deep directory walk): project file, engine root, UE version and build id.
        /// Used for the session header before the background discovery runs.
        /// </summary>
        public static Workspace Locate(WorkspaceOptions options, EngineLocator locator = null)
        {
            locator ??= new EngineLocator();
            var ws = new Workspace { SolutionDir = string.IsNullOrEmpty(options.SolutionDir) ? null : Path.GetFullPath(options.SolutionDir) };

            string project = options.ProjectFile;
            if (!string.IsNullOrEmpty(project) && Directory.Exists(project))
                project = FirstUProject(project);
            if (string.IsNullOrEmpty(project) && ws.SolutionDir != null)
                project = FindProjectFile(ws.SolutionDir);

            if (!string.IsNullOrEmpty(project))
            {
                ws.ProjectFile = Path.GetFullPath(project);
                ws.ProjectRoot = Path.GetDirectoryName(ws.ProjectFile);
                ws.ProjectName = Path.GetFileNameWithoutExtension(ws.ProjectFile);
                if (File.Exists(ws.ProjectFile))
                {
                    ws.Project = UnrealDescriptor.Load(ws.ProjectFile);
                    ws.EngineAssociation = ws.Project.EngineAssociation;
                    if (ws.Project.Error != null) ws.Warnings.Add("uproject: " + ws.Project.Error);
                }
                else ws.Warnings.Add("Project file not found: " + ws.ProjectFile);
            }

            ws.EngineRoot = locator.Find(ws.ProjectFile, options.EngineRootOverride, ws.EngineAssociation);
            ws.EngineRootMethod = locator.LastMethod;
            if (ws.EngineRoot == null && ws.SolutionDir != null)
            {
                // Engine source tree opened directly (UE5.sln in the engine root) or a solution below it.
                for (string d = ws.SolutionDir; d != null; d = Path.GetDirectoryName(d))
                {
                    if (EngineLocator.IsEngineRoot(d))
                    {
                        ws.EngineRoot = d;
                        ws.EngineRootMethod = "solution-in-engine";
                        break;
                    }
                }
            }
            if (ws.EngineRoot != null && !Directory.Exists(ws.EngineRoot))
            {
                ws.Warnings.Add("Engine root does not exist: " + ws.EngineRoot);
            }
            if (ws.EngineRoot == null && ws.ProjectFile != null)
                ws.Warnings.Add($"Engine root not found (EngineAssociation='{ws.EngineAssociation}'); pass --engine <root>");

            ws.EngineVersion = UnrealVersion.Read(ws.EngineRoot);
            ws.EngineBuildId = EngineLocator.ComputeBuildId(ws.EngineRoot, ws.EngineVersion);
            return ws;
        }

        /// <summary>Full discovery: <see cref="Locate"/> plus plugins (with enabled state) and the roots to list.</summary>
        public static Workspace Discover(WorkspaceOptions options, EngineLocator locator = null)
        {
            var ws = Locate(options, locator);
            DiscoverPluginsAndRoots(ws, options);
            return ws;
        }

        public static void DiscoverPluginsAndRoots(Workspace ws, WorkspaceOptions options)
        {
            bool includeEngine = options.IncludeEngine && ws.EngineRoot != null && Directory.Exists(ws.EngineDir);

            var projectPlugins = ws.ProjectRoot == null ? new List<string>() : FindPlugins(Path.Combine(ws.ProjectRoot, "Plugins"));
            var enginePlugins = includeEngine ? FindPlugins(Path.Combine(ws.EngineDir, "Plugins")) : new List<string>();

            var byName = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
            void Add(string file, ItemOrigin origin)
            {
                var d = UnrealDescriptor.Load(file);
                if (d.Error != null) ws.Warnings.Add("uplugin " + file + ": " + d.Error);
                var info = new PluginInfo
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    DescriptorPath = Path.GetFullPath(file),
                    Directory = Path.GetDirectoryName(Path.GetFullPath(file)),
                    Origin = origin,
                    Descriptor = d,
                };
                // A project plugin shadows an engine plugin of the same name.
                if (byName.TryGetValue(info.Name, out var existing) && existing.Origin == ItemOrigin.ProjectPlugin) return;
                byName[info.Name] = info;
            }
            foreach (var f in enginePlugins) Add(f, ItemOrigin.EnginePlugin);
            foreach (var f in projectPlugins) Add(f, ItemOrigin.ProjectPlugin);

            ResolveEnabled(byName, ws.Project);
            ws.Plugins.AddRange(byName.Values.OrderBy(p => p.Origin).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase));

            // Project
            if (ws.ProjectRoot != null && Directory.Exists(ws.ProjectRoot))
            {
                AddRoot(ws.SourceRoots, Path.Combine(ws.ProjectRoot, "Source"), ItemOrigin.Project, RootKind.Source);
                AddRoot(ws.ConfigRoots, Path.Combine(ws.ProjectRoot, "Config"), ItemOrigin.Project, RootKind.Config);
                AddRoot(ws.ConfigRoots, Path.Combine(ws.ProjectRoot, "Shaders"), ItemOrigin.Project, RootKind.Shaders);
                AddRoot(ws.ContentRoots, Path.Combine(ws.ProjectRoot, "Content"), ItemOrigin.Project, RootKind.Content, "/Game");
                if (ws.ProjectFile != null && File.Exists(ws.ProjectFile)) ws.DescriptorFiles.Add((ws.ProjectFile, ItemOrigin.Project));
            }
            foreach (var p in ws.Plugins.Where(p => p.Origin == ItemOrigin.ProjectPlugin && p.Enabled)) AddPluginRoots(ws, p);

            // Engine
            if (includeEngine)
            {
                string e = ws.EngineDir;
                AddRoot(ws.SourceRoots, Path.Combine(e, "Source"), ItemOrigin.Engine, RootKind.Source);
                AddRoot(ws.ConfigRoots, Path.Combine(e, "Config"), ItemOrigin.Engine, RootKind.Config);
                AddRoot(ws.ConfigRoots, Path.Combine(e, "Shaders"), ItemOrigin.Engine, RootKind.Shaders);
                AddRoot(ws.ContentRoots, Path.Combine(e, "Content"), ItemOrigin.Engine, RootKind.Content, "/Engine");
                foreach (var p in ws.Plugins.Where(p => p.Origin == ItemOrigin.EnginePlugin && (p.Enabled || options.IncludeDisabledEnginePlugins)))
                    AddPluginRoots(ws, p);
            }
        }

        private static void AddPluginRoots(Workspace ws, PluginInfo p)
        {
            AddRoot(ws.SourceRoots, Path.Combine(p.Directory, "Source"), p.Origin, RootKind.Source, null, p.Name);
            AddRoot(ws.ConfigRoots, Path.Combine(p.Directory, "Config"), p.Origin, RootKind.Config, null, p.Name);
            AddRoot(ws.ConfigRoots, Path.Combine(p.Directory, "Shaders"), p.Origin, RootKind.Shaders, null, p.Name);
            AddRoot(ws.ContentRoots, Path.Combine(p.Directory, "Content"), p.Origin, RootKind.Content, "/" + p.Name, p.Name);
            ws.DescriptorFiles.Add((p.DescriptorPath, p.Origin));
        }

        /// <summary>
        /// UE rules: an explicit uproject entry wins; otherwise project plugins are enabled unless EnabledByDefault=false,
        /// engine plugins are disabled unless EnabledByDefault=true. Enabled plugins enable the plugins they depend on
        /// (unless the uproject explicitly disables them).
        /// </summary>
        internal static void ResolveEnabled(Dictionary<string, PluginInfo> plugins, UnrealDescriptor project)
        {
            var explicitRefs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (project != null)
                foreach (var r in project.Plugins) explicitRefs[r.Name] = r.Enabled;

            foreach (var p in plugins.Values)
            {
                if (explicitRefs.TryGetValue(p.Name, out bool en))
                {
                    p.Enabled = en;
                    p.EnabledReason = en ? "uproject" : "uproject-disabled";
                }
                else if (p.Origin == ItemOrigin.ProjectPlugin)
                {
                    p.Enabled = p.Descriptor.EnabledByDefault ?? true;
                    p.EnabledReason = p.Enabled ? "default" : "disabled-by-default";
                }
                else
                {
                    p.Enabled = p.Descriptor.EnabledByDefault ?? false;
                    p.EnabledReason = p.Enabled ? "default" : "disabled-by-default";
                }
            }

            var queue = new Queue<PluginInfo>(plugins.Values.Where(p => p.Enabled));
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                foreach (var dep in p.Descriptor.Plugins)
                {
                    if (!dep.Enabled || !plugins.TryGetValue(dep.Name, out var target) || target.Enabled) continue;
                    if (explicitRefs.TryGetValue(dep.Name, out bool en) && !en) continue;
                    target.Enabled = true;
                    target.EnabledReason = "dependency:" + p.Name;
                    queue.Enqueue(target);
                }
            }
        }

        private static void AddRoot(List<IndexRoot> list, string path, ItemOrigin origin, RootKind kind, string mount = null, string plugin = null)
        {
            if (!Directory.Exists(path)) return;
            string full = Path.GetFullPath(path);
            if (list.Any(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new IndexRoot(full, origin, kind, mount, plugin));
        }

        /// <summary>First *.uproject in <paramref name="dir"/> or its parents.</summary>
        public static string FindProjectFile(string dir)
        {
            for (string d = Path.GetFullPath(dir); d != null; d = Path.GetDirectoryName(d))
            {
                string f = FirstUProject(d);
                if (f != null) return f;
            }
            return null;
        }

        private static string FirstUProject(string dir)
        {
            try
            {
                return Directory.EnumerateFiles(dir, "*.uproject").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>All *.uplugin below a Plugins folder (category folders allowed; a plugin folder holds no nested plugins).</summary>
        public static List<string> FindPlugins(string pluginsDir)
        {
            var result = new ConcurrentBag<string>();
            if (!Directory.Exists(pluginsDir)) return new List<string>();
            ParallelDirectoryWalk.Run(new[] { pluginsDir }, dir =>
            {
                string[] plugins;
                try { plugins = Directory.GetFiles(dir, "*.uplugin"); }
                catch (Exception) { return Array.Empty<string>(); }
                if (plugins.Length > 0)
                {
                    foreach (var p in plugins) result.Add(Path.GetFullPath(p));
                    return Array.Empty<string>();
                }
                try
                {
                    return Directory.GetDirectories(dir).Where(s => !FileLister.IsSkippedDirectory(Path.GetFileName(s))).ToArray();
                }
                catch (Exception) { return Array.Empty<string>(); }
            });
            return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
