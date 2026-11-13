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

        /// <summary>Release body (Markdown generated from the CHANGELOG section); may be null.</summary>
        public string Notes { get; set; }
    }

    /// <summary>One line of release notes as shown in the settings window.</summary>
    public sealed class NoteLine
    {
        public NoteLine(NoteLineKind kind, string text) { Kind = kind; Text = text; }
        public NoteLineKind Kind { get; }
        public string Text { get; }
    }

    public enum NoteLineKind { Heading, Bullet, Text }

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
            var info = new ReleaseInfo { Version = version, Tag = tag, PageUrl = o.Value<string>("html_url") ?? ReleasesPage, Notes = o.Value<string>("body") };
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

        /// <summary>
        /// Release notes Markdown → plain lines for the settings window: "### 改进" becomes a heading, "- " / "* " items
        /// become bullets, emphasis / code marks and link targets are dropped, blank lines are skipped. Stops at the
        /// "### 下载" section that scripts/release.py appends (install instructions, not changes). Capped at
        /// <paramref name="maxLines"/> lines.
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<NoteLine> NotesLines(string markdown, int maxLines = 200)
        {
            var result = new System.Collections.Generic.List<NoteLine>();
            if (string.IsNullOrWhiteSpace(markdown)) return result;
            foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                if (result.Count >= maxLines) break;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("<!--", StringComparison.Ordinal) || line.Trim('-', '*', '_', ' ').Length == 0) continue;
                var kind = NoteLineKind.Text;
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    kind = NoteLineKind.Heading;
                    line = line.TrimStart('#').Trim();
                    if (line == "下载" || line.Equals("Download", StringComparison.OrdinalIgnoreCase)) break;
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    kind = NoteLineKind.Bullet;
                    line = line.Substring(2).Trim();
                }
                line = StripInline(line);
                if (line.Length > 0) result.Add(new NoteLine(kind, line));
            }
            return result;
        }

        private static string StripInline(string s)
        {
            s = s.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
            // [text](url) → text
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '[')
                {
                    int close = s.IndexOf("](", i + 1, StringComparison.Ordinal);
                    int end = close < 0 ? -1 : s.IndexOf(')', close + 2);
                    if (close > i && end > close)
                    {
                        sb.Append(s, i + 1, close - i - 1);
                        i = end;
                        continue;
                    }
                }
                sb.Append(s[i]);
            }
            return sb.ToString().Trim();
        }

        /// <summary>File name safe for the download folder.</summary>
        public static string SafeFileName(ReleaseInfo r)
        {
            string name = r?.VsixName ?? "UEToVS.vsix";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
