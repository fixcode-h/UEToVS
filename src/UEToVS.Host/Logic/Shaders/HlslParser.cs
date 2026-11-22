using System;
using System.Collections.Generic;
using System.Text;

namespace UEToVS.Host.Logic.Shaders
{
    public enum HlslSymbolKind
    {
        Function,
        Struct,
        Macro,
        CBuffer,
        Variable,
        Typedef,
    }

    /// <summary>一个着色器文件里声明的名字（文件结构、转到定义、补全用）。行列从 0 起。</summary>
    public sealed class HlslSymbol
    {
        public string Name { get; set; }
        public HlslSymbolKind Kind { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        /// <summary>声明那一行去掉首尾空白，截到 160 字（悬停、补全说明显示）。</summary>
        public string Signature { get; set; }

        /// <summary>声明所在的结构体（成员变量 / 成员函数），顶层为 null。</summary>
        public string Container { get; set; }

        public override string ToString() => Kind + " " + Name + "@" + Line;
    }

    public sealed class HlslInclude
    {
        public string Path { get; set; }
        public int Line { get; set; }
    }

    /// <summary>一次解析的结果：按出现顺序的声明和 #include。</summary>
    public sealed class HlslDocument
    {
        public static readonly HlslDocument Empty = new HlslDocument();

        public List<HlslSymbol> Symbols { get; } = new List<HlslSymbol>();
        public List<HlslInclude> Includes { get; } = new List<HlslInclude>();
    }

    /// <summary>
    /// .usf / .ush 的轻量声明解析：顶层函数定义、struct、cbuffer、全局变量 / 资源、typedef 和 #define，外加 #include 列表。
    /// 不做预处理（#if 两边的定义都记下），不进主 C++ 索引；只在打开文件、转到定义、补全或文件结构时按需跑一次（一般几 ms）。纯逻辑。
    /// </summary>
    public static class HlslParser
    {
        /// <summary>超过这个大小的文件不解析（生成的巨型 shader），返回空结果。</summary>
        public const int MaxLength = 4 * 1024 * 1024;

        private static readonly HashSet<string> NotTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "return", "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "discard", "struct",
            "cbuffer", "tbuffer", "typedef", "namespace", "using", "true", "false", "sizeof",
        };

        /// <summary>出现在类型前面、不算返回类型的修饰词。</summary>
        private static readonly HashSet<string> Modifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "static", "const", "inline", "uniform", "groupshared", "extern", "precise", "volatile", "row_major", "column_major",
            "nointerpolation", "linear", "centroid", "noperspective", "sample", "shared", "globallycoherent", "export",
        };

        private enum Tk : byte { Ident, Punct, Number }

        private struct Tok
        {
            public Tk Kind;
            public int Start;
            public int Length;
            public char Ch;
        }

        public static HlslDocument Parse(string text)
        {
            var doc = new HlslDocument();
            if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return doc;
            var lineStarts = LineStarts(text);
            string code = Strip(text, doc, lineStarts);
            var toks = Tokenize(code);
            Scan(text, code, toks, lineStarts, doc);
            doc.Symbols.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
            return doc;
        }

        private static List<int> LineStarts(string text)
        {
            var list = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') list.Add(i + 1);
            return list;
        }

