using System;
using System.Collections.Generic;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using UEToVS.Host.Logic.Diagnostics;

namespace UEToVS.Host.Diagnostics
{
    /// <summary>
    /// Host logging lifecycle (§10.5). Logs go to &lt;solution&gt;/.vs/UEToVS/Logs/host-*.log; before a UE solution is known
    /// (or for none) the per-user fallback StoragePaths.Fallback() is used.
    /// The first call goes through <see cref="LogSetup.Initialize"/> (file sink + unhandled-exception hooks); later
    /// solution switches only swap the file sink, so the process-wide exception hooks are installed exactly once.
    /// </summary>
    internal static class HostLog
    {
        private static readonly object Gate = new object();
        private static JsonLinesFileSink _file;
        private static bool _initialized;

        public static StoragePaths Paths { get; private set; }

        public static OutputPaneSink Pane { get; private set; }

        /// <summary>Current host log file (null before initialization).</summary>
        public static string CurrentFile => _file?.CurrentFile;

        public static string LogDirectory => Paths?.Logs;

        /// <summary>Attaches the Output-window sink once (Info+).</summary>
        public static void AttachOutputPane(OutputPaneSink pane)
        {
            lock (Gate)
            {
                if (Pane != null) return;
                Pane = pane;
                Log.AddSink(pane);
            }
        }

        /// <summary>
        /// Points the host log at <paramref name="paths"/>. Returns true when the target changed. Never throws: if the
        /// directory cannot be used, logging continues where it was and the failure is logged.
        /// </summary>
        public static bool Use(StoragePaths paths, LogLevel level, int retentionDays, int maxTotalMB)
        {
            lock (Gate)
            {
                if (_initialized && Paths != null && string.Equals(Paths.Root, paths.Root, StringComparison.OrdinalIgnoreCase))
                {
                    Log.MinLevel = level;
                    return false;
                }
                try
                {
                    if (!_initialized)
                    {
                        _file = LogSetup.Initialize(paths, "host", level);
                        _initialized = true;
                    }
                    else
                    {
                        paths.EnsureCreated();
                        var next = new JsonLinesFileSink(paths.Logs, "host");
                        var old = _file;
                        Log.AddSink(next);
                        _file = next;
                        if (old != null)
                        {
                            Log.RemoveSink(old);
                            old.Flush();
                            old.Dispose();
                        }
                        Log.MinLevel = level;
                    }
                    Paths = paths;
                    ApplyRetention(retentionDays, maxTotalMB);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.For("HostLog").Error("log.switch.failed", "Cannot open log directory", ex, ("dir", paths.Logs));
                    return false;
                }
            }
        }

        public static void ApplyRetention(int retentionDays, int maxTotalMB)
        {
            var file = _file;
            if (file == null || Paths == null) return;
            file.RetentionDays = Math.Max(1, retentionDays);
            file.MaxTotalBytes = Math.Max(1, maxTotalMB) * 1024L * 1024L;
            var deleted = LogRetention.Apply(Paths.Logs, file.RetentionDays, file.MaxTotalBytes, new List<string> { file.CurrentFile });
            if (deleted.Count > 0)
                Log.For("HostLog").Info("log.retention", "Deleted old log files", ("count", deleted.Count), ("days", retentionDays), ("maxMB", maxTotalMB));
        }

        public static void SetLevel(LogLevel level) => Log.MinLevel = level;

        public static void Shutdown()
        {
            lock (Gate)
            {
                Log.Flush();
                Log.Shutdown();
                _file = null;
                Pane = null;
            }
        }
    }
}
