using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UEToVS.Core.Diagnostics
{
    /// <summary>
    /// Writes JSON lines to &lt;dir&gt;/&lt;prefix&gt;-yyyyMMdd-HHmmss-&lt;pid&gt;[-n].log on a background thread.
    /// Rolls over at <see cref="MaxFileBytes"/>, deletes files older than <see cref="RetentionDays"/> or beyond
    /// <see cref="MaxTotalBytes"/>. Writing never blocks callers; if the queue overflows, oldest-first drops are counted
    /// and reported in the next line.
    /// </summary>
    public sealed class JsonLinesFileSink : ILogSink
    {
        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), 20000);
        private readonly Thread _writer;
        private readonly string _dir;
        private readonly string _prefix;
        private readonly string _stamp;
        private StreamWriter _stream;
        private long _bytes;
        private int _part;
        private int _dropped;
        private readonly ManualResetEventSlim _flushed = new ManualResetEventSlim(true);

        public JsonLinesFileSink(string directory, string prefix)
        {
            _dir = directory;
            _prefix = prefix;
            _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + System.Diagnostics.Process.GetCurrentProcess().Id;
            Directory.CreateDirectory(_dir);
            CleanupOldFiles();
            Open();
            _writer = new Thread(Run) { IsBackground = true, Name = "UEToVS log writer", Priority = ThreadPriority.BelowNormal };
            _writer.Start();
        }

        public long MaxFileBytes { get; set; } = 10L * 1024 * 1024;
        public long MaxTotalBytes { get; set; } = 200L * 1024 * 1024;
        public int RetentionDays { get; set; } = 7;

        /// <summary>Current file path (for "open log" commands and diagnostics).</summary>
        public string CurrentFile { get; private set; }

        public void Write(LogEvent e)
        {
            string line = JsonLineFormatter.Format(e);
            if (!_queue.TryAdd(line)) Interlocked.Increment(ref _dropped);
        }

        public void Flush()
        {
            if (_queue.IsAddingCompleted) return;
            _flushed.Reset();
            if (!_queue.TryAdd(null)) _flushed.Set(); // null = flush marker
            _flushed.Wait(2000);
        }

        private void Run()
        {
            try
            {
                foreach (var line in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (line == null)
                        {
                            _stream?.Flush();
                            _flushed.Set();
                            continue;
                        }
                        int dropped = Interlocked.Exchange(ref _dropped, 0);
                        if (dropped > 0) WriteLine($"{{\"ts\":\"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\",\"lvl\":\"warn\",\"mod\":\"Log\",\"evt\":\"log.dropped\",\"data\":{{\"count\":{dropped}}}}}");
                        WriteLine(line);
                        if (_queue.Count == 0) _stream?.Flush();
                    }
                    catch
                    {
                        // disk full / file locked: try to reopen next time
                        try { _stream?.Dispose(); } catch { }
                        _stream = null;
                    }
                }
            }
            finally
            {
                try { _stream?.Flush(); _stream?.Dispose(); } catch { }
                _flushed.Set();
            }
        }

        private void WriteLine(string line)
        {
            if (_stream == null) Open();
            if (_bytes >= MaxFileBytes)
            {
                _part++;
                _stream.Dispose();
                Open();
                CleanupOldFiles();
            }
            _stream.WriteLine(line);
            _bytes += Encoding.UTF8.GetByteCount(line) + 2;
        }

        private void Open()
        {
            CurrentFile = Path.Combine(_dir, $"{_prefix}-{_stamp}{(_part > 0 ? "-" + _part : "")}.log");
            var fs = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            _stream = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
            _bytes = fs.Length;
        }

        private void CleanupOldFiles()
        {
            try
            {
                var files = new DirectoryInfo(_dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                long total = 0;
                var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
                foreach (var f in files)
                {
                    total += f.Length;
                    if (f.FullName == CurrentFile) continue;
                    if (f.LastWriteTimeUtc < cutoff || total > MaxTotalBytes)
                    {
                        try { f.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _writer.Join(3000);
        }
    }
}
