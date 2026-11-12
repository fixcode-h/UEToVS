using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// Wall time, process CPU time and item counts per index phase, logged as <c>index.phase.done</c> and kept
    /// (most recent first, bounded) for the performance panel. CPU is the process-wide CPU delta during the phase.
    /// </summary>
    public sealed class PhaseRecorder
    {
        private static readonly Logger L = Log.For("Index");
        private const int MaxEntries = 40;

        private readonly object _gate = new object();
        private readonly List<PhaseTiming> _history = new List<PhaseTiming>();

        public sealed class Scope : IDisposable
        {
            private readonly PhaseRecorder _owner;
            private readonly long _start = Stopwatch.GetTimestamp();
            private readonly TimeSpan _cpuStart = ProcessCpu();
            private readonly List<(string Key, object Value)> _data = new List<(string, object)>();
            private bool _done;

            internal Scope(PhaseRecorder owner, IndexPhase phase, string stage)
            {
                _owner = owner;
                Stage = stage;
                Timing = new PhaseTiming { Phase = phase, StartedUtc = DateTime.UtcNow, Running = true, Detail = stage };
            }

            public PhaseTiming Timing { get; }
            public string Stage { get; }

            public int Items
            {
                get => Timing.Items;
                set => Timing.Items = value;
            }

            /// <summary>Adds a key=value to the log line and the panel's detail text.</summary>
            public void Add(string key, object value) => _data.Add((key, value));

            public void Dispose() => Complete(failed: false);

            public void Complete(bool failed)
            {
                if (_done) return;
                _done = true;
                Timing.DurationMs = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
                Timing.CpuMs = (ProcessCpu() - _cpuStart).TotalMilliseconds;
                Timing.Running = false;
                var parts = new List<string>();
                if (Stage != null) parts.Add(Stage);
                parts.AddRange(_data.Select(d => d.Key + "=" + d.Value));
                Timing.Detail = string.Join(" ", parts);
                var data = new List<(string, object)>
                {
                    ("phase", Timing.Phase.ToString()),
                    ("stage", Stage),
                    ("items", Timing.Items),
                    ("cpuMs", Math.Round(Timing.CpuMs, 1)),
                    ("aborted", failed),
                };
                data.AddRange(_data);
                L.Write(failed ? LogLevel.Warn : LogLevel.Info, "index.phase.done", null, null, Math.Round(Timing.DurationMs, 1), data.ToArray());
            }
        }

        public Scope Begin(IndexPhase phase, string stage = null)
        {
            var s = new Scope(this, phase, stage);
            lock (_gate)
            {
                _history.Insert(0, s.Timing);
                if (_history.Count > MaxEntries) _history.RemoveAt(_history.Count - 1);
            }
            L.Info("index.phase.start", null, ("phase", phase.ToString()), ("stage", stage));
            return s;
        }

        /// <summary>Copies (running phases get their current duration).</summary>
        public List<PhaseTiming> Snapshot()
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                return _history.Select(t => new PhaseTiming
                {
                    Phase = t.Phase,
                    StartedUtc = t.StartedUtc,
                    DurationMs = t.Running ? (now - t.StartedUtc).TotalMilliseconds : t.DurationMs,
                    Running = t.Running,
                    Items = t.Items,
                    CpuMs = t.CpuMs,
                    Detail = t.Detail,
                }).ToList();
            }
        }

        private static TimeSpan ProcessCpu()
        {
            try { return Process.GetCurrentProcess().TotalProcessorTime; }
            catch (Exception) { return TimeSpan.Zero; }
        }
    }
}
