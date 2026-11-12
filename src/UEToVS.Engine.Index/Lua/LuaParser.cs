using System;
using System.Collections.Generic;
using System.Text;

namespace UEToVS.Engine.Index.Lua
{
    /// <summary>
    /// Fast, error-tolerant declaration scanner for Lua 5.x (and UnLua / sluaunreal / EmmyLua-annotated) scripts. It is not
    /// a full parser: it tokenizes (comments, short and long strings, numbers, operators), tracks block / brace / paren
    /// nesting and recognizes the declaration shapes agents look for:
    /// <list type="bullet">
    /// <item><c>function a.b:c(...)</c>, <c>local function f(...)</c> (any depth)</item>
    /// <item><c>[local] a.b = function(...)</c>, <c>= {...}</c>, <c>= class(...)</c>, <c>= value</c> (file level only, so <c>self.x = …</c> inside methods is not noise)</item>
    /// <item><c>name = …</c> fields of a table constructor assigned to a named table (nested tables too)</item>
    /// <item><c>---@class Name : Base</c> annotations</item>
    /// </list>
    /// Malformed input never throws; unbalanced nesting just ends the file.
    /// </summary>
    public static class LuaParser
    {
        private const int MaxSignature = 160;

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
            "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
        };

        /// <summary>Last segment of a constructor call that makes the assigned value a class (lower-cased).</summary>
        private static readonly HashSet<string> ClassFactories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "class", "Class", "extend", "subclass", "createclass", "declareclass", "defineclass", "newclass", "inherit",
        };

        private enum T : byte { Name, Keyword, String, Number, Op, Eof }

        private struct Token
        {
            public T Type;
            public string Text;
            public int Line;
            public int Col;
            public bool Is(string op) => (Type == T.Op || Type == T.Keyword) && Text == op;
        }

        public static List<LuaSymbol> Parse(string text)
        {
            var symbols = new List<LuaSymbol>();
            if (string.IsNullOrEmpty(text)) return symbols;
            var lines = new LineIndex(text);
            var tokens = Tokenize(text, lines, symbols);
            new Scanner(tokens, lines, text, symbols).Run();
            symbols.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
            return symbols;
        }

        // ================================================================== lines

        private sealed class LineIndex
        {
            private readonly List<int> _starts = new List<int> { 0 };
            private readonly string _text;

            public LineIndex(string text)
            {
                _text = text;
                for (int i = 0; i < text.Length; i++)
                    if (text[i] == '\n') _starts.Add(i + 1);
            }

            public (int Line, int Col) At(int offset)
            {
                int lo = 0, hi = _starts.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (_starts[mid] <= offset) lo = mid; else hi = mid - 1;
                }
                return (lo, offset - _starts[lo]);
            }

            public string LineText(int line)
            {
                if (line < 0 || line >= _starts.Count) return string.Empty;
                int start = _starts[line];
                int end = line + 1 < _starts.Count ? _starts[line + 1] : _text.Length;
                return _text.Substring(start, end - start).TrimEnd('\r', '\n');
            }
        }

        // ================================================================== lexer

        private static List<Token> Tokenize(string s, LineIndex lines, List<LuaSymbol> symbols)
        {
            var list = new List<Token>(s.Length / 4);
            int i = 0, n = s.Length;
            if (n >= 2 && s[0] == '#' && s[1] == '!') // shebang
                while (i < n && s[i] != '\n') i++;
            while (i < n)
            {
                char c = s[i];
                if (c == '\n' || c == '\r' || c == ' ' || c == '\t' || c == '\f' || c == '\v' || c == '﻿') { i++; continue; }
                int start = i;
                if (c == '-' && i + 1 < n && s[i + 1] == '-')
                {
                    i += 2;
                    int level = LongBracketLevel(s, i);
                    if (level >= 0)
                    {
                        i = SkipLongBracket(s, i, level);
                        continue;
                    }
                    int eol = s.IndexOf('\n', i);
                    if (eol < 0) eol = n;
                    Annotation(s, start, eol, lines, symbols);
                    i = eol;
                    continue;
                }
                if (IsIdentStart(c))
                {
                    while (i < n && IsIdentPart(s[i])) i++;
                    string word = s.Substring(start, i - start);
                    Add(list, Keywords.Contains(word) ? T.Keyword : T.Name, word, start, lines);
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
                {
                    i = SkipNumber(s, i);
                    Add(list, T.Number, s.Substring(start, i - start), start, lines);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    i++;
                    while (i < n && s[i] != c && s[i] != '\n')
                    {
                        if (s[i] == '\\' && i + 1 < n) i++;
                        i++;
                    }
                    if (i < n && s[i] == c) i++;
                    Add(list, T.String, s.Substring(start, i - start), start, lines);
                    continue;
                }
                if (c == '[')
                {
                    int level = LongBracketLevel(s, i);
                    if (level >= 0)
                    {
                        i = SkipLongBracket(s, i, level);
                        Add(list, T.String, "[[...]]", start, lines);
                        continue;
                    }
                }
                string op = Operator(s, i);
                i += op.Length;
                Add(list, T.Op, op, start, lines);
            }
            list.Add(new Token { Type = T.Eof, Text = string.Empty, Line = int.MaxValue });
            return list;
        }

        private static void Add(List<Token> list, T type, string text, int offset, LineIndex lines)
        {
            var (line, col) = lines.At(offset);
            list.Add(new Token { Type = type, Text = text, Line = line, Col = col });
        }

        private static bool IsIdentStart(char c) => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c > 0x7F && char.IsLetter(c);
        private static bool IsIdentPart(char c) => IsIdentStart(c) || (c >= '0' && c <= '9');

        /// <summary>Level of a long bracket opening at <paramref name="i"/> (<c>[[</c> = 0, <c>[==[</c> = 2), or -1.</summary>
        private static int LongBracketLevel(string s, int i)
        {
            if (i >= s.Length || s[i] != '[') return -1;
            int j = i + 1, level = 0;
            while (j < s.Length && s[j] == '=') { level++; j++; }
            return j < s.Length && s[j] == '[' ? level : -1;
        }

        private static int SkipLongBracket(string s, int i, int level)
        {
            string close = "]" + new string('=', level) + "]";
            int end = s.IndexOf(close, i + level + 2, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + close.Length;
        }

        private static int SkipNumber(string s, int i)
        {
            int n = s.Length;
            if (s[i] == '0' && i + 1 < n && (s[i + 1] == 'x' || s[i + 1] == 'X'))
            {
                i += 2;
                while (i < n && (Uri.IsHexDigit(s[i]) || s[i] == '.' || ((s[i] == 'p' || s[i] == 'P') && i + 1 < n)))
                {
                    if ((s[i] == 'p' || s[i] == 'P') && (s[i + 1] == '+' || s[i + 1] == '-')) i++;
                    i++;
                }
                return i;
            }
            while (i < n && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
            {
                if ((s[i] == 'e' || s[i] == 'E') && i + 1 < n && (s[i + 1] == '+' || s[i + 1] == '-')) i++;
                i++;
            }
            return i;
        }

        private static readonly string[] MultiOps = { "...", "==", "~=", "<=", ">=", "..", "::", "//", "<<", ">>" };

        private static string Operator(string s, int i)
        {
            foreach (var op in MultiOps)
                if (string.CompareOrdinal(s, i, op, 0, op.Length) == 0) return op;
            return s[i].ToString();
        }

        /// <summary><c>---@class Name : Base</c> (EmmyLua / LuaLS) → a class symbol on that line.</summary>
        private static void Annotation(string s, int start, int eol, LineIndex lines, List<LuaSymbol> symbols)
        {
            int i = start;
            while (i < eol && s[i] == '-') i++;
            while (i < eol && (s[i] == ' ' || s[i] == '\t')) i++;
            const string tag = "@class";
            if (string.CompareOrdinal(s, i, tag, 0, tag.Length) != 0) return;
            i += tag.Length;
            if (i < eol && !(s[i] == ' ' || s[i] == '\t')) return;
            while (i < eol && (s[i] == ' ' || s[i] == '\t')) i++;
            if (i < eol && s[i] == '(') // ---@class (exact) Name
            {
                int close = s.IndexOf(')', i);
                if (close < 0 || close > eol) return;
                i = close + 1;
                while (i < eol && (s[i] == ' ' || s[i] == '\t')) i++;
            }
            int nameStart = i;
            while (i < eol && (IsIdentPart(s[i]) || s[i] == '.')) i++;
            if (i == nameStart) return;
            string full = s.Substring(nameStart, i - nameStart).Trim('.');
            if (full.Length == 0) return;
            var (line, col) = lines.At(nameStart);
            int dot = full.LastIndexOf('.');
            symbols.Add(new LuaSymbol
            {
                Name = dot < 0 ? full : full.Substring(dot + 1),
                Container = dot < 0 ? null : full.Substring(0, dot),
                Kind = LuaSymbolKind.Class,
                Line = line,
                Column = col + (dot < 0 ? 0 : dot + 1),
                Signature = Clip(s.Substring(start, eol - start).Trim()),
            });
        }

        private static string Clip(string s)
        {
            s = s.Trim();
            return s.Length <= MaxSignature ? s : s.Substring(0, MaxSignature - 1) + "…";
        }

        // ================================================================== declarations

        private sealed class Scanner
        {
            private readonly List<Token> _t;
            private readonly LineIndex _lines;
            private readonly List<LuaSymbol> _out;

            /// <summary>Open blocks: true = function body.</summary>
            private readonly List<bool> _blocks = new List<bool>();
            private int _functionDepth;
            private int _parenDepth;

            /// <summary>Open table constructors; Container is set when the table is a named one whose fields we record.</summary>
            private readonly List<(string Container, int Blocks, int Parens)> _braces = new List<(string, int, int)>();

            public Scanner(List<Token> tokens, LineIndex lines, string text, List<LuaSymbol> output)
            {
                _t = tokens;
                _lines = lines;
                _out = output;
            }

            private static readonly Token None = new Token { Type = T.Eof, Text = string.Empty };

            private Token At(int i) => i < 0 ? None : i < _t.Count ? _t[i] : _t[_t.Count - 1];

            private bool AtFileLevel => _functionDepth == 0 && _parenDepth == 0 && _braces.Count == 0;

            public void Run()
            {
                for (int i = 0; i < _t.Count && _t[i].Type != T.Eof; i++)
                {
                    var tok = _t[i];
                    switch (tok.Type)
                    {
                        case T.Keyword:
                            switch (tok.Text)
                            {
                                case "function":
                                    NamedFunction(i);
                                    Open(true);
                                    break;
                                case "do":
                                case "if":
                                case "repeat":
                                    Open(false);
                                    break;
                                case "end":
                                case "until":
                                    Close();
                                    break;
                                case "local":
                                    if (AtFileLevel && _blocks.Count == 0 && At(i + 1).Type == T.Name) LocalStatement(i); // block locals are not file-level
                                    break;
                            }
                            break;
                        case T.Op:
                            switch (tok.Text)
                            {
                                case "(":
                                case "[":
                                    _parenDepth++;
                                    break;
                                case ")":
                                case "]":
                                    if (_parenDepth > 0) _parenDepth--;
                                    break;
                                case "{":
                                    _braces.Add((PendingTable, _blocks.Count, _parenDepth));
                                    PendingTable = null;
                                    break;
                                case "}":
                                    if (_braces.Count > 0) _braces.RemoveAt(_braces.Count - 1);
                                    break;
                            }
                            break;
                        case T.Name:
                            if (At(i - 1).Is(".") || At(i - 1).Is(":") || At(i - 1).Is("local") || At(i - 1).Is("function") || At(i - 1).Is("goto") || At(i - 1).Is("for")) break;
                            if (AtFileLevel) Assignment(i);
                            else if (InNamedTable(out string container) && (At(i - 1).Is("{") || At(i - 1).Is(",") || At(i - 1).Is(";")) && IsAssign(At(i + 1)))
                                Value(container, tok, i + 2, isLocal: false, isField: true);
                            break;
                    }
                }
            }

            /// <summary>Container for the next '{' token (set when its value is assigned to a name we track).</summary>
            private string PendingTable;

            private void Open(bool function)
            {
                _blocks.Add(function);
                if (function) _functionDepth++;
            }

            private void Close()
            {
                if (_blocks.Count == 0) return;
                bool function = _blocks[_blocks.Count - 1];
                _blocks.RemoveAt(_blocks.Count - 1);
                if (function) _functionDepth--;
                // A block ending inside an unterminated table constructor: drop the constructors opened inside it.
                while (_braces.Count > 0 && _braces[_braces.Count - 1].Blocks > _blocks.Count) _braces.RemoveAt(_braces.Count - 1);
            }

            private bool InNamedTable(out string container)
            {
                container = null;
                if (_braces.Count == 0) return false;
                var top = _braces[_braces.Count - 1];
                if (top.Container == null || top.Blocks != _blocks.Count || top.Parens != _parenDepth) return false;
                container = top.Container;
                return true;
            }

            private static bool IsAssign(Token t) => t.Type == T.Op && t.Text == "=";

            /// <summary>Reads <c>a.b.c</c> (and <c>a.b:c</c> when <paramref name="allowColon"/>) from <paramref name="i"/>; returns the index after it.</summary>
            private int Chain(int i, bool allowColon, List<Token> parts, out bool colon)
            {
                colon = false;
                parts.Add(At(i));
                i++;
                while ((At(i).Is(".") || (allowColon && At(i).Is(":"))) && At(i + 1).Type == T.Name)
                {
                    if (At(i).Is(":")) colon = true;
                    parts.Add(At(i + 1));
                    i += 2;
                    if (colon) break;
                }
                return i;
            }

            private static string Join(List<Token> parts, int count)
            {
                var sb = new StringBuilder();
                for (int k = 0; k < count; k++)
                {
                    if (k > 0) sb.Append('.');
                    sb.Append(parts[k].Text);
                }
                return sb.ToString();
            }

            /// <summary>Parameter list starting at '(' (index <paramref name="i"/>): names, '...' and the index after ')'.</summary>
            private (List<string> Params, int Next) Params(int i)
            {
                var ps = new List<string>();
                if (!At(i).Is("(")) return (ps, i);
                i++;
                while (At(i).Type != T.Eof && !At(i).Is(")"))
                {
                    if (At(i).Type == T.Name || At(i).Is("...")) ps.Add(At(i).Text);
                    else if (!At(i).Is(",")) break; // not a parameter list after all
                    i++;
                }
                return (ps, At(i).Is(")") ? i + 1 : i);
            }

            /// <summary><c>[local] function a.b:c(...)</c> at token <paramref name="i"/> (the keyword).</summary>
            private void NamedFunction(int i)
            {
                if (At(i + 1).Type != T.Name) return; // anonymous
                bool isLocal = At(i - 1).Is("local");
                var parts = new List<Token>();
                int next = Chain(i + 1, allowColon: true, parts, out bool colon);
                var (ps, _) = Params(next);
                var nameTok = parts[parts.Count - 1];
                string container = parts.Count > 1 ? Join(parts, parts.Count - 1) : null;
                string full = container == null ? nameTok.Text : container + (colon ? ":" : ".") + nameTok.Text;
                _out.Add(new LuaSymbol
                {
                    Name = nameTok.Text,
                    Container = container,
                    Kind = colon || (ps.Count > 0 && ps[0] == "self") ? LuaSymbolKind.Method : LuaSymbolKind.Function,
                    Line = nameTok.Line,
                    Column = nameTok.Col,
                    Signature = Clip((isLocal ? "local function " : "function ") + full + "(" + string.Join(", ", ps) + ")"),
                    IsLocal = isLocal,
                });
            }

            /// <summary><c>local a, b = …</c> / <c>local a</c> at file level (token <paramref name="i"/> is <c>local</c>).</summary>
            private void LocalStatement(int i)
            {
                var names = new List<Token>();
                int j = i + 1;
                while (At(j).Type == T.Name)
                {
                    names.Add(At(j));
                    j++;
                    if (At(j).Is("<") && At(j + 1).Type == T.Name && At(j + 2).Is(">")) j += 3; // <const> / <close>
                    if (!At(j).Is(",")) break;
                    j++;
                }
                if (names.Count == 0) return;
                if (IsAssign(At(j)))
                {
                    // The first value tells the first name's kind; further names are plain variables.
                    Value(null, names[0], j + 1, isLocal: true, isField: false);
                    for (int k = 1; k < names.Count; k++) AddVariable(null, names[k], true, false);
                }
                else
                {
                    foreach (var nt in names) AddVariable(null, nt, true, false);
                }
            }

            /// <summary><c>a.b.c = …</c> at file level starting at name token <paramref name="i"/>.</summary>
            private void Assignment(int i)
            {
                var parts = new List<Token>();
                int next = Chain(i, allowColon: false, parts, out _);
                if (!IsAssign(At(next))) return;
                var nameTok = parts[parts.Count - 1];
                string container = parts.Count > 1 ? Join(parts, parts.Count - 1) : null;
                Value(container, nameTok, next + 1, isLocal: false, isField: container != null);
            }

            /// <summary>Records <paramref name="nameTok"/> by what its value (at token <paramref name="v"/>) looks like.</summary>
            private void Value(string container, Token nameTok, int v, bool isLocal, bool isField)
            {
                string qualified = container == null ? nameTok.Text : container + "." + nameTok.Text;
                var val = At(v);
                if (val.Is("function"))
                {
                    var (ps, _) = Params(v + 1);
                    _out.Add(new LuaSymbol
                    {
                        Name = nameTok.Text,
                        Container = container,
                        Kind = ps.Count > 0 && ps[0] == "self" ? LuaSymbolKind.Method : LuaSymbolKind.Function,
                        Line = nameTok.Line,
                        Column = nameTok.Col,
                        Signature = Clip((isLocal ? "local " : "") + qualified + " = function(" + string.Join(", ", ps) + ")"),
                        IsLocal = isLocal,
                    });
                    return;
                }
                if (val.Is("{"))
                {
                    PendingTable = qualified;
                    _out.Add(Symbol(container, nameTok, LuaSymbolKind.Table, isLocal));
                    return;
                }
                if (val.Type == T.Name && IsClassFactory(v))
                {
                    _out.Add(Symbol(container, nameTok, LuaSymbolKind.Class, isLocal));
                    return;
                }
                AddVariable(container, nameTok, isLocal, isField);
            }

            /// <summary><c>class(...)</c>, <c>UnLua.Class(...)</c>, <c>Base:extend(...)</c>, <c>Class "X"</c> starting at <paramref name="v"/>.</summary>
            private bool IsClassFactory(int v)
            {
                int j = v;
                string last = At(j).Text;
                j++;
                while ((At(j).Is(".") || At(j).Is(":")) && At(j + 1).Type == T.Name)
                {
                    last = At(j + 1).Text;
                    j += 2;
                }
                if (!ClassFactories.Contains(last)) return false;
                var call = At(j);
                return call.Is("(") || call.Type == T.String || call.Is("{");
            }

            private void AddVariable(string container, Token nameTok, bool isLocal, bool isField) =>
                _out.Add(Symbol(container, nameTok, isField ? LuaSymbolKind.Field : LuaSymbolKind.Variable, isLocal));

            private LuaSymbol Symbol(string container, Token nameTok, LuaSymbolKind kind, bool isLocal) => new LuaSymbol
            {
                Name = nameTok.Text,
                Container = container,
                Kind = kind,
                Line = nameTok.Line,
                Column = nameTok.Col,
                Signature = Clip(_lines.LineText(nameTok.Line)),
                IsLocal = isLocal,
            };
        }
    }
}
