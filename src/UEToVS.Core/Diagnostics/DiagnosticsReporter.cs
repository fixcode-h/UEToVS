using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace UEToVS.Core.Diagnostics
{
    /// <summary>
    /// Writes the session header and the periodic resource / latency lines that make a log file self-explanatory
    /// for a person or an AI reading it later:
    ///   session.start   — versions, OS, CPU, memory, paths, feature flags (once)
    ///   resource.sample — private bytes, working set, managed heap, GC counts, threads (every 60 s)
    ///   perf.summary    — count / errors / p50 / p95 / max per operation (every 5 min)
    /// Extra fields can be added through <see cref="ResourceExtras"/> (e.g. index sizes) and checked against budgets.
    /// </summary>
    public sealed class DiagnosticsReporter : IDisposable
    {
        private static readonly Logger L = Log.For("Diagnostics");
        private readonly Timer _resourceTimer;
        private readonly Timer _perfTimer;

        public DiagnosticsReporter(TimeSpan resourceInterval, TimeSpan perfInterval)
        {
            _resourceTimer = new Timer(_ => SafeSample(), null, resourceInterval, resourceInterval);
            _perfTimer = new Timer(_ => SafeSummary(), null, perfInterval, perfInterval);
        }

        /// <summary>Private-bytes budget; over it, resource.sample is logged as Warn.</summary>
        public long PrivateBytesBudget { get; set; } = long.MaxValue;

        /// <summary>Additional fields for resource.sample (called on a timer thread).</summary>
        public Func<IEnumerable<(string Key, object Value)>> ResourceExtras { get; set; }

        public static void WriteSessionHeader(string product, string version, IEnumerable<(string Key, object Value)> extra)
        {
            var p = Process.GetCurrentProcess();
            var data = new List<(string, object)>
            {
                ("product", product),
                ("version", version),
                ("pid", p.Id),
                ("os", RuntimeInformation.OSDescription),
                ("arch", RuntimeInformation.ProcessArchitecture.ToString()),
                ("runtime", RuntimeInformation.FrameworkDescription),
                ("cpus", Environment.ProcessorCount),
                ("is64", Environment.Is64BitProcess),
                ("culture", System.Globalization.CultureInfo.CurrentUICulture.Name),
                ("cmd", Environment.CommandLine.Length > 500 ? Environment.CommandLine.Substring(0, 500) : Environment.CommandLine),
                ("logLevel", Log.MinLevel.ToString()),
            };
            if (extra != null) data.AddRange(extra);
            L.Info("session.start", product + " " + version + " started", data.ToArray());
        }

        public void SafeSample()
        {
            try
            {
                var p = Process.GetCurrentProcess();
                p.Refresh();
                long priv = p.PrivateMemorySize64;
                var data = new List<(string, object)>
                {
                    ("privateMB", Mb(priv)),
                    ("workingSetMB", Mb(p.WorkingSet64)),
                    ("managedMB", Mb(GC.GetTotalMemory(false))),
                    ("gen0", GC.CollectionCount(0)),
                    ("gen1", GC.CollectionCount(1)),
                    ("gen2", GC.CollectionCount(2)),
                    ("threads", p.Threads.Count),
                    ("handles", p.HandleCount),
                    ("cpuSec", Math.Round(p.TotalProcessorTime.TotalSeconds, 1)),
                };
                var extras = ResourceExtras?.Invoke();
                if (extras != null) data.AddRange(extras);
                bool over = priv > PrivateBytesBudget;
                if (over) data.Add(("budgetMB", Mb(PrivateBytesBudget)));
                if (over) L.Warn("resource.over_budget", "Private memory above budget", data.ToArray());
                else L.Info("resource.sample", null, data.ToArray());
            }
            catch (Exception ex)
            {
                L.Warn("resource.sample.failed", "Resource sampling failed", ex);
            }
        }

        public void SafeSummary()
        {
            try
            {
                var rows = Log.Latency.SnapshotAndReset();
                if (rows.Count == 0) return;
                var table = rows.Select(r => new Dictionary<string, object>
                {
                    ["op"] = r.Key,
                    ["n"] = r.Count,
                    ["err"] = r.Errors,
                    ["p50"] = Math.Round(r.P50, 1),
                    ["p95"] = Math.Round(r.P95, 1),
                    ["max"] = Math.Round(r.Max, 1),
                }).ToList();
                L.Info("perf.summary", null, ("ops", table));
            }
            catch (Exception ex)
            {
                L.Warn("perf.summary.failed", "Perf summary failed", ex);
            }
        }

        private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);

        public void Dispose()
        {
            _resourceTimer.Dispose();
            _perfTimer.Dispose();
            SafeSummary();
        }
    }
}
