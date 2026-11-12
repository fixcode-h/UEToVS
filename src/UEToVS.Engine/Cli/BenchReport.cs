using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UEToVS.Engine.Cli
{
    /// <summary>Latency and result statistics of one operation over all samples.</summary>
    public sealed class BenchOpStats
    {
        public string Op { get; set; }
        public int Count { get; set; }
        public int Empty { get; set; }
        public int Errors { get; set; }
        public double P50 { get; set; }
        public double P95 { get; set; }
        public double P99 { get; set; }
        public double Mean { get; set; }
        public double Max { get; set; }

        /// <summary>Share of samples (0..1) that returned nothing (errors count as empty).</summary>
        public double EmptyRatio => Count == 0 ? 0 : Math.Round((double)(Empty + Errors) / Count, 4);

        public static BenchOpStats From(string op, IReadOnlyList<double> ms, int empty, int errors)
        {
            var sorted = ms.OrderBy(x => x).ToArray();
            return new BenchOpStats
            {
                Op = op,
                Count = sorted.Length,
                Empty = empty,
                Errors = errors,
                P50 = Percentile(sorted, 0.50),
                P95 = Percentile(sorted, 0.95),
                P99 = Percentile(sorted, 0.99),
                Mean = sorted.Length == 0 ? 0 : Math.Round(sorted.Average(), 3),
                Max = sorted.Length == 0 ? 0 : Math.Round(sorted[sorted.Length - 1], 3),
            };
        }

        /// <summary>Nearest-rank percentile of an ascending array (0 when empty).</summary>
        public static double Percentile(double[] sorted, double p)
        {
            if (sorted == null || sorted.Length == 0) return 0;
            int rank = (int)Math.Ceiling(p * sorted.Length);
            return Math.Round(sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)], 3);
        }

        public JsonObject ToJson() => new JsonObject
        {
            ["count"] = Count, ["p50"] = P50, ["p95"] = P95, ["p99"] = P99, ["mean"] = Mean, ["max"] = Max,
            ["empty"] = Empty, ["errors"] = Errors, ["emptyRatio"] = EmptyRatio,
        };
    }

    /// <summary>
    /// One benchmark run as a JSON line of &lt;solution&gt;\.vs\UEToVS\bench\bench.jsonl, and the comparison with the
    /// previous run of the same project.
    /// </summary>
    public sealed class BenchReport
    {
        public const string FileName = "bench.jsonl";

        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string EngineVersion { get; set; }
        public string Project { get; set; }
        public int Seed { get; set; }
        public int Samples { get; set; }
        public bool Reindexed { get; set; }

        /// <summary>An index was on disk and was loaded (not --reindex).</summary>
        public bool IndexReused { get; set; }
        public double IndexMs { get; set; }
        public int FilesParsed { get; set; }
        public int Files { get; set; }
        public long Symbols { get; set; }
        public double PeakWorkingSetMb { get; set; }
        public double PeakPrivateMb { get; set; }
        public double ManagedHeapMb { get; set; }
        public double TotalMs { get; set; }
        public List<BenchOpStats> Ops { get; } = new List<BenchOpStats>();

        public string ToJsonLine()
        {
            var ops = new JsonObject();
            foreach (var o in Ops) ops[o.Op] = o.ToJson();
            var o2 = new JsonObject
            {
                ["event"] = "bench.result",
                ["timestampUtc"] = TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
                ["engineVersion"] = EngineVersion,
                ["project"] = Project,
                ["seed"] = Seed,
                ["samples"] = Samples,
                ["reindexed"] = Reindexed,
                ["index"] = new JsonObject { ["ms"] = Math.Round(IndexMs, 1), ["reused"] = IndexReused, ["filesParsed"] = FilesParsed, ["files"] = Files, ["symbols"] = Symbols },
                ["memory"] = new JsonObject
                {
                    ["peakWorkingSetMB"] = Math.Round(PeakWorkingSetMb, 1), ["peakPrivateMB"] = Math.Round(PeakPrivateMb, 1),
                    ["managedHeapMB"] = Math.Round(ManagedHeapMb, 1),
                },
                ["ops"] = ops,
                ["totalMs"] = Math.Round(TotalMs, 1),
            };
            return o2.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>Parses a line written by <see cref="ToJsonLine"/>; null for anything else.</summary>
        public static BenchReport Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                var n = JsonNode.Parse(line) as JsonObject;
                if (n == null || (string)n["event"] != "bench.result") return null;
                var r = new BenchReport
                {
                    TimestampUtc = DateTime.Parse((string)n["timestampUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    EngineVersion = (string)n["engineVersion"],
                    Project = (string)n["project"],
                    Seed = (int?)n["seed"] ?? 0,
                    Samples = (int?)n["samples"] ?? 0,
                    Reindexed = (bool?)n["reindexed"] ?? false,
                    IndexMs = (double?)n["index"]?["ms"] ?? 0,
                    IndexReused = (bool?)n["index"]?["reused"] ?? false,
                    FilesParsed = (int?)n["index"]?["filesParsed"] ?? 0,
                    Files = (int?)n["index"]?["files"] ?? 0,
                    Symbols = (long?)n["index"]?["symbols"] ?? 0,
                    PeakWorkingSetMb = (double?)n["memory"]?["peakWorkingSetMB"] ?? 0,
                    PeakPrivateMb = (double?)n["memory"]?["peakPrivateMB"] ?? 0,
                    ManagedHeapMb = (double?)n["memory"]?["managedHeapMB"] ?? 0,
                    TotalMs = (double?)n["totalMs"] ?? 0,
                };
                if (n["ops"] is JsonObject ops)
                    foreach (var kv in ops)
                    {
                        var v = kv.Value;
                        r.Ops.Add(new BenchOpStats
                        {
                            Op = kv.Key, Count = (int?)v?["count"] ?? 0, Empty = (int?)v?["empty"] ?? 0, Errors = (int?)v?["errors"] ?? 0,
                            P50 = (double?)v?["p50"] ?? 0, P95 = (double?)v?["p95"] ?? 0, P99 = (double?)v?["p99"] ?? 0,
                            Mean = (double?)v?["mean"] ?? 0, Max = (double?)v?["max"] ?? 0,
                        });
                    }
                return r;
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>The newest earlier run of the same project in <paramref name="file"/> (null when none).</summary>
        public static BenchReport LoadPrevious(string file, string project)
        {
            if (!File.Exists(file)) return null;
            BenchReport last = null;
            foreach (var line in File.ReadLines(file))
            {
                var r = Parse(line);
                if (r != null && string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase)) last = r;
            }
            return last;
        }

        /// <summary>Human-readable table of this run (and the change against <paramref name="previous"/>).</summary>
        public string Format(BenchReport previous)
        {
            var sb = new StringBuilder();
            string P(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            sb.AppendLine($"project:  {Project}");
            sb.AppendLine($"samples:  {Samples} (seed {Seed}){(previous != null ? $"   previous: {previous.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm} v{previous.EngineVersion}" : string.Empty)}");
            sb.AppendLine($"index:    {P(IndexMs / 1000)} s, {Files} files, {Symbols} symbols, {FilesParsed} processed{(Reindexed ? " (rebuilt)" : IndexReused ? " (reused)" : " (new)")}" +
                          Delta(previous?.IndexMs, IndexMs));
            sb.AppendLine($"memory:   peak working set {P(PeakWorkingSetMb)} MB{Delta(previous?.PeakWorkingSetMb, PeakWorkingSetMb)}, peak private {P(PeakPrivateMb)} MB, managed heap {P(ManagedHeapMb)} MB");
            sb.AppendLine();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-12} {1,7} {2,18} {3,18} {4,18} {5,16}", "op", "count", "p50 ms", "p95 ms", "p99 ms", "empty"));
            foreach (var o in Ops)
            {
                var p = previous?.Ops.FirstOrDefault(x => x.Op == o.Op);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-12} {1,7} {2,18} {3,18} {4,18} {5,16}", o.Op, o.Count,
                    P(o.P50) + Delta(p?.P50, o.P50), P(o.P95) + Delta(p?.P95, o.P95), P(o.P99) + Delta(p?.P99, o.P99),
                    (o.EmptyRatio * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" + RatioDelta(p?.EmptyRatio, o.EmptyRatio)));
            }
            return sb.ToString();
        }

        /// <summary>" (+12%)" / " (-3%)"; a "!" marks a slowdown of more than 10%. Empty without a previous value.</summary>
        public static string Delta(double? before, double now)
        {
            if (before == null || before.Value <= 0) return string.Empty;
            double pct = (now - before.Value) / before.Value * 100;
            string s = (pct >= 0 ? "+" : "") + pct.ToString("0", CultureInfo.InvariantCulture) + "%";
            return " (" + s + (pct > 10 ? "!" : string.Empty) + ")";
        }

        private static string RatioDelta(double? before, double now)
        {
            if (before == null) return string.Empty;
            double pts = (now - before.Value) * 100;
            if (Math.Abs(pts) < 0.05) return " (=)";
            return " (" + (pts >= 0 ? "+" : "") + pts.ToString("0.0", CultureInfo.InvariantCulture) + "pt" + (pts > 1 ? "!" : string.Empty) + ")";
        }
    }
}
