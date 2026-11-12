using System;
using System.Collections.Generic;
using System.Globalization;

namespace UEToVS.UI.Logic
{
    /// <summary>Number formatting and chart-axis math for the performance components (StatTile, LineChart, DataTable).</summary>
    public static class PerfMath
    {
        private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB" };

        /// <summary>1536 → "1.5 KB", 734003200 → "700 MB". Binary units (1024).</summary>
        public static string FormatBytes(double bytes)
        {
            if (double.IsNaN(bytes)) return "—";
            bool neg = bytes < 0;
            double v = Math.Abs(bytes);
            int u = 0;
            while (v >= 1024 && u < ByteUnits.Length - 1) { v /= 1024; u++; }
            string num = u == 0 ? v.ToString("0", CultureInfo.InvariantCulture)
                : v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture)
                : v >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture)
                : v.ToString("0.##", CultureInfo.InvariantCulture);
            return (neg ? "-" : "") + num + " " + ByteUnits[u];
        }

        /// <summary>0.42 → "0.42 ms", 1530 → "1.53 s", 125000 → "2m 05s".</summary>
        public static string FormatDuration(double ms)
        {
            if (double.IsNaN(ms) || ms < 0) return "—";
            if (ms < 1) return ms.ToString("0.##", CultureInfo.InvariantCulture) + " ms";
            if (ms < 1000) return ms.ToString(ms < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " ms";
            double s = ms / 1000;
            if (s < 60) return s.ToString(s < 10 ? "0.##" : "0.#", CultureInfo.InvariantCulture) + " s";
            int total = (int)Math.Round(s);
            int h = total / 3600, m = total / 60 % 60, sec = total % 60;
            return h > 0 ? string.Format(CultureInfo.InvariantCulture, "{0}h {1:00}m", h, m)
                         : string.Format(CultureInfo.InvariantCulture, "{0}m {1:00}s", m, sec);
        }

        /// <summary>12345 → "12,345"; ≥ 10M → "12.3M".</summary>
        public static string FormatCount(long n)
        {
            if (Math.Abs(n) >= 10_000_000) return (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            return n.ToString("#,0", CultureInfo.InvariantCulture);
        }

        public static string FormatPercent(double fraction) =>
            double.IsNaN(fraction) ? "—" : (fraction * 100).ToString(fraction < 0.1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

        /// <summary>
        /// "Nice" axis: expands [min,max] to round numbers with about <paramref name="maxTicks"/> ticks (1/2/5 × 10^n steps).
        /// </summary>
        public static (double Min, double Max, double Step) NiceAxis(double min, double max, int maxTicks = 4)
        {
            if (double.IsNaN(min) || double.IsNaN(max)) { min = 0; max = 1; }
            if (max < min) { var t = min; min = max; max = t; }
            if (max - min < 1e-12) { max = min + (Math.Abs(min) > 1e-12 ? Math.Abs(min) * 0.1 : 1); }
            maxTicks = Math.Max(1, maxTicks);
            double range = NiceNumber(max - min, false);
            double step = NiceNumber(range / maxTicks, true);
            double nmin = Math.Floor(min / step) * step;
            double nmax = Math.Ceiling(max / step) * step;
            return (nmin, nmax, step);
        }

        private static double NiceNumber(double range, bool round)
        {
            double exp = Math.Floor(Math.Log10(range));
            double f = range / Math.Pow(10, exp);
            double nf;
            if (round) nf = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
            else nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
            return nf * Math.Pow(10, exp);
        }

        /// <summary>
        /// Stable sort permutation: returns row indices ordered by <paramref name="compare"/> (ascending or descending).
        /// The data itself is not touched, so a virtualized table can map view row → data row cheaply.
        /// </summary>
        public static int[] SortPermutation(int count, Comparison<int> compare, bool descending)
        {
            var idx = new int[count];
            for (int i = 0; i < count; i++) idx[i] = i;
            if (compare == null || count < 2) return idx;
            // Array.Sort is unstable: tie-break by original index.
            Array.Sort(idx, (a, b) =>
            {
                int c = compare(a, b);
                if (descending) c = -c;
                return c != 0 ? c : a.CompareTo(b);
            });
            return idx;
        }
    }

    /// <summary>Fixed-capacity ring buffer of samples (chart series). Index 0 is the oldest retained sample.</summary>
    public sealed class SampleBuffer
    {
        private readonly double[] _data;
        private int _start;

        public SampleBuffer(int capacity)
        {
            _data = new double[Math.Max(1, capacity)];
        }

        public int Capacity => _data.Length;
        public int Count { get; private set; }

        public double this[int i] => _data[(_start + i) % _data.Length];

        public void Add(double v)
        {
            if (Count < _data.Length)
            {
                _data[(_start + Count) % _data.Length] = v;
                Count++;
            }
            else
            {
                _data[_start] = v;
                _start = (_start + 1) % _data.Length;
            }
        }

        public void Clear()
        {
            _start = 0;
            Count = 0;
        }

        public void SetAll(IReadOnlyList<double> values)
        {
            Clear();
            if (values == null) return;
            int from = Math.Max(0, values.Count - _data.Length);
            for (int i = from; i < values.Count; i++) Add(values[i]);
        }

        public (double Min, double Max) Range()
        {
            if (Count == 0) return (0, 0);
            double mn = double.MaxValue, mx = double.MinValue;
            for (int i = 0; i < Count; i++)
            {
                double v = this[i];
                if (double.IsNaN(v)) continue;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            return mn > mx ? (0, 0) : (mn, mx);
        }
    }
}
