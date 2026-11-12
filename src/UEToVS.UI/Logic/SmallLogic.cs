using System;
using System.Collections.Generic;
using System.Threading;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// Search-input history with shell-like up/down browsing. Newest first, de-duplicated, bounded.
    /// While browsing, the text the user had typed is kept and restored when moving past the newest entry.
    /// </summary>
    public sealed class SearchHistory
    {
        private readonly List<string> _items = new List<string>();
        private int _cursor = -1;
        private string _draft;

        public SearchHistory(int capacity = 50)
        {
            Capacity = Math.Max(1, capacity);
        }

        public int Capacity { get; }
        public IReadOnlyList<string> Items => _items;
        public bool IsBrowsing => _cursor >= 0;

        public void Add(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            _items.RemoveAll(s => string.Equals(s, text, StringComparison.Ordinal));
            _items.Insert(0, text);
            if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
            ResetBrowsing();
        }

        public void ResetBrowsing()
        {
            _cursor = -1;
            _draft = null;
        }

        /// <summary>Older entry. Returns null when there is nothing older (text unchanged).</summary>
        public string Older(string currentText)
        {
            if (_items.Count == 0 || _cursor >= _items.Count - 1) return null;
            if (_cursor < 0) _draft = currentText;
            _cursor++;
            return _items[_cursor];
        }

        /// <summary>Newer entry, or the original draft after the newest. Returns null when not browsing.</summary>
        public string Newer()
        {
            if (_cursor < 0) return null;
            _cursor--;
            if (_cursor < 0)
            {
                var d = _draft ?? string.Empty;
                _draft = null;
                return d;
            }
            return _items[_cursor];
        }

        public void Load(IEnumerable<string> items)
        {
            _items.Clear();
            if (items != null)
                foreach (var s in items)
                    if (!string.IsNullOrWhiteSpace(s) && !_items.Contains(s) && _items.Count < Capacity) _items.Add(s);
            ResetBrowsing();
        }
    }

    /// <summary>
    /// Tracks the latest query generation so results of superseded (stale) queries are dropped, and owns the
    /// CancellationTokenSource of the in-flight query. Thread-safe.
    /// </summary>
    public sealed class QueryGate
    {
        private int _generation;
        private CancellationTokenSource _cts;
        private readonly object _gate = new object();

        public int Current => Volatile.Read(ref _generation);

        /// <summary>Cancels the previous query and starts a new generation.</summary>
        public (int Generation, CancellationToken Token) Begin()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                int g = Interlocked.Increment(ref _generation);
                return (g, _cts.Token);
            }
        }

        public bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);

        /// <summary>Cancels the in-flight query and invalidates its generation.</summary>
        public void CancelAll()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                Interlocked.Increment(ref _generation);
            }
        }
    }

    /// <summary>Shortcut text → keycaps ("Ctrl+Shift+F" → ["Ctrl","Shift","F"], "Ctrl+K, Ctrl+C" → two chords).</summary>
    public static class ShortcutText
    {
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["control"] = "Ctrl",
            ["ctl"] = "Ctrl",
            ["ctrl"] = "Ctrl",
            ["shift"] = "Shift",
            ["alt"] = "Alt",
            ["win"] = "Win",
            ["enter"] = "Enter",
            ["return"] = "Enter",
            ["esc"] = "Esc",
            ["escape"] = "Esc",
            ["up"] = "↑",
            ["down"] = "↓",
            ["left"] = "←",
            ["right"] = "→",
            ["pageup"] = "PgUp",
            ["pgup"] = "PgUp",
            ["pagedown"] = "PgDn",
            ["pgdn"] = "PgDn",
            ["space"] = "Space",
            ["tab"] = "Tab",
            ["del"] = "Del",
            ["delete"] = "Del",
            ["backspace"] = "Backspace",
        };

        /// <summary>Chords separated by ',' or ' '; keys inside a chord separated by '+'. A literal "+" key is written "Plus" or "++".</summary>
        public static List<List<string>> Parse(string shortcut)
        {
            var chords = new List<List<string>>();
            if (string.IsNullOrWhiteSpace(shortcut)) return chords;
            foreach (var chordRaw in shortcut.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var chord = chordRaw.Trim();
                if (chord.Length == 0) continue;
                var keys = new List<string>();
                bool endsWithPlus = chord.EndsWith("++", StringComparison.Ordinal) || chord == "+";
                if (endsWithPlus) chord = chord.Substring(0, Math.Max(0, chord.Length - (chord == "+" ? 1 : 2)));
                foreach (var k in chord.Split('+'))
                {
                    var key = k.Trim();
                    if (key.Length == 0) continue;
                    keys.Add(Normalize(key));
                }
                if (endsWithPlus) keys.Add("+");
                if (keys.Count > 0) chords.Add(keys);
            }
            return chords;
        }

        public static string Normalize(string key)
        {
            if (Aliases.TryGetValue(key, out var a)) return a;
            if (key.Equals("plus", StringComparison.OrdinalIgnoreCase)) return "+";
            return key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key.Substring(1);
        }
    }

    /// <summary>Glyph letter for <see cref="SymbolKind"/> (TypeGlyph atom when no ImageMoniker is available).</summary>
    public static class SymbolGlyphs
    {
        public static string Letter(SymbolKind kind)
        {
            switch (kind)
            {
                case SymbolKind.Class: return "C";
                case SymbolKind.Struct: return "S";
                case SymbolKind.Interface: return "I";
                case SymbolKind.Enum: return "E";
                case SymbolKind.EnumMember: return "e";
                case SymbolKind.Function: return "ƒ";
                case SymbolKind.Method: return "m";
                case SymbolKind.Property: return "P";
                case SymbolKind.Field: return "f";
                case SymbolKind.Variable: return "v";
                case SymbolKind.Parameter: return "p";
                case SymbolKind.Macro: return "#";
                case SymbolKind.Namespace: return "N";
                case SymbolKind.Typedef: return "T";
                case SymbolKind.Delegate: return "D";
                case SymbolKind.File: return "F";
                case SymbolKind.Asset: return "A";
                case SymbolKind.Module: return "M";
                case SymbolKind.Keyword: return "K";
                case SymbolKind.Action: return "›";
                default: return "?";
            }
        }
    }
}
