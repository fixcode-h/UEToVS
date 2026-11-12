using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UEToVS.Core.Storage;

namespace UEToVS.Mcp.Data
{
    /// <summary>One error / warning of the last build (1-based line / column; 0 = not given).</summary>
    public sealed class BuildErrorItem
    {
        public string Severity { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string Tool { get; set; }
    }

    public sealed class BuildErrorsFile
    {
        public string Path { get; set; }
        public string Source { get; set; }
        public string Result { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }
        public DateTime WrittenUtc { get; set; }
        public long DurationMs { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public bool Truncated { get; set; }
        public List<BuildErrorItem> Items { get; } = new List<BuildErrorItem>();
    }

    /// <summary>
    /// Reads &lt;solution&gt;\.vs\UEToVS\build\last-build.json, written by the VS extension when a build ends
    /// (src/UEToVS.Host/Logic/Build/BuildRecord.cs owns the format). Small file, read per call.
    /// </summary>
    public static class BuildErrorsReader
    {
        public static string PathFor(string solutionDir) => System.IO.Path.Combine(StoragePaths.ForSolution(solutionDir).Root, "build", "last-build.json");

        /// <summary>Null when the file does not exist; throws <see cref="ToolException"/> when it is unreadable.</summary>
        public static BuildErrorsFile Read(string solutionDir)
        {
            string path = PathFor(solutionDir);
            if (!File.Exists(path)) return null;
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                throw new ToolException("build_errors_unreadable", "Cannot read " + path + ": " + ex.Message);
            }
            var f = Parse(json) ?? throw new ToolException("build_errors_unreadable", path + " is not a UEToVS build record (unknown version or invalid JSON).");
            f.Path = path;
            try
            {
                f.WrittenUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
            }
            return f;
        }

        public static BuildErrorsFile Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || Int(r, "version") != 1) return null;
                var f = new BuildErrorsFile
                {
                    Source = Str(r, "source"),
                    Result = Str(r, "result"),
                    StartedUtc = Date(r, "startedUtc"),
                    FinishedUtc = Date(r, "finishedUtc"),
                    DurationMs = r.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0,
                    Errors = Int(r, "errors"),
                    Warnings = Int(r, "warnings"),
                    Truncated = r.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True,
                };
                if (r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var i in items.EnumerateArray())
                    {
                        if (i.ValueKind != JsonValueKind.Object) continue;
                        f.Items.Add(new BuildErrorItem
                        {
                            Severity = Str(i, "severity") ?? "error",
                            File = Str(i, "file"),
                            Line = Int(i, "line"),
                            Column = Int(i, "column"),
                            Code = Str(i, "code"),
                            Message = Str(i, "message") ?? string.Empty,
                            Tool = Str(i, "tool"),
                        });
                    }
                return f;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

        private static DateTime Date(JsonElement e, string name) =>
            Str(e, name) is string s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d.ToUniversalTime() : DateTime.MinValue;
    }
}
