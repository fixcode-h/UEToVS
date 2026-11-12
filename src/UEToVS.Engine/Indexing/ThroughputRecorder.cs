using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// Throughput statistics of one pipeline stage (e.g. "parse.engine"): files/s, MB/s, per-file parse time
    /// percentiles (log-bucket histogram, no per-file allocation), the slowest files, I/O vs CPU share and thread
    /// utilization. Lock-free on the hot path except when a file enters the slowest-20 list. Logs
    /// <c>index.throughput</c> periodically while running and <c>index.done</c> when finished.
    /// </summary>
    public sealed class ThroughputRecorder : IDisposable
    {
        private static readonly Logger L = Log.For("Index");
        public const int SlowestCount = 20;

        // Histogram: bucket b covers [Lo(b), Lo(b+1)) microseconds with Lo(b) = 10 * 1.2^b; 80 buckets reach ~20 min.
        private const int Buckets = 80;
        private static readonly double[] BucketLowUs = Enumerable.Range(0, Buckets + 1).Select(b => 10 * Math.Pow(1.2, b)).ToArray();

        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly long[] _histogram = new long[Buckets + 1];
        private readonly object _slowGate = new object();
        private readonly List<SlowFile> _slowest = new List<SlowFile>();
        private readonly Func<int> _threads;
        private readonly Func<int> _configuredThreads;
        private readonly Timer _timer;
        private long _slowThresholdTicks;
        private long _end;
        private int _filesDone, _filesParsed, _cacheHits, _total;
        private long _bytes, _readTicks, _parseTicks, _applyTicks, _busyTicks, _availableTicks, _throttledTicks, _maxParseTicks;
        private int _segments;
        private long _segmentTicks;
        private int _lastLoggedDone;
        private long _lastLoggedTs;
        private long _lastLoggedBytes;

        public ThroughputRecorder(string stage, int total, Func<int> threads, Func<int> configuredThreads, TimeSpan? logInterval = null)
        {
            Stage = stage;
            _total = total;
            _threads = threads ?? (() => 1);
            _configuredThreads = configuredThreads ?? _threads;
            _lastLoggedTs = _start;
            var interval = logInterval ?? TimeSpan.FromSeconds(5);
            if (interval > TimeSpan.Zero) _timer = new Timer(_ => LogProgress(), null, interval, interval);
        }

        public string Stage { get; }
        public bool Running => Volatile.Read(ref _end) == 0;
        public int FilesDone => Volatile.Read(ref _filesDone);
        public int Total => Volatile.Read(ref _total);

        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public void AddTotal(int n) => Interlocked.Add(ref _total, n);

        /// <summary>A file whose cached data was reused (no read or parse).</summary>
        public void RecordCacheHit()
        {
            Interlocked.Increment(ref _cacheHits);
            Interlocked.Increment(ref _filesDone);
        }

        /// <summary>A file that was skipped or failed before parsing (counted as done).</summary>
        public void RecordOther(long readTicks = 0)
        {
            if (readTicks > 0) Interlocked.Add(ref _readTicks, readTicks);
            Interlocked.Increment(ref _filesDone);
        }

        /// <summary>A file that was read (and possibly only hashed: <paramref name="parsed"/> false when the content was unchanged).</summary>
        public void RecordFile(string path, long bytes, long readTicks, long parseTicks, long applyTicks, bool parsed)
        {
            Interlocked.Increment(ref _filesDone);
            if (parsed) Interlocked.Increment(ref _filesParsed);
            else Interlocked.Increment(ref _cacheHits);
            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Add(ref _readTicks, readTicks);
            Interlocked.Add(ref _parseTicks, parseTicks);
            Interlocked.Add(ref _applyTicks, applyTicks);
            if (!parsed) return;

            double us = parseTicks * 1_000_000.0 / Stopwatch.Frequency;
            Interlocked.Increment(ref _histogram[Bucket(us)]);
            long max;
            while (parseTicks > (max = Volatile.Read(ref _maxParseTicks)) && Interlocked.CompareExchange(ref _maxParseTicks, parseTicks, max) != max) { }

            long total = readTicks + parseTicks;
            if (total <= Volatile.Read(ref _slowThresholdTicks)) return;
            lock (_slowGate)
            {
                double ms = Ms(total);
                if (_slowest.Count >= SlowestCount && ms <= _slowest[_slowest.Count - 1].Ms) return;
                int i = 0;
                while (i < _slowest.Count && _slowest[i].Ms >= ms) i++;
                _slowest.Insert(i, new SlowFile { Path = path, Ms = Math.Round(ms, 2), Bytes = bytes });
                if (_slowest.Count > SlowestCount) _slowest.RemoveAt(_slowest.Count - 1);
                if (_slowest.Count == SlowestCount) Volatile.Write(ref _slowThresholdTicks, (long)(_slowest[_slowest.Count - 1].Ms * Stopwatch.Frequency / 1000.0));
            }
        }

        /// <summary>Worker thread time: busy (working on files) and available (allowed to run, busy or idle).</summary>
        public void RecordWorker(long busyTicks, long availableTicks, long throttledTicks)
        {
            Interlocked.Add(ref _busyTicks, busyTicks);
            Interlocked.Add(ref _availableTicks, availableTicks);
            Interlocked.Add(ref _throttledTicks, throttledTicks);
        }

        public void RecordThrottled(long ticks) => Interlocked.Add(ref _throttledTicks, ticks);

        public void RecordSegment(long ticks)
        {
            Interlocked.Increment(ref _segments);
            Interlocked.Add(ref _segmentTicks, ticks);
        }

        private static int Bucket(double us)
        {
            if (us < BucketLowUs[0]) return 0;
            int b = (int)(Math.Log(us / 10) / Math.Log(1.2));
            return Math.Clamp(b, 0, Buckets);
        }

        /// <summary>Percentile (0–1) of per-file parse time in ms, interpolated inside the bucket.</summary>
        public double ParsePercentileMs(double p)
        {
            long n = 0;
            for (int b = 0; b <= Buckets; b++) n += Volatile.Read(ref _histogram[b]);
            if (n == 0) return 0;
            double rank = p * (n - 1) + 1;
            long seen = 0;
            for (int b = 0; b <= Buckets; b++)
            {
                long c = Volatile.Read(ref _histogram[b]);
                if (c == 0) continue;
                if (seen + c >= rank)
                {
                    double lo = BucketLowUs[b], hi = b < Buckets ? BucketLowUs[b + 1] : lo * 1.2;
                    double frac = (rank - seen) / c;
                    double us = lo + (hi - lo) * frac;
                    return Math.Min(us / 1000.0, Ms(Volatile.Read(ref _maxParseTicks)));
                }
                seen += c;
            }
            return Ms(Volatile.Read(ref _maxParseTicks));
        }

        public void Complete()
        {
            if (Interlocked.CompareExchange(ref _end, Stopwatch.GetTimestamp(), 0) != 0) return;
            _timer?.Dispose();
            var t = ToContract();
            L.Info("index.done", null, Fields(t, done: true));
        }

        public void Dispose() => Complete();

        private double ElapsedMs
        {
            get
            {
                long end = Volatile.Read(ref _end);
                return Ms((end == 0 ? Stopwatch.GetTimestamp() : end) - _start);
            }
        }

        public IndexThroughput ToContract()
        {
            double elapsed = ElapsedMs;
            double read = Ms(Interlocked.Read(ref _readTicks)), parse = Ms(Interlocked.Read(ref _parseTicks)), apply = Ms(Interlocked.Read(ref _applyTicks));
            double busyWork = read + parse + apply;
            long available = Interlocked.Read(ref _availableTicks);
            long busy = Interlocked.Read(ref _busyTicks);
            long bytes = Interlocked.Read(ref _bytes);
            int done = FilesDone;
            var t = new IndexThroughput
            {
                Stage = Stage,
                Running = Running,
                StartedUtc = _startedUtc,
                ElapsedMs = Math.Round(elapsed, 1),
                Threads = SafeThreads(_threads),
                ConfiguredThreads = SafeThreads(_configuredThreads),
                FilesDone = done,
                FilesTotal = Total,
                FilesParsed = Volatile.Read(ref _filesParsed),
                CacheHits = Volatile.Read(ref _cacheHits),
                BytesRead = bytes,
                FilesPerSecond = elapsed > 0 ? Math.Round(done * 1000.0 / elapsed, 1) : 0,
                MegabytesPerSecond = elapsed > 0 ? Math.Round(bytes / 1048576.0 * 1000.0 / elapsed, 2) : 0,
                ParseP50Ms = Math.Round(ParsePercentileMs(0.5), 3),
                ParseP95Ms = Math.Round(ParsePercentileMs(0.95), 3),
                ParseMaxMs = Math.Round(Ms(Volatile.Read(ref _maxParseTicks)), 3),
                ReadMs = Math.Round(read, 1),
                ParseMs = Math.Round(parse, 1),
                ApplyMs = Math.Round(apply, 1),
                IoShare = busyWork > 0 ? Math.Round(read / busyWork, 3) : 0,
                ThreadUtilization = available > 0 ? Math.Round(Math.Min(1.0, (double)busy / available), 3) : 0,
                ThrottledMs = Math.Round(Ms(Interlocked.Read(ref _throttledTicks)), 1),
                SegmentsWritten = Volatile.Read(ref _segments),
                SegmentWriteMs = Math.Round(Ms(Interlocked.Read(ref _segmentTicks)), 1),
            };
            lock (_slowGate) t.SlowestFiles.AddRange(_slowest.Select(s => new SlowFile { Path = s.Path, Ms = s.Ms, Bytes = s.Bytes }));
            return t;
        }

        private static int SafeThreads(Func<int> f)
        {
            try { return f(); } catch (Exception) { return 0; }
        }

        /// <summary>Periodic <c>index.throughput</c> line with interval and overall rates.</summary>
        public void LogProgress()
        {
            if (!Running) return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                int done = FilesDone;
                long bytes = Interlocked.Read(ref _bytes);
                double intervalMs = Ms(now - Interlocked.Exchange(ref _lastLoggedTs, now));
                int prevDone = Interlocked.Exchange(ref _lastLoggedDone, done);
                long prevBytes = Interlocked.Exchange(ref _lastLoggedBytes, bytes);
                var t = ToContract();
                var fields = new List<(string, object)>(Fields(t, done: false))
                {
                    ("intervalFilesPerSec", intervalMs > 0 ? Math.Round((done - prevDone) * 1000.0 / intervalMs, 1) : 0),
                    ("intervalMBPerSec", intervalMs > 0 ? Math.Round((bytes - prevBytes) / 1048576.0 * 1000.0 / intervalMs, 2) : 0),
                };
                L.Info("index.throughput", null, fields.ToArray());
            }
            catch (Exception ex)
            {
                L.Warn("index.throughput.failed", "Throughput logging failed", ex);
            }
        }

        private static (string, object)[] Fields(IndexThroughput t, bool done)
        {
            var list = new List<(string, object)>
            {
                ("stage", t.Stage),
                ("done", t.FilesDone),
                ("total", t.FilesTotal),
                ("pct", t.FilesTotal > 0 ? Math.Round(t.FilesDone * 100.0 / t.FilesTotal, 1) : 100),
                ("parsed", t.FilesParsed),
                ("cacheHits", t.CacheHits),
                ("elapsedMs", t.ElapsedMs),
                ("filesPerSec", t.FilesPerSecond),
                ("mbPerSec", t.MegabytesPerSecond),
                ("mb", Math.Round(t.BytesRead / 1048576.0, 1)),
                ("parseP50Ms", t.ParseP50Ms),
                ("parseP95Ms", t.ParseP95Ms),
                ("parseMaxMs", t.ParseMaxMs),
                ("readMs", t.ReadMs),
                ("parseMs", t.ParseMs),
                ("applyMs", t.ApplyMs),
                ("ioShare", t.IoShare),
                ("threads", t.Threads),
                ("configuredThreads", t.ConfiguredThreads),
                ("utilization", t.ThreadUtilization),
                ("throttledMs", t.ThrottledMs),
                ("segments", t.SegmentsWritten),
                ("segmentWriteMs", t.SegmentWriteMs),
            };
            if (done) list.Add(("slowest", t.SlowestFiles.Select(s => $"{s.Ms:F1}ms {s.Bytes}B {s.Path}").ToArray()));
            return list.ToArray();
        }
    }
}
