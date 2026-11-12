using System;
using System.Text.RegularExpressions;
using UEToVS.Host.Logic.Link;

namespace UEToVS.Host.Logic.Build
{
    /// <summary>
    /// Bridges Live Coding output (<see cref="IBuildOutputSink"/>, from the UE editor link) to the generic build-error
    /// pipeline: strips the UE log prefix of every line, feeds <see cref="BuildOutputParser"/> and, when the compile
    /// finishes, hands a <see cref="BuildRecord"/> (source "liveCoding") to <see cref="Completed"/>. One build at a time;
    /// thread-safe (calls arrive in order on a background thread). Never throws.
    /// </summary>
    public sealed class LiveCodingBuildCollector : IBuildOutputSink
    {
        public const string RecordSource = "live-coding";

        // [2024.01.01-10.00.00:000][  0]LogLiveCoding: Error: ...  /  LogLiveCoding: Display: ...
        private static readonly Regex UePrefix = new Regex(
            @"^\s*(?:\[[^\]]*\]\s*\[\s*\d+\s*\])?\s*Log\w+:\s*(?:(?:Fatal|Error|Warning|Display|Log|Verbose|VeryVerbose):\s*)?",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly object _gate = new object();
        private readonly Func<string> _baseDirectory;
        private BuildOutputParser _parser;
        private DateTime _startedUtc;

        public LiveCodingBuildCollector(Func<string> baseDirectory = null) => _baseDirectory = baseDirectory ?? (() => null);

        /// <summary>Raised (on the producer's thread) with the parsed record when a compile finishes.</summary>
        public event Action<BuildRecord> Completed;

        /// <summary>Removes the UE log prefix ("[time][frame]LogLiveCoding: Error: ") from a line.</summary>
        public static string StripUePrefix(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;
            var m = UePrefix.Match(line);
            return m.Success && m.Length > 0 ? line.Substring(m.Length) : line;
        }

        public void BuildStarted(string source, string projectRoot)
        {
            lock (_gate)
            {
                _parser = new BuildOutputParser(projectRoot ?? _baseDirectory());
                _startedUtc = DateTime.UtcNow;
            }
        }

        public void OutputLine(string source, string line)
        {
            if (line == null) return;
            lock (_gate)
            {
                if (_parser == null) { _parser = new BuildOutputParser(_baseDirectory()); _startedUtc = DateTime.UtcNow; }
                try { _parser.AddLine(StripUePrefix(line)); } catch (Exception) { /* one bad line never breaks the build */ }
            }
        }

        public void BuildFinished(string source, bool? succeeded, string result)
        {
            BuildRecord record;
            lock (_gate)
            {
                if (_parser == null) return;
                string r = succeeded == true ? "succeeded" : succeeded == false ? (result == "cancelled" ? "canceled" : "failed") : (result ?? "unknown");
                if (_parser.Errors > 0) r = "failed";
                record = BuildRecord.From(_parser, _startedUtc, DateTime.UtcNow, r, RecordSource);
                _parser = null;
            }
            try { Completed?.Invoke(record); } catch (Exception) { /* sinks must not throw */ }
        }
    }
}