        private static int LineOf(List<int> starts, int offset)
        {
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (starts[mid] <= offset) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>
        /// 注释、字符串换成空格（保留偏移）；预处理行整行抹掉，顺手记下 #define 和 #include。
        /// </summary>
        private static string Strip(string text, HlslDocument doc, List<int> lineStarts)
        {
            var c = text.ToCharArray();
            int i = 0;
            bool lineStart = true;
            while (i < c.Length)
            {
                char ch = c[i];
                if (ch == '\n')
                {
                    lineStart = true;
                    i++;
                    continue;
                }
                if (lineStart && (ch == ' ' || ch == '\t' || ch == '\r'))
                {
                    i++;
                    continue;
                }
                if (lineStart && ch == '#')
                {
                    i = Directive(text, c, i, doc, lineStarts);
                    continue;
                }
                lineStart = false;
                if (ch == '/' && i + 1 < c.Length && c[i + 1] == '/')
                {
                    while (i < c.Length && c[i] != '\n') c[i++] = ' ';
                    continue;
                }
                if (ch == '/' && i + 1 < c.Length && c[i + 1] == '*')
                {
                    while (i < c.Length && !(c[i] == '*' && i + 1 < c.Length && c[i + 1] == '/'))
                    {
                        if (c[i] != '\n') c[i] = ' ';
                        i++;
                    }
                    if (i < c.Length) c[i++] = ' ';
                    if (i < c.Length) c[i++] = ' ';
                    continue;
                }
                if (ch == '"' || ch == '\'')
                {
                    char q = ch;
                    c[i++] = ' ';
                    while (i < c.Length && c[i] != q && c[i] != '\n')
                    {
                        if (c[i] == '\\' && i + 1 < c.Length && c[i + 1] != '\n') c[i++] = ' ';
                        c[i++] = ' ';
                    }
                    if (i < c.Length && c[i] == q) c[i++] = ' ';
                    continue;
                }
                i++;
            }
            return new string(c);
        }

        /// <summary>抹掉从 <paramref name="i"/> 起的预处理指令（含 \ 续行），返回下一行开头。</summary>
        private static int Directive(string text, char[] c, int i, HlslDocument doc, List<int> lineStarts)
        {
            int start = i;
            int end = i;
            while (end < c.Length)
            {
                if (c[end] == '\n')
                {
                    int k = end - 1;
                    if (k >= 0 && c[k] == '\r') k--;
                    if (k >= start && c[k] == '\\')
                    {
                        end++;
                        continue;
                    }
                    break;
                }
                end++;
            }
            string line = text.Substring(start, end - start);
            int p = 1;
            while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
            int ws = p;
            while (p < line.Length && char.IsLetter(line[p])) p++;
            string word = line.Substring(ws, p - ws);
            if (word == "define")
            {
                while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
                int ns = p;
                while (p < line.Length && (char.IsLetterOrDigit(line[p]) || line[p] == '_')) p++;
                if (p > ns && !char.IsDigit(line[ns]))
                {
                    int lineNo = LineOf(lineStarts, start);
                    doc.Symbols.Add(new HlslSymbol
                    {
                        Name = line.Substring(ns, p - ns), Kind = HlslSymbolKind.Macro, Line = lineNo,
                        Column = start + ns - lineStarts[lineNo], Signature = Sig(FirstLine(line)),
                    });
                }
            }
            else if (word == "include")
            {
                string inc = HlslLexer.IncludeAt(FirstLine(line), -1, out _, out _);
                if (!string.IsNullOrWhiteSpace(inc)) doc.Includes.Add(new HlslInclude { Path = inc.Trim(), Line = LineOf(lineStarts, start) });
            }
            for (int k = start; k < end; k++)
                if (c[k] != '\n' && c[k] != '\r') c[k] = ' ';
            return end;
        }

        private static string FirstLine(string s)
        {
            int n = s.IndexOf('\n');
            return (n < 0 ? s : s.Substring(0, n)).TrimEnd('\r', '\\', ' ', '\t');
        }

        private static string Sig(string s)
        {
            s = (s ?? string.Empty).Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "…" : s;
        }

        private static List<Tok> Tokenize(string code)
        {
            var list = new List<Tok>(code.Length / 6);
            int i = 0;
            while (i < code.Length)
            {
                char ch = code[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (char.IsLetter(ch) || ch == '_')
                {
                    int s = i;
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                    list.Add(new Tok { Kind = Tk.Ident, Start = s, Length = i - s });
                    continue;
                }
                if (char.IsDigit(ch))
                {
                    int s = i;
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '.' || code[i] == '_')) i++;
                    list.Add(new Tok { Kind = Tk.Number, Start = s, Length = i - s });
                    continue;
                }
                list.Add(new Tok { Kind = Tk.Punct, Start = i, Length = 1, Ch = ch });
                i++;
            }
            return list;
        }

