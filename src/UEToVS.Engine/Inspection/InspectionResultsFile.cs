using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using UEToVS.Contracts;

namespace UEToVS.Engine.Inspection
{
    /// <summary>
    /// Inspection results as JSON lines: a header record, one record per finding, a summary record at the end. Findings are
    /// streamed to disk as they come, so memory holds the counts and one page only, whatever the project size. Pages are
    /// read back by streaming the file again (filter + skip + take); a file still being written is read up to its last
    /// complete line.
    /// </summary>
    public static class InspectionResultsFile
    {
        public const int FormatVersion = 1;

        /// <summary>Inspection files kept in the folder (older ones are deleted when a run starts).</summary>
        public const int KeepFiles = 10;

        public static string NewPath(string folder, DateTime nowLocal)
        {
            string name = nowLocal.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, name + ".jsonl");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, name + "-" + i + ".jsonl");
            return path;
        }

        /// <summary>Deletes all but the newest <paramref name="keep"/> .jsonl files. Never throws.</summary>
        public static int Prune(string folder, int keep = KeepFiles)
        {
            int deleted = 0;
            try
            {
                if (!Directory.Exists(folder)) return 0;
                foreach (var f in new DirectoryInfo(folder).GetFiles("*.jsonl").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Math.Max(0, keep)))
                {
                    try { f.Delete(); deleted++; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return deleted;
        }

        public static void WriteHeader(Utf8JsonWriter w, string solutionDir, bool includePlugins, int files, DateTime startedUtc)
        {
            w.WriteStartObject();
            w.WriteString("type", "inspection");
            w.WriteNumber("version", FormatVersion);
            w.WriteString("solution", solutionDir ?? string.Empty);
            w.WriteBoolean("includePlugins", includePlugins);
            w.WriteNumber("files", files);
            w.WriteString("started", startedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }

        public static void WriteFinding(Utf8JsonWriter w, InspectionFinding f)
        {
            w.WriteStartObject();
            w.WriteString("type", "finding");
            w.WriteString("file", f.FilePath);
            w.WriteNumber("line", f.Line);
            w.WriteNumber("col", f.Column);
            w.WriteNumber("len", f.Length);
            w.WriteNumber("sev", (int)f.Severity);
            w.WriteString("code", f.Code);
            w.WriteStartArray("args");
            if (f.Args != null) foreach (var a in f.Args) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteString("msg", f.Message);
            if (f.LineText != null) w.WriteString("text", f.LineText);
            if (f.Fix != null && f.Fix.Count > 0)
            {
                w.WriteStartArray("fix");
                foreach (var e in f.Fix)
                {
                    w.WriteStartObject();
                    w.WriteString("file", e.FilePath);
                    w.WriteNumber("line", e.Line);
                    w.WriteNumber("col", e.Column);
                    w.WriteNumber("len", e.Length);
                    w.WriteString("old", e.OldText);
                    w.WriteString("new", e.NewText);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        public static void WriteSummary(Utf8JsonWriter w, string state, int filesDone, int findings, double ms, long peakWorkingSet, IEnumerable<InspectionRuleCount> rules)
        {
            w.WriteStartObject();
            w.WriteString("type", "summary");
            w.WriteString("state", state);
            w.WriteNumber("filesDone", filesDone);
            w.WriteNumber("findings", findings);
            w.WriteNumber("ms", Math.Round(ms, 1));
            w.WriteNumber("peakWorkingSet", peakWorkingSet);
            w.WriteStartObject("rules");
            foreach (var r in rules) w.WriteNumber(r.Code, r.Count);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        /// <summary>Whether a finding passes the request's filters (rule, file substring, fixable only).</summary>
        public static bool Matches(InspectionFinding f, string rule, string file, bool fixableOnly) =>
            (string.IsNullOrEmpty(rule) || string.Equals(f.Code, rule, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(file) || (f.FilePath ?? string.Empty).IndexOf(file, StringComparison.OrdinalIgnoreCase) >= 0) &&
            (!fixableOnly || (f.Fix != null && f.Fix.Count > 0));

        /// <summary>
        /// One page of findings: those matching the filters, skipping <paramref name="offset"/>, at most <paramref name="limit"/>;
        /// <paramref name="matched"/> counts every match. Memory: one line and one page.
        /// </summary>
        public static List<InspectionFinding> ReadPage(string path, string rule, string file, bool fixableOnly, int offset, int limit, out int matched, CancellationToken ct)
        {
            matched = 0;
            var page = new List<InspectionFinding>(Math.Min(Math.Max(limit, 0), 2000));
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return page;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            using (var reader = new StreamReader(fs))
            {
                string line;
                int n = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
                    var f = ParseFinding(line);
                    if (f == null || !Matches(f, rule, file, fixableOnly)) continue;
                    if (matched >= offset && page.Count < limit) page.Add(f);
                    matched++;
                }
            }
            return page;
        }

        /// <summary>The finding on one line, or null (header / summary / incomplete last line of a file still being written).</summary>
        public static InspectionFinding ParseFinding(string line)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf("\"finding\"", StringComparison.Ordinal) < 0) return null;
            try
            {
                using (var doc = JsonDocument.Parse(line))
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("type", out var t) || t.GetString() != "finding") return null;
                    var f = new InspectionFinding
                    {
                        FilePath = Str(r, "file"),
                        Line = Int(r, "line"),
                        Column = Int(r, "col"),
                        Length = Int(r, "len"),
                        Severity = (CodeDiagnosticSeverity)Int(r, "sev"),
                        Code = Str(r, "code"),
                        Message = Str(r, "msg"),
                        LineText = Str(r, "text"),
                    };
                    if (r.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                        foreach (var a in args.EnumerateArray()) f.Args.Add(a.GetString());
                    if (r.TryGetProperty("fix", out var fix) && fix.ValueKind == JsonValueKind.Array)
                    {
                        f.Fix = new List<FileTextEdit>();
                        foreach (var e in fix.EnumerateArray())
                            f.Fix.Add(new FileTextEdit { FilePath = Str(e, "file"), Line = Int(e, "line"), Column = Int(e, "col"), Length = Int(e, "len"), OldText = Str(e, "old"), NewText = Str(e, "new") });
                    }
                    return f;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
    }
}
