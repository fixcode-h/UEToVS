using System;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Update
{
    /// <summary>The newest published UEToVS release (GitHub Releases "latest").</summary>
    public sealed class ReleaseInfo
    {
        public Version Version { get; set; }
        public string Tag { get; set; }
        public string PageUrl { get; set; }
        public string VsixName { get; set; }
        public string VsixUrl { get; set; }
        public long VsixSize { get; set; }
    }

    /// <summary>
    /// Pure rules of "检查更新": where releases live, parsing the GitHub "latest release" answer (tag vX.Y.Z plus a
    /// UEToVS-X.Y.Z.vsix asset, as CI publishes them) and comparing it with the installed version.
    /// </summary>
    public static class UpdateCheck
    {
        public const string Repository = "fixcode-h/UEToVS";
        public static readonly string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        public static readonly string ReleasesPage = "https://github.com/" + Repository + "/releases";

        /// <summary>"0.4.4", "v0.4.4", "0.4.4+abc", "0.4.4-beta" → 0.4.4; null when unparsable.</summary>
        public static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = text.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            int cut = s.IndexOfAny(new[] { '+', '-', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            if (!Version.TryParse(s, out var v)) return null;
            // Normalise 0.4 → 0.4.0 so 0.4 == 0.4.0.
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        /// <summary>Parses the GitHub "latest release" JSON; null when it has no usable version or VSIX asset.</summary>
        public static ReleaseInfo Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            JObject o;
            try { o = JObject.Parse(json); }
            catch (Newtonsoft.Json.JsonException) { return null; }
            if (o.Value<bool?>("draft") == true || o.Value<bool?>("prerelease") == true) return null;
            string tag = o.Value<string>("tag_name");
            var version = ParseVersion(tag);
            if (version == null) return null;
            var info = new ReleaseInfo { Version = version, Tag = tag, PageUrl = o.Value<string>("html_url") ?? ReleasesPage };
            if (o["assets"] is JArray assets)
            {
                foreach (var a in assets)
                {
                    string name = a.Value<string>("name");
                    if (name == null || !name.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase)) continue;
                    bool preferred = name.StartsWith("UEToVS", StringComparison.OrdinalIgnoreCase);
                    if (info.VsixUrl != null && !preferred) continue;
                    info.VsixName = name;
                    info.VsixUrl = a.Value<string>("browser_download_url");
                    info.VsixSize = a.Value<long?>("size") ?? 0;
                    if (preferred) break;
                }
            }
            return info.VsixUrl == null ? null : info;
        }

        /// <summary>True when <paramref name="latest"/> is newer than the installed version (unknown installed version: never).</summary>
        public static bool IsNewer(Version latest, Version installed) => latest != null && installed != null && latest > installed;

        /// <summary>File name safe for the download folder.</summary>
        public static string SafeFileName(ReleaseInfo r)
        {
            string name = r?.VsixName ?? "UEToVS.vsix";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