        private static void Scan(string text, string code, List<Tok> t, List<int> lineStarts, HlslDocument doc)
        {
            string W(int k) => code.Substring(t[k].Start, t[k].Length);
            bool P(int k, char ch) => k >= 0 && k < t.Count && t[k].Kind == Tk.Punct && t[k].Ch == ch;
            bool I(int k) => k >= 0 && k < t.Count && t[k].Kind == Tk.Ident;

            void Add(int k, HlslSymbolKind kind, string container)
            {
                int line = LineOf(lineStarts, t[k].Start);
                int ls = lineStarts[line];
                int le = line + 1 < lineStarts.Count ? lineStarts[line + 1] : text.Length;
                doc.Symbols.Add(new HlslSymbol
                {
                    Name = W(k), Kind = kind, Line = line, Column = t[k].Start - ls, Signature = Sig(text.Substring(ls, le - ls)), Container = container,
                });
            }

            // 括号层级：只在顶层（depth 0）和 struct 体内（structDepth）认声明
            int depth = 0;
            var structStack = new Stack<(string Name, int Depth)>();
            int stmtStart = 0; // 当前语句的第一个 token
            for (int k = 0; k < t.Count; k++)
            {
                var tok = t[k];
                if (tok.Kind == Tk.Punct)
                {
                    switch (tok.Ch)
                    {
                        case '{':
                            depth++;
                            stmtStart = k + 1;
                            continue;
                        case '}':
                            depth = Math.Max(0, depth - 1);
                            if (structStack.Count > 0 && structStack.Peek().Depth == depth + 1) structStack.Pop();
                            stmtStart = k + 1;
                            continue;
                        case ';':
                            stmtStart = k + 1;
                            continue;
                    }
                    continue;
                }
                if (tok.Kind != Tk.Ident) continue;
                bool inStruct = structStack.Count > 0 && structStack.Peek().Depth == depth;
                if (depth != 0 && !inStruct) continue;
                string container = inStruct ? structStack.Peek().Name : null;
                string w = W(k);

                if ((w == "struct" || w == "class" || w == "interface") && I(k + 1))
                {
                    // struct Name { … } / struct Name : Base { … }；前置声明（struct Name;）不算
                    int j = k + 2;
                    if (P(j, ':')) { j++; while (j < t.Count && !P(j, '{') && !P(j, ';')) j++; }
                    if (P(j, '{'))
                    {
                        Add(k + 1, HlslSymbolKind.Struct, container);
                        structStack.Push((W(k + 1), depth + 1));
                        depth++;
                        k = j;
                        stmtStart = k + 1;
                    }
                    continue;
                }
                if ((w == "cbuffer" || w == "tbuffer") && I(k + 1))
                {
                    Add(k + 1, HlslSymbolKind.CBuffer, null);
                    // cbuffer X [: register(b0)] { … }：HLSL 里成员是全局名字，所以 Container 记 null
                    int j = k + 2;
                    while (j < t.Count && j < k + 12 && !P(j, '{') && !P(j, ';')) j++;
                    if (P(j, '{'))
                    {
                        structStack.Push((null, depth + 1));
                        depth++;
                        k = j;
                        stmtStart = k + 1;
                    }
                    continue;
                }
                if (w == "typedef" && depth == 0)
                {
                    int j = k + 1;
                    while (j < t.Count && !P(j, ';')) j++;
                    if (j - 1 > k + 1 && I(j - 1)) Add(j - 1, HlslSymbolKind.Typedef, null);
                    k = j;
                    stmtStart = k + 1;
                    continue;
                }
                if (k != stmtStart && !IsTypeStart(k)) continue;

                // 声明：[修饰词] 类型 [<…>] 名字 ( … ) [: SEMANTIC] { / ; ，或 类型 名字 [ ; = : [ ,
                int q = k;
                while (I(q) && Modifiers.Contains(W(q))) q++;
                if (!I(q) || NotTypes.Contains(W(q))) continue;
                int afterType = q + 1;
                if (P(afterType, '<'))
                {
                    int angle = 0;
                    for (; afterType < t.Count; afterType++)
                    {
                        if (P(afterType, '<')) angle++;
                        else if (P(afterType, '>') && --angle == 0) { afterType++; break; }
                        else if (P(afterType, ';') || P(afterType, '{')) break;
                    }
                }
                if (!I(afterType) || NotTypes.Contains(W(afterType))) continue;
                int name = afterType;
                if (P(name + 1, '('))
                {
                    int close = Match(t, name + 1, '(', ')');
                    if (close < 0) continue;
                    int j = close + 1;
                    if (P(j, ':')) { j++; while (I(j) || P(j, '(') || P(j, ')') || t.Count > j && t[j].Kind == Tk.Number) j++; }
                    // 函数后面可能跟着 UE 的属性宏，如 [numthreads(8,8,1)] 在前、这里只看 { 或 ;
                    if (P(j, '{'))
                    {
                        Add(name, HlslSymbolKind.Function, container);
                        int end = Match(t, j, '{', '}');
                        k = end < 0 ? t.Count : end;
                        stmtStart = k + 1;
                    }
                    else if (P(j, ';') && depth == 0)
                    {
                        k = j;
                        stmtStart = k + 1;
                    }
                    continue;
                }
                if (P(name + 1, ';') || P(name + 1, '=') || P(name + 1, ':') || P(name + 1, '[') || P(name + 1, ','))
                {
                    Add(name, HlslSymbolKind.Variable, container);
                    // 同一语句里逗号分开的其他名字（float A, B;）
                    int j = name + 1;
                    int par = 0;
                    for (; j < t.Count && !(P(j, ';') && par == 0); j++)
                    {
                        if (P(j, '(') || P(j, '{') || P(j, '[')) par++;
                        else if (P(j, ')') || P(j, '}') || P(j, ']')) { if (--par < 0) break; }
                        else if (par == 0 && P(j, ',') && I(j + 1) && (P(j + 2, ';') || P(j + 2, ',') || P(j + 2, '=') || P(j + 2, '[') || P(j + 2, ':')))
                            Add(j + 1, HlslSymbolKind.Variable, container);
                    }
                    k = Math.Max(k, j - 1);
                }
            }

            bool IsTypeStart(int k)
            {
                // [numthreads(…)] 之类的属性结束后也算语句开头
                int p = k - 1;
                return P(p, ']') && Bracket(p);
            }

            bool Bracket(int p)
            {
                int d = 0;
                for (int j = p; j >= 0; j--)
                {
                    if (P(j, ']')) d++;
                    else if (P(j, '[') && --d == 0) return j == stmtStart || j - 1 >= 0 && P(j - 1, ']') && Bracket(j - 1);
                }
                return false;
            }
        }

