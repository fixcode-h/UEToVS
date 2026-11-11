using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UEToVS.Core.Protocol;

namespace UEToVS.Indexer.Unreal
{
    /// <summary>A directory tree to index and how its items should be classified.</summary>
    internal sealed class IndexRoot
    {
        public IndexRoot(string path, ItemOrigin origin, bool isContent = false, string contentMount = null)
        {
            Path = System.IO.Path.GetFullPath(path);
            Origin = origin;
            IsContent = isContent;
            ContentMount = contentMount;
        }

        public string Path { get; }
        public ItemOrigin Origin { get; }
        public bool IsContent { get; }

        /// <summary>UE mount point for content, e.g. "/Game" or "/MyPlugin".</summary>
        public string ContentMount { get; }

        public bool IsProject => Origin == ItemOrigin.Project || Origin == ItemOrigin.ProjectPlugin;
    }

    /// <summary>Finds the project, its engine and the folders that make up both.</summary>
    internal sealed class ProjectLayout
    {
        public string ProjectFile { get; private set; }
        public string ProjectRoot { get; private set; }
        public string EngineRoot { get; private set; }
        public string EngineVersion { get; private set; }
        public List<IndexRoot> SourceRoots { get; } = new List<IndexRoot>();
        public List<IndexRoot> ContentRoots { get; } = new List<IndexRoot>();

        /// <summary>Single files outside source roots worth indexing (uproject, Config/*.ini...).</summary>
        public List<IndexRoot> ConfigRoots { get; } = new List<IndexRoot>();

        public static ProjectLayout Discover(string projectFile, string engineRootOverride)
        {
            var layout = new ProjectLayout();
            if (!string.IsNullOrEmpty(projectFile))
            {
                if (Directory.Exists(projectFile))
                    projectFile = Directory.EnumerateFiles(projectFile, "*.uproject").FirstOrDefault() ?? projectFile;
                layout.ProjectFile = Path.GetFullPath(projectFile);
                layout.ProjectRoot = File.Exists(layout.ProjectFile) ? Path.GetDirectoryName(layout.ProjectFile) : layout.ProjectFile;
            }

            layout.EngineRoot = !string.IsNullOrEmpty(engineRootOverride)
                ? NormalizeEngineRoot(engineRootOverride)
                : EngineLocator.Find(layout.ProjectFile);
            layout.EngineVersion = EngineLocator.ReadVersion(layout.EngineRoot);
            layout.BuildRoots();
            return layout;
        }

        public static string NormalizeEngineRoot(string path)
        {
            path = Path.GetFullPath(path);
            // accept both "UE_5.4" and "UE_5.4/Engine"
            if (Directory.Exists(Path.Combine(path, "Engine", "Source"))) return path;
            if (string.Equals(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), "Engine", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));
            return path;
        }

        private void BuildRoots()
        {
            if (ProjectRoot != null && Directory.Exists(ProjectRoot))
            {
                AddIfExists(SourceRoots, Path.Combine(ProjectRoot, "Source"), ItemOrigin.Project);
                AddIfExists(ContentRoots, Path.Combine(ProjectRoot, "Content"), ItemOrigin.Project, true, "/Game");
                AddIfExists(ConfigRoots, Path.Combine(ProjectRoot, "Config"), ItemOrigin.Project);
                AddIfExists(ConfigRoots, Path.Combine(ProjectRoot, "Shaders"), ItemOrigin.Project);
                foreach (var plugin in FindPlugins(Path.Combine(ProjectRoot, "Plugins")))
                {
                    string dir = Path.GetDirectoryName(plugin);
                    string name = Path.GetFileNameWithoutExtension(plugin);
                    AddIfExists(SourceRoots, Path.Combine(dir, "Source"), ItemOrigin.ProjectPlugin);
                    AddIfExists(ContentRoots, Path.Combine(dir, "Content"), ItemOrigin.ProjectPlugin, true, "/" + name);
                    AddIfExists(ConfigRoots, Path.Combine(dir, "Config"), ItemOrigin.ProjectPlugin);
                    AddIfExists(ConfigRoots, Path.Combine(dir, "Shaders"), ItemOrigin.ProjectPlugin);
                }
            }

            if (EngineRoot != null && Directory.Exists(Path.Combine(EngineRoot, "Engine")))
            {
                string engine = Path.Combine(EngineRoot, "Engine");
                foreach (var sub in new[] { "Runtime", "Editor", "Developer", "Programs" })
                    AddIfExists(SourceRoots, Path.Combine(engine, "Source", sub), ItemOrigin.Engine);
                AddIfExists(SourceRoots, Path.Combine(engine, "Source", "ThirdParty"), ItemOrigin.ThirdParty);
                AddIfExists(ConfigRoots, Path.Combine(engine, "Shaders"), ItemOrigin.Engine);
                AddIfExists(ConfigRoots, Path.Combine(engine, "Config"), ItemOrigin.Engine);
                AddIfExists(ContentRoots, Path.Combine(engine, "Content"), ItemOrigin.Engine, true, "/Engine");
                foreach (var plugin in FindPlugins(Path.Combine(engine, "Plugins")))
                {
                    string dir = Path.GetDirectoryName(plugin);
                    string name = Path.GetFileNameWithoutExtension(plugin);
                    AddIfExists(SourceRoots, Path.Combine(dir, "Source"), ItemOrigin.EnginePlugin);
                    AddIfExists(ContentRoots, Path.Combine(dir, "Content"), ItemOrigin.EnginePlugin, true, "/" + name);
                    AddIfExists(ConfigRoots, Path.Combine(dir, "Shaders"), ItemOrigin.EnginePlugin);
                }
            }
        }

