using System;
using System.Collections.Generic;
using System.Text;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>
    /// Multi clipboard (Ctrl+Shift+V): the last N texts copied / cut in VS, newest first. Text only, at most
    /// <see cref="MaxBytes"/> per entry; copying an entry again moves it to the top. Memory only (nothing is written to disk).
    /// Thread-safe.
    /// </summary>
    public sealed class ClipboardRing
    {
        public const int MaxBytes = 1024 * 1024;
        public const int DefaultCapacity = 16;

        private readonly object _gate = new object();
        private readonly List<string> _items = new List<string>();
        private int _capacity;

        public ClipboardRing(int capacity = DefaultCapacity)
        {
            Capacity = capacity;
        }

        public int Capacity
        {
            get => _capacity;
            set
            {
                lock (_gate)
                {
                    _capacity = Math.Max(1, Math.Min(100, value));
                    if (_items.Count > _capacity) _items.RemoveRange(_capacity, _items.Count - _capacity);
                }
            }
        }

        public int Count
        {
            get { lock (_gate) return _items.Count; }
        }

        /// <summary>Newest first (a copy).</summary>
        public List<string> Items
        {
            get { lock (_gate) return new List<string>(_items); }
        }

        /// <summary>Records <paramref name="text"/>; false when it is empty / whitespace or too large.</summary>
        public bool Add(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length > MaxBytes || Encoding.UTF8.GetByteCount(text) > MaxBytes) return false;
            lock (_gate)
            {
                _items.Remove(text);
                _items.Insert(0, text);
                if (_items.Count > _capacity) _items.RemoveRange(_capacity, _items.Count - _capacity);
            }
            return true;
        }

        public void Clear()
        {
            lock (_gate) _items.Clear();
        }

        /// <summary>One-line preview of an entry (whitespace collapsed, at most <paramref name="max"/> characters).</summary>
        public static string Preview(string text, int max = 80)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(Math.Min(text.Length, max + 1));
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
                if (sb.Length > max) break;
            }
            return sb.Length > max ? sb.ToString(0, max) + "…" : sb.ToString();
        }

        public static int LineCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 1;
            foreach (char c in text) if (c == '\n') n++;
            return n;
        }
    }
}
