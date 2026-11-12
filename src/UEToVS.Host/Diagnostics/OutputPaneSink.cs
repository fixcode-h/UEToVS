using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Diagnostics
{
    /// <summary>
    /// Human-readable mirror of the log in Output > "UEToVS" (Info and above, §10.5). Writes are queued and flushed in
    /// batches on the UI thread (at most every 200 ms), so logging from any thread never blocks or marshals per line.
    /// Never logs through <see cref="Log"/> itself (would recurse).
    /// </summary>
    internal sealed class OutputPaneSink : ILogSink
    {
        private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();
        private readonly JoinableTaskFactoryHolder _jtf;
        private IVsOutputWindowPane _pane;
        private int _scheduled;
        private int _queued;
        private int _dropped;
        private const int MaxQueued = 5000;

        public OutputPaneSink(Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf)
        {
            _jtf = new JoinableTaskFactoryHolder(jtf);
        }

        public LogLevel MinLevel { get; set; } = LogLevel.Info;

        /// <summary>Creates the pane; call on the UI thread.</summary>
        public void EnsurePane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_pane != null) return;
            var output = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (output == null) return;
            var guid = PackageGuids.OutputPane;
            output.CreatePane(ref guid, "UEToVS", 1, 1);
            output.GetPane(ref guid, out _pane);
        }

        public void Activate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsurePane();
            _pane?.Activate();
        }

        public void Write(LogEvent e)
        {
            if (e.Level < MinLevel) return;
            // ---- v0.4.5: UBT output is streamed verbatim through WriteRaw (Unreal/UnrealTools.cs); only the log file gets the events.
            if (string.Equals(e.Module, RawModule, StringComparison.Ordinal) && e.Event != null && e.Event.StartsWith("ubt.out", StringComparison.Ordinal)) return;
            if (string.Equals(e.Module, RawModule, StringComparison.Ordinal) && string.Equals(e.Event, "ubt.err", StringComparison.Ordinal)) return;
            if (Interlocked.Increment(ref _queued) > MaxQueued)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Increment(ref _dropped);
                return;
            }
            _pending.Enqueue(Format(e));
            Schedule();
        }

        /// <summary>Logger module whose ubt.out / ubt.err lines are written raw instead of formatted.</summary>
        public const string RawModule = "UBT";

        /// <summary>A line written as is (tool output such as UBT), in order with the log lines; any thread.</summary>
        public void WriteRaw(string line)
        {
            if (line == null) return;
            if (Interlocked.Increment(ref _queued) > MaxQueued)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Increment(ref _dropped);
                return;
            }
            _pending.Enqueue(line);
            Schedule();
        }

        /// <summary>One line: "12:01:02.123 WARN  [Engine] engine.exit: Engine exited (code=1, …)".</summary>
        internal static string Format(LogEvent e)
        {
            var sb = new StringBuilder(128);
            sb.Append(e.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ');
            sb.Append(LevelText(e.Level)).Append(" [").Append(e.Module).Append("] ");
            sb.Append(e.Event);
            if (!string.IsNullOrEmpty(e.Message)) sb.Append(": ").Append(e.Message);
            if (e.ElapsedMs.HasValue) sb.Append(" (").Append(e.ElapsedMs.Value.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms)");
            if (e.Data != null && e.Data.Count > 0)
            {
                sb.Append("  {");
                sb.Append(string.Join(", ", e.Data.Select(kv => kv.Key + "=" + Short(kv.Value))));
                sb.Append('}');
            }
            if (e.Exception != null) sb.Append("  ").Append(e.Exception.GetType().Name).Append(": ").Append(e.Exception.Message);
            return sb.ToString();
        }

        private static string Short(object v)
        {
            if (v == null) return "null";
            string s = v is System.Collections.IEnumerable en && !(v is string)
                ? "[" + string.Join(", ", en.Cast<object>().Take(8)) + "]"
                : Convert.ToString(v, CultureInfo.InvariantCulture);
            return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
        }

        private static string LevelText(LogLevel l)
        {
            switch (l)
            {
                case LogLevel.Error: return "ERROR";
                case LogLevel.Warn: return "WARN ";
                case LogLevel.Info: return "INFO ";
                case LogLevel.Debug: return "DEBUG";
                default: return "TRACE";
            }
        }

        private void Schedule()
        {
            if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
            var jtf = _jtf.Factory;
            if (jtf == null) return;
            _ = jtf.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    await jtf.SwitchToMainThreadAsync();
                    FlushOnUiThread();
                }
                catch
                {
                    // shutting down; nothing to report to (this IS the reporter)
                }
                finally
                {
                    Interlocked.Exchange(ref _scheduled, 0);
                    if (!_pending.IsEmpty) Schedule();
                }
            });
        }

        private void FlushOnUiThread()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsurePane();
            var sb = new StringBuilder();
            while (_pending.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _queued);
                sb.Append(line).Append(Environment.NewLine);
            }
            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0) sb.Append($"(… {dropped} lines dropped from the Output pane; see the log file)").Append(Environment.NewLine);
            if (sb.Length > 0) _pane?.OutputStringThreadSafe(sb.ToString());
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            _jtf.Factory = null;
        }

        /// <summary>Lets Dispose stop scheduling without races.</summary>
        private sealed class JoinableTaskFactoryHolder
        {
            public JoinableTaskFactoryHolder(Microsoft.VisualStudio.Threading.JoinableTaskFactory f) => Factory = f;
            public volatile Microsoft.VisualStudio.Threading.JoinableTaskFactory Factory;
        }
    }
}
