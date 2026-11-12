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
    /// Collects parse results into batches per kind (project / engine) and hands full batches to one writer thread
    /// that turns each into a base segment (<see cref="ISymbolIndexBackend.AppendBatch"/>), so writing never blocks
    /// parsing. Back-pressure: at most <see cref="QueueCapacity"/> batches wait for the writer; parsers block (and
    /// the wait is recorded as throttled time) beyond that, which bounds memory.
    /// </summary>
    public sealed class BulkSink : IDisposable
    {
        private static readonly Logger L = Log.For("Index");
        public const int QueueCapacity = 1;

        private readonly ISymbolIndexBackend _backend;
        private readonly CancellationToken _ct;
        private readonly BlockingCollection<(bool Engine, List<(string, ParseResult)> Batch)> _queue =
            new BlockingCollection<(bool, List<(string, ParseResult)>)>(QueueCapacity);
        private readonly object _gate = new object();
        private readonly Task _writer;
        private readonly List<(string, ParseResult)>[] _pending = { new List<(string, ParseResult)>(), new List<(string, ParseResult)>() };
        private readonly long[] _pendingBytes = new long[2];
        private int _inFlight;
        private Exception _error;

        public BulkSink(ISymbolIndexBackend backend, CancellationToken ct, int batchFiles = 3000, long batchSourceBytes = 16L * 1024 * 1024)
        {
            _backend = backend;
            _ct = ct;
            BatchFiles = batchFiles;
            BatchSourceBytes = batchSourceBytes;
            _writer = Task.Factory.StartNew(WriterLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public int BatchFiles { get; }
        public long BatchSourceBytes { get; }

        /// <summary>Receives segment write times (set per stage).</summary>
        public ThroughputRecorder Recorder { get; set; }

        public int SegmentsWritten { get; private set; }

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

        /// <summary>
        /// Writes one batch; when memory runs out it collects, splits the batch in halves and retries, so a heavy batch
        /// (or a tight heap) costs a few smaller segments instead of failing the whole index run. A single file that still
        /// cannot be written is dropped from the segment (it stays searchable by file name) and logged.
        /// </summary>
        private void AppendResilient(bool engine, List<(string, ParseResult)> batch, int depth)
        {
            try
            {
                _backend.AppendBatch(engine, batch, _ct);
                SegmentsWritten++;
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
                AppendResilient(engine, batch.GetRange(0, half), depth + 1);
                AppendResilient(engine, batch.GetRange(half, batch.Count - half), depth + 1);
            }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (var (engine, batch) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (_error == null && !_ct.IsCancellationRequested)
                        {
                            long t0 = Stopwatch.GetTimestamp();
                            AppendResilient(engine, batch, 0);
                            Recorder?.RecordSegment(Stopwatch.GetTimestamp() - t0);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        L.Error("index.segment.write_failed", "Writing a segment batch failed", ex, ("files", batch.Count), ("engine", engine));
                        lock (_gate) _error ??= ex;
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            Interlocked.Decrement(ref _inFlight);
                            Monitor.PulseAll(_gate);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                L.Error("index.segment.writer_failed", "Segment writer stopped", ex);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            try { _writer.Wait(TimeSpan.FromSeconds(30)); } catch (Exception) { }
            _queue.Dispose();
        }
    }

    /// <summary>The segment writer stopped: the whole stage fails once instead of every remaining file logging a parse failure.</summary>
    public sealed class SegmentWriteFailedException : InvalidOperationException
    {
        public SegmentWriteFailedException(string message, Exception inner) : base(message, inner) { }
    }
}
