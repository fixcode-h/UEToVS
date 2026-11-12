using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>Kind of a brace block found by <see cref="CodeScan.EnclosingBody"/>.</summary>
    public enum BodyKind
    {
        None,
        Function,
        Lambda,
    }

    /// <summary>A function or lambda body: token indices of its braces.</summary>
    public sealed class BodyRange
    {
        public BodyKind Kind { get; set; }
        public int OpenToken { get; set; }
        public int CloseToken { get; set; }

        /// <summary>For functions: token index of the '(' of the parameter list (-1 when not found).</summary>
        public int ParamOpenToken { get; set; } = -1;
    }

    /// <summary>
    /// Statement-level helpers over <see cref="CppTextScan"/> tokens (v0.4.5 refactorings): token lookup by offset, the
    /// function / lambda body around an offset, control-statement heads. Pure and tolerant: anything unclear yields "not found".
    /// </summary>
    public static class CodeScan
    {
        public static readonly HashSet<string> ControlKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "while", "switch", "catch", "return", "sizeof", "alignof", "decltype", "static_assert", "co_return", "co_await", "co_yield", "throw",
            "new", "delete", "typeid", "noexcept", "alignas",
        };

        private static readonly HashSet<string> TrailingQualifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "const", "volatile", "override", "final", "noexcept", "mutable", "constexpr", "&", "&&",
        };

        public static int End(ScanToken t) => t.Offset + t.Text.Length;

        /// <summary>Index of the first token whose offset is &gt;= <paramref name="offset"/> (Count when none).</summary>
        public static int IndexAtOrAfter(List<ScanToken> t, int offset)
        {
            int lo = 0, hi = t.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (t[mid].Offset < offset) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>Index of the last token ending at or before <paramref name="offset"/> (-1 when none).</summary>
        public static int IndexEndingAtOrBefore(List<ScanToken> t, int offset)
        {
            int i = IndexAtOrAfter(t, offset) - 1;
            while (i >= 0 && End(t[i]) > offset) i--;
            return i;
        }

        /// <summary>Index of the '(' matching the ')' at <paramref name="close"/>, or -1.</summary>
        public static int MatchOpen(List<ScanToken> t, int close)
        {
            string c = t[close].Text, o = c == ")" ? "(" : c == "]" ? "[" : c == "}" ? "{" : null;
            if (o == null) return -1;
            int depth = 0;
            for (int k = close; k >= 0; k--)
            {
                if (t[k].Is(c)) depth++;
                else if (t[k].Is(o) && --depth == 0) return k;
            }
            return -1;
        }

        /// <summary>Open '{' token indices enclosing <paramref name="offset"/>, outermost first.</summary>
        public static List<int> OpenBraces(List<ScanToken> t, int offset)
        {
            var stack = new List<int>();
            for (int k = 0; k < t.Count && t[k].Offset < offset; k++)
            {
                if (t[k].Is("{")) stack.Add(k);
                else if (t[k].Is("}") && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            return stack;
        }

        /// <summary>Classifies the '{' at <paramref name="open"/>: function body, lambda body or anything else (class, control block, initializer).</summary>
        public static BodyKind Classify(List<ScanToken> t, int open, out int paramOpen)
        {
            paramOpen = -1;
            int k = open - 1;
            if (k < 0) return BodyKind.None;
            if (t[k].Is("]")) return BodyKind.Lambda;
            // trailing return type "-> Type" (lambdas and functions): skip back to the ')' before "->"
            int arrow = -1;
            for (int m = k; m >= 0 && m > k - 16; m--)
            {
                if (t[m].Is(">") && m > 0 && t[m - 1].Is("-")) { arrow = m - 1; break; }
                if (t[m].Is(";") || t[m].Is("{") || t[m].Is("}") || t[m].Is(")")) break;
            }
            if (arrow > 0) k = arrow - 1;
            while (k >= 0)
            {
                if (TrailingQualifiers.Contains(t[k].Text)) { k--; continue; }
                if (t[k].Is(")"))
                {
                    int o = MatchOpen(t, k);
                    if (o >= 1 && t[o - 1].Is("noexcept")) { k = o - 2; continue; } // noexcept(expr)
                }
                break;
            }
            if (k >= 0 && t[k].Is("}"))
            {
                // ctor initializer list ending with a brace initializer: ": Base(X), Health{100} {"
                int o = MatchOpen(t, k);
                return o >= 2 && t[o - 1].IsIdent && (t[o - 2].Is(",") || t[o - 2].Is(":")) && !t[o - 2].Is("::") ? BodyKind.Function : BodyKind.None;
            }
            if (k < 0 || !t[k].Is(")")) return BodyKind.None;
            int po = MatchOpen(t, k);
            if (po < 1) return BodyKind.None;
            var before = t[po - 1];
            if (before.Is("]") || (before.Is(">") && IsLambdaTemplate(t, po - 1))) { paramOpen = po; return BodyKind.Lambda; }
            if (!before.IsIdent && !before.Is(">")) return BodyKind.None;
            if (before.IsIdent && ControlKeywords.Contains(before.Text)) return BodyKind.None;
            paramOpen = po;
            return BodyKind.Function;
        }

        private static bool IsLambdaTemplate(List<ScanToken> t, int gt)
        {
            int depth = 0;
            for (int m = gt; m >= 0 && m > gt - 40; m--)
            {
                if (t[m].Is(">")) depth++;
                else if (t[m].Is("<") && --depth == 0) return m > 0 && t[m - 1].Is("]");
            }
            return false;
        }

        /// <summary>
        /// The innermost function or lambda body containing <paramref name="offset"/> (null when there is none: class body,
        /// namespace, initializer). For a ctor the body after its initializer list counts as the function body.
        /// </summary>
        public static BodyRange EnclosingBody(List<ScanToken> t, int offset, bool stopAtLambda = true)
        {
            var braces = OpenBraces(t, offset);
            for (int i = braces.Count - 1; i >= 0; i--)
            {
                int open = braces[i];
                var kind = Classify(t, open, out int po);
                if (kind == BodyKind.None) continue;
                if (kind == BodyKind.Lambda && !stopAtLambda) continue;
                return new BodyRange { Kind = kind, OpenToken = open, CloseToken = CppTextScan.MatchClose(t, open), ParamOpenToken = po };
            }
            return null;
        }

        /// <summary>Whether a lambda body starting inside [from, to) contains token <paramref name="k"/>.</summary>
        public static bool InsideLambda(List<ScanToken> t, int from, int to, int k)
        {
            for (int m = from; m < to && m < k; m++)
            {
                if (!t[m].Is("{")) continue;
                int close = CppTextScan.MatchClose(t, m);
                if (k > m && k < close && Classify(t, m, out _) == BodyKind.Lambda) return true;
            }
            return false;
        }

        /// <summary>The control keyword whose parenthesised head ends at the ')' at <paramref name="close"/> (if / for / while / switch / catch), or null.</summary>
        public static string ControlHeadOf(List<ScanToken> t, int close)
        {
            if (close < 0 || !t[close].Is(")")) return null;
            int open = MatchOpen(t, close);
            if (open < 1 || !t[open - 1].IsIdent) return null;
            string kw = t[open - 1].Text;
            return kw == "if" || kw == "for" || kw == "while" || kw == "switch" || kw == "catch" ? kw : null;
        }

        /// <summary>
        /// Token index where the statement containing token <paramref name="k"/> starts, inside the block opened at
        /// <paramref name="blockOpen"/>: after the previous ';' / '{' / '}' at the same nesting level (parentheses skipped).
        /// </summary>
        public static int StatementStart(List<ScanToken> t, int k, int blockOpen)
        {
            int depth = 0;
            for (int m = k - 1; m > blockOpen; m--)
            {
                var tok = t[m];
                if (tok.Is(")") || tok.Is("]")) depth++;
                else if (tok.Is("(") || tok.Is("[")) { if (depth > 0) depth--; }
                else if (depth == 0 && (tok.Is(";") || tok.Is("{") || tok.Is("}"))) return m + 1;
            }
            return blockOpen + 1;
        }

        /// <summary>The innermost '{' around token <paramref name="k"/> after <paramref name="limit"/> (or <paramref name="limit"/>).</summary>
        public static int InnermostBlock(List<ScanToken> t, int k, int limit)
        {
            int depth = 0;
            for (int m = k - 1; m > limit; m--)
            {
                if (t[m].Is("}")) depth++;
                else if (t[m].Is("{")) { if (depth == 0) return m; depth--; }
            }
            return limit;
        }

        /// <summary>Text with all white space removed (comparing expressions written differently).</summary>
        public static string Squash(string s)
        {
            var chars = (s ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray();
            return new string(chars);
        }

        /// <summary>Leading white space of the line containing <paramref name="offset"/>.</summary>
        public static string LineIndent(string text, int offset)
        {
            int ls = LineStart(text, offset);
            int i = ls;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(ls, i - ls);
        }

        public static int LineStart(string text, int offset)
        {
            int i = Math.Min(offset, text.Length);
            while (i > 0 && text[i - 1] != '\n') i--;
            return i;
        }

        /// <summary>Offset of the line break ending the line of <paramref name="offset"/> (the '\r' of "\r\n"), or the text length.</summary>
        public static int LineEnd(string text, int offset)
        {
            int i = text.IndexOf('\n', Math.Min(offset, text.Length));
            if (i < 0) return text.Length;
            return i > 0 && text[i - 1] == '\r' ? i - 1 : i;
        }

        /// <summary>Whether [from, to) holds only blanks.</summary>
        public static bool IsBlank(string text, int from, int to)
        {
            for (int i = Math.Max(0, from); i < Math.Min(to, text.Length); i++) if (!char.IsWhiteSpace(text[i])) return false;
            return true;
        }

        /// <summary>Whether [from, to) holds only blanks and comments.</summary>
        public static bool IsBlankOrComment(string text, int from, int to)
        {
            string s = text.Substring(Math.Max(0, from), Math.Max(0, Math.Min(to, text.Length) - Math.Max(0, from)));
            return CppTextScan.Tokenize(s).Count == 0 && s.IndexOf('#') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\'') < 0;
        }

        /// <summary>Removes the common leading white space of the non-blank lines and indents them by <paramref name="indent"/>.</summary>
        public static List<string> Reindent(IEnumerable<string> lines, string indent)
        {
            var list = lines.ToList();
            int common = int.MaxValue;
            foreach (var l in list)
            {
                if (l.Trim().Length == 0) continue;
                int n = 0;
                while (n < l.Length && (l[n] == ' ' || l[n] == '\t')) n++;
                common = Math.Min(common, n);
            }
            if (common == int.MaxValue) common = 0;
            return list.Select(l => l.Trim().Length == 0 ? string.Empty : indent + l.Substring(Math.Min(common, l.Length)).TrimEnd()).ToList();
        }

        /// <summary>Whether <paramref name="name"/> occurs as an identifier token in tokens [from, to).</summary>
        public static bool HasIdentifier(List<ScanToken> t, int from, int to, string name)
        {
            for (int k = Math.Max(0, from); k < Math.Min(to, t.Count); k++) if (t[k].IsIdent && t[k].Is(name)) return true;
            return false;
        }
    }
}