        private static int Match(List<Tok> t, int open, char o, char c)
        {
            int d = 0;
            for (int j = open; j < t.Count; j++)
            {
                if (t[j].Kind != Tk.Punct) continue;
                if (t[j].Ch == o) d++;
                else if (t[j].Ch == c && --d == 0) return j;
            }
            return -1;
        }

        /// <summary>行内 <paramref name="column"/> 处（或其左边紧挨着）的标识符，没有时返回 null。</summary>
        public static string WordAt(string line, int column, out int start)
        {
            start = -1;
            if (string.IsNullOrEmpty(line) || column < 0 || column > line.Length) return null;
            int s = column, e = column;
            while (s > 0 && IsWord(line[s - 1])) s--;
            while (e < line.Length && IsWord(line[e])) e++;
            if (e <= s || char.IsDigit(line[s])) return null;
            start = s;
            return line.Substring(s, e - s);
        }

        public static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>光标前面紧挨着的是不是成员访问（a.b 的 b），这时文件内的同名顶层定义多半不是要找的。</summary>
        public static bool IsMemberAccess(string line, int wordStart)
        {
            int i = wordStart - 1;
            while (i >= 0 && line[i] == ' ') i--;
            return i >= 0 && line[i] == '.';
        }

        public static string Describe(HlslSymbolKind kind, bool english)
        {
            switch (kind)
            {
                case HlslSymbolKind.Function: return english ? "function" : "函数";
                case HlslSymbolKind.Struct: return english ? "struct" : "结构体";
                case HlslSymbolKind.Macro: return english ? "macro" : "宏";
                case HlslSymbolKind.CBuffer: return "cbuffer";
                case HlslSymbolKind.Typedef: return "typedef";
                default: return english ? "variable" : "变量";
            }
        }
    }
}
