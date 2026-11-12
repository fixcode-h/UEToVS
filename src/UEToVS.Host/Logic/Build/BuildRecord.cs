using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UEToVS.Core.Storage;

namespace UEToVS.Host.Logic.Build
{
    /// <summary>
    /// The last build's errors and warnings, persisted as <c>&lt;solution&gt;\.vs\UEToVS\build\last-build.json</c> (read back on the
    /// next VS start and by the MCP tool get_build_errors). JSON names are a contract with src/UEToVS.Mcp/Data/BuildErrorsReader.cs.
    /// </summary>
    public sealed class BuildRecord
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>"vs-build" (VS solution build / UBT), "live-coding" or another producer.</summary>
        public string Source { get; set; } = "vs-build";

        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }
        public long DurationMs { get; set; }

        /// <summary>"succeeded", "failed", "canceled" (or "running" while a build is in progress).</summary>
        public string Result { get; set; }

        public int Errors { get; set; }
        public int Warnings { get; set; }
        public int Notes { get; set; }
        public bool Truncated { get; set; }
        public int LinesParsed { get; set; }
        public List<string> UnparsedSamples { get; set; } = new List<string>();
        public List<BuildDiagnostic> Items { get; set; } = new List<BuildDiagnostic>();

        /// <summary>A finished record from a parser's results.</summary>
        public static BuildRecord From(BuildOutputParser parser, DateTime startedUtc, DateTime finishedUtc, string result, string source = "vs-build") => new BuildRecord
        {
            Source = source,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            DurationMs = Math.Max(0, (long)(finishedUtc - startedUtc).TotalMilliseconds),
            Result = result,
            Errors = parser.Errors,
            Warnings = parser.Warnings,
            Notes = parser.Notes,
            Truncated = parser.Truncated,
            LinesParsed = parser.LinesSeen,
            UnparsedSamples = parser.UnparsedSamples.ToList(),
            Items = parser.Items.ToList(),
        };
    }

    /// <summary>Reads / writes <see cref="BuildRecord"/> (UTF-8 JSON, camelCase, atomic replace).</summary>
    public static class BuildRecordStore
    {
        public const string FolderName = "build";
        public const string FileName = "last-build.json";

        public static string PathFor(string solutionDir) =>
            string.IsNullOrEmpty(solutionDir) ? null : Path.Combine(StoragePaths.ForSolution(solutionDir).Root, FolderName, FileName);

        public static string SeverityText(BuildSeverity s) => s == BuildSeverity.Error ? "error" : s == BuildSeverity.Warning ? "warning" : "note";

        public static BuildSeverity ParseSeverity(string s) =>
            string.Equals(s, "error", StringComparison.OrdinalIgnoreCase) ? BuildSeverity.Error
            : string.Equals(s, "warning", StringComparison.OrdinalIgnoreCase) ? BuildSeverity.Warning : BuildSeverity.Info;

        public static string Serialize(BuildRecord r)
        {
            var items = new JArray();
            foreach (var d in r.Items ?? new List<BuildDiagnostic>())
            {
                var o = new JObject { ["severity"] = SeverityText(d.Severity) };
                if (d.File != null) o["file"] = d.File;
                if (d.Line > 0) o["line"] = d.Line;
                if (d.Column > 0) o["column"] = d.Column;
                if (d.Code != null) o["code"] = d.Code;
                o["message"] = d.Message ?? string.Empty;
                if (d.Tool != null) o["tool"] = d.Tool;
                items.Add(o);
            }
            var root = new JObject
            {
                ["version"] = r.Version,
                ["source"] = r.Source,
                ["startedUtc"] = r.StartedUtc.ToUniversalTime().ToString("o"),
                ["finishedUtc"] = r.FinishedUtc.ToUniversalTime().ToString("o"),
                ["durationMs"] = r.DurationMs,
                ["result"] = r.Result,
                ["errors"] = r.Errors,
                ["warnings"] = r.Warnings,
                ["notes"] = r.Notes,
                ["truncated"] = r.Truncated,
                ["linesParsed"] = r.LinesParsed,
                ["unparsedSamples"] = new JArray((r.UnparsedSamples ?? new List<string>()).Cast<object>().ToArray()),
                ["items"] = items,
            };
            return root.ToString(Formatting.Indented);
        }

        /// <summary>Null when the text is not a build record of a known version.</summary>
        public static BuildRecord Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            JObject o;
            try
            {
                o = JObject.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
            int version = (int?)o["version"] ?? 0;
            if (version < 1 || version > BuildRecord.CurrentVersion) return null;
            var r = new BuildRecord
            {
                Version = version,
                Source = (string)o["source"],
                StartedUtc = Date(o["startedUtc"]),
                FinishedUtc = Date(o["finishedUtc"]),
                DurationMs = (long?)o["durationMs"] ?? 0,
                Result = (string)o["result"],
                Errors = (int?)o["errors"] ?? 0,
                Warnings = (int?)o["warnings"] ?? 0,
                Notes = (int?)o["notes"] ?? 0,
                Truncated = (bool?)o["truncated"] ?? false,
                LinesParsed = (int?)o["linesParsed"] ?? 0,
            };
            if (o["unparsedSamples"] is JArray samples) r.UnparsedSamples = samples.Select(s => (string)s).Where(s => s != null).ToList();
            if (o["items"] is JArray arr)
                foreach (var t in arr.OfType<JObject>())
                    r.Items.Add(new BuildDiagnostic
                    {
                        Severity = ParseSeverity((string)t["severity"]),
                        File = (string)t["file"],
                        Line = (int?)t["line"] ?? 0,
                        Column = (int?)t["column"] ?? 0,
                        Code = (string)t["code"],
                        Message = (string)t["message"] ?? string.Empty,
                        Tool = (string)t["tool"],
                    });
            return r;
        }

        private static DateTime Date(JToken t)
        {
            if (t == null) return DateTime.MinValue;
            if (t.Type == JTokenType.Date) return ((DateTime)t).ToUniversalTime();
            return DateTime.TryParse((string)t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d.ToUniversalTime() : DateTime.MinValue;
        }

        /// <summary>Writes through a temp file + replace so readers (the MCP server) never see half a file. Returns the error or null.</summary>
        public static string TryWrite(string path, BuildRecord record)
        {
            if (string.IsNullOrEmpty(path) || record == null) return "no-path";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, Serialize(record), new System.Text.UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static BuildRecord TryRead(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : Deserialize(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>A build record indexed by file for the squiggle tagger, the Error List and "next build error". Immutable.</summary>
    public sealed class BuildErrorSet
    {
        public static readonly BuildErrorSet Empty = new BuildErrorSet(null);

        private readonly Dictionary<string, List<BuildDiagnostic>> _byFile = new Dictionary<string, List<BuildDiagnostic>>(StringComparer.OrdinalIgnoreCase);

        public BuildErrorSet(BuildRecord record)
        {
            Record = record;
            if (record?.Items == null) return;
            foreach (var d in record.Items)
            {
                if (!d.HasLocation) continue;
                string key = NormalizePath(d.File);
                if (!_byFile.TryGetValue(key, out var list)) _byFile[key] = list = new List<BuildDiagnostic>();
                list.Add(d);
            }
        }

        public BuildRecord Record { get; }

        public int Count => Record?.Items?.Count ?? 0;

        public IEnumerable<string> Files => _byFile.Keys;

        /// <summary>Diagnostics of one file (errors and warnings; notes too when <paramref name="includeNotes"/>).</summary>
        public IReadOnlyList<BuildDiagnostic> ForFile(string path, bool includeNotes = false)
        {
            if (string.IsNullOrEmpty(path) || !_byFile.TryGetValue(NormalizePath(path), out var list)) return Array.Empty<BuildDiagnostic>();
            return includeNotes ? list : list.Where(d => d.Severity != BuildSeverity.Info).ToList();
        }

        /// <summary>Diagnostics with a location in output order: errors first, then warnings (what "next build error" walks through).</summary>
        public List<BuildDiagnostic> NavigationOrder()
        {
            var items = Record?.Items ?? new List<BuildDiagnostic>();
            return items.Where(d => d.HasLocation && d.Severity == BuildSeverity.Error)
                        .Concat(items.Where(d => d.HasLocation && d.Severity == BuildSeverity.Warning)).ToList();
        }

        /// <summary>Index into <paramref name="order"/> after <paramref name="current"/> (wraps); -1 when empty.</summary>
        public static int NextIndex(int count, int current) => count <= 0 ? -1 : current < 0 || current >= count - 1 ? 0 : current + 1;

        /// <summary>Case-insensitive key with '/' → '\' (MSVC and clang may print either).</summary>
        public static string NormalizePath(string path) => (path ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
    }

    /// <summary>Where a build diagnostic squiggle goes on its line.</summary>
    public static class BuildDiagnosticSpan
    {
        /// <summary>
        /// (start, length) on <paramref name="lineText"/> (0-based, UTF-16): from the printed 1-based column over the identifier
        /// there (one character when it is punctuation); without a usable column, the line without leading / trailing
        /// white space. Length 0 = nothing to draw (blank line).
        /// </summary>
        public static (int Start, int Length) Compute(string lineText, int column)
        {
            lineText = lineText ?? string.Empty;
            int col = column - 1;
            if (col >= 0 && col < lineText.Length && !char.IsWhiteSpace(lineText[col]))
            {
                int end = col;
                if (IsIdent(lineText[col]))
                    while (end < lineText.Length && IsIdent(lineText[end])) end++;
                else end = col + 1;
                return (col, end - col);
            }
            int first = 0;
            while (first < lineText.Length && char.IsWhiteSpace(lineText[first])) first++;
            int last = lineText.Length;
            while (last > first && char.IsWhiteSpace(lineText[last - 1])) last--;
            return (first, last - first);
        }

        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
