using UEToVS.Core.Storage;

namespace UEToVS.Core.Diagnostics
{
    /// <summary>One-call logging bootstrap for a process ("host" or "engine").</summary>
    public static class LogSetup
    {
        /// <summary>
        /// Creates &lt;.vs/UEToVS/Logs&gt;, attaches a JSON-lines file sink named after the process and hooks unhandled
        /// exceptions. Returns the sink so callers can show <see cref="JsonLinesFileSink.CurrentFile"/>.
        /// </summary>
        public static JsonLinesFileSink Initialize(StoragePaths paths, string processName, LogLevel level = LogLevel.Info)
        {
            paths.EnsureCreated();
            Log.ProcessName = processName;
            Log.MinLevel = level;
            var sink = new JsonLinesFileSink(paths.Logs, processName);
            Log.AddSink(sink);
            Log.CaptureUnhandledExceptions();
            return sink;
        }
    }
}
