using System;
using System.Collections.Concurrent;
using System.Linq;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Tests
{
    /// <summary>Collects log events while alive (process-wide: filter by event name).</summary>
    internal sealed class CapturingLogSink : ILogSink
    {
        public readonly ConcurrentQueue<LogEvent> Events = new ConcurrentQueue<LogEvent>();

        public CapturingLogSink() => Log.AddSink(this);

        public void Write(LogEvent e) => Events.Enqueue(e);

        public void Flush()
        {
        }

        public string Dump(Func<LogEvent, bool> filter = null) =>
            string.Join("\n", Events.Where(filter ?? (_ => true)).Select(e =>
                $"{e.Level} {e.Event} {e.Message} {string.Join(" ", e.Data?.Select(kv => kv.Key + "=" + Format(kv.Value)) ?? Array.Empty<string>())} {e.Exception}"));

        private static string Format(object v) => v is Array a ? "[" + string.Join(",", a.Cast<object>()) + "]" : v?.ToString();

        public void Dispose() => Log.RemoveSink(this);
    }
}
