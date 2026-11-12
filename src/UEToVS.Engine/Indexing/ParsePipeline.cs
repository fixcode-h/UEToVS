using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Indexing
{
    /// <summary>Per worker thread state: a reusable read buffer (no LOH allocation per file) and the thread's index.</summary>
    public sealed class WorkerState
    {
        private byte[] _buffer = new byte[64 * 1024];

        public WorkerState(int index) => Index = index;

        public int Index { get; }

        /// <summary>
        /// Reads a whole file into the worker's buffer with one sequential read (no FileStream buffering). Returns the
        /// byte count, or -1 when the file is larger than <paramref name="maxBytes"/>. The memory is valid until the next call.
        /// </summary>
        public int ReadFile(string path, long maxBytes, out ReadOnlyMemory<byte> content)
        {
            content = ReadOnlyMemory<byte>.Empty;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            long len = fs.Length;
            if (len > maxBytes) return -1;
            if (_buffer.Length < len + 1)
            {
                long size = _buffer.Length;
                while (size < len + 1) size *= 2;
                _buffer = new byte[Math.Min(size, maxBytes + 1)];
            }
            int total = 0;
            while (total < _buffer.Length)
            {
                int n = fs.Read(_buffer, total, _buffer.Length - total);
                if (n <= 0) break;
                total += n;
            }
            if (total > maxBytes) return -1; // grew while reading
            content = new ReadOnlyMemory<byte>(_buffer, 0, total);
            return total;
        }
    }

    /// <summary>
    /// Runs a pre-ordered list of items on dedicated worker threads whose number and priority follow a
    /// <see cref="WorkerThrottle"/> (configured threads, build policy, user pause) and change while running. Items are
    /// claimed in small batches from a shared cursor (one interlocked add per batch, no queue, no per-item task), so
    /// the order of the list is the processing order (priority) and scheduling overhead stays negligible.
    /// </summary>
    public static class ParsePipeline
    {
        private static readonly Logger L = Log.For("Index");
        public const int ClaimSize = 4;

        public static Task RunAsync<T>(IReadOnlyList<T> items, WorkerThrottle throttle, ThroughputRecorder recorder, Action<T, WorkerState, CancellationToken> process,
            string threadName, CancellationToken ct)
        {
            if (items.Count == 0) return Task.CompletedTask;
            var run = new Run<T>(items, throttle, recorder, process, threadName, ct);
            return run.Start();
        }

        private sealed class Run<T>
        {
            private readonly IReadOnlyList<T> _items;
            private readonly WorkerThrottle _throttle;
            private readonly ThroughputRecorder _rec;
            private readonly Action<T, WorkerState, CancellationToken> _process;
            private readonly string _threadName;
            private readonly CancellationTokenSource _cts;

            /// <summary>Cancelled when every item is claimed (or the run is cancelled): parked workers stop waiting and exit.</summary>
            private readonly CancellationTokenSource _exhausted;
            private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _gate = new object();
            private int _next;
            private int _threads;
            private int _alive;
            private Exception _error;

            public Run(IReadOnlyList<T> items, WorkerThrottle throttle, ThroughputRecorder rec, Action<T, WorkerState, CancellationToken> process, string threadName, CancellationToken ct)
            {
                _items = items;
                _throttle = throttle;
                _rec = rec;
                _process = process;
                _threadName = threadName;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _exhausted = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            }

            public Task Start()
            {
                _throttle.Changed += OnThrottleChanged;
                EnsureThreads(Math.Min(_throttle.Configured, _items.Count));
                return _done.Task;
            }

            private void OnThrottleChanged()
            {
                if (Volatile.Read(ref _next) >= _items.Count) return;
                EnsureThreads(Math.Min(_throttle.Configured, _items.Count));
            }

            private void EnsureThreads(int wanted)
            {
                lock (_gate)
                {
                    if (_done.Task.IsCompleted) return;
                    while (_threads < wanted)
                    {
                        int index = _threads++;
                        _alive++;
                        var t = new Thread(() => Worker(index)) { IsBackground = true, Name = $"{_threadName} {index}", Priority = _throttle.Priority };
                        t.Start();
                    }
                }
            }

            private void Worker(int index)
            {
                var state = new WorkerState(index);
                long busy = 0, throttled = 0;
                long life = Stopwatch.GetTimestamp();
                var ct = _cts.Token;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        long waited;
                        try
                        {
                            waited = _throttle.WaitForTurn(index, _exhausted.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            break; // all items taken by other workers while this one was parked
                        }
                        throttled += waited;
                        SetPriority(_throttle.Priority);
                        int start = Interlocked.Add(ref _next, ClaimSize) - ClaimSize;
                        if (start >= _items.Count)
                        {
                            _exhausted.Cancel();
                            break;
                        }
                        int end = Math.Min(start + ClaimSize, _items.Count);
                        long t0 = Stopwatch.GetTimestamp();
                        for (int i = start; i < end; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            _process(_items[i], state, ct);
                        }
                        busy += Stopwatch.GetTimestamp() - t0;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    L.Error("index.pipeline.worker_failed", "Index worker failed", ex, ("thread", _threadName), ("worker", index));
                    lock (_gate) _error ??= ex;
                    _cts.Cancel();
                }
                finally
                {
                    _rec?.RecordWorker(busy, Math.Max(0, Stopwatch.GetTimestamp() - life - throttled), throttled);
                    bool last;
                    lock (_gate) last = --_alive == 0;
                    if (last) Finish();
                }
            }

            private void Finish()
            {
                _throttle.Changed -= OnThrottleChanged;
                Exception error;
                lock (_gate) error = _error;
                if (error != null) _done.TrySetException(error);
                else if (_cts.IsCancellationRequested) _done.TrySetCanceled();
                else _done.TrySetResult(true);
                _exhausted.Dispose();
                _cts.Dispose();
            }

            private static void SetPriority(ThreadPriority p)
            {
                try
                {
                    if (Thread.CurrentThread.Priority != p) Thread.CurrentThread.Priority = p;
                }
                catch (Exception)
                {
                    // not permitted on some platforms
                }
            }
        }
    }

    /// <summary>
    /// Collects parse results into batches per kind (project / engine) and hands full batches to segment writer threads
    /// that turn each into a base segment, so writing never blocks parsing. With an <see cref="IParallelBulkBackend"/>
    /// up to <see cref="Writers"/> batches are encoded and written to disk at the same time (the expensive part: a
    /// diagnostics package showed one writer at ~220 ms per segment being the whole 16 s of an 81k-file engine parse,
    /// with the parsers throttled behind it) and then published strictly in the order they were taken from the queue,
    /// one at a time, so the index ends up exactly as with a single writer. Other backends get one writer calling
    /// <see cref="ISymbolIndexBackend.AppendBatch"/>.
    /// Memory bound: at most <see cref="QueueCapacity"/> batches wait in the queue, <see cref="Writers"/> are being
    /// written, plus one partial batch per kind being filled — i.e. Writers + 3 batches of ≤ <see cref="BatchFiles"/>
    /// files / <see cref="BatchSourceBytes"/> source bytes each. Parsers block beyond that (recorded as throttled time).
    /// </summary>
    public sealed class BulkSink : IDisposable
    {
        private static readonly Logger L = Log.For("Index");
        public const int QueueCapacity = 1;
        public const int MaxWriters = 4;

        private readonly ISymbolIndexBackend _backend;
        private readonly IParallelBulkBackend _parallel;
        private readonly CancellationToken _ct;
        private readonly BlockingCollection<(bool Engine, List<(string, ParseResult)> Batch)> _queue =
            new BlockingCollection<(bool, List<(string, ParseResult)>)>(QueueCapacity);
        private readonly object _gate = new object();
        private readonly object _takeGate = new object();
        private readonly object _turnGate = new object();
        private readonly Task[] _writers;
        private readonly List<(string, ParseResult)>[] _pending = { new List<(string, ParseResult)>(), new List<(string, ParseResult)>() };
        private readonly long[] _pendingBytes = new long[2];
        private long _nextTake;
        private long _nextPublish;
        private int _inFlight;
        private int _segmentsWritten;
        private Exception _error;

        /// <param name="writers">Segment writer threads (1..<see cref="MaxWriters"/>; only used with an
        /// <see cref="IParallelBulkBackend"/>). Default: <see cref="DefaultWriters"/> of the processor count.</param>
        public BulkSink(ISymbolIndexBackend backend, CancellationToken ct, int batchFiles = 3000, long batchSourceBytes = 16L * 1024 * 1024, int writers = 0)
        {
            _backend = backend;
            _parallel = backend as IParallelBulkBackend;
            _ct = ct;
            BatchFiles = batchFiles;
            BatchSourceBytes = batchSourceBytes;
            Writers = _parallel == null ? 1 : Math.Max(1, Math.Min(MaxWriters, writers > 0 ? writers : DefaultWriters(Environment.ProcessorCount)));
            _writers = new Task[Writers];
            for (int i = 0; i < Writers; i++)
                _writers[i] = Task.Factory.StartNew(WriterLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Writers for <paramref name="parseThreads"/> parser threads: half of them, 1..<see cref="MaxWriters"/>.</summary>
        public static int DefaultWriters(int parseThreads) => Math.Max(1, Math.Min(MaxWriters, parseThreads / 2));

        public int BatchFiles { get; }
        public long BatchSourceBytes { get; }

        /// <summary>Segment writer threads.</summary>
        public int Writers { get; }

        /// <summary>Receives segment write times (set per stage).</summary>
        public ThroughputRecorder Recorder { get; set; }

        public int SegmentsWritten => Volatile.Read(ref _segmentsWritten);

        public void Add(bool engine, string path, ParseResult result, long sourceBytes)
        {
            List<(string, ParseResult)> full = null;
            int k = engine ? 1 : 0;
            lock (_gate)
            {
                ThrowIfFailed();
                _pending[k].Add((path, result));
                _pendingBytes[k] += sourceBytes;
                if (_pending[k].Count >= BatchFiles || _pendingBytes[k] >= BatchSourceBytes)
                {
                    full = _pending[k];
                    _pending[k] = new List<(string, ParseResult)>();
                    _pendingBytes[k] = 0;
                    Interlocked.Increment(ref _inFlight);
                }
            }
            if (full != null) Enqueue(engine, full);
        }

        private void Enqueue(bool engine, List<(string, ParseResult)> batch)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                _queue.Add((engine, batch), _ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Decrement(ref _inFlight);
                throw;
            }
            long waited = Stopwatch.GetTimestamp() - t0;
            if (waited > Stopwatch.Frequency / 1000) Recorder?.RecordThrottled(waited);
        }

        /// <summary>Writes the partial batch of one kind and waits until every queued batch is written.</summary>
        public void Flush(bool engine)
        {
            List<(string, ParseResult)> rest = null;
            int k = engine ? 1 : 0;
            lock (_gate)
            {
                if (_pending[k].Count > 0)
                {
                    rest = _pending[k];
                    _pending[k] = new List<(string, ParseResult)>();
                    _pendingBytes[k] = 0;
                    Interlocked.Increment(ref _inFlight);
                }
            }
            if (rest != null) Enqueue(engine, rest);
            lock (_gate)
            {
                while (Volatile.Read(ref _inFlight) > 0 && _error == null)
                {
                    _ct.ThrowIfCancellationRequested();
                    Monitor.Wait(_gate, 200);
                }
                ThrowIfFailed();
            }
        }

        private void ThrowIfFailed()
        {
            if (_error != null) throw new SegmentWriteFailedException("Writing index segments failed: " + _error.Message, _error);
        }

        private bool Stopped => Volatile.Read(ref _error) != null || _ct.IsCancellationRequested;

        /// <summary>
        /// Writes one batch; when memory runs out it collects, splits the batch in halves and retries, so a heavy batch
        /// (or a tight heap) costs a few smaller segments instead of failing the whole index run. A single file that still
        /// cannot be written is dropped from the segment (it stays searchable by file name) and logged.
        /// <paramref name="write"/> is AppendBatch (single writer) or PrepareBatch (parallel; results collected in order).
        /// </summary>
        private void WriteResilient(bool engine, List<(string, ParseResult)> batch, int depth, Action<List<(string, ParseResult)>> write)
        {
            try
            {
                write(batch);
            }
            catch (OutOfMemoryException ex)
            {
                long before = GC.GetTotalMemory(false);
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                long after = GC.GetTotalMemory(false);
                if (batch.Count <= 1)
                {
                    L.Error("index.segment.file_dropped", "Out of memory writing a single file; it stays file-only", ex,
                        ("path", batch.Count == 1 ? batch[0].Item1 : null), ("engine", engine), ("managedMBBefore", before / 1048576), ("managedMBAfter", after / 1048576));
                    return;
                }
                int half = batch.Count / 2;
                L.Warn("index.segment.split_retry", "Out of memory writing a segment; retrying in two halves", null,
                    ("files", batch.Count), ("depth", depth), ("engine", engine), ("managedMBBefore", before / 1048576), ("managedMBAfter", after / 1048576));
                WriteResilient(engine, batch.GetRange(0, half), depth + 1, write);
                WriteResilient(engine, batch.GetRange(half, batch.Count - half), depth + 1, write);
            }
        }

        private void WriterLoop()
        {
            try
            {
                while (true)
                {
                    (bool Engine, List<(string, ParseResult)> Batch) item;
                    long seq;
                    // Taking and numbering together: the publish order is the queue order.
                    lock (_takeGate)
                    {
                        if (!_queue.TryTake(out item, Timeout.Infinite)) break; // completed and empty
                        seq = _nextTake++;
                    }
                    ProcessBatch(item.Engine, item.Batch, seq);
                }
            }
            catch (Exception ex)
            {
                L.Error("index.segment.writer_failed", "Segment writer stopped", ex);
            }
        }

        private void ProcessBatch(bool engine, List<(string, ParseResult)> batch, long seq)
        {
            var prepared = new List<IDisposable>();
            long t0 = Stopwatch.GetTimestamp();
            bool myTurn = false;
            try
            {
                // 1) parallel part: encode + write the segment file(s)
                if (_parallel != null && !Stopped)
                    WriteResilient(engine, batch, 0, b =>
                    {
                        var p = _parallel.PrepareBatch(engine, b, _ct);
                        if (p != null) prepared.Add(p);
                    });
                // 2) serial part, in queue order
                WaitForTurn(seq);
                myTurn = true;
                if (!Stopped)
                {
                    if (_parallel != null)
                    {
                        foreach (var p in prepared)
                        {
                            _parallel.PublishBatch(p);
                            Interlocked.Increment(ref _segmentsWritten);
                        }
                        prepared.Clear();
                    }
                    else
                    {
                        WriteResilient(engine, batch, 0, b =>
                        {
                            _backend.AppendBatch(engine, b, _ct);
                            Interlocked.Increment(ref _segmentsWritten);
                        });
                    }
                    Recorder?.RecordSegment(Stopwatch.GetTimestamp() - t0);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                L.Error("index.segment.write_failed", "Writing a segment batch failed", ex, ("files", batch.Count), ("engine", engine), ("seq", seq));
                lock (_gate) _error ??= ex;
            }
            finally
            {
                foreach (var p in prepared)
                {
                    try { p.Dispose(); } catch (Exception ex) { L.Warn("index.segment.discard_failed", "Discarding an unpublished segment failed", ex); }
                }
                // Hand the turn on even when this batch failed or was skipped, so later batches never wait forever.
                if (!myTurn) WaitForTurn(seq);
                lock (_turnGate)
                {
                    _nextPublish = seq + 1;
                    Monitor.PulseAll(_turnGate);
                }
                lock (_gate)
                {
                    Interlocked.Decrement(ref _inFlight);
                    Monitor.PulseAll(_gate);
                }
            }
        }

        /// <summary>Blocks until every batch taken before <paramref name="seq"/> was published (or skipped).</summary>
        private void WaitForTurn(long seq)
        {
            lock (_turnGate)
            {
                while (_nextPublish != seq) Monitor.Wait(_turnGate);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            try { Task.WaitAll(_writers, TimeSpan.FromSeconds(30)); } catch (Exception) { }
            _queue.Dispose();
        }
    }

    /// <summary>The segment writer stopped: the whole stage fails once instead of every remaining file logging a parse failure.</summary>
    public sealed class SegmentWriteFailedException : InvalidOperationException
    {
        public SegmentWriteFailedException(string message, Exception inner) : base(message, inner) { }
    }
}
