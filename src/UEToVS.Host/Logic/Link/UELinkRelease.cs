using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Link
{
    /// <summary>manifest.json of a UELink release (docs/protocol.md §4 of fixcode-h/UELink).</summary>
    public sealed class UELinkManifest
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public int VersionCode { get; set; }
        public int Protocol { get; set; }
        public string Asset { get; set; }
        public string Url { get; set; }
        public string Sha256 { get; set; }
    }

    /// <summary>
    /// UELink releases: where to look (the latest release of fixcode-h/UELink, a mirror URL, or a local folder for
    /// offline / intranet use), manifest parsing, sha256 check and unpacking the source zip into a cache folder that
    /// <see cref="LinkPluginInstaller.Install(string, string, LinkPluginInstaller.InstallOptions)"/> copies from.
    /// No network code here (the host downloads); unit-tested.
    /// </summary>
    public static class UELinkRelease
    {
        public const string DefaultManifestUrl = "https://github.com/fixcode-h/UELink/releases/latest/download/manifest.json";

        /// <summary>Where the manifest comes from: setting "link.uelinkSource" (empty = GitHub latest release).</summary>
        public sealed class Source
        {
            /// <summary>Manifest URL (null for a local folder).</summary>
            public string ManifestUrl { get; set; }

            /// <summary>Local folder holding manifest.json and the zip (null for a URL).</summary>
            public string Folder { get; set; }

            /// <summary>"github" | "mirror" (written to .install.json).</summary>
            public string Kind { get; set; }

            /// <summary>URL of the zip: GitHub uses the manifest's url, a mirror serves the asset next to manifest.json.</summary>
            public string ZipUrl(UELinkManifest m)
            {
                if (ManifestUrl == null || m == null) return null;
                if (Kind == "github") return m.Url;
                return ManifestUrl.Substring(0, ManifestUrl.LastIndexOf('/') + 1) + m.Asset;
            }
        }

        public static Source ResolveSource(string setting)
        {
            string s = (setting ?? string.Empty).Trim();
            if (s.Length == 0) return new Source { ManifestUrl = DefaultManifestUrl, Kind = "github" };
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return new Source { ManifestUrl = s.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? s : s.TrimEnd('/') + "/manifest.json", Kind = "mirror" };
            return new Source { Folder = s, Kind = "mirror" };
        }

        /// <summary>Parses manifest.json; null when it is not one (missing version, asset or sha256).</summary>
        public static UELinkManifest ParseManifest(string json)
        {
            try
            {
                var o = JObject.Parse(json ?? string.Empty);
                var m = new UELinkManifest
                {
                    Name = o["name"]?.ToString(),
                    Version = o["version"]?.ToString(),
                    VersionCode = o["versionCode"]?.Type == JTokenType.Integer ? (int)o["versionCode"] : 0,
                    Protocol = o["protocol"]?.Type == JTokenType.Integer ? (int)o["protocol"] : 0,
                    Asset = o["asset"]?.ToString(),
                    Url = o["url"]?.ToString(),
                    Sha256 = o["sha256"]?.ToString()?.Trim().ToLowerInvariant(),
                };
                if (string.IsNullOrEmpty(m.Version) || string.IsNullOrEmpty(m.Asset) || string.IsNullOrEmpty(m.Sha256)) return null;
                // The asset name ends up in a path: refuse anything that is not a plain file name.
                if (m.Asset.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || m.Asset.Contains("..")) return null;
                return m;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// Checks the zip against the manifest and unpacks it into &lt;cacheRoot&gt;/&lt;version&gt; (the folder holding
        /// UELink.uplugin, returned). A cache folder that already holds this version is reused. Throws
        /// <see cref="InvalidDataException"/> on a sha256 mismatch or an unsafe / unexpected zip.
        /// </summary>
        public static string Unpack(byte[] zip, UELinkManifest manifest, string cacheRoot)
        {
            string sha = Sha256Hex(zip);
            if (!string.Equals(sha, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UELink " + manifest.Version + ": sha256 " + sha + " does not match the manifest (" + manifest.Sha256 + ")");
            string dest = Path.Combine(cacheRoot, SafeFolderName(manifest.Version));
            string tmp = dest + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                {
                    foreach (var e in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue; // folder entry
                        string full = Path.GetFullPath(Path.Combine(tmp, e.FullName));
                        if (Path.IsPathRooted(e.FullName) || e.FullName.Split('/', '\\').Contains("..") || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("unsafe path in the UELink zip: " + e.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(full));
                        using (var input = e.Open())
                        using (var output = File.Create(full))
                            input.CopyTo(output);
                    }
                }
                string plugin = FindPluginFolder(tmp) ?? throw new InvalidDataException("UELink.uplugin not found in the UELink zip");
                string rel = plugin.Length > tmp.Length ? plugin.Substring(tmp.Length).TrimStart('\\', '/') : string.Empty;
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.Move(tmp, dest);
                return rel.Length == 0 ? dest : Path.Combine(dest, rel);
            }
            finally
            {
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            }
        }

        /// <summary>The unpacked plugin folder of <paramref name="version"/> in the cache (null when not there).</summary>
        public static string Cached(string cacheRoot, string version)
        {
            if (string.IsNullOrEmpty(cacheRoot) || string.IsNullOrEmpty(version)) return null;
            string dest = Path.Combine(cacheRoot, SafeFolderName(version));
            return Directory.Exists(dest) ? FindPluginFolder(dest) : null;
        }

        private static string FindPluginFolder(string dir)
        {
            // The release zip holds UELink/UELink.uplugin; accept the plugin at the root or one level down.
            if (File.Exists(Path.Combine(dir, LinkPluginInstaller.PluginName + ".uplugin"))) return dir;
            foreach (var sub in Directory.GetDirectories(dir))
                if (File.Exists(Path.Combine(sub, LinkPluginInstaller.PluginName + ".uplugin"))) return sub;
            return null;
        }

        private static string SafeFolderName(string version) =>
            new string(version.Select(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' ? ch : '_').ToArray());
    }
}
