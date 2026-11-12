using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UEToVS.Host.Logic.Link
{
    public enum MsvcSeverity
    {
        Error = 0,
        Warning = 1,
        Note = 2,
    }

    /// <summary>
    /// One compiler / linker diagnostic parsed from an MSVC style line: <c>C:\P\Foo.cpp(12): error C2065: 'x': undeclared</c>,
    /// <c>Foo.cpp(12,5): warning C4996: ...</c>, <c>Foo.obj : error LNK2019: unresolved external ...</c>. Tolerates a UE log
    /// prefix (<c>[2024.01.01-10.00.00:000][  0]LogLiveCoding: Error: </c>) and leading whitespace. Minimal fallback
    /// parser; the generic UBT/MSVC parser of the build-errors feature is the real consumer.
    /// </summary>
    public sealed class MsvcErrorLine
    {
        // path(line[,col]) : severity CODE: message      (path may contain spaces and a drive colon)
        private static readonly Regex WithLine = new Regex(
            @"(?<path>(?:[A-Za-z]:)?[^:<>""|?*()\r\n]*?[^:<>""|?*()\s\r\n])\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<sev>fatal error|error|warning|note)\s*(?<code>[A-Z]+\d+)?\s*:\s*(?<msg>.*)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

        // file : error LNK2019: message   /   LINK : fatal error LNK1104: ...
        private static readonly Regex WithoutLine = new Regex(
            @"(?<path>(?:[A-Za-z]:)?[^:<>""|?*\r\n]*?[^:<>""|?*\s\r\n])\s+:\s*(?<sev>fatal error|error|warning)\s+(?<code>(?:LNK|C|D|U)\d+)\s*:\s*(?<msg>.*)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

        public string FilePath { get; private set; }

        /// <summary>1-based line; 0 when the diagnostic has no line (linker).</summary>
        public int Line { get; private set; }

        /// <summary>1-based column; 0 when unknown.</summary>
        public int Column { get; private set; }

        public MsvcSeverity Severity { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }

        public static bool TryParse(string text, out MsvcErrorLine result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 8192) return false;
            string line = text.Trim();
            try
            {
                var m = WithLine.Match(line);
                if (m.Success)
                {
                    result = new MsvcErrorLine
                    {
                        FilePath = CleanPath(m.Groups["path"].Value),
                        Line = int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture),
                        Column = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value, CultureInfo.InvariantCulture) : 0,
                        Severity = SeverityOf(m.Groups["sev"].Value),
                        Code = m.Groups["code"].Success ? m.Groups["code"].Value : null,
                        Message = m.Groups["msg"].Value.Trim(),
                    };
                    return result.FilePath.Length > 0;
                }
                m = WithoutLine.Match(line);
                if (m.Success)
                {
                    result = new MsvcErrorLine
                    {
                        FilePath = CleanPath(m.Groups["path"].Value),
                        Severity = SeverityOf(m.Groups["sev"].Value),
                        Code = m.Groups["code"].Value,
                        Message = m.Groups["msg"].Value.Trim(),
                    };
                    return result.FilePath.Length > 0;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                result = null;
            }
            catch (OverflowException)
            {
                result = null;
            }
            return false;
        }

        private static MsvcSeverity SeverityOf(string s)
        {
            s = s.ToLowerInvariant();
            if (s.EndsWith("error", StringComparison.Ordinal)) return MsvcSeverity.Error;
            return s == "warning" ? MsvcSeverity.Warning : MsvcSeverity.Note;
        }

        /// <summary>Strips a UE log prefix ("[time][frame]LogX: Error: ") or "1>" that precedes the path.</summary>
        private static string CleanPath(string path)
        {
            path = path.Trim();
            int drive = -1;
            for (int i = 1; i < path.Length; i++)
                if (path[i] == ':' && i + 1 < path.Length && (path[i + 1] == '\\' || path[i + 1] == '/') && char.IsLetter(path[i - 1]) && (i == 1 || !char.IsLetterOrDigit(path[i - 2])))
                {
                    drive = i - 1;
                    break;
                }
            if (drive > 0) path = path.Substring(drive);
            else
            {
                int gt = path.IndexOf('>');
                if (gt >= 0 && gt < 4) path = path.Substring(gt + 1);
                int bracket = path.LastIndexOf(']');
                if (bracket >= 0) path = path.Substring(bracket + 1);
                int colon = path.LastIndexOf(": ", StringComparison.Ordinal);
                if (colon >= 0) path = path.Substring(colon + 2);
            }
            return path.Trim();
        }

        public override string ToString() => FilePath + "(" + Line + (Column > 0 ? "," + Column : "") + "): " + Severity + " " + Code + ": " + Message;
    }

    /// <summary>Summary of one Live Coding compile (kept for the status bar / banner and the log).</summary>
    public sealed class LiveCodingBuild
    {
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string Result { get; set; }
        public bool? Succeeded { get; set; }
        public int Lines { get; set; }
        public int DroppedLines { get; set; }
        public List<MsvcErrorLine> Errors { get; } = new List<MsvcErrorLine>();
        public List<MsvcErrorLine> Warnings { get; } = new List<MsvcErrorLine>();
        public double DurationMs { get; set; }
    }

    /// <summary>
    /// Turns the plugin's <c>liveCoding</c> events into <see cref="IBuildOutputSink"/> calls (source "liveCoding") and keeps
    /// a small summary with the MSVC errors of the last compile. Robust to a missing "started" (output first) and to a
    /// missing "finished" (a new "started" closes the previous build as unknown). Thread-safe (one lock); the sinks are
    /// called outside the lock, in event order.
    /// </summary>
    public sealed class LiveCodingOutputCollector
    {
        public const string Source = "liveCoding";
        public const int MaxLinesPerBuild = 20000;
        public const int MaxDiagnostics = 500;

        private readonly object _gate = new object();
        private readonly List<IBuildOutputSink> _sinks = new List<IBuildOutputSink>();
        private readonly Func<string> _projectRoot;
        private LiveCodingBuild _current;

        public LiveCodingOutputCollector(Func<string> projectRoot = null)
        {
            _projectRoot = projectRoot ?? (() => null);
        }

        /// <summary>Last finished build (null before the first one).</summary>
        public LiveCodingBuild LastBuild { get; private set; }

        public bool IsBuilding
        {
            get
            {
                lock (_gate) return _current != null;
            }
        }

        /// <summary>Raised after a build finished (background thread).</summary>
        public event Action<LiveCodingBuild> BuildCompleted;

        public void AddSink(IBuildOutputSink sink)
        {
            if (sink == null) return;
            lock (_gate)
                if (!_sinks.Contains(sink)) _sinks.Add(sink);
        }

        public void RemoveSink(IBuildOutputSink sink)
        {
            lock (_gate) _sinks.Remove(sink);
        }

        public void OnEvent(LiveCodingEvent e)
        {
            if (e == null) return;
            switch (e.Phase)
            {
                case LiveCodingPhase.Started:
                    Finish(null, "unknown", 0); // a "finished" got lost
                    Start();
                    foreach (var l in e.Lines) Line(l);
                    break;
                case LiveCodingPhase.Output:
                    if (!IsBuilding) Start();
                    foreach (var l in e.Lines) Line(l);
                    break;
                case LiveCodingPhase.Finished:
                    if (!IsBuilding) Start();
                    foreach (var l in e.Lines) Line(l);
                    Finish(SucceededOf(e.Result), e.Result ?? "unknown", e.DurationMs);
                    break;
            }
        }

        /// <summary>The editor link dropped while compiling: close the build as unknown.</summary>
        public void OnDisconnected() => Finish(null, "disconnected", 0);

        public static bool? SucceededOf(string result)
        {
            switch (result)
            {
                case "success":
                case "noChanges": return true;
                case "failed":
                case "cancelled": return false;
                default: return null;
            }
        }

        private void Start()
        {
            IBuildOutputSink[] sinks;
            lock (_gate)
            {
                _current = new LiveCodingBuild { StartedUtc = DateTime.UtcNow };
                sinks = _sinks.ToArray();
            }
            string root = SafeRoot();
            foreach (var s in sinks) Call(() => s.BuildStarted(Source, root));
        }

        private void Line(string line)
        {
            if (line == null) return;
            IBuildOutputSink[] sinks;
            lock (_gate)
            {
                var b = _current;
                if (b == null) return;
                if (b.Lines >= MaxLinesPerBuild)
                {
                    b.DroppedLines++;
                    return;
                }
                b.Lines++;
                if (MsvcErrorLine.TryParse(line, out var d))
                {
                    if (d.Severity == MsvcSeverity.Error && b.Errors.Count < MaxDiagnostics) b.Errors.Add(d);
                    else if (d.Severity == MsvcSeverity.Warning && b.Warnings.Count < MaxDiagnostics) b.Warnings.Add(d);
                }
                sinks = _sinks.ToArray();
            }
            foreach (var s in sinks) Call(() => s.OutputLine(Source, line));
        }

        private void Finish(bool? succeeded, string result, double durationMs)
        {
            IBuildOutputSink[] sinks;
            LiveCodingBuild b;
            lock (_gate)
            {
                b = _current;
                if (b == null) return;
                _current = null;
                b.FinishedUtc = DateTime.UtcNow;
                b.Result = result;
                // "success" with compiler errors in the output cannot be trusted; errors win.
                b.Succeeded = succeeded == true && b.Errors.Count > 0 ? false : succeeded;
                b.DurationMs = durationMs > 0 ? durationMs : (b.FinishedUtc.Value - b.StartedUtc).TotalMilliseconds;
                LastBuild = b;
                sinks = _sinks.ToArray();
            }
            foreach (var s in sinks) Call(() => s.BuildFinished(Source, b.Succeeded, result));
            try
            {
                BuildCompleted?.Invoke(b);
            }
            catch (Exception)
            {
                // listeners log their own failures
            }
        }

        private string SafeRoot()
        {
            try
            {
                return _projectRoot();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Call(Action a)
        {
            try
            {
                a();
            }
            catch (Exception)
            {
                // A sink must not break the stream (contract: sinks do not throw).
            }
        }
    }
}
