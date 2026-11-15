using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace UEToVS.Engine.Indexing
{
    /// <summary>
    /// Text of editor buffers with unsaved changes, pushed by the host (v0.7.5). While a file has an entry here the index and
    /// the parsed-document cache use this text instead of the file on disk, so navigation, Quick Info and the light bulb see a
    /// declaration added to an unsaved header or an implementation deleted from an unsaved .cpp. Saving, reverting or closing
    /// the editor removes the entry and the file is read from disk again. Thread-safe.
    /// </summary>
    public sealed class UnsavedBuffers
    {
        public sealed class Buffer
        {
            public Buffer(string text, long version)
            {
                Text = text;
                Bytes = Encoding.UTF8.GetBytes(text);
                Version = version;
            }

            public string Text { get; }
            public byte[] Bytes { get; }

            /// <summary>Unique per pushed text (process-wide counter): part of the document cache stamp.</summary>
            public long Version { get; }
        }

        private readonly ConcurrentDictionary<string, Buffer> _map = new ConcurrentDictionary<string, Buffer>(StringComparer.OrdinalIgnoreCase);
        private long _version;

        public int Count => _map.Count;

        /// <summary>Stores the text; false when the same text was already stored.</summary>
        public bool Set(string path, string text)
        {
            if (string.IsNullOrEmpty(path) || text == null) return false;
            if (_map.TryGetValue(path, out var old) && string.Equals(old.Text, text, StringComparison.Ordinal)) return false;
            _map[path] = new Buffer(text, Interlocked.Increment(ref _version));
            return true;
        }

        /// <summary>Drops the entry; false when there was none.</summary>
        public bool Remove(string path) => !string.IsNullOrEmpty(path) && _map.TryRemove(path, out _);

        public bool TryGet(string path, out Buffer buffer)
        {
            buffer = null;
            return !string.IsNullOrEmpty(path) && _map.Count > 0 && _map.TryGetValue(path, out buffer);
        }
    }
}
