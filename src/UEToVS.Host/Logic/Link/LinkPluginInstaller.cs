using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Link
{
    public enum LinkPluginLocation
    {
        /// <summary>&lt;Project&gt;/Plugins/.../UEToVSLink (where "install" puts it).</summary>
        Project = 0,

        /// <summary>&lt;Engine&gt;/Plugins/Marketplace/... (Fab / Marketplace installs).</summary>
        EngineMarketplace = 1,

        /// <summary>&lt;Engine&gt;/Plugins/... anywhere else.</summary>
        Engine = 2,
    }

    /// <summary>One UEToVSLink found on disk.</summary>
    public sealed class LinkPluginInstall
    {
        public string Directory { get; set; }
        public string UPluginFile { get; set; }
        public LinkPluginLocation Location { get; set; }
        public string VersionName { get; set; }
        public int Version { get; set; }
        public bool HasBinaries { get; set; }
    }

    public enum LinkPluginStatusKind
    {
        /// <summary>No .uproject (engine solution) and nothing found.</summary>
        NoProject = 0,
        NotInstalled = 1,
        UpToDate = 2,
        /// <summary>Installed version is older than the one bundled with the extension.</summary>
        UpdateAvailable = 3,
        /// <summary>Installed version is newer (another UEToVS build installed it): leave it.</summary>
        Newer = 4,
    }

    /// <summary>What the settings card shows.</summary>
    public sealed class LinkPluginStatus
    {
        public LinkPluginStatusKind Kind { get; set; }

        /// <summary>The installation that counts (project first, then engine), or null.</summary>
        public LinkPluginInstall Installed { get; set; }

        public List<LinkPluginInstall> All { get; } = new List<LinkPluginInstall>();
        public string BundledVersionName { get; set; }
        public int BundledVersion { get; set; }

        /// <summary>Install / update target: &lt;Project&gt;/Plugins/UEToVSLink (null without a project).</summary>
        public string InstallTarget { get; set; }

        public bool CanInstall => InstallTarget != null && (Kind == LinkPluginStatusKind.NotInstalled || Kind == LinkPluginStatusKind.UpdateAvailable);
        public bool CanUninstall => Installed != null;
    }

    public sealed class LinkPluginOperationResult
    {
        public bool Ok { get; set; }

        /// <summary>"ok" | "locked" (a file is in use: close the editor) | "missingSource" | "noProject" | "notInstalled" | "error".</summary>
        public string Code { get; set; }

        public string Detail { get; set; }
        public int Files { get; set; }
        public bool UProjectChanged { get; set; }

        public static LinkPluginOperationResult Fail(string code, string detail = null) => new LinkPluginOperationResult { Ok = false, Code = code, Detail = detail };
    }

    /// <summary>
    /// UEToVSLink install / update / uninstall (settings window card, "install editor plugin" command). Finds existing
    /// installations (project Plugins, engine Plugins incl. Marketplace), compares the installed .uplugin version with the
    /// one bundled in the VSIX, copies the bundled sources (Binaries / Intermediate are never shipped), and uninstalls by
    /// deleting the plugin folder after checking that no file is locked (a running editor holds the DLL: nothing is
    /// deleted then) and removing a UEToVSLink entry from the .uproject "Plugins" list. File system only; unit-tested.
    /// </summary>
    public static class LinkPluginInstaller
    {
        public const string PluginName = "UEToVSLink";

        public static string ProjectTarget(string projectRoot) =>
            string.IsNullOrEmpty(projectRoot) ? null : Path.Combine(projectRoot, "Plugins", PluginName);

        /// <summary>Reads Version / VersionName of a .uplugin (null when unreadable).</summary>
        public static (int Version, string VersionName)? ReadVersion(string upluginFile)
        {
            try
            {
                if (string.IsNullOrEmpty(upluginFile) || !File.Exists(upluginFile)) return null;
                var o = JObject.Parse(File.ReadAllText(upluginFile));
                int v = o["Version"]?.Type == JTokenType.Integer ? (int)o["Version"] : 0;
                return (v, o["VersionName"]?.ToString());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Compares two plugin versions: VersionName as dotted numbers ("0.2.0" &lt; "0.10.0"), falling back to the integer
        /// Version. Negative = a older than b.
        /// </summary>
        public static int CompareVersions(string nameA, int versionA, string nameB, int versionB)
        {
            var a = ParseDotted(nameA);
            var b = ParseDotted(nameB);
            if (a != null && b != null)
            {
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                    if (x != y) return x.CompareTo(y);
                }
                return 0;
            }
            return versionA.CompareTo(versionB);
        }

        private static int[] ParseDotted(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Trim().TrimStart('v', 'V').Split('.', '-', '+');
            var list = new List<int>();
            foreach (var p in parts)
            {
                if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) break;
                list.Add(n);
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        /// <summary>Every UEToVSLink.uplugin under the project's and the engine's Plugins folders (bounded depth).</summary>
        public static List<LinkPluginInstall> Find(string projectRoot, string engineRoot)
        {
            var list = new List<LinkPluginInstall>();
            if (!string.IsNullOrEmpty(projectRoot)) Collect(Path.Combine(projectRoot, "Plugins"), LinkPluginLocation.Project, list);
            if (!string.IsNullOrEmpty(engineRoot))
            {
                string plugins = Path.Combine(engineRoot, "Plugins");
                if (!Directory.Exists(plugins)) plugins = Path.Combine(engineRoot, "Engine", "Plugins");
                Collect(plugins, LinkPluginLocation.Engine, list);
            }
            return list;
        }

        private static void Collect(string pluginsDir, LinkPluginLocation location, List<LinkPluginInstall> into)
        {
            if (!Directory.Exists(pluginsDir)) return;
            // Plugins/UEToVSLink, Plugins/<Group>/UEToVSLink, Plugins/Marketplace/<Name>/UEToVSLink.uplugin: depth ≤ 3.
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((pluginsDir, 0));
            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                string file = Path.Combine(dir, PluginName + ".uplugin");
                if (depth > 0 && File.Exists(file))
                {
                    var v = ReadVersion(file);
                    bool marketplace = dir.IndexOf(Path.DirectorySeparatorChar + "Marketplace" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       dir.IndexOf("/Marketplace/", StringComparison.OrdinalIgnoreCase) >= 0;
                    into.Add(new LinkPluginInstall
                    {
                        Directory = dir,
                        UPluginFile = file,
                        Location = location == LinkPluginLocation.Engine && marketplace ? LinkPluginLocation.EngineMarketplace : location,
                        Version = v?.Version ?? 0,
                        VersionName = v?.VersionName,
                        HasBinaries = Directory.Exists(Path.Combine(dir, "Binaries")),
                    });
                    continue;
                }
                if (depth >= 3) continue;
                string[] subs;
                try
                {
                    subs = Directory.GetDirectories(dir);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var s in subs)
                {
                    string name = Path.GetFileName(s);
                    if (name.StartsWith(".", StringComparison.Ordinal) || name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Intermediate", StringComparison.OrdinalIgnoreCase) || name.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Content", StringComparison.OrdinalIgnoreCase) || name.Equals("Resources", StringComparison.OrdinalIgnoreCase))
                        continue;
                    queue.Enqueue((s, depth + 1));
                }
            }
        }

        public static LinkPluginStatus GetStatus(string projectRoot, string engineRoot, string bundledSource)
        {
            var st = new LinkPluginStatus { InstallTarget = ProjectTarget(projectRoot) };
            var bundled = ReadVersion(bundledSource == null ? null : Path.Combine(bundledSource, PluginName + ".uplugin"));
            st.BundledVersion = bundled?.Version ?? 0;
            st.BundledVersionName = bundled?.VersionName;
            st.All.AddRange(Find(projectRoot, engineRoot));
            st.Installed = st.All.FirstOrDefault(i => i.Location == LinkPluginLocation.Project) ?? st.All.FirstOrDefault();
            if (st.Installed == null) st.Kind = projectRoot == null ? LinkPluginStatusKind.NoProject : LinkPluginStatusKind.NotInstalled;
            else if (bundled == null) st.Kind = LinkPluginStatusKind.UpToDate;
            else
            {
                int c = CompareVersions(st.Installed.VersionName, st.Installed.Version, st.BundledVersionName, st.BundledVersion);
                st.Kind = c < 0 ? LinkPluginStatusKind.UpdateAvailable : c > 0 ? LinkPluginStatusKind.Newer : LinkPluginStatusKind.UpToDate;
            }
            return st;
        }

        /// <summary>First file under <paramref name="dir"/> that cannot be opened exclusively (in use), or null.</summary>
        public static string FindLockedFile(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch (UnauthorizedAccessException)
                {
                    // read-only file: deletable after clearing the attribute, not "in use"
                }
                catch (IOException)
                {
                    return f;
                }
            }
            return null;
        }

        /// <summary>
        /// Copies the bundled plugin sources over <paramref name="target"/> (install or update). On update the old
        /// Binaries / Intermediate are removed first (they belong to the old sources and UE rebuilds them), unless locked.
        /// </summary>
        public static LinkPluginOperationResult Install(string bundledSource, string target)
        {
            if (string.IsNullOrEmpty(target)) return LinkPluginOperationResult.Fail("noProject");
            if (string.IsNullOrEmpty(bundledSource) || !Directory.Exists(bundledSource)) return LinkPluginOperationResult.Fail("missingSource", bundledSource);
            string locked = FindLockedFile(Path.Combine(target, "Binaries"));
            if (locked != null) return LinkPluginOperationResult.Fail("locked", locked);
            try
            {
                foreach (var sub in new[] { "Binaries", "Intermediate" })
                {
                    string d = Path.Combine(target, sub);
                    if (Directory.Exists(d)) DeleteTree(d);
                }
                int n = 0;
                string src = Path.GetFullPath(bundledSource).TrimEnd('\\', '/');
                foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(src.Length).TrimStart('\\', '/');
                    string first = rel.Split('\\', '/')[0];
                    if (first.Equals("Binaries", StringComparison.OrdinalIgnoreCase) || first.Equals("Intermediate", StringComparison.OrdinalIgnoreCase)) continue;
                    string dest = Path.Combine(target, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                    File.Copy(file, dest, true);
                    n++;
                }
                return new LinkPluginOperationResult { Ok = true, Code = "ok", Files = n, Detail = target };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return LinkPluginOperationResult.Fail("error", ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Deletes the plugin folder (sources, Binaries, Intermediate) and removes the UEToVSLink entry from the .uproject.
        /// Checks for locked files first and deletes nothing when one is found.
        /// </summary>
        public static LinkPluginOperationResult Uninstall(string pluginDir, string uprojectFile)
        {
            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir)) return LinkPluginOperationResult.Fail("notInstalled", pluginDir);
            if (!File.Exists(Path.Combine(pluginDir, PluginName + ".uplugin"))) return LinkPluginOperationResult.Fail("error", "not a UEToVSLink folder: " + pluginDir);
            string locked = FindLockedFile(pluginDir);
            if (locked != null) return LinkPluginOperationResult.Fail("locked", locked);
            var result = new LinkPluginOperationResult { Ok = true, Code = "ok", Detail = pluginDir };
            try
            {
                result.Files = Directory.GetFiles(pluginDir, "*", SearchOption.AllDirectories).Length;
                DeleteTree(pluginDir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return LinkPluginOperationResult.Fail("error", ex.GetType().Name + ": " + ex.Message);
            }
            try
            {
                if (!string.IsNullOrEmpty(uprojectFile) && File.Exists(uprojectFile))
                {
                    string json = File.ReadAllText(uprojectFile);
                    string updated = RemovePluginEntry(json);
                    if (updated != null)
                    {
                        File.WriteAllText(uprojectFile, updated);
                        result.UProjectChanged = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Detail = pluginDir + " (uproject not updated: " + ex.Message + ")";
            }
            return result;
        }

        /// <summary>
        /// .uproject text without the UEToVSLink entry of "Plugins" (keeps everything else, tab indentation like UE);
        /// null when there is no such entry or the text is not valid JSON.
        /// </summary>
        public static string RemovePluginEntry(string uprojectJson)
        {
            if (string.IsNullOrWhiteSpace(uprojectJson)) return null;
            JObject root;
            try
            {
                root = JObject.Parse(uprojectJson);
            }
            catch (JsonException)
            {
                return null;
            }
            if (!(root["Plugins"] is JArray plugins)) return null;
            var remove = plugins.OfType<JObject>().Where(p => string.Equals(p["Name"]?.ToString(), PluginName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (remove.Count == 0) return null;
            foreach (var p in remove) p.Remove();
            using (var sw = new StringWriter(CultureInfo.InvariantCulture))
            using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented, Indentation = 1, IndentChar = '\t' })
            {
                root.WriteTo(w);
                w.Flush();
                string nl = uprojectJson.Contains("\r\n") ? "\r\n" : "\n";
                string text = sw.ToString().Replace("\r\n", "\n").Replace("\n", nl);
                return uprojectJson.EndsWith("\n", StringComparison.Ordinal) ? text + nl : text;
            }
        }

        private static void DeleteTree(string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }
}
