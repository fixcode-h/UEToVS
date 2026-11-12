using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Navigation
{
    /// <summary>
    /// Bounded LRU of parsed documents. Editor buffers are keyed by (path, TextVersion); files on disk by
    /// (path, mtime, length), so a save invalidates them. Both an entry count and a byte budget apply.
    /// Thread-safe; parsing happens outside the lock (two threads may parse the same file once each).
    /// </summary>
    public sealed class DocumentCache
    {
        private static readonly Logger L = Log.For("Nav");

        private readonly object _gate = new object();
        private readonly Dictionary<string, LinkedListNode<Entry>> _map = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
        private long _bytes;
        private long _hits, _misses;

        public DocumentCache(int maxEntries = 96, long maxBytes = 96L * 1024 * 1024)
        {
            MaxEntries = Math.Max(4, maxEntries);
            MaxBytes = Math.Max(1024 * 1024, maxBytes);
        }

        public int MaxEntries { get; }
        public long MaxBytes { get; }

        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        public long Bytes
        {
            get { lock (_gate) return _bytes; }
        }

        public long Hits => Interlocked.Read(ref _hits);
        public long Misses => Interlocked.Read(ref _misses);

        private sealed class Entry
        {
            public string Key;
            public string Stamp;
            public DocumentModel Doc;
        }

        /// <summary>Document for an editor buffer (text != null) or the file on disk; null when the file cannot be read.</summary>
        public DocumentModel Get(string path, string text, long textVersion)
        {
            if (string.IsNullOrEmpty(path)) path = "<buffer>";
            if (text != null)
            {
                string stamp = "b:" + (textVersion != 0 ? textVersion.ToString() : "h" + text.Length + ":" + StableHash(text));
                return GetOrAdd(path, stamp, () => DocumentModel.Parse(path, textVersion, text));
            }
            return GetFile(path);
        }

        /// <summary>Document for a file on disk, re-parsed when its mtime or length changes.</summary>
        public DocumentModel GetFile(string path)
        {
            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
                if (!fi.Exists) return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return null;
            }
            long ticks = fi.LastWriteTimeUtc.Ticks;
            string fileStamp = "f:" + ticks + ":" + fi.Length;
            return GetOrAdd(path, fileStamp, () =>
            {
                try
                {
                    return DocumentModel.Parse(path, ticks, File.ReadAllBytes(path));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    L.Warn("nav.read_failed", "Could not read file for navigation", ex, ("path", path));
                    return null;
                }
            });
        }

        private DocumentModel GetOrAdd(string path, string stamp, Func<DocumentModel> parse)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(path, out var node) && node.Value.Stamp == stamp)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    Interlocked.Increment(ref _hits);
                    return node.Value.Doc;
                }
            }
            Interlocked.Increment(ref _misses);
            var doc = parse();
            if (doc == null) return null;
            lock (_gate)
            {
                if (_map.TryGetValue(path, out var old))
                {
                    _lru.Remove(old);
                    _bytes -= old.Value.Doc.ApproxBytes;
                    _map.Remove(path);
                }
                var node = _lru.AddFirst(new Entry { Key = path, Stamp = stamp, Doc = doc });
                _map[path] = node;
                _bytes += doc.ApproxBytes;
                while ((_map.Count > MaxEntries || _bytes > MaxBytes) && _lru.Count > 1)
                {
                    var last = _lru.Last;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _bytes -= last.Value.Doc.ApproxBytes;
                }
            }
            return doc;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
                _lru.Clear();
                _bytes = 0;
            }
        }

        private static ulong StableHash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h;
        }
    }
}
