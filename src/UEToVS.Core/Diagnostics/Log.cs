using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace UEToVS.Core.Diagnostics
{
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        Off = 5,
    }

    /// <summary>One structured log record. Serialized as one JSON line (see <see cref="JsonLineFormatter"/>).</summary>
    public sealed class LogEvent
    {
        public DateTime Timestamp;
        public LogLevel Level;
        public string Process;
        public string Module;

        /// <summary>Stable machine-readable event id, e.g. "rpc.search" or "index.parse.done".</summary>
        public string Event;

        public string Message;
        public string RequestId;
        public double? ElapsedMs;
        public IReadOnlyList<KeyValuePair<string, object>> Data;
        public Exception Exception;
        public int ThreadId;
    }

    public interface ILogSink : IDisposable
    {
        void Write(LogEvent e);
        void Flush();
    }

    /// <summary>
    /// Process-wide logging hub. Cheap when a level is disabled; never throws into the caller.
    /// Usage: <c>static readonly Logger L = Log.For("Index");</c> then <c>L.Info("index.ready", "...", ("files", n));</c>
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static ILogSink[] _sinks = Array.Empty<ILogSink>();

        public static LogLevel MinLevel { get; set; } = LogLevel.Info;

        /// <summary>"host" or "engine"; written on every line.</summary>
        public static string ProcessName { get; set; } = "unknown";

        /// <summary>Per-request latency aggregation; fed by <see cref="Logger.Time"/>.</summary>
        public static LatencyStats Latency { get; } = new LatencyStats();

        /// <summary>Same data as <see cref="Latency"/> but never reset (performance panel: "since start").</summary>
        public static LatencyStats LatencySinceStart { get; } = new LatencyStats();

        public static Logger For(string module) => new Logger(module);

        public static void AddSink(ILogSink sink)
        {
            lock (Gate)
            {
                var list = new List<ILogSink>(_sinks) { sink };
                _sinks = list.ToArray();
            }
        }

        public static void RemoveSink(ILogSink sink)
        {
            lock (Gate)
            {
                var list = new List<ILogSink>(_sinks);
                list.Remove(sink);
                _sinks = list.ToArray();
            }
        }

        public static bool IsEnabled(LogLevel level) => level >= MinLevel && MinLevel != LogLevel.Off;

        public static bool TryParseLevel(string text, out LogLevel level) =>
            Enum.TryParse(text ?? string.Empty, true, out level);

        public static void Write(LogEvent e)
        {
            var sinks = _sinks;
            for (int i = 0; i < sinks.Length; i++)
            {
                try { sinks[i].Write(e); }
                catch { /* a broken sink must never break the product */ }
            }
        }

        public static void Flush()
        {
            foreach (var s in _sinks)
            {
                try { s.Flush(); } catch { }
            }
        }

        /// <summary>Hooks AppDomain/TaskScheduler so nothing fails silently. Call once per process.</summary>
        public static void CaptureUnhandledExceptions()
        {
            var l = For("Process");
            AppDomain.CurrentDomain.UnhandledException += (s, a) =>
            {
                l.Error("process.unhandled", "Unhandled exception (terminating=" + a.IsTerminating + ")", a.ExceptionObject as Exception);
                Flush();
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, a) =>
            {
                l.Error("process.unobserved_task", "Unobserved task exception", a.Exception);
                a.SetObserved();
            };
        }

        public static void Shutdown()
        {
            ILogSink[] sinks;
            lock (Gate)
            {
                sinks = _sinks;
                _sinks = Array.Empty<ILogSink>();
            }
            foreach (var s in sinks)
            {
                try { s.Flush(); s.Dispose(); } catch { }
            }
        }

        private static readonly AsyncLocal<string> CurrentRequest = new AsyncLocal<string>();

        /// <summary>Request id flowing with the async context (set by RPC handlers, attached to every log line).</summary>
        public static string RequestId
        {
            get => CurrentRequest.Value;
            set => CurrentRequest.Value = value;
        }
    }

    /// <summary>Module-scoped logger.</summary>
    public sealed class Logger
    {
        internal Logger(string module) => Module = module;

        public string Module { get; }

        public void Trace(string evt, string message, params (string Key, object Value)[] data) => Write(LogLevel.Trace, evt, message, null, null, data);
        public void Debug(string evt, string message, params (string Key, object Value)[] data) => Write(LogLevel.Debug, evt, message, null, null, data);
        public void Info(string evt, string message, params (string Key, object Value)[] data) => Write(LogLevel.Info, evt, message, null, null, data);
        public void Warn(string evt, string message, params (string Key, object Value)[] data) => Write(LogLevel.Warn, evt, message, null, null, data);
        public void Warn(string evt, string message, Exception ex, params (string Key, object Value)[] data) => Write(LogLevel.Warn, evt, message, ex, null, data);
        public void Error(string evt, string message, Exception ex, params (string Key, object Value)[] data) => Write(LogLevel.Error, evt, message, ex, null, data);

        public void Write(LogLevel level, string evt, string message, Exception ex, double? elapsedMs, (string Key, object Value)[] data)
        {
            if (!Log.IsEnabled(level)) return;
            KeyValuePair<string, object>[] kv = null;
            if (data != null && data.Length > 0)
            {
                kv = new KeyValuePair<string, object>[data.Length];
                for (int i = 0; i < data.Length; i++) kv[i] = new KeyValuePair<string, object>(data[i].Key, data[i].Value);
            }
            Log.Write(new LogEvent
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Process = Log.ProcessName,
                Module = Module,
                Event = evt,
                Message = message,
                RequestId = Log.RequestId,
                ElapsedMs = elapsedMs,
                Data = kv,
                Exception = ex,
                ThreadId = Environment.CurrentManagedThreadId,
            });
        }

        /// <summary>
        /// Times a block. Always records latency stats; logs Debug normally and Warn when over <paramref name="budgetMs"/>.
        /// <c>using (L.Time("rpc.search", 30, ("pattern", p))) { ... }</c>
        /// </summary>
        public PerfScope Time(string evt, double budgetMs = double.MaxValue, params (string Key, object Value)[] data) =>
            new PerfScope(this, evt, budgetMs, data);
    }

    public sealed class PerfScope : IDisposable
    {
        private readonly Logger _logger;
        private readonly string _evt;
        private readonly double _budgetMs;
        private readonly (string Key, object Value)[] _data;
        private readonly long _start;
        private bool _failed;
        private bool _disposed;

        internal PerfScope(Logger logger, string evt, double budgetMs, (string Key, object Value)[] data)
        {
            _logger = logger;
            _evt = evt;
            _budgetMs = budgetMs;
            _data = data;
            _start = Stopwatch.GetTimestamp();
        }

        public double ElapsedMs => (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Marks the scope as failed (counted as an error in the periodic summary).</summary>
        public void Fail() => _failed = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            double ms = ElapsedMs;
            string key = _logger.Module + "/" + _evt;
            Log.Latency.Record(key, ms, _failed);
            Log.LatencySinceStart.Record(key, ms, _failed);
            bool over = ms > _budgetMs;
            var level = over ? LogLevel.Warn : LogLevel.Debug;
            if (!Log.IsEnabled(level)) return;
            string msg = over ? $"{_evt} took {ms:F1} ms (budget {_budgetMs:F0} ms)" : null;
            _logger.Write(level, over ? _evt + ".slow" : _evt, msg, null, ms, _data);
        }
    }
}
