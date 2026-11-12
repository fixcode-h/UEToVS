using System;
using System.Collections;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Link
{
    /// <summary>UE's ELogVerbosity values (the plugin sends the number).</summary>
    public enum UeLogVerbosity
    {
        NoLogging = 0,
        Fatal = 1,
        Error = 2,
        Warning = 3,
        Display = 4,
        Log = 5,
        Verbose = 6,
        VeryVerbose = 7,
    }

    /// <summary>The five level filters of the log window (Fatal counts as Error, VeryVerbose as Verbose).</summary>
    public enum UeLogLevel
    {
        Error = 0,
        Warning = 1,
        Display = 2,
        Log = 3,
        Verbose = 4,
    }

    public static class UeLogVerbosities
    {
        public static UeLogVerbosity FromWire(int value) =>
            value >= (int)UeLogVerbosity.Fatal && value <= (int)UeLogVerbosity.VeryVerbose ? (UeLogVerbosity)value : UeLogVerbosity.Log;

        public static UeLogLevel LevelOf(UeLogVerbosity v)
        {
            switch (v)
            {
                case UeLogVerbosity.Fatal:
                case UeLogVerbosity.Error: return UeLogLevel.Error;
                case UeLogVerbosity.Warning: return UeLogLevel.Warning;
                case UeLogVerbosity.Display: return UeLogLevel.Display;
                case UeLogVerbosity.Verbose:
                case UeLogVerbosity.VeryVerbose: return UeLogLevel.Verbose;
                default: return UeLogLevel.Log;
            }
        }

        /// <summary>Verbosity name as written in UE_LOG (Error, Warning, ...); null for other text (a variable).</summary>
        public static UeLogVerbosity? Parse(string name)
        {
            switch ((name ?? string.Empty).Trim())
            {
                case "Fatal": return UeLogVerbosity.Fatal;
                case "Error": return UeLogVerbosity.Error;
                case "Warning": return UeLogVerbosity.Warning;
                case "Display": return UeLogVerbosity.Display;
                case "Log": return UeLogVerbosity.Log;
                case "Verbose": return UeLogVerbosity.Verbose;
                case "VeryVerbose": return UeLogVerbosity.VeryVerbose;
                default: return null;
            }
        }
    }

    /// <summary>One log line as it arrives from the plugin (time = editor seconds since the plugin started).</summary>
    public readonly struct UeLogRecord
    {
        public UeLogRecord(double editorTime, string category, UeLogVerbosity verbosity, string message)
        {
            EditorTime = editorTime;
            Category = category ?? string.Empty;
            Verbosity = verbosity;
            Message = message ?? string.Empty;
        }

        public double EditorTime { get; }
        public string Category { get; }
        public UeLogVerbosity Verbosity { get; }
        public string Message { get; }
    }

    /// <summary>A stored log line (immutable; shared by the buffer and the filtered views).</summary>
    public sealed class UeLogLine
    {
        public UeLogLine(long seq, DateTime received, UeLogRecord record)
        {
            Seq = seq;
            Received = received;
            EditorTime = record.EditorTime;
            Category = record.Category;
            Verbosity = record.Verbosity;
            Message = record.Message;
        }

        public long Seq { get; }
        public DateTime Received { get; }
        public double EditorTime { get; }
        public string Category { get; }
        public UeLogVerbosity Verbosity { get; }
        public string Message { get; }
        public UeLogLevel Level => UeLogVerbosities.LevelOf(Verbosity);

        public override string ToString() => Category + ": " + Verbosity + ": " + Message;
    }

    /// <summary>
    /// Ring buffer of the most recent log lines (capacity from the settings, default 20 000) so a chatty PIE session
    /// cannot grow memory. Categories are interned (one string per category) and counted for the category filter.
    /// Not thread-safe: the window feeds it from the UI thread in batches.
    /// </summary>
    public sealed class UeLogBuffer
    {
        public const int DefaultCapacity = 20000;
        public const int MinCapacity = 1000;
        public const int MaxCapacity = 500000;

        private readonly Dictionary<string, string> _interned = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private UeLogLine[] _items;
        private int _start;
        private int _count;
        private long _nextSeq = 1;

        public UeLogBuffer(int capacity = DefaultCapacity)
        {
            _items = new UeLogLine[ClampCapacity(capacity)];
        }

        public static int ClampCapacity(int capacity) => Math.Max(MinCapacity, Math.Min(MaxCapacity, capacity <= 0 ? DefaultCapacity : capacity));

        public int Capacity => _items.Length;
        public int Count => _count;

        /// <summary>Lines evicted (oldest first) since the buffer was created / cleared.</summary>
        public long Evicted { get; private set; }

        /// <summary>Seq of the oldest line still stored (= <see cref="NextSeq"/> when empty).</summary>
        public long FirstSeq => _nextSeq - _count;

        public long NextSeq => _nextSeq;
        public int ErrorCount { get; private set; }
        public int WarningCount { get; private set; }

        /// <summary>Lines per category currently stored.</summary>
        public IReadOnlyDictionary<string, int> CategoryCounts => _categoryCounts;

        public UeLogLine this[int index] => index < 0 || index >= _count ? null : _items[(_start + index) % _items.Length];

        /// <summary>The stored line with this seq, or null once evicted.</summary>
        public UeLogLine BySeq(long seq) => seq < FirstSeq || seq >= _nextSeq ? null : this[(int)(seq - FirstSeq)];

        public UeLogLine Add(UeLogRecord record, DateTime received)
        {
            string cat = Intern(record.Category);
            var line = new UeLogLine(_nextSeq++, received, new UeLogRecord(record.EditorTime, cat, record.Verbosity, record.Message));
            if (_count == _items.Length)
            {
                Forget(_items[_start]);
                _items[_start] = line;
                _start = (_start + 1) % _items.Length;
                Evicted++;
            }
            else
            {
                _items[(_start + _count) % _items.Length] = line;
                _count++;
            }
            _categoryCounts.TryGetValue(cat, out int n);
            _categoryCounts[cat] = n + 1;
            if (line.Level == UeLogLevel.Error) ErrorCount++;
            else if (line.Level == UeLogLevel.Warning) WarningCount++;
            return line;
        }

        /// <summary>Changes the capacity, keeping the newest lines.</summary>
        public void SetCapacity(int capacity)
        {
            capacity = ClampCapacity(capacity);
            if (capacity == _items.Length) return;
            int keep = Math.Min(_count, capacity);
            var next = new UeLogLine[capacity];
            for (int i = 0; i < _count - keep; i++)
            {
                Forget(this[i]);
                Evicted++;
            }
            for (int i = 0; i < keep; i++) next[i] = this[_count - keep + i];
            _items = next;
            _start = 0;
            _count = keep;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _start = 0;
            _count = 0;
            _categoryCounts.Clear();
            _interned.Clear();
            ErrorCount = 0;
            WarningCount = 0;
            Evicted = 0;
        }

        private string Intern(string category)
        {
            category = category ?? string.Empty;
            if (_interned.TryGetValue(category, out var s)) return s;
            if (_interned.Count > 4096) return category; // pathological category churn: stop interning
            _interned[category] = category;
            return category;
        }

        private void Forget(UeLogLine line)
        {
            if (line == null) return;
            if (_categoryCounts.TryGetValue(line.Category, out int n))
            {
                if (n <= 1) _categoryCounts.Remove(line.Category);
                else _categoryCounts[line.Category] = n - 1;
            }
            if (line.Level == UeLogLevel.Error) ErrorCount--;
            else if (line.Level == UeLogLevel.Warning) WarningCount--;
        }
    }

    /// <summary>Log window filter: levels, one category (null = all) and search words (all must occur, case-insensitive).</summary>
    public sealed class UeLogFilter
    {
        private readonly bool[] _levels = { true, true, true, true, true };
        private string[] _terms = Array.Empty<string>();
        private string _search = string.Empty;

        public string Category { get; set; }

        public string Search
        {
            get => _search;
            set
            {
                _search = value ?? string.Empty;
                _terms = _search.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public bool IsLevelShown(UeLogLevel level) => _levels[(int)level];

        public void SetLevelShown(UeLogLevel level, bool shown) => _levels[(int)level] = shown;

        /// <summary>No level hidden, no category, no search.</summary>
        public bool IsEmpty
        {
            get
            {
                foreach (bool b in _levels) if (!b) return false;
                return string.IsNullOrEmpty(Category) && _terms.Length == 0;
            }
        }

        public bool Matches(UeLogLine line)
        {
            if (line == null || !_levels[(int)line.Level]) return false;
            if (!string.IsNullOrEmpty(Category) && !string.Equals(line.Category, Category, StringComparison.Ordinal)) return false;
            foreach (var t in _terms)
                if (line.Message.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0 && line.Category.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            return true;
        }

        public override string ToString()
        {
            var hidden = new List<string>();
            for (int i = 0; i < _levels.Length; i++) if (!_levels[i]) hidden.Add(((UeLogLevel)i).ToString());
            return "levelsHidden=[" + string.Join(",", hidden) + "] category=" + (Category ?? "*") + " search=" + _search;
        }
    }

    /// <summary>
    /// Filtered view over a <see cref="UeLogBuffer"/> for the virtualized list: holds references to the matching lines
    /// (so a paused view stays stable while the buffer keeps rotating). <see cref="Sync"/> appends the new lines and
    /// drops evicted ones; <see cref="Rebuild"/> re-filters everything after a filter change.
    /// </summary>
    public sealed class UeLogView : IReadOnlyList<UeLogLine>
    {
        private readonly UeLogBuffer _buffer;
        private readonly List<UeLogLine> _lines = new List<UeLogLine>();
        private int _head;
        private long _seen;

        public UeLogView(UeLogBuffer buffer, UeLogFilter filter = null)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            Filter = filter ?? new UeLogFilter();
            _seen = buffer.FirstSeq - 1;
        }

        public UeLogFilter Filter { get; }

        public int Count => _lines.Count - _head;

        public UeLogLine this[int index] => _lines[_head + index];

        /// <summary>Re-applies the filter to every stored line.</summary>
        public void Rebuild()
        {
            _lines.Clear();
            _head = 0;
            for (int i = 0; i < _buffer.Count; i++)
            {
                var l = _buffer[i];
                if (Filter.Matches(l)) _lines.Add(l);
            }
            _seen = _buffer.NextSeq - 1;
        }

        /// <summary>Drops lines the buffer evicted and appends new matching ones. Returns (appended, removed from the front).</summary>
        public (int Appended, int Removed) Sync()
        {
            int removed = 0;
            long first = _buffer.FirstSeq;
            while (_head < _lines.Count && _lines[_head].Seq < first)
            {
                _head++;
                removed++;
            }
            if (_head > 4096 && _head > _lines.Count / 2)
            {
                _lines.RemoveRange(0, _head);
                _head = 0;
            }
            int appended = 0;
            long from = Math.Max(_seen + 1, first);
            for (long s = from; s < _buffer.NextSeq; s++)
            {
                var l = _buffer.BySeq(s);
                if (Filter.Matches(l))
                {
                    _lines.Add(l);
                    appended++;
                }
            }
            _seen = _buffer.NextSeq - 1;
            return (appended, removed);
        }

        /// <summary>Lines the buffer has that the view has not looked at yet (the "N new lines" hint while paused).</summary>
        public long Pending => Math.Max(0, _buffer.NextSeq - 1 - _seen);

        public void Clear()
        {
            _lines.Clear();
            _head = 0;
            _seen = _buffer.NextSeq - 1;
        }

        public IEnumerator<UeLogLine> GetEnumerator()
        {
            for (int i = _head; i < _lines.Count; i++) yield return _lines[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
