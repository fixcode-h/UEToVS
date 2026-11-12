using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Unreal;

namespace UEToVS.Mcp.Data
{
    /// <summary>A console variable / command definition found in C++.</summary>
    public sealed class CVarDefinition
    {
        public string Name { get; set; }
        public string Variable { get; set; }

        /// <summary>TAutoConsoleVariable&lt;int32&gt;, FAutoConsoleVariableRef, FAutoConsoleCommand...</summary>
        public string Declaration { get; set; }

        public string Help { get; set; }
        public string File { get; set; }

        /// <summary>0-based.</summary>
        public int Line { get; set; }
    }

    /// <summary>
    /// Data behind search_config: ini files (parsed with the engine's <see cref="ConfigCatalog.Parse"/>), console variables
    /// and native gameplay tags from C++ files that the index's identifier occurrences point to (only files that mention
    /// TAutoConsoleVariable / FAutoConsoleVariableRef / UE_DEFINE_GAMEPLAY_TAG… are read, project files only for CVars).
    /// Everything is cached per file by size + time stamp and capped; nothing scans the source tree. Thread-safe.
    /// </summary>
    public sealed class ConfigSearch
    {
        private static readonly Logger L = Log.For("Mcp.Config");

        public const int MaxIniFiles = 400;
        public const int MaxCodeFiles = 600;
        public const long MaxCodeFileBytes = 4L * 1024 * 1024;

        /// <summary>Identifiers whose occurrences mark a file as defining console variables / commands.</summary>
        public static readonly string[] CVarMarkers =
        {
            "TAutoConsoleVariable", "FAutoConsoleVariableRef", "FAutoConsoleVariable", "FAutoConsoleCommand",
            "FAutoConsoleCommandWithWorld", "FAutoConsoleCommandWithWorldAndArgs", "FAutoConsoleCommandWithArgsAndOutputDevice",
        };

