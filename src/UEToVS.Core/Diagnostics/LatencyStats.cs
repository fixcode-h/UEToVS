using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Core.Diagnostics
{
    /// <summary>
    /// Per-operation latency aggregation for the periodic "perf.summary" log line (count, errors, p50, p95, max).
    /// Keeps a bounded reservoir per key, so memory is constant regardless of traffic.
    /// </summary>
    public sealed class LatencyStats
    {
        private const int Reservoir = 512;
        private readonly object _gate = new object();
        private Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        private readonly Random _rng = new Random(1);

        private sealed class Bucket
        {
            public long Count;
            public long Errors;
            public double Max;
            public double Total;
            public readonly double[] Samples = new double[Reservoir];
            public int Filled;
        }

        public sealed class Summary
        {
            public string Key;
            public long Count;
            public long Errors;
            public double P50;
            public double P95;
            public double Max;
            public double Mean;
        }

        public void Record(string key, double ms, bool failed = false)
        {
            lock (_gate)
            {
                if (!_buckets.TryGetValue(key, out var b)) _buckets[key] = b = new Bucket();
                b.Count++;
                if (failed) b.Errors++;
                b.Total += ms;
                if (ms > b.Max) b.Max = ms;
                if (b.Filled < Reservoir) b.Samples[b.Filled++] = ms;
                else
                {
                    long r = (long)(_rng.NextDouble() * b.Count);
                    if (r < Reservoir) b.Samples[r] = ms;
                }
            }
        }

        /// <summary>Returns the summaries since the last call and resets the counters.</summary>
        public List<Summary> SnapshotAndReset()
        {
            Dictionary<string, Bucket> old;
            lock (_gate)
            {
                old = _buckets;
                _buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            }
            return Summarize(old);
        }

        /// <summary>Returns the current summaries without resetting.</summary>
        public List<Summary> Snapshot()
        {
            lock (_gate) return Summarize(_buckets);
        }

        private static List<Summary> Summarize(Dictionary<string, Bucket> old)
        {
            var result = new List<Summary>(old.Count);
            foreach (var kv in old.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var b = kv.Value;
                var s = new double[b.Filled];
                Array.Copy(b.Samples, s, b.Filled);
                Array.Sort(s);
                result.Add(new Summary
                {
                    Key = kv.Key,
                    Count = b.Count,
                    Errors = b.Errors,
                    P50 = Percentile(s, 0.50),
                    P95 = Percentile(s, 0.95),
                    Max = b.Max,
                    Mean = b.Count == 0 ? 0 : b.Total / b.Count,
                });
            }
            return result;
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, idx))];
        }
    }
}
