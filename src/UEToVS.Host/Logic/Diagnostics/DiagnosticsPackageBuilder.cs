using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UEToVS.Host.Logic.Diagnostics
{
    public sealed class DiagnosticsPackageResult
    {
        public string ZipPath { get; set; }
        public int LogFiles { get; set; }
        public long LogBytes { get; set; }
        public bool IndexManifestIncluded { get; set; }
        public List<string> Skipped { get; } = new List<string>();
    }

    /// <summary>
    /// One-click diagnostics package (§10.5): zip of &lt;.vs/UEToVS&gt;/Logs, sanitized settings, the index manifest and an
    /// environment summary, ready to hand to a person or an AI. Log files are read with FileShare.ReadWrite because the
    /// current one is still being written. Never includes source code.
    /// </summary>
    public static class DiagnosticsPackageBuilder
    {
        /// <summary>Size cap for logs inside the package (newest files first).</summary>
        public const long MaxLogBytes = 100L * 1024 * 1024;

        /// <param name="storageRoot">&lt;solution&gt;/.vs/UEToVS (contains Logs and Index).</param>
        /// <param name="outputDirectory">Where the zip is created.</param>
        /// <param name="sanitizedSettingsJson">Settings already passed through <see cref="Sanitizer"/>.</param>
        /// <param name="extraTextFiles">name → content (e.g. environment.txt, intellisense-state.json).</param>
        /// <param name="sanitizeLine">Applied to every line of the logs and the index manifest (user name / profile path);
        /// null copies them unchanged.</param>
        public static DiagnosticsPackageResult Create(string storageRoot, string outputDirectory, string sanitizedSettingsJson,
                                                      IDictionary<string, string> extraTextFiles, DateTime? now = null,
                                                      Func<string, string> sanitizeLine = null)
        {
            var result = new DiagnosticsPackageResult();
            Directory.CreateDirectory(outputDirectory);
            string stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string zipPath = Path.Combine(outputDirectory, "uetovs-diagnostics-" + stamp + ".zip");
            for (int n = 1; File.Exists(zipPath); n++) zipPath = Path.Combine(outputDirectory, $"uetovs-diagnostics-{stamp}-{n}.zip");
            string tmp = zipPath + ".tmp";

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // Logs (newest first, capped).
                string logs = Path.Combine(storageRoot, "Logs");
                if (Directory.Exists(logs))
                {
                    long total = 0;
                    foreach (var f in new DirectoryInfo(logs).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc))
                    {
                        if (total + f.Length > MaxLogBytes)
                        {
                            result.Skipped.Add("Logs/" + f.Name + " (size cap)");
                            continue;
                        }
                        try
                        {
                            AddFileShared(zip, f.FullName, "Logs/" + f.Name, sanitizeLine);
                            total += f.Length;
                            result.LogFiles++;
                        }
                        catch (IOException ex)
                        {
                            result.Skipped.Add("Logs/" + f.Name + " (" + ex.Message + ")");
                        }
                    }
                    result.LogBytes = total;
                }

                // Index manifest: the engine's manifest.json if present, otherwise a generated listing (names + sizes).
                string index = Path.Combine(storageRoot, "Index");
                string manifest = Path.Combine(index, "manifest.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        AddFileShared(zip, manifest, "Index/manifest.json", sanitizeLine);
                        result.IndexManifestIncluded = true;
                    }
                    catch (IOException ex)
                    {
                        result.Skipped.Add("Index/manifest.json (" + ex.Message + ")");
                    }
                }
                else if (Directory.Exists(index))
                {
                    var sb = new StringBuilder();
                    foreach (var f in new DirectoryInfo(index).GetFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
                        sb.Append(f.FullName.Substring(index.Length).TrimStart('\\', '/')).Append('\t').Append(f.Length).Append('\t')
                          .Append(f.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
                    AddText(zip, "Index/listing.tsv", sb.ToString());
                    result.IndexManifestIncluded = true;
                }

                if (sanitizedSettingsJson != null) AddText(zip, "settings.json", sanitizedSettingsJson);
                if (extraTextFiles != null)
                    foreach (var kv in extraTextFiles)
                        AddText(zip, kv.Key, kv.Value ?? string.Empty);
                AddText(zip, "README.txt",
                    "UEToVS diagnostics package\n" +
                    "Logs/*.log: JSON Lines (ts, lvl, proc, mod, evt, msg, req, ms, data, ex). host-* = VS process, engine-* = engine process.\n" +
                    "Start with evt=session.start, then search lvl=error / evt=*.slow / resource.* / perf.summary.\n" +
                    (sanitizeLine != null
                        ? "No source code is included; user name and profile paths are replaced in every file.\n"
                        : "No source code is included; user name and profile paths are replaced in settings.\n"));
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
            result.ZipPath = zipPath;
            return result;
        }

        private static void AddFileShared(ZipArchive zip, string path, string entryName, Func<string, string> sanitizeLine = null)
        {
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using (var dst = entry.Open())
                {
                    if (sanitizeLine == null)
                    {
                        src.CopyTo(dst);
                        return;
                    }
                    // Line by line: the current log is still being written and logs can be large.
                    using (var reader = new StreamReader(src, Encoding.UTF8, true))
                    using (var writer = new StreamWriter(dst, new UTF8Encoding(false)))
                    {
                        writer.NewLine = "\n";
                        string line;
                        while ((line = reader.ReadLine()) != null) writer.WriteLine(sanitizeLine(line));
                    }
                }
            }
        }

        private static void AddText(ZipArchive zip, string entryName, string text)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var dst = entry.Open())
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                dst.Write(bytes, 0, bytes.Length);
            }
        }
    }

    /// <summary>Removes personal data from text that goes into a diagnostics package.</summary>
    public static class Sanitizer
    {
        /// <summary>Keys whose values are always masked (case-insensitive, substring match).</summary>
        public static readonly string[] SecretKeyHints = { "token", "password", "secret", "apikey", "api_key" };

        /// <summary>
        /// <see cref="Sanitize"/> with the patterns compiled once, for many lines (logs). Lines without the profile path or
        /// the user name skip the regexes.
        /// </summary>
        public static Func<string, string> CreateLineSanitizer(string userName, string userProfile)
        {
            var patterns = new List<Regex>();
            if (!string.IsNullOrEmpty(userProfile))
                foreach (var variant in new[] { userProfile, userProfile.Replace("\\", "\\\\"), userProfile.Replace('\\', '/') }.Distinct(StringComparer.OrdinalIgnoreCase))
                    patterns.Add(new Regex(Regex.Escape(variant), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
            Regex user = !string.IsNullOrEmpty(userName) && userName.Length >= 2
                ? new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(userName) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)
                : null;
            string probe = !string.IsNullOrEmpty(userName) && userName.Length >= 2 ? userName : null;
            return line =>
            {
                if (string.IsNullOrEmpty(line)) return line;
                // every pattern contains the user name in practice (C:\Users\<name>); cheap pre-check first
                if (probe != null && line.IndexOf(probe, StringComparison.OrdinalIgnoreCase) < 0 && patterns.Count > 0
                    && line.IndexOf(userProfile, StringComparison.OrdinalIgnoreCase) < 0) return line;
                foreach (var r in patterns) line = r.Replace(line, "%USERPROFILE%");
                return user == null ? line : user.Replace(line, "<user>");
            };
        }

        /// <summary>Replaces the user profile path and user name with placeholders.</summary>
        public static string Sanitize(string text, string userName, string userProfile)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!string.IsNullOrEmpty(userProfile))
            {
                text = ReplaceIgnoreCase(text, userProfile, "%USERPROFILE%");
                text = ReplaceIgnoreCase(text, userProfile.Replace("\\", "\\\\"), "%USERPROFILE%");
                text = ReplaceIgnoreCase(text, userProfile.Replace('\\', '/'), "%USERPROFILE%");
            }
            if (!string.IsNullOrEmpty(userName) && userName.Length >= 2)
                text = Regex.Replace(text, @"(?<![A-Za-z0-9])" + Regex.Escape(userName) + @"(?![A-Za-z0-9])", "<user>", RegexOptions.IgnoreCase);
            return text;
        }

        /// <summary>Masks values of secret-looking keys: returns a copy of the dictionary.</summary>
        public static Dictionary<string, object> MaskSecrets(IDictionary<string, object> values)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in values)
            {
                bool secret = SecretKeyHints.Any(h => kv.Key.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0);
                result[kv.Key] = secret && kv.Value != null && kv.Value.ToString().Length > 0 ? "***" : kv.Value;
            }
            return result;
        }

        private static string ReplaceIgnoreCase(string text, string find, string replacement)
        {
            if (string.IsNullOrEmpty(find)) return text;
            return Regex.Replace(text, Regex.Escape(find), replacement.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }
    }
}