        private static readonly Regex CVarRegex = new Regex(
            @"\b(?<decl>TAutoConsoleVariable\s*<[^>]+>|FAutoConsoleVariableRef|FAutoConsoleVariable|FAutoConsoleCommand\w*)\s+(?<var>\w+)\s*\(\s*(?:TEXT\s*\(\s*)?""(?<name>[^""\r\n]+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex StringLiteral = new Regex(@"(?:TEXT\s*\(\s*)?""(?<s>(?:[^""\\\r\n]|\\.)*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly CodeIndex _index;
        private readonly object _gate = new object();
        private readonly Dictionary<string, (long Len, long Ticks, IniFileData Data)> _ini = new Dictionary<string, (long, long, IniFileData)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (long Len, long Ticks, List<CVarDefinition> CVars, List<TagDefinition> Tags)> _code =
            new Dictionary<string, (long, long, List<CVarDefinition>, List<TagDefinition>)>(StringComparer.OrdinalIgnoreCase);

        public ConfigSearch(CodeIndex index) => _index = index;

        // ------------------------------------------------------------------ ini

        /// <summary>The project's (and with <paramref name="includeEngine"/> the engine's and its plugins') Config/**/*.ini, parsed.</summary>
        public List<IniFileData> IniFiles(bool includeEngine, CancellationToken ct)
        {
            var ws = _index.Offline.Workspace(full: true);
            var result = new List<IniFileData>();
            if (ws == null) return result;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int read = 0;
            foreach (var root in ws.ConfigRoots.Where(r => r.IsProject || includeEngine))
            {
                IEnumerable<string> files;
                try { files = Directory.Exists(root.Path) ? Directory.EnumerateFiles(root.Path, "*.ini", SearchOption.AllDirectories) : Enumerable.Empty<string>(); }
                catch (Exception) { continue; }
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (result.Count >= MaxIniFiles) break;
                    var data = Ini(f, ref read);
                    if (data != null) result.Add(data);
                }
            }
            if (read > 0) L.Info("mcp.config.ini", null, ("files", result.Count), ("parsed", read), ("includeEngine", includeEngine), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            return result;
        }

        private IniFileData Ini(string path, ref int read)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > ConfigCatalog.MaxFileBytes) return null;
                lock (_gate)
                    if (_ini.TryGetValue(path, out var c) && c.Len == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks) return c.Data;
                var data = ConfigCatalog.Parse(path, File.ReadAllText(path));
                read++;
                lock (_gate) _ini[path] = (fi.Length, fi.LastWriteTimeUtc.Ticks, data);
                return data;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>0-based line of <c>key=</c> inside <c>[section]</c> (or of the section header when key is null); -1 when not found.</summary>
        public int IniLine(string path, string section, string key)
        {
            var text = _index.Texts.Get(path);
            if (text == null) return -1;
            bool inSection = false;
            for (int i = 0; i < text.LineCount; i++)
            {
                string line = text.Line(i).Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = line.IndexOf(']');
                    inSection = close > 1 && string.Equals(line.Substring(1, close - 1).Trim(), section, StringComparison.OrdinalIgnoreCase);
                    if (inSection && key == null) return i;
                    continue;
                }
                if (!inSection || key == null) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line.Substring(0, eq).Trim().TrimStart('+', '-', '.', '!').Trim(), key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ code (CVars, native tags)

        /// <summary>
        /// Files that mention any of <paramref name="markers"/>, from the on-disk index's occurrences (identifiers in project
        /// and, when asked, engine files). Capped at <paramref name="max"/>.
        /// </summary>
        public List<string> OccurrenceFiles(IEnumerable<string> markers, bool includeNonProject, int max, CancellationToken ct, out bool truncated)
        {
            truncated = false;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!_index.Offline.IsAvailable) return new List<string>();
            string projectRoot = _index.Offline.Workspace()?.ProjectRoot;
            string prefix = projectRoot == null ? null : projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            bool cut = false;
            _index.Offline.Use(s =>
            {
                foreach (var m in markers)
                {
                    string last = null;
                    foreach (var h in s.FindOccurrences(m, ct))
                    {
                        if (h.Path == last) continue;
                        last = h.Path;
                        if (!includeNonProject && prefix != null && !h.Path.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        files.Add(h.Path);
                        if (files.Count >= max)
                        {
                            cut = true;
                            return 0;
                        }
                    }
                }
                return 0;
            });
            truncated = cut;
            return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>CVars and native tags defined in one C++ file (cached by size + time stamp).</summary>
        public (List<CVarDefinition> CVars, List<TagDefinition> Tags) CodeFile(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > MaxCodeFileBytes) return (new List<CVarDefinition>(), new List<TagDefinition>());
                lock (_gate)
                    if (_code.TryGetValue(path, out var c) && c.Len == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks) return (c.CVars, c.Tags);
                string text = File.ReadAllText(path);
                var cvars = ExtractCVars(text, path);
                var tags = GameplayTagCatalog.Extract(text, Path.GetFileName(path));
                lock (_gate) _code[path] = (fi.Length, fi.LastWriteTimeUtc.Ticks, cvars, tags);
                return (cvars, tags);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return (new List<CVarDefinition>(), new List<TagDefinition>());
            }
        }

        /// <summary>Console variable / command definitions in C++ text, with the first string literal after the name as help.</summary>
        public static List<CVarDefinition> ExtractCVars(string text, string path)
        {
            var list = new List<CVarDefinition>();
            if (string.IsNullOrEmpty(text) || text.IndexOf("AutoConsole", StringComparison.Ordinal) < 0) return list;
            foreach (Match m in CVarRegex.Matches(text))
            {
                var d = new CVarDefinition
                {
                    Name = m.Groups["name"].Value.Trim(),
                    Variable = m.Groups["var"].Value,
                    Declaration = Regex.Replace(m.Groups["decl"].Value, @"\s+", ""),
                    File = path,
                    Line = LineOf(text, m.Index),
                };
                // help: the next string literal before the end of the statement
                int end = text.IndexOf(';', m.Index + m.Length);
                if (end < 0) end = Math.Min(text.Length, m.Index + m.Length + 2000);
                var help = StringLiteral.Match(text.Substring(m.Index + m.Length, Math.Min(end, m.Index + m.Length + 2000) - (m.Index + m.Length)));
                if (help.Success) d.Help = help.Groups["s"].Value.Replace("\\n", " ").Replace("\\\"", "\"").Trim();
                list.Add(d);
            }
            return list;
        }

        private static int LineOf(string text, int index)
        {
            int line = 0;
            for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }

        // ------------------------------------------------------------------ UPROPERTY(config) ↔ ini

        /// <summary>Default{Category}.ini for UCLASS(config=Category): Game → DefaultGame.ini, Engine → DefaultEngine.ini...</summary>
        public static string DefaultIniFor(string configCategory) => string.IsNullOrEmpty(configCategory) ? null : "Default" + configCategory + ".ini";

        /// <summary>[/Script/Module.ClassWithoutPrefix]: the section UE reads a class's config properties from.</summary>
        public static string ScriptSection(string module, string className)
        {
            if (string.IsNullOrEmpty(module) || string.IsNullOrEmpty(className)) return null;
            string bare = className.Length > 1 && (className[0] == 'U' || className[0] == 'A') && char.IsUpper(className[1]) ? className.Substring(1) : className;
            return "/Script/" + module + "." + bare;
        }

        /// <summary>Splits a [/Script/Module.Class] section into (module, class without prefix); null when it is not one.</summary>
        public static (string Module, string Class)? ParseScriptSection(string section)
        {
            if (section == null || !section.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = section.Substring("/Script/".Length);
            int dot = rest.IndexOf('.');
            if (dot <= 0 || dot == rest.Length - 1) return null;
            return (rest.Substring(0, dot), rest.Substring(dot + 1));
        }

        /// <summary>Query match: every space-separated word occurs (case-insensitive) in one of the texts.</summary>
        public static bool Matches(string query, params string[] texts)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            foreach (var word in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (!texts.Any(t => t != null && t.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            return true;
        }

        public static bool IsProjectOrigin(ItemOrigin? o) => o == ItemOrigin.Project || o == ItemOrigin.ProjectPlugin;
    }
}
