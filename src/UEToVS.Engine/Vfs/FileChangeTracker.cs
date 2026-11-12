using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Vfs
{
    /// <summary>xxHash64 of file content (§9.4): decides whether a touched file needs re-parsing.</summary>
    public static class ContentHasher
    {
        public static ulong Hash(ReadOnlySpan<byte> content) => XxHash64.HashToUInt64(content);
    }

    /// <summary>A coalesced set of changes handed to the index.</summary>
    public sealed class ChangeBatch
    {
        public List<FileChange> Changes { get; } = new List<FileChange>();

        /// <summary>Mass change (branch switch, sync): process in background chunks.</summary>
        public bool IsBatch { get; set; }

        /// <summary>The watcher lost events (buffer overflow): only a rescan is reliable.</summary>
        public bool NeedsRescan { get; set; }

        public string Source { get; set; }
    }

    /// <summary>
    /// Collects file changes from FileSystemWatcher and from the host (<c>NotifyFilesChangedAsync</c>), coalesces them
    /// per path, debounces (quiet period <see cref="Debounce"/>, at most <see cref="MaxDelay"/> after the first event) and
    /// hands them to the index one batch at a time. More than <see cref="BatchThreshold"/> changes at once (a git branch
    /// switch) switch to batch mode and log <c>fs.batch</c> with counts.
    /// </summary>
    public sealed class FileChangeTracker : IDisposable
    {
        private static readonly Logger L = Log.For("Vfs");

        private readonly Func<ChangeBatch, Task> _apply;
        private readonly object _gate = new object();
        private readonly Dictionary<string, FileChangeKind> _pending = new Dictionary<string, FileChangeKind>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
        private readonly Timer _timer;
        private DateTime _firstPendingUtc;
        private bool _overflow;
        private string _source;
        private bool _disposed;

        public FileChangeTracker(Func<ChangeBatch, Task> apply)
        {
            _apply = apply;
            _timer = new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(300);
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(2);
        public int BatchThreshold { get; set; } = 200;

        public long EventsReceived;
        public long BatchesApplied;

        public int WatcherCount
        {
            get { lock (_gate) return _watchers.Count; }
        }

        /// <summary>Watches each directory recursively. Failures (inotify limits, missing dirs) are logged, not thrown.</summary>
        public void Watch(IEnumerable<string> directories)
        {
            foreach (var dir in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var w = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    string root = dir;
                    w.Changed += (s, e) => OnFsEvent(root, e.FullPath, FileChangeKind.Changed);
                    w.Created += (s, e) => OnFsEvent(root, e.FullPath, FileChangeKind.Created);
                    w.Deleted += (s, e) => OnFsEvent(root, e.FullPath, FileChangeKind.Deleted);
                    w.Renamed += (s, e) =>
                    {
                        OnFsEvent(root, e.OldFullPath, FileChangeKind.Deleted);
                        OnFsEvent(root, e.FullPath, FileChangeKind.Created);
                    };
                    w.Error += (s, e) => OnWatcherError(root, e.GetException());
                    w.EnableRaisingEvents = true;
                    lock (_gate) _watchers.Add(w);
                    L.Debug("fs.watch", null, ("dir", dir));
                }
                catch (Exception ex)
                {
                    L.Warn("fs.watch.failed", "Cannot watch directory; changes there are only seen through the host", ex, ("dir", dir));
                }
            }
        }

        private void OnFsEvent(string root, string path, FileChangeKind kind)
        {
            try
            {
                // Cheap filter on the watcher thread: build output, VCS folders and unrelated extensions.
                if (FileLister.IsInSkippedDirectory(root, path)) return;
                bool known = FileLister.TryGetKind(path, out _) || AssetLister.IsAsset(path);
                bool maybeDir = string.IsNullOrEmpty(Path.GetExtension(path));
                if (!known && !maybeDir) return;
                Enqueue(new[] { new FileChange { FilePath = path, Kind = kind } }, "watcher");
            }
            catch (Exception ex)
            {
                L.Warn("fs.event.failed", "File event handling failed", ex);
            }
        }

        private void OnWatcherError(string root, Exception ex)
        {
            L.Warn("fs.overflow", "File watcher lost events; a rescan will follow", ex, ("dir", root));
            lock (_gate)
            {
                _overflow = true;
                Schedule();
            }
        }

        /// <summary>Adds changes (host notifications or watcher events). Never blocks on indexing.</summary>
        public void Enqueue(IEnumerable<FileChange> changes, string source)
        {
            if (changes == null) return;
            lock (_gate)
            {
                if (_disposed) return;
                foreach (var c in changes)
                {
                    if (c == null || string.IsNullOrEmpty(c.FilePath)) continue;
                    Interlocked.Increment(ref EventsReceived);
                    string path;
                    try { path = Path.GetFullPath(c.FilePath); }
                    catch (Exception) { continue; }
                    var kind = c.Kind == FileChangeKind.Renamed ? FileChangeKind.Created : c.Kind;
                    if (_pending.TryGetValue(path, out var prev))
                    {
                        // created+deleted cancel out to "deleted"; deleted+created is a content change.
                        if (prev == FileChangeKind.Deleted && kind == FileChangeKind.Created) kind = FileChangeKind.Changed;
                        else if (prev == FileChangeKind.Created && kind == FileChangeKind.Changed) kind = FileChangeKind.Created;
                    }
                    _pending[path] = kind;
                }
                _source = _source == null ? source : (_source == source ? source : "mixed");
                Schedule();
            }
        }

        private void Schedule()
        {
            // caller holds _gate
            var now = DateTime.UtcNow;
            if (_pending.Count == 0 && !_overflow) return;
            if (_firstPendingUtc == default) _firstPendingUtc = now;
            var dueIn = Debounce;
            var latest = _firstPendingUtc + MaxDelay - now;
            if (latest < dueIn) dueIn = latest < TimeSpan.Zero ? TimeSpan.Zero : latest;
            _timer.Change(dueIn, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Applies everything pending now (also used by tests).</summary>
        public async Task FlushAsync()
        {
            if (!await _flushLock.WaitAsync(0).ConfigureAwait(false))
            {
                // A flush is running; it reschedules when done.
                return;
            }
            try
            {
                ChangeBatch batch;
                lock (_gate)
                {
                    if (_pending.Count == 0 && !_overflow) return;
                    batch = new ChangeBatch { NeedsRescan = _overflow, Source = _source ?? "unknown" };
                    foreach (var kv in _pending) batch.Changes.Add(new FileChange { FilePath = kv.Key, Kind = kv.Value });
                    _pending.Clear();
                    _overflow = false;
                    _source = null;
                    _firstPendingUtc = default;
                }
                batch.IsBatch = batch.NeedsRescan || batch.Changes.Count >= BatchThreshold;
                if (batch.IsBatch)
                {
                    L.Info("fs.batch", "Mass file change: batch mode", ("total", batch.Changes.Count),
                        ("created", batch.Changes.Count(c => c.Kind == FileChangeKind.Created)),
                        ("changed", batch.Changes.Count(c => c.Kind == FileChangeKind.Changed)),
                        ("deleted", batch.Changes.Count(c => c.Kind == FileChangeKind.Deleted)),
                        ("rescan", batch.NeedsRescan), ("source", batch.Source));
                }
                else
                {
                    L.Debug("fs.changes", null, ("total", batch.Changes.Count), ("source", batch.Source));
                }
                Interlocked.Increment(ref BatchesApplied);
                await _apply(batch).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                L.Error("fs.apply.failed", "Applying file changes failed", ex);
            }
            finally
            {
                _flushLock.Release();
                lock (_gate) Schedule();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var w in _watchers)
                {
                    try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
                }
                _watchers.Clear();
            }
            _timer.Dispose();
        }
    }
}
