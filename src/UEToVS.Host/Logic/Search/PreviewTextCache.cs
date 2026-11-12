using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Search
{
    /// <summary>A file loaded for the code preview: its text plus the lexer states the preview colouring needs.</summary>
    public sealed class PreviewText
    {
        public PreviewText(string path, string text, DateTime lastWriteUtc, long bytes)
        {
            Path = path;
            Text = text ?? string.Empty;
            LastWriteUtc = lastWriteUtc;
            Bytes = bytes;
            LineStates = CppLexer.LineStartStates(Text, out int lines);
            LineCount = lines;
        }

        public string Path { get; }
        public string Text { get; }
        public DateTime LastWriteUtc { get; }
        public long Bytes { get; }
        public int LineCount { get; }

        /// <summary>Line → lexer state for lines that start inside a block comment / raw string (<see cref="CppLexer.LineStartStates"/>).</summary>
        public IReadOnlyDictionary<int, CppLexState> LineStates { get; }

        public CppLexState StateAt(int line) => LineStates.TryGetValue(line, out var s) ? s : CppLexState.Normal;
    }

    /// <summary>
    /// Small LRU of preview texts so moving back and forth in a result list does not re-read files. Bounded by entry
    /// count and total characters; an entry is valid only while the file's last-write time is unchanged. Thread-safe.
    /// </summary>
    public sealed class PreviewTextCache
    {
        private readonly object _gate = new object();
        private readonly LinkedList<PreviewText> _lru = new LinkedList<PreviewText>();
        private readonly Dictionary<string, LinkedListNode<PreviewText>> _map = new Dictionary<string, LinkedListNode<PreviewText>>(StringComparer.OrdinalIgnoreCase);
        private long _chars;

        public PreviewTextCache(int maxEntries = 12, long maxChars = 16 * 1024 * 1024)
        {
            MaxEntries = Math.Max(1, maxEntries);
            MaxChars = Math.Max(1, maxChars);
        }

        public int MaxEntries { get; }
        public long MaxChars { get; }

        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        public long Chars
        {
            get { lock (_gate) return _chars; }
        }

        /// <summary>The cached text when present and the file has not changed since (<paramref name="lastWriteUtc"/>).</summary>
        public PreviewText TryGet(string path, DateTime lastWriteUtc)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_gate)
            {
                if (!_map.TryGetValue(path, out var node)) return null;
                if (node.Value.LastWriteUtc != lastWriteUtc)
                {
                    Remove(node);
                    return null;
                }
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value;
            }
        }

        public void Add(PreviewText text)
        {
            if (text == null || string.IsNullOrEmpty(text.Path)) return;
            lock (_gate)
            {
                if (_map.TryGetValue(text.Path, out var old)) Remove(old);
                if (text.Text.Length > MaxChars) return; // never evict everything for one huge file
                var node = _lru.AddFirst(text);
                _map[text.Path] = node;
                _chars += text.Text.Length;
                while (_lru.Count > MaxEntries || _chars > MaxChars) Remove(_lru.Last);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _lru.Clear();
                _map.Clear();
                _chars = 0;
            }
        }

        private void Remove(LinkedListNode<PreviewText> node)
        {
            _lru.Remove(node);
            _map.Remove(node.Value.Path);
            _chars -= node.Value.Text.Length;
        }
    }
}
