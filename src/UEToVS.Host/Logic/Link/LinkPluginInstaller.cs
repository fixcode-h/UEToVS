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
        /// <summary>&lt;Project&gt;/Plugins/.../UELink (where "install" puts it).</summary>
        Project = 0,

        /// <summary>&lt;Engine&gt;/Plugins/Marketplace/... (Fab / Marketplace installs).</summary>
        EngineMarketplace = 1,

        /// <summary>&lt;Engine&gt;/Plugins/... anywhere else.</summary>
        Engine = 2,
    }

    /// <summary>One UELink (or one of the old plugins it replaces) found on disk.</summary>
    public sealed class LinkPluginInstall
    {
        /// <summary>Plugin name = .uplugin file name: "UELink", or "UEToVSLink" / "AgentPanelBridge" for an old one.</summary>
        public string Name { get; set; } = LinkPluginInstaller.PluginName;

        public string Directory { get; set; }
        public string UPluginFile { get; set; }
        public LinkPluginLocation Location { get; set; }
        public string VersionName { get; set; }
        public int Version { get; set; }
        public bool HasBinaries { get; set; }

        /// <summary>Who installed it, from Plugins/UELink/.install.json ("uetovs" | "agent-panel"; null when unknown).</summary>
        public string InstalledBy { get; set; }

        public bool IsLegacy => !string.Equals(Name, LinkPluginInstaller.PluginName, StringComparison.OrdinalIgnoreCase);
    }

    public enum LinkPluginStatusKind
    {
        /// <summary>No .uproject (engine solution) and nothing found.</summary>
        NoProject = 0,
        NotInstalled = 1,
        UpToDate = 2,
        /// <summary>Installed version is older than the best available one, or only an old plugin (UEToVSLink / AgentPanelBridge) is installed.</summary>
        UpdateAvailable = 3,
        /// <summary>Installed version is newer (Agent Panel or another UEToVS build installed it): leave it, never downgrade.</summary>
        Newer = 4,
    }

    /// <summary>What the settings card shows.</summary>
    public sealed class LinkPluginStatus
    {
        public LinkPluginStatusKind Kind { get; set; }

        /// <summary>The UELink installation that counts (project first, then engine), or null.</summary>
        public LinkPluginInstall Installed { get; set; }

        /// <summary>Every UELink found (project and engine).</summary>
        public List<LinkPluginInstall> All { get; } = new List<LinkPluginInstall>();

        /// <summary>Old plugins merged into UELink (UEToVSLink, AgentPanelBridge) still on disk; project ones are removed by Install.</summary>
        public List<LinkPluginInstall> Legacy { get; } = new List<LinkPluginInstall>();

        /// <summary>Version of the source Install would copy (bundled with the VSIX, or a newer release downloaded from the UELink repository).</summary>
        public string BundledVersionName { get; set; }
        public int BundledVersion { get; set; }

        /// <summary>Latest release of the UELink repository when it is newer than <see cref="BundledVersionName"/> (null otherwise).</summary>
        public string RemoteVersionName { get; set; }

        /// <summary>Why the release check failed (shown on the card; the bundled copy is used then).</summary>
        public string RemoteError { get; set; }

        /// <summary>Install / update target: &lt;Project&gt;/Plugins/UELink (null without a project).</summary>
        public string InstallTarget { get; set; }

        public bool CanInstall => InstallTarget != null && (Kind == LinkPluginStatusKind.NotInstalled || Kind == LinkPluginStatusKind.UpdateAvailable);
        public bool CanUninstall => Installed != null;
    }

    public sealed class LinkPluginOperationResult
    {
        public bool Ok { get; set; }

        /// <summary>"ok" | "locked" (a file is in use: close the editor) | "missingSource" | "noProject" | "notInstalled" | "newer" | "error".</summary>
        public string Code { get; set; }

        public string Detail { get; set; }
        public int Files { get; set; }
        public bool UProjectChanged { get; set; }

        /// <summary>Old plugin folders removed by Install (UEToVSLink / AgentPanelBridge).</summary>
        public List<string> RemovedLegacy { get; } = new List<string>();

        public static LinkPluginOperationResult Fail(string code, string detail = null) => new LinkPluginOperationResult { Ok = false, Code = code, Detail = detail };
    }

    /// <summary>
    /// UELink install / update / uninstall (settings window card, "install editor plugin" command). UELink is the editor
    /// plugin shared with Agent Panel (public repository fixcode-h/UELink, which merged UEToVSLink and AgentPanelBridge);
    /// both clients follow its install contract (docs/protocol.md §5 there): take the highest version, never downgrade,
    /// replace only Source / Config / UELink.uplugin (Binaries / Intermediate stay, UBT rebuilds by timestamp), remove the
    /// old plugins and their .uproject entries, and write Plugins/UELink/.install.json. File system only; unit-tested.
    /// </summary>
    public static class LinkPluginInstaller
    {
        public const string PluginName = "UELink";

        /// <summary>Plugins merged into UELink: removed when UELink is installed (UELink's Build.cs refuses to build next to them).</summary>
        public static readonly string[] LegacyPluginNames = { "UEToVSLink", "AgentPanelBridge" };

        public const string InstallRecordFile = ".install.json";

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
        /// Compares two plugin versions: VersionName as semver-like dotted numbers ("0.2.0" &lt; "0.10.0"; a prerelease
        /// "2.0.0-beta.1" is older than "2.0.0"), falling back to the integer Version. Negative = a older than b.
        /// </summary>
        public static int CompareVersions(string nameA, int versionA, string nameB, int versionB)
        {
            var a = ParseVersion(nameA);
            var b = ParseVersion(nameB);
            if (a == null || b == null) return versionA.CompareTo(versionB);
            for (int i = 0; i < Math.Max(a.Value.Numbers.Length, b.Value.Numbers.Length); i++)
            {
                int x = i < a.Value.Numbers.Length ? a.Value.Numbers[i] : 0, y = i < b.Value.Numbers.Length ? b.Value.Numbers[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            string pa = a.Value.Prerelease, pb = b.Value.Prerelease;
            if (pa == null || pb == null) return pa == null ? (pb == null ? 0 : 1) : -1;
            // Prerelease identifiers: numeric ones compare as numbers and sort before text ones (semver §11).
            var ia = pa.Split('.');
            var ib = pb.Split('.');
            for (int i = 0; i < Math.Min(ia.Length, ib.Length); i++)
            {
                bool na = int.TryParse(ia[i], NumberStyles.None, CultureInfo.InvariantCulture, out int xa);
                bool nb = int.TryParse(ib[i], NumberStyles.None, CultureInfo.InvariantCulture, out int xb);
                int c = na && nb ? xa.CompareTo(xb) : na ? -1 : nb ? 1 : string.CompareOrdinal(ia[i], ib[i]);
                if (c != 0) return c;
            }
            return ia.Length.CompareTo(ib.Length);
        }

        private static (int[] Numbers, string Prerelease)? ParseVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimStart('v', 'V');
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);
            int dash = s.IndexOf('-');
            string pre = dash >= 0 ? s.Substring(dash + 1) : null;
            var list = new List<int>();
            foreach (var p in (dash >= 0 ? s.Substring(0, dash) : s).Split('.'))
            {
                if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) break;
                list.Add(n);
            }
            return list.Count == 0 ? ((int[], string)?)null : (list.ToArray(), string.IsNullOrEmpty(pre) ? null : pre);
        }

        /// <summary>Every UELink.uplugin under the project's and the engine's Plugins folders (bounded depth).</summary>
        public static List<LinkPluginInstall> Find(string projectRoot, string engineRoot) => Find(projectRoot, engineRoot, new[] { PluginName });

        /// <summary>Every plugin named in <paramref name="names"/> under the project's and the engine's Plugins folders.</summary>
        public static List<LinkPluginInstall> Find(string projectRoot, string engineRoot, IReadOnlyCollection<string> names)
        {
            var list = new List<LinkPluginInstall>();
            if (!string.IsNullOrEmpty(projectRoot)) Collect(Path.Combine(projectRoot, "Plugins"), LinkPluginLocation.Project, names, list);
            if (!string.IsNullOrEmpty(engineRoot))
            {
                string plugins = Path.Combine(engineRoot, "Plugins");
                if (!Directory.Exists(plugins)) plugins = Path.Combine(engineRoot, "Engine", "Plugins");
                Collect(plugins, LinkPluginLocation.Engine, names, list);
            }
            return list;
        }

        private static void Collect(string pluginsDir, LinkPluginLocation location, IReadOnlyCollection<string> names, List<LinkPluginInstall> into)
        {
            if (!Directory.Exists(pluginsDir)) return;
            // Plugins/UELink, Plugins/<Group>/UELink, Plugins/Marketplace/<Name>/UELink.uplugin: depth ≤ 3.
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((pluginsDir, 0));
            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                string name = depth > 0 ? names.FirstOrDefault(n => File.Exists(Path.Combine(dir, n + ".uplugin"))) : null;
                if (name != null)
                {
                    string file = Path.Combine(dir, name + ".uplugin");
                    var v = ReadVersion(file);
                    bool marketplace = dir.IndexOf(Path.DirectorySeparatorChar + "Marketplace" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       dir.IndexOf("/Marketplace/", StringComparison.OrdinalIgnoreCase) >= 0;
                    into.Add(new LinkPluginInstall
                    {
                        Name = name,
                        Directory = dir,
                        UPluginFile = file,
                        Location = location == LinkPluginLocation.Engine && marketplace ? LinkPluginLocation.EngineMarketplace : location,
                        Version = v?.Version ?? 0,
                        VersionName = v?.VersionName,
                        HasBinaries = Directory.Exists(Path.Combine(dir, "Binaries")),
                        InstalledBy = ReadInstallRecord(dir)?["by"]?.ToString(),
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
                    string sub = Path.GetFileName(s);
                    if (sub.StartsWith(".", StringComparison.Ordinal) || sub.Equals("Binaries", StringComparison.OrdinalIgnoreCase) ||
                        sub.Equals("Intermediate", StringComparison.OrdinalIgnoreCase) || sub.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                        sub.Equals("Content", StringComparison.OrdinalIgnoreCase) || sub.Equals("Resources", StringComparison.OrdinalIgnoreCase) ||
                        sub.Equals("Config", StringComparison.OrdinalIgnoreCase))
                        continue;
                    queue.Enqueue((s, depth + 1));
                }
            }
        }

        /// <summary>Plugins/UELink/.install.json (null when missing or unreadable).</summary>
        public static JObject ReadInstallRecord(string pluginDir)
        {
            try
            {
                string f = Path.Combine(pluginDir, InstallRecordFile);
                return File.Exists(f) ? JObject.Parse(File.ReadAllText(f)) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        /// <param name="source">Folder holding UELink.uplugin that Install would copy (bundled or downloaded; null when none).</param>
        public static LinkPluginStatus GetStatus(string projectRoot, string engineRoot, string source)
        {
            var st = new LinkPluginStatus { InstallTarget = ProjectTarget(projectRoot) };
            var bundled = ReadVersion(source == null ? null : Path.Combine(source, PluginName + ".uplugin"));
            st.BundledVersion = bundled?.Version ?? 0;
            st.BundledVersionName = bundled?.VersionName;
            foreach (var found in Find(projectRoot, engineRoot, new[] { PluginName }.Concat(LegacyPluginNames).ToArray()))
                (found.IsLegacy ? st.Legacy : st.All).Add(found);
            st.Installed = st.All.FirstOrDefault(i => i.Location == LinkPluginLocation.Project) ?? st.All.FirstOrDefault();
            if (st.Installed == null)
            {
                // Only an old plugin (UEToVSLink / AgentPanelBridge) in the project: installing UELink replaces it.
                if (projectRoot == null) st.Kind = LinkPluginStatusKind.NoProject;
                else st.Kind = st.Legacy.Any(l => l.Location == LinkPluginLocation.Project) && bundled != null ? LinkPluginStatusKind.UpdateAvailable : LinkPluginStatusKind.NotInstalled;
            }
            else if (bundled == null) st.Kind = LinkPluginStatusKind.UpToDate;
            else
            {
                int c = CompareVersions(st.Installed.VersionName, st.Installed.Version, st.BundledVersionName, st.BundledVersion);
                st.Kind = c < 0 ? LinkPluginStatusKind.UpdateAvailable : c > 0 ? LinkPluginStatusKind.Newer : LinkPluginStatusKind.UpToDate;
                // An old plugin left in the project still has to go (UELink does not build next to it).
                if (st.Kind == LinkPluginStatusKind.UpToDate && st.Installed.Location == LinkPluginLocation.Project && st.Legacy.Any(l => l.Location == LinkPluginLocation.Project))
                    st.Kind = LinkPluginStatusKind.UpdateAvailable;
            }
            return st;
        }

        /// <summary>
        /// Folds the latest release into the status: when it is newer than the local source (bundled / cached) it becomes
        /// <see cref="LinkPluginStatus.RemoteVersionName"/>, and an installed UELink older than it gets an update. A newer
        /// installed version stays <see cref="LinkPluginStatusKind.Newer"/> (never downgrade).
        /// </summary>
        public static void ApplyRemote(LinkPluginStatus st, string remoteVersion, string remoteError)
        {
            st.RemoteError = remoteError;
            if (string.IsNullOrEmpty(remoteVersion) || CompareVersions(remoteVersion, 0, st.BundledVersionName, st.BundledVersion) <= 0) return;
            st.RemoteVersionName = remoteVersion;
            if (st.Installed == null) return; // NotInstalled / NoProject / legacy-only UpdateAvailable stay as they are
            int c = CompareVersions(st.Installed.VersionName, st.Installed.Version, remoteVersion, 0);
            if (c < 0) st.Kind = LinkPluginStatusKind.UpdateAvailable;
            else if (st.Kind == LinkPluginStatusKind.Newer && c == 0) st.Kind = LinkPluginStatusKind.UpToDate;
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

        /// <summary>How Install writes .install.json.</summary>
        public sealed class InstallOptions
        {
            /// <summary>Folder of the .uproject: old plugins under its Plugins folder are removed (null = leave them).</summary>
            public string ProjectRoot { get; set; }

            /// <summary>.uproject file: entries of the old plugins are removed from its "Plugins" list.</summary>
            public string UProjectFile { get; set; }

            /// <summary>"bundled" | "github" | "mirror".</summary>
            public string Source { get; set; } = "bundled";

            /// <summary>For the install record ("at"); tests pass a fixed time.</summary>
            public DateTime? NowUtc { get; set; }
        }

        public static LinkPluginOperationResult Install(string source, string target) => Install(source, target, null);

        /// <summary>
        /// Copies UELink's Source / Config / UELink.uplugin from <paramref name="source"/> over <paramref name="target"/>
        /// (install or update; old files under Source / Config are removed first so deleted sources do not linger;
        /// Binaries / Intermediate stay and UBT rebuilds them), removes the old plugins it replaces from the project and
        /// the .uproject, and writes .install.json. Refuses while a file is in use (the editor is running) and never
        /// replaces a newer UELink.
        /// </summary>
        public static LinkPluginOperationResult Install(string source, string target, InstallOptions options)
        {
            options = options ?? new InstallOptions();
            if (string.IsNullOrEmpty(target)) return LinkPluginOperationResult.Fail("noProject");
            var from = ReadVersion(string.IsNullOrEmpty(source) ? null : Path.Combine(source, PluginName + ".uplugin"));
            if (from == null) return LinkPluginOperationResult.Fail("missingSource", source);
            var current = ReadVersion(Path.Combine(target, PluginName + ".uplugin"));
            if (current != null && CompareVersions(current.Value.VersionName, current.Value.Version, from.Value.VersionName, from.Value.Version) > 0)
                return LinkPluginOperationResult.Fail("newer", current.Value.VersionName);

            var legacy = string.IsNullOrEmpty(options.ProjectRoot)
                ? new List<LinkPluginInstall>()
                : Find(options.ProjectRoot, null, LegacyPluginNames);
            foreach (var dir in new[] { Path.Combine(target, "Binaries") }.Concat(legacy.Select(l => l.Directory)))
            {
                string locked = FindLockedFile(dir);
                if (locked != null) return LinkPluginOperationResult.Fail("locked", locked);
            }
            var previous = ReadInstallRecord(target);
            var result = new LinkPluginOperationResult { Ok = true, Code = "ok", Detail = target };
            try
            {
                foreach (var l in legacy)
                {
                    DeleteTree(l.Directory);
                    result.RemovedLegacy.Add(l.Directory);
                }
                foreach (var sub in new[] { "Source", "Config" })
                {
                    string d = Path.Combine(target, sub);
                    if (Directory.Exists(d)) DeleteTree(d);
                }
                string src = Path.GetFullPath(source).TrimEnd('\\', '/');
                foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(src.Length).TrimStart('\\', '/');
                    string first = rel.Split('\\', '/')[0];
                    bool wanted = first.Equals("Source", StringComparison.OrdinalIgnoreCase) || first.Equals("Config", StringComparison.OrdinalIgnoreCase) ||
                                  rel.Equals(PluginName + ".uplugin", StringComparison.OrdinalIgnoreCase);
                    if (!wanted) continue;
                    string dest = Path.Combine(target, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                    File.Copy(file, dest, true);
                    result.Files++;
                }
                // The record is shared with Agent Panel: keep the mode another client chose ("local" = ignored by the VCS,
                // "vcs" = added to it); UEToVS itself only copies files and leaves version control to the user.
                var record = new JObject
                {
                    ["version"] = from.Value.VersionName,
                    ["by"] = "uetovs",
                    ["source"] = options.Source,
                    ["mode"] = previous?["mode"]?.ToString() ?? "files",
                    ["at"] = (options.NowUtc ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                };
                File.WriteAllText(Path.Combine(target, InstallRecordFile), record.ToString(Formatting.Indented) + "\n");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return LinkPluginOperationResult.Fail("error", ex.GetType().Name + ": " + ex.Message);
            }
            try
            {
                if (legacy.Count > 0 || !string.IsNullOrEmpty(options.UProjectFile))
                    result.UProjectChanged = RemoveFromUProject(options.UProjectFile, LegacyPluginNames);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Detail = target + " (uproject not updated: " + ex.Message + ")";
            }
            return result;
        }

        /// <summary>
        /// Deletes the plugin folder (sources, Binaries, Intermediate) and removes its entry from the .uproject.
        /// Checks for locked files first and deletes nothing when one is found.
        /// </summary>
        public static LinkPluginOperationResult Uninstall(string pluginDir, string uprojectFile)
        {
            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir)) return LinkPluginOperationResult.Fail("notInstalled", pluginDir);
            string name = new[] { PluginName }.Concat(LegacyPluginNames).FirstOrDefault(n => File.Exists(Path.Combine(pluginDir, n + ".uplugin")));
            if (name == null) return LinkPluginOperationResult.Fail("error", "not a UELink folder: " + pluginDir);
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
                result.UProjectChanged = RemoveFromUProject(uprojectFile, new[] { name });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Detail = pluginDir + " (uproject not updated: " + ex.Message + ")";
            }
            return result;
        }

        private static bool RemoveFromUProject(string uprojectFile, IReadOnlyCollection<string> names)
        {
            if (string.IsNullOrEmpty(uprojectFile) || !File.Exists(uprojectFile)) return false;
            string updated = RemovePluginEntry(File.ReadAllText(uprojectFile), names);
            if (updated == null) return false;
            File.WriteAllText(uprojectFile, updated);
            return true;
        }

        /// <summary>.uproject text without a UELink entry in "Plugins" (see the overload).</summary>
        public static string RemovePluginEntry(string uprojectJson) => RemovePluginEntry(uprojectJson, new[] { PluginName });

        /// <summary>
        /// .uproject text without the entries named <paramref name="names"/> in "Plugins" (keeps everything else, tab
        /// indentation like UE); null when there is no such entry or the text is not valid JSON.
        /// </summary>
        public static string RemovePluginEntry(string uprojectJson, IReadOnlyCollection<string> names)
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
            var remove = plugins.OfType<JObject>().Where(p => names.Any(n => string.Equals(p["Name"]?.ToString(), n, StringComparison.OrdinalIgnoreCase))).ToList();
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
