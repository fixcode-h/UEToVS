using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UEToVS.Host.Logic.Build
{
    public enum BuildSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>
    /// One error / warning / note of a build (UBT → MSVC / clang / UHT / linker / MSBuild / csc), or of Live Coding.
    /// <see cref="Line"/> and <see cref="Column"/> are 1-based as compilers print them (0 = not given).
    /// </summary>
    public sealed class BuildDiagnostic
    {
        /// <summary>Absolute path (as printed, or resolved against the parser's base directory); null for linker / UBT summary lines.</summary>
        public string File { get; set; }

        public int Line { get; set; }

        public int Column { get; set; }

        public BuildSeverity Severity { get; set; }

        /// <summary>C2065, C4996, LNK2019, MSB3073, CS0103, -Wunused-variable … (null when the line has none, e.g. UHT).</summary>
        public string Code { get; set; }

        public string Message { get; set; }

        /// <summary>"msvc", "clang", "uht", "ubt", "linker", "msbuild", "csc" or the tool named on the line.</summary>
        public string Tool { get; set; }

        public bool HasLocation => !string.IsNullOrEmpty(File) && Line > 0;

        public override string ToString() =>
            (File != null ? File + "(" + Line + (Column > 0 ? "," + Column : "") + "): " : "") + Severity.ToString().ToLowerInvariant() +
            (Code != null ? " " + Code : "") + ": " + Message;
    }

    /// <summary>
    /// Turns build output lines into <see cref="BuildDiagnostic"/>s. Accepts any text line (VS "Build" pane, UBT / UHT console
    /// output, Live Coding log lines): the MSBuild node prefix (<c>3&gt;</c>) and UE log prefixes (<c>[time][frame]LogCompile: Error:</c>)
    /// are stripped first. Understood forms:
    /// <list type="bullet">
    /// <item>MSVC / UHT / csc / clang-cl / MSBuild: <c>File(Line[,Col[,EndLine,EndCol]]): [fatal] error|warning|note [CODE]: Message</c>
    /// (UHT writes <c>Error:</c> / <c>Warning:</c> capitalized).</item>
    /// <item>clang / gcc: <c>File:Line[:Col]: error|warning|note|fatal error: Message [-Wflag]</c> (Windows drive letters allowed).</item>
    /// <item>Tool lines without a source location: <c>LINK : fatal error LNK1104: …</c>, <c>X.cpp.obj : error LNK2019: …</c>,
    /// <c>UnrealBuildTool : error : …</c>.</item>
    /// <item>UBT / UHT summary lines: <c>ERROR: …</c>, <c>Error: …</c>, <c>LogCompile: Error: …</c>.</item>
    /// </list>
    /// Duplicates (UBT repeats some errors in its summary) are dropped; at most <see cref="MaxItems"/> are kept. Lines that
    /// mention error / warning but match none of the forms are counted and sampled (<see cref="UnparsedSamples"/>) for the log.
    /// Not thread-safe; one instance per build.
    /// </summary>
    public sealed class BuildOutputParser
    {
        public const int MaxItems = 5000;
        public const int MaxSamples = 8;
        public const int MaxSampleLength = 300;
        public const int MaxLineLength = 8192;

        private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private static readonly Regex NodePrefix = new Regex(@"^\s*\d{1,4}>\s?", Opts);

        // [2026.11.12-10.00.00:123][  0]LogLiveCoding: Display: …   /   LogCompile: Error: …
        private static readonly Regex UeLogPrefix = new Regex(
            @"^(?:\[[^\]]*\])*\s*(?<cat>Log\w+):\s*(?:(?<verb>Display|Error|Warning|Log|Verbose|VeryVerbose|Fatal):\s*)?(?<rest>.*)$", Opts);

        private static readonly Regex Msvc = new Regex(
            @"^\s*(?<file>(?:[A-Za-z]:[\\/]|\\\\|/)?[^<>|""?*\r\n]*?[^<>|""?*\s\r\n(])" +
            @"\((?<line>\d+)(?:,(?<col>\d+))?(?:[,-]\d+(?:,\d+)?)?\)\s*:\s*" +
            @"(?<sev>fatal\s+error|error|warning|note|info|message)\b\s*(?<code>[A-Za-z]{1,8}\d+)?\s*:\s*(?<msg>.*)$",
            Opts | RegexOptions.IgnoreCase);

        private static readonly Regex Clang = new Regex(
            @"^\s*(?<file>(?:[A-Za-z]:)?[^:<>|""?*\r\n]*[^:<>|""?*\s\r\n]):(?<line>\d+):(?:(?<col>\d+):)?\s*" +
            @"(?<sev>fatal error|error|warning|note)\s*:\s*(?<msg>.*?)(?:\s+\[(?<code>-W[^\]]+)\])?\s*$", Opts);

        private static readonly Regex ToolLine = new Regex(
            @"^\s*(?<tool>[^:()\r\n]*[^:()\s\r\n])\s*:\s*(?<sev>fatal\s+error|error|warning)\b\s*(?<code>[A-Za-z]{1,8}\d+)?\s*:\s*(?<msg>.*)$",
            Opts | RegexOptions.IgnoreCase);

        private static readonly Regex Plain = new Regex(@"^\s*(?<sev>ERROR|Error|error|WARNING|Warning|warning)\s*:\s*(?<msg>\S.*)$", Opts);

        // Lines that mention errors but are summaries, not diagnostics: "  3 Error(s)", "========== Build: 0 succeeded, 1 failed",
        // "Result: Failed (OtherCompilationError)", "warning(s)", MSBuild's "Build FAILED.".
        private static readonly Regex Summary = new Regex(
            @"^\s*(?:\d+\s+(?:error|warning)\(s\)|=+\s|Result:\s|Build (?:FAILED|succeeded)|Total (?:execution )?time|\d+\s+(?:errors?|warnings?)(?:\s+and\s+\d+\s+(?:errors?|warnings?))?\s+generated)",
            Opts | RegexOptions.IgnoreCase);

        private static readonly Regex Mentions = new Regex(@"\b(?:error|warning)\b", Opts | RegexOptions.IgnoreCase);

        private readonly string _baseDirectory;
        private readonly List<BuildDiagnostic> _items = new List<BuildDiagnostic>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _samples = new List<string>();

        /// <param name="baseDirectory">Relative file paths are resolved against it (null = keep them as printed).</param>
        public BuildOutputParser(string baseDirectory = null)
        {
            _baseDirectory = baseDirectory;
        }

        public IReadOnlyList<BuildDiagnostic> Items => _items;

        /// <summary>Up to <see cref="MaxSamples"/> lines that mention error / warning but were not understood (trimmed, shortened).</summary>
        public IReadOnlyList<string> UnparsedSamples => _samples;

        public int LinesSeen { get; private set; }
        public int Parsed { get; private set; }
        public int Duplicates { get; private set; }
        public int Unparsed { get; private set; }

        /// <summary>More than <see cref="MaxItems"/> diagnostics: the rest were dropped.</summary>
        public bool Truncated { get; private set; }

        public int Errors { get; private set; }
        public int Warnings { get; private set; }
        public int Notes { get; private set; }

        /// <summary>Adds one line; returns the new diagnostic, or null (no diagnostic, duplicate, or over the cap).</summary>
        public BuildDiagnostic AddLine(string line)
        {
            if (line == null) return null;
            LinesSeen++;
            if (line.Length > MaxLineLength) line = line.Substring(0, MaxLineLength);
            var d = ParseLine(line, _baseDirectory);
            if (d == null)
            {
                if (IsCandidate(line))
                {
                    Unparsed++;
                    if (_samples.Count < MaxSamples)
                    {
                        string s = line.Trim();
                        _samples.Add(s.Length > MaxSampleLength ? s.Substring(0, MaxSampleLength) + "…" : s);
                    }
                }
                return null;
            }
            Parsed++;
            if (!_seen.Add(Key(d)))
            {
                Duplicates++;
                return null;
            }
            if (_items.Count >= MaxItems)
            {
                Truncated = true;
                return null;
            }
            _items.Add(d);
            switch (d.Severity)
            {
                case BuildSeverity.Error: Errors++; break;
                case BuildSeverity.Warning: Warnings++; break;
                default: Notes++; break;
            }
            return d;
        }

        /// <summary>Adds every line of a block of text (\n or \r\n).</summary>
        public void AddText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;
                int end = i;
                if (end > start && text[end - 1] == '\r') end--;
                if (end > start) AddLine(text.Substring(start, end - start));
                start = i + 1;
            }
        }

        private static string Key(BuildDiagnostic d) =>
            (d.File ?? "") + "|" + d.Line + "|" + d.Column + "|" + (int)d.Severity + "|" + (d.Code ?? "") + "|" + d.Message;

        private static bool IsCandidate(string line) => Mentions.IsMatch(line) && line.IndexOf(':') >= 0 && !Summary.IsMatch(StripNode(line));

        private static string StripNode(string line)
        {
            var m = NodePrefix.Match(line);
            return m.Success ? line.Substring(m.Length) : line;
        }

        /// <summary>Parses one line without state (no dedup, no counters). Null when the line is not a diagnostic.</summary>
        public static BuildDiagnostic ParseLine(string line, string baseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            string rest = StripNode(line);
            string logCategory = null, logVerbosity = null;
            var log = UeLogPrefix.Match(rest);
            if (log.Success)
            {
                logCategory = log.Groups["cat"].Value;
                logVerbosity = log.Groups["verb"].Success ? log.Groups["verb"].Value : null;
                rest = log.Groups["rest"].Value;
            }
            if (rest.Length == 0 || Summary.IsMatch(rest)) return null;

            var m = Msvc.Match(rest);
            if (m.Success && LooksLikePath(m.Groups["file"].Value))
            {
                string code = m.Groups["code"].Success ? m.Groups["code"].Value : null;
                string sev = m.Groups["sev"].Value;
                string file = ResolvePath(m.Groups["file"].Value.Trim(), baseDirectory);
                return new BuildDiagnostic
                {
                    File = file,
                    Line = ParseInt(m.Groups["line"].Value),
                    Column = m.Groups["col"].Success ? ParseInt(m.Groups["col"].Value) : 0,
                    Severity = Severity(sev),
                    Code = code,
                    Message = m.Groups["msg"].Value.Trim(),
                    Tool = ToolFor(code, file, sev, logCategory, clang: false),
                };
            }

            m = Clang.Match(rest);
            if (m.Success && LooksLikePath(m.Groups["file"].Value))
            {
                string file = ResolvePath(m.Groups["file"].Value.Trim(), baseDirectory);
                return new BuildDiagnostic
                {
                    File = file,
                    Line = ParseInt(m.Groups["line"].Value),
                    Column = m.Groups["col"].Success ? ParseInt(m.Groups["col"].Value) : 0,
                    Severity = Severity(m.Groups["sev"].Value),
                    Code = m.Groups["code"].Success ? m.Groups["code"].Value : null,
                    Message = m.Groups["msg"].Value.Trim(),
                    Tool = "clang",
                };
            }

            m = ToolLine.Match(rest);
            if (m.Success)
            {
                string tool = m.Groups["tool"].Value.Trim();
                string code = m.Groups["code"].Success ? m.Groups["code"].Value : null;
                return new BuildDiagnostic
                {
                    Severity = Severity(m.Groups["sev"].Value),
                    Code = code,
                    Message = m.Groups["msg"].Value.Trim(),
                    Tool = ToolForName(tool, code),
                };
            }

            m = Plain.Match(rest);
            if (m.Success)
            {
                return new BuildDiagnostic
                {
                    Severity = Severity(m.Groups["sev"].Value),
                    Message = m.Groups["msg"].Value.Trim(),
                    Tool = string.Equals(logCategory, "LogCompile", StringComparison.Ordinal) ? "uht" : "ubt",
                };
            }

            // "LogCompile: Error: Failed to generate code" style without a second "Error:".
            if (logVerbosity == "Error" || logVerbosity == "Fatal" || logVerbosity == "Warning")
            {
                if (string.Equals(logCategory, "LogCompile", StringComparison.Ordinal) || string.Equals(logCategory, "LogLiveCoding", StringComparison.Ordinal))
                    return new BuildDiagnostic
                    {
                        Severity = logVerbosity == "Warning" ? BuildSeverity.Warning : BuildSeverity.Error,
                        Message = rest.Trim(),
                        Tool = logCategory == "LogCompile" ? "uht" : "livecoding",
                    };
            }
            return null;
        }

        private static int ParseInt(string s) => int.TryParse(s, out int v) ? v : 0;

        private static BuildSeverity Severity(string text)
        {
            string s = text.ToLowerInvariant();
            if (s.Contains("error")) return BuildSeverity.Error;
            if (s.Contains("warning")) return BuildSeverity.Warning;
            return BuildSeverity.Info;
        }

        /// <summary>A file part with an extension and no URL scheme ("http://x(1): error" is not a file).</summary>
        private static bool LooksLikePath(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            file = file.Trim();
            if (file.IndexOf("://", StringComparison.Ordinal) >= 0) return false;
            int slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
            int dot = file.LastIndexOf('.');
            return dot > slash && dot < file.Length - 1;
        }

        /// <summary>Windows drive / UNC / Unix absolute paths count as rooted on every OS (tests run on Linux).</summary>
        public static bool IsRooted(string path) =>
            !string.IsNullOrEmpty(path) &&
            (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/') || path.StartsWith("\\\\", StringComparison.Ordinal) || path[0] == '/');

        private static string ResolvePath(string file, string baseDirectory)
        {
            if (!IsRooted(file) && !string.IsNullOrEmpty(baseDirectory)) file = Path.Combine(baseDirectory, file);
            if (file.Contains("..") && Path.IsPathRooted(file))
            {
                try
                {
                    file = Path.GetFullPath(file);
                }
                catch (Exception)
                {
                    // keep as printed
                }
            }
            return file;
        }

        private static string ToolFor(string code, string file, string sev, string logCategory, bool clang)
        {
            if (code != null)
            {
                if (code.StartsWith("LNK", StringComparison.OrdinalIgnoreCase)) return "linker";
                if (code.StartsWith("MSB", StringComparison.OrdinalIgnoreCase)) return "msbuild";
                if (code.StartsWith("CS", StringComparison.OrdinalIgnoreCase)) return "csc";
                if (code.StartsWith("C", StringComparison.OrdinalIgnoreCase)) return "msvc";
                return "msvc";
            }
            if (clang) return "clang";
            // UHT prints "Error:" / "Warning:" capitalized without a code, on a header.
            if (string.Equals(logCategory, "LogCompile", StringComparison.Ordinal)) return "uht";
            if (sev.Length > 0 && char.IsUpper(sev[0])) return "uht";
            string ext = Path.GetExtension(file ?? string.Empty).ToLowerInvariant();
            if (ext == ".cs") return "csc";
            if (ext == ".targets" || ext == ".props" || ext.EndsWith("proj", StringComparison.Ordinal)) return "msbuild";
            return "msvc"; // clang-cl in MSVC format
        }

        private static string ToolForName(string tool, string code)
        {
            if (code != null && code.StartsWith("LNK", StringComparison.OrdinalIgnoreCase)) return "linker";
            if (string.Equals(tool, "LINK", StringComparison.OrdinalIgnoreCase) || tool.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ||
                tool.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) || tool.EndsWith(".o", StringComparison.OrdinalIgnoreCase)) return "linker";
            if (tool.IndexOf("UnrealBuildTool", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(tool, "UBT", StringComparison.OrdinalIgnoreCase)) return "ubt";
            if (tool.IndexOf("UnrealHeaderTool", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(tool, "UHT", StringComparison.OrdinalIgnoreCase)) return "uht";
            if (code != null && code.StartsWith("MSB", StringComparison.OrdinalIgnoreCase)) return "msbuild";
            if (string.Equals(tool, "clang", StringComparison.OrdinalIgnoreCase) || tool.EndsWith("clang++", StringComparison.OrdinalIgnoreCase)) return "clang";
            return tool.Length > 40 ? tool.Substring(0, 40) : tool;
        }
    }
}