        private static void AddIfExists(List<IndexRoot> list, string path, ItemOrigin origin, bool content = false, string mount = null)
        {
            if (Directory.Exists(path) && !list.Any(r => string.Equals(r.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                list.Add(new IndexRoot(path, origin, content, mount));
        }

        /// <summary>All *.uplugin files below a Plugins folder (plugins may be nested in category folders).</summary>
        internal static IEnumerable<string> FindPlugins(string pluginsDir)
        {
            if (!Directory.Exists(pluginsDir)) yield break;
            var stack = new Stack<string>();
            stack.Push(pluginsDir);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] plugins;
                try { plugins = Directory.GetFiles(dir, "*.uplugin"); }
                catch (Exception) { continue; }
                if (plugins.Length > 0)
                {
                    foreach (var p in plugins) yield return p;
                    continue; // a plugin folder does not contain other plugins
                }
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch (Exception) { continue; }
                foreach (var s in subs)
                {
                    string n = Path.GetFileName(s);
                    if (n.StartsWith(".") || n == "Binaries" || n == "Intermediate") continue;
                    stack.Push(s);
                }
            }
        }
    }

    internal static class EngineLocator
    {
        /// <summary>Resolves the engine root ("C:/UE_5.4", the folder that contains "Engine") for a project.</summary>
        public static string Find(string projectFile)
        {
            if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile)) return null;

            // 1) project inside a source engine tree (e.g. UnrealEngine/MyGame/MyGame.uproject)
            string dir = Path.GetDirectoryName(projectFile);
            for (string d = Path.GetDirectoryName(dir); d != null; d = Path.GetDirectoryName(d))
                if (File.Exists(Path.Combine(d, "Engine", "Build", "Build.version"))) return d;

            string association = ReadEngineAssociation(projectFile);
            if (string.IsNullOrEmpty(association)) return null;

            // 2) relative / absolute path association
            if (association.IndexOfAny(new[] { '/', '\\' }) >= 0)
            {
                string p = Path.GetFullPath(Path.Combine(dir, association));
                if (Directory.Exists(p)) return ProjectLayout.NormalizeEngineRoot(p);
            }

            // 3) source build registered under HKCU\Software\Epic Games\Unreal Engine\Builds (GUID association)
            string fromRegistry = FindRegisteredBuild(association);
            if (fromRegistry != null) return fromRegistry;

            // 4) launcher install ("5.4"): LauncherInstalled.dat, then the registry key of the installed version
            string fromLauncher = FindLauncherInstall(association);
            return fromLauncher;
        }

        public static string ReadEngineAssociation(string projectFile)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(projectFile), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.TryGetProperty("EngineAssociation", out var e)) return e.GetString();
            }
            catch (Exception) { }
            return null;
        }

        private static string FindRegisteredBuild(string association)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
                var value = key?.GetValue(association) as string;
                if (!string.IsNullOrEmpty(value) && Directory.Exists(value)) return ProjectLayout.NormalizeEngineRoot(value);
            }
            catch (Exception) { }
            return null;
        }

        private static string FindLauncherInstall(string version)
        {
            try
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string dat = Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
                if (File.Exists(dat))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(dat));
                    foreach (var item in doc.RootElement.GetProperty("InstallationList").EnumerateArray())
                    {
                        string app = item.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                        string loc = item.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                        if (app == "UE_" + version && Directory.Exists(loc)) return ProjectLayout.NormalizeEngineRoot(loc);
                    }
                }
            }
            catch (Exception) { }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine\" + version);
                    var dir = key?.GetValue("InstalledDirectory") as string;
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return ProjectLayout.NormalizeEngineRoot(dir);
                }
                catch (Exception) { }
            }
            return null;
        }

        public static string ReadVersion(string engineRoot)
        {
            if (engineRoot == null) return null;
            string file = Path.Combine(engineRoot, "Engine", "Build", "Build.version");
            try
            {
                if (!File.Exists(file)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                return $"{r.GetProperty("MajorVersion").GetInt32()}.{r.GetProperty("MinorVersion").GetInt32()}.{r.GetProperty("PatchVersion").GetInt32()}-{(r.TryGetProperty("Changelist", out var c) ? c.GetInt32() : 0)}";
            }
            catch (Exception) { return null; }
        }
    }
}
