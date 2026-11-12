using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>A [Start, Start+Length) range inside one line (UTF-16 columns).</summary>
    public readonly struct LineSpan : IEquatable<LineSpan>
    {
        public LineSpan(int start, int length)
        {
            Start = start;
            Length = length;
        }

        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;
        public bool IsEmpty => Length <= 0;

        public bool Contains(int column) => column >= Start && column < End;

        public bool Equals(LineSpan o) => Start == o.Start && Length == o.Length;
        public override bool Equals(object obj) => obj is LineSpan o && Equals(o);
        public override int GetHashCode() => (Start * 397) ^ Length;
        public override string ToString() => "[" + Start + "," + End + ")";
    }

    /// <summary>
    /// Finds the C++ identifier at a column of one line (Ctrl+click hit test, reference-highlight pre-check). Purely lexical
    /// and conservative: no hit inside // comments, string / char literals, on numbers or on C++ keywords (nothing to
    /// navigate to). Multi-line comments are the engine's business (it answers "no-word").
    /// </summary>
    public static class IdentifierFinder
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "alignas", "alignof", "and", "asm", "auto", "bool", "break", "case", "catch", "char", "char8_t", "char16_t", "char32_t",
            "class", "const", "consteval", "constexpr", "constinit", "const_cast", "continue", "co_await", "co_return", "co_yield",
            "decltype", "default", "delete", "do", "double", "dynamic_cast", "else", "enum", "explicit", "export", "extern", "false",
            "final", "float", "for", "friend", "goto", "if", "inline", "int", "long", "mutable", "namespace", "new", "noexcept", "not",
            "nullptr", "operator", "or", "override", "private", "protected", "public", "register", "reinterpret_cast", "requires",
            "return", "short", "signed", "sizeof", "static", "static_assert", "static_cast", "struct", "switch", "template", "this",
            "thread_local", "throw", "true", "try", "typedef", "typeid", "typename", "union", "unsigned", "using", "virtual", "void",
            "volatile", "wchar_t", "while", "xor",
        };

        public static bool IsIdentifierChar(char c) => c == '_' || char.IsLetterOrDigit(c);

        public static bool IsKeyword(string word) => word != null && Keywords.Contains(word);

        /// <summary>
        /// Identifier covering <paramref name="column"/> (the character under the mouse). With <paramref name="caretMode"/>
        /// the character left of the column also counts (a caret right after a word is "on" it).
        /// </summary>
        public static bool TryFind(string line, int column, bool caretMode, out LineSpan span)
        {
            span = default;
            if (string.IsNullOrEmpty(line) || column < 0 || column > line.Length) return false;
            if (TryFindIncludePath(line, column, out span)) return true;
            int at = column;
            if (at >= line.Length || !IsIdentifierChar(line[at]))
            {
                if (!caretMode || at == 0 || !IsIdentifierChar(line[at - 1])) return false;
                at--;
            }
            int start = at, end = at + 1;
            while (start > 0 && IsIdentifierChar(line[start - 1])) start--;
            while (end < line.Length && IsIdentifierChar(line[end])) end++;
            if (char.IsDigit(line[start])) return false; // number literal (or suffix like 10u)
            string word = line.Substring(start, end - start);
            if (IsKeyword(word)) return false;
            int before = start - 1;
            while (before >= 0 && char.IsWhiteSpace(line[before])) before--;
            if (before >= 0 && line[before] == '#') return false; // #include / #define / #pragma ...
            if (InCommentOrLiteral(line, start)) return false;
            span = new LineSpan(start, end - start);
            return true;
        }

        /// <summary>#include "Foo/Bar.h" / &lt;Foo.h&gt;: the path between the delimiters is one navigable span.</summary>
        public static bool TryFindIncludePath(string line, int column, out LineSpan span)
        {
            span = default;
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length || line[i] != '#') return false;
            i++;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (string.CompareOrdinal(line, i, "include", 0, 7) != 0) return false;
            i += 7;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length || (line[i] != '"' && line[i] != '<')) return false;
            char close = line[i] == '"' ? '"' : '>';
            int start = i + 1;
            int end = line.IndexOf(close, start);
            if (end <= start) return false;
            if (column < start || column > end) return false;
            span = new LineSpan(start, end - start);
            return true;
        }

        public static string WordAt(string line, int column, bool caretMode) =>
            TryFind(line, column, caretMode, out var s) ? line.Substring(s.Start, s.Length) : null;

        /// <summary>True when <paramref name="column"/> is after a // on the line or inside a "..." / '...' literal.</summary>
        public static bool InCommentOrLiteral(string line, int column)
        {
            char quote = '\0';
            for (int i = 0; i < line.Length && i < column; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    // Digit separators (1'000'000) are not char literals.
                    if (c == '\'' && i > 0 && char.IsLetterOrDigit(line[i - 1])) continue;
                    quote = c;
                    continue;
                }
                if (c == '/' && i + 1 < line.Length)
                {
                    if (line[i + 1] == '/') return true;
                    if (line[i + 1] == '*')
                    {
                        int close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (close < 0 || close + 2 > column) return true;
                        i = close + 1;
                    }
                }
            }
            return quote != '\0';
        }
    }

    [Flags]
    public enum KeyMods
    {
        None = 0,
        Ctrl = 1,
        Shift = 2,
        Alt = 4,
    }

    /// <summary>What the Ctrl+click mouse processor should do.</summary>
    public enum CtrlClickAction
    {
        None = 0,
        /// <summary>Ctrl held: show the underline + hand cursor for the identifier under the mouse.</summary>
        Hover = 1,
        /// <summary>Ctrl went away / mouse left a word: remove the underline.</summary>
        ClearHover = 2,
        /// <summary>Swallow the mouse-down (no caret move / selection start while Ctrl+clicking a word).</summary>
        Arm = 3,
        /// <summary>Ctrl+click completed on a word: navigate.</summary>
        Navigate = 4,
        /// <summary>The gesture turned into a drag / modifiers changed: let VS handle it.</summary>
        Cancel = 5,
    }

    /// <summary>
    /// Ctrl+left-click = go to definition (Rider/VA). Pure state machine fed by the mouse processor: Ctrl alone (no Shift /
    /// Alt — Ctrl+Alt+click is VS multi-caret, Ctrl+Shift is extend-selection), left button, no drag beyond
    /// <see cref="DragThreshold"/> pixels between down and up, and an identifier under the mouse on both down and up.
    /// </summary>
    public sealed class CtrlClickGesture
    {
        public const double DragThreshold = 4;

        private bool _armed;
        private double _downX, _downY;
        private LineSpan _downSpan;
        private int _downLine = -1;

        public bool Enabled { get; set; } = true;

        public bool IsArmed => _armed;

        public static bool IsCtrlOnly(KeyMods mods) => mods == KeyMods.Ctrl;

        /// <summary>Mouse move / modifier change. <paramref name="onWord"/>: an identifier is under the mouse.</summary>
        public CtrlClickAction OnMove(KeyMods mods, bool onWord, double x, double y)
        {
            if (_armed && (Math.Abs(x - _downX) > DragThreshold || Math.Abs(y - _downY) > DragThreshold || !IsCtrlOnly(mods)))
            {
                _armed = false;
                return CtrlClickAction.Cancel;
            }
            if (!Enabled) return CtrlClickAction.ClearHover;
            return IsCtrlOnly(mods) && onWord ? CtrlClickAction.Hover : CtrlClickAction.ClearHover;
        }

        public CtrlClickAction OnLeftDown(KeyMods mods, bool onWord, int line, LineSpan span, double x, double y, int clickCount = 1)
        {
            _armed = false;
            if (!Enabled || !IsCtrlOnly(mods) || !onWord || clickCount != 1) return CtrlClickAction.None;
            _armed = true;
            _downX = x;
            _downY = y;
            _downLine = line;
            _downSpan = span;
            return CtrlClickAction.Arm;
        }

        /// <summary>Navigate only if released on the same identifier it was pressed on.</summary>
        public CtrlClickAction OnLeftUp(KeyMods mods, bool onWord, int line, LineSpan span, double x, double y)
        {
            if (!_armed) return CtrlClickAction.None;
            _armed = false;
            if (!IsCtrlOnly(mods) || !onWord) return CtrlClickAction.Cancel;
            if (Math.Abs(x - _downX) > DragThreshold || Math.Abs(y - _downY) > DragThreshold) return CtrlClickAction.Cancel;
            if (line != _downLine || !span.Equals(_downSpan)) return CtrlClickAction.Cancel;
            return CtrlClickAction.Navigate;
        }

        public void Reset() => _armed = false;
    }
}
