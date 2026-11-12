using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UEToVS.Mcp.Data
{
    /// <summary>
    /// A source file's raw bytes with line starts. The index stores occurrence offsets as UTF-8 byte offsets into the
    /// file as read from disk (the parser gets the raw bytes); columns are reported in UTF-16 units like everywhere else.
    /// </summary>
    public sealed class SourceText
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);
        private readonly byte[] _bytes;
        private readonly int[] _lineStarts;
        private readonly int _bom;

        public SourceText(string path, byte[] bytes, DateTime mtimeUtc)
        {
            Path = path;
            _bytes = bytes ?? Array.Empty<byte>();
            MtimeUtc = mtimeUtc;
            _bom = _bytes.Length >= 3 && _bytes[0] == 0xEF && _bytes[1] == 0xBB && _bytes[2] == 0xBF ? 3 : 0;
            var starts = new List<int> { 0 };
            for (int i = 0; i < _bytes.Length; i++)
                if (_bytes[i] == (byte)'\n') starts.Add(i + 1);
            _lineStarts = starts.ToArray();
        }

        public static SourceText FromString(string path, string text) => new SourceText(path, Utf8.GetBytes(text ?? string.Empty), DateTime.MinValue);

        public string Path { get; }
        public DateTime MtimeUtc { get; }
        public int LineCount => _lineStarts.Length;
        public int ByteLength => _bytes.Length;

        /// <summary>0-based line and UTF-16 column of a UTF-8 byte offset.</summary>
        public (int Line, int Column) Position(int byteOffset)
        {
            if (byteOffset < 0) byteOffset = 0;
            if (byteOffset > _bytes.Length) byteOffset = _bytes.Length;
            int lo = 0, hi = _lineStarts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_lineStarts[mid] <= byteOffset) lo = mid;
                else hi = mid - 1;
            }
            int start = lo == 0 ? Math.Min(_bom, byteOffset) : _lineStarts[lo];
            return (lo, Utf8.GetCharCount(_bytes, start, byteOffset - start));
        }

        /// <summary>0-based line text without the line break; empty when out of range.</summary>
        public string Line(int line)
        {
            if (line < 0 || line >= _lineStarts.Length) return string.Empty;
            int start = line == 0 ? _bom : _lineStarts[line];
            int end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _bytes.Length;
            while (end > start && (_bytes[end - 1] == '\n' || _bytes[end - 1] == '\r')) end--;
            return end > start ? Utf8.GetString(_bytes, start, end - start) : string.Empty;
        }
    }

    /// <summary>Small LRU of <see cref="SourceText"/> keyed by path and validated by mtime. Thread-safe.</summary>
    public sealed class SourceTextCache
    {
        public const long MaxFileBytes = 8L * 1024 * 1024;
        private readonly int _capacity;
        private readonly LinkedList<SourceText> _lru = new LinkedList<SourceText>();
        private readonly Dictionary<string, LinkedListNode<SourceText>> _map = new Dictionary<string, LinkedListNode<SourceText>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        public SourceTextCache(int capacity = 64) => _capacity = Math.Max(1, capacity);

        /// <summary>Null when the file is missing, unreadable or larger than <see cref="MaxFileBytes"/>.</summary>
        public SourceText Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            DateTime mtime;
            long length;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                mtime = fi.LastWriteTimeUtc;
                length = fi.Length;
            }
            catch (Exception)
            {
                return null;
            }
            lock (_gate)
            {
                if (_map.TryGetValue(path, out var node) && node.Value.MtimeUtc == mtime)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value;
                }
            }
            if (length > MaxFileBytes) return null;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                return null;
            }
            var st = new SourceText(path, bytes, mtime);
            lock (_gate)
            {
                if (_map.TryGetValue(path, out var old))
                {
                    _lru.Remove(old);
                    _map.Remove(path);
                }
                _map[path] = _lru.AddFirst(st);
                while (_lru.Count > _capacity)
                {
                    var last = _lru.Last;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Path);
                }
            }
            return st;
        }
    }
}
