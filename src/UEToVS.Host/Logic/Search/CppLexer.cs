using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Search
{
    /// <summary>Lexical classes of the preview colouring (mapped to VS's standard classification types).</summary>
    public enum CppTokenKind
    {
        Comment,
        String,
        Number,
        Keyword,
        Preprocessor,
    }

    /// <summary>A coloured span of one line: [Start, Start + Length) in line-relative characters.</summary>
    public readonly struct CppToken
    {
        public CppToken(int start, int length, CppTokenKind kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
        }

        public int Start { get; }
        public int Length { get; }
        public CppTokenKind Kind { get; }

        public override string ToString() => Kind + "[" + Start + "," + (Start + Length) + ")";
    }

    /// <summary>Lexer state carried from one line to the next (inside a block comment or a raw string).</summary>
    public readonly struct CppLexState : IEquatable<CppLexState>
    {
        public static readonly CppLexState Normal = default;

        public CppLexState(bool inComment, string rawDelimiter)
        {
            InComment = inComment;
            RawDelimiter = rawDelimiter;
        }

        public bool InComment { get; }

        /// <summary>Closing sequence of an open raw string, e.g. <c>)xyz"</c>; null when not in one.</summary>
        public string RawDelimiter { get; }

        public bool IsNormal => !InComment && RawDelimiter == null;

        public bool Equals(CppLexState o) => InComment == o.InComment && string.Equals(RawDelimiter, o.RawDelimiter, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is CppLexState o && Equals(o);

        public override int GetHashCode() => (InComment ? 1 : 0) ^ (RawDelimiter?.GetHashCode() ?? 0);
    }

    /// <summary>
    /// Small line-based C++ lexer for the code preview (comments, strings / chars / raw strings, numbers, keywords,
    /// preprocessor directives). The editor's own C++ colouring and the engine's semantic colouring are layered on top;
    /// this guarantees a coloured preview even when neither applies to a read-only preview buffer. Pure and fast: one
    /// pass per line, no allocations besides the output tokens.
    /// </summary>
    public static class CppLexer
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "alignas", "alignof", "and", "asm", "auto", "bool", "break", "case", "catch", "char", "char8_t", "char16_t",
            "char32_t", "class", "concept", "const", "consteval", "constexpr", "constinit", "const_cast", "continue",
            "co_await", "co_return", "co_yield", "decltype", "default", "delete", "do", "double", "dynamic_cast", "else",
            "enum", "explicit", "export", "extern", "false", "final", "float", "for", "friend", "goto", "if", "inline",
            "int", "long", "mutable", "namespace", "new", "noexcept", "not", "nullptr", "operator", "or", "override",
            "private", "protected", "public", "register", "reinterpret_cast", "requires", "return", "short", "signed",
            "sizeof", "static", "static_assert", "static_cast", "struct", "switch", "template", "this", "thread_local",
            "throw", "true", "try", "typedef", "typeid", "typename", "union", "unsigned", "using", "virtual", "void",
            "volatile", "wchar_t", "while", "__forceinline", "__declspec", "__cdecl", "__stdcall", "__int64",
        };

        public static bool IsKeyword(string word) => word != null && Keywords.Contains(word);

        /// <summary>Lexes one line (without its line break) starting in <paramref name="state"/>; returns the state at its end.</summary>
        public static CppLexState LexLine(string line, CppLexState state, List<CppToken> output)
        {
            line = line ?? string.Empty;
            int n = line.Length, i = 0;
            if (state.InComment)
            {
                int end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    Add(output, 0, n, CppTokenKind.Comment);
                    return state;
                }
                Add(output, 0, end + 2, CppTokenKind.Comment);
                i = end + 2;
                state = CppLexState.Normal;
            }
            else if (state.RawDelimiter != null)
            {
                int end = line.IndexOf(state.RawDelimiter, StringComparison.Ordinal);
                if (end < 0)
                {
                    Add(output, 0, n, CppTokenKind.String);
                    return state;
                }
                int stop = end + state.RawDelimiter.Length;
                Add(output, 0, stop, CppTokenKind.String);
                i = stop;
                state = CppLexState.Normal;
            }

            bool lineStart = true; // only whitespace so far → '#' starts a directive
            for (int k = 0; k < i; k++) if (!char.IsWhiteSpace(line[k])) lineStart = false;
            bool inInclude = false;
            while (i < n)
            {
                char c = line[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                char next = i + 1 < n ? line[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    Add(output, i, n - i, CppTokenKind.Comment);
                    return state;
                }
                if (c == '/' && next == '*')
                {
                    int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        Add(output, i, n - i, CppTokenKind.Comment);
                        return new CppLexState(true, null);
                    }
                    Add(output, i, end + 2 - i, CppTokenKind.Comment);
                    i = end + 2;
                    continue;
                }
                if (c == '#' && lineStart)
                {
                    int j = i + 1;
                    while (j < n && (line[j] == ' ' || line[j] == '\t')) j++;
                    int w = j;
                    while (w < n && IsIdent(line[w])) w++;
                    Add(output, i, w - i, CppTokenKind.Preprocessor);
                    inInclude = string.CompareOrdinal(line, j, "include", 0, 7) == 0 && w - j == 7;
                    lineStart = false;
                    i = w;
                    continue;
                }
                lineStart = false;
                if (c == '<' && inInclude)
                {
                    int end = line.IndexOf('>', i + 1);
                    int stop = end < 0 ? n : end + 1;
                    Add(output, i, stop - i, CppTokenKind.String);
                    i = stop;
                    continue;
                }
                if (IsIdentStart(c))
                {
                    int s = i;
                    while (i < n && IsIdent(line[i])) i++;
                    // String / char literal prefixes: L u U u8 R LR uR u8R UR.
                    if (i < n && (line[i] == '"' || line[i] == '\'') && IsLiteralPrefix(line, s, i - s))
                    {
                        bool raw = line[i - 1] == 'R' && line[i] == '"';
                        if (raw)
                        {
                            var st = LexRaw(line, s, i, output, out int rawEnd);
                            if (st.HasValue) return st.Value;
                            i = rawEnd;
                            continue;
                        }
                        i = LexQuoted(line, s, i, output);
                        continue;
                    }
                    if (Keywords.Contains(line.Substring(s, i - s))) Add(output, s, i - s, CppTokenKind.Keyword);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    i = LexQuoted(line, i, i, output);
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
                {
                    int s = i;
                    i++;
                    while (i < n)
                    {
                        char d = line[i];
                        if (char.IsLetterOrDigit(d) || d == '.' || d == '_' || d == '\'') { i++; continue; }
                        if ((d == '+' || d == '-') && (line[i - 1] == 'e' || line[i - 1] == 'E' || line[i - 1] == 'p' || line[i - 1] == 'P')
                            && !(line[s] == '0' && s + 1 < n && (line[s + 1] == 'x' || line[s + 1] == 'X') && (line[i - 1] == 'e' || line[i - 1] == 'E')))
                        {
                            i++;
                            continue;
                        }
                        break;
                    }
                    Add(output, s, i - s, CppTokenKind.Number);
                    continue;
                }
                i++;
            }
            return state;
        }

        /// <summary>Raw string from <paramref name="start"/>: null when it closes on this line (at <paramref name="end"/>).</summary>
        private static CppLexState? LexRaw(string line, int start, int quote, List<CppToken> output, out int end)
        {
            int n = line.Length;
            end = n;
            int open = line.IndexOf('(', quote + 1);
            if (open < 0 || open - quote - 1 > 16)
            {
                // Malformed raw string: colour the rest of the line.
                Add(output, start, n - start, CppTokenKind.String);
                return null;
            }
            string closing = ")" + line.Substring(quote + 1, open - quote - 1) + "\"";
            int close = line.IndexOf(closing, open + 1, StringComparison.Ordinal);
            if (close < 0)
            {
                Add(output, start, n - start, CppTokenKind.String);
                return new CppLexState(false, closing);
            }
            end = close + closing.Length;
            Add(output, start, end - start, CppTokenKind.String);
            return null;
        }

        private static int LexQuoted(string line, int start, int quote, List<CppToken> output)
        {
            char q = line[quote];
            int n = line.Length, i = quote + 1;
            while (i < n)
            {
                char c = line[i];
                if (c == '\\') { i += 2; continue; }
                i++;
                if (c == q) break;
            }
            i = Math.Min(i, n);
            Add(output, start, i - start, CppTokenKind.String);
            return i;
        }

        private static bool IsLiteralPrefix(string line, int start, int length)
        {
            switch (length)
            {
                case 1: return line[start] == 'L' || line[start] == 'u' || line[start] == 'U' || line[start] == 'R';
                case 2: return string.CompareOrdinal(line, start, "u8", 0, 2) == 0 || (line[start + 1] == 'R' && (line[start] == 'L' || line[start] == 'u' || line[start] == 'U'));
                case 3: return string.CompareOrdinal(line, start, "u8R", 0, 3) == 0;
                default: return false;
            }
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static void Add(List<CppToken> output, int start, int length, CppTokenKind kind)
        {
            if (output != null && length > 0) output.Add(new CppToken(start, length, kind));
        }

        /// <summary>
        /// Lexer state at the start of each line of <paramref name="text"/>, split the way the VS editor splits lines
        /// (\r\n, \r, \n, U+0085, U+2028, U+2029). Lines that start in <see cref="CppLexState.Normal"/> are not stored:
        /// the result maps line number → state only for lines inside a block comment or raw string.
        /// </summary>
        public static Dictionary<int, CppLexState> LineStartStates(string text, out int lineCount)
        {
            var states = new Dictionary<int, CppLexState>();
            text = text ?? string.Empty;
            var state = CppLexState.Normal;
            int line = 0, i = 0, n = text.Length;
            while (true)
            {
                int s = i;
                while (i < n && !IsBreak(text[i])) i++;
                if (!state.IsNormal) states[line] = state;
                string lineText = text.Substring(s, i - s);
                state = LexLine(lineText, state, null);
                if (i >= n) break;
                if (text[i] == '\r' && i + 1 < n && text[i + 1] == '\n') i += 2;
                else i++;
                line++;
            }
            lineCount = line + 1;
            return states;
        }

        private static bool IsBreak(char c) => c == '\n' || c == '\r' || c == '\u0085' || c == '\u2028' || c == '\u2029';
    }
}
