using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>What a macro expansion step shows in Quick Info.</summary>
    public enum MacroStepKind
    {
        /// <summary>The macro's body with the arguments substituted.</summary>
        OneLevel = 0,

        /// <summary>Every nested macro expanded.</summary>
        Full = 1,

        /// <summary>The full expansion with integer constant sub-expressions computed.</summary>
        Folded = 2,
    }

    public sealed class MacroStep
    {
        public MacroStep(MacroStepKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public MacroStepKind Kind { get; }

        public string Text { get; }

        public string Label => IntelliSenseStrings.MacroStepLabel(Kind);
    }

    /// <summary>
    /// Makes macro expansions readable for Quick Info: drops the parentheses macro authors add for safety where they
    /// change nothing (<c>((o))-&gt;tt_</c> → <c>o-&gt;tt_</c>), computes integer constants
    /// (<c>((2) | ((0) &lt;&lt; 4))</c> → <c>2</c>) as a separate last step so the formula stays visible, and breaks long
    /// declaration lists (GENERATED_BODY and friends) at <c>;</c> / braces with indentation.
    /// Conservative by design: a parenthesis that could be a cast, a call or a keyword's operand is kept.
    /// </summary>
    public static class MacroExpansionFormatter
    {
        /// <summary>Bigger inputs are shown as they are (the passes are linear but repeated).</summary>
        public const int MaxTokens = 6000;

        /// <summary>Lines longer than this are broken at top-level <c>;</c> and braces.</summary>
        public const int WrapAt = 110;

        public static IReadOnlyList<MacroStep> Steps(string oneLevel, string full)
        {
            var steps = new List<MacroStep>();
            if (string.IsNullOrWhiteSpace(oneLevel) && string.IsNullOrWhiteSpace(full)) return steps;
            string one = Simplify(oneLevel ?? full);
            steps.Add(new MacroStep(MacroStepKind.OneLevel, one));
            string last = one;
            if (!string.IsNullOrWhiteSpace(full) && !string.Equals(Normalize(full), Normalize(oneLevel), StringComparison.Ordinal))
            {
                string f = Simplify(full);
                if (!string.Equals(f, one, StringComparison.Ordinal)) steps.Add(new MacroStep(MacroStepKind.Full, f));
                last = f;
            }
            string folded = Fold(last);
            if (!string.Equals(folded, last, StringComparison.Ordinal)) steps.Add(new MacroStep(MacroStepKind.Folded, folded));
            return steps;
        }

        /// <summary>Removes redundant parentheses and wraps long text; no constant folding.</summary>
        public static string Simplify(string text) => Run(text, fold: false);

        /// <summary><see cref="Simplify"/> plus integer constant folding.</summary>
        public static string Fold(string text) => Run(text, fold: true);

        private static string Normalize(string s) => s == null ? string.Empty : string.Join(" ", s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

        private static string Run(string text, bool fold)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            text = text.Trim();
            var toks = Lex(text);
            if (toks == null || toks.Count > MaxTokens) return text;
            for (int pass = 0; pass < 64; pass++)
            {
                bool changed = RemoveParens(toks);
                if (fold) changed |= FoldConstants(toks);
                if (!changed) break;
            }
            return Render(toks);
        }

        // ---- tokens ----

        private enum TK { Ident, Number, Str, Punct }

        private sealed class Tok
        {
            public Tok(TK kind, string text, bool space, bool newline = false)
            {
                Kind = kind;
                Text = text;
                Space = space;
                NewLine = newline;
            }

            public TK Kind;
            public string Text;

            /// <summary>Whitespace before the token in the source (kept so the author's spacing survives).</summary>
            public bool Space;

            /// <summary>A line break before the token in the source (multi-line expansions keep their lines).</summary>
            public bool NewLine;
        }

        private static readonly string[] Puncts3 = { "<<=", ">>=", "...", "->*" };
        private static readonly string[] Puncts2 = { "->", "::", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "##" };

        private static List<Tok> Lex(string s)
        {
            var list = new List<Tok>();
            int i = 0;
            bool space = false, newline = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { space = true; newline |= c == '\n'; i++; continue; }
                int start = i;
                TK kind;
                if (char.IsLetter(c) || c == '_')
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    // string prefixes: L"x", u8"x", TEXT stays an identifier
                    if (i < s.Length && (s[i] == '"' || s[i] == '\'') && IsStringPrefix(s.Substring(start, i - start)))
                    {
                        i = SkipQuoted(s, i);
                        if (i < 0) return null;
                        kind = TK.Str;
                    }
                    else kind = TK.Ident;
                }
                else if (char.IsDigit(c) || c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.' || s[i] == '\''
                                            || (s[i] == '+' || s[i] == '-') && (s[i - 1] == 'e' || s[i - 1] == 'E' || s[i - 1] == 'p' || s[i - 1] == 'P') && !IsHex(s, start))) i++;
                    kind = TK.Number;
                }
                else if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(s, i);
                    if (i < 0) return null;
                    kind = TK.Str;
                }
                else
                {
                    string p = Puncts3.FirstOrDefault(x => string.CompareOrdinal(s, i, x, 0, 3) == 0)
                               ?? Puncts2.FirstOrDefault(x => string.CompareOrdinal(s, i, x, 0, 2) == 0)
                               ?? c.ToString();
                    i += p.Length;
                    kind = TK.Punct;
                }
                list.Add(new Tok(kind, s.Substring(start, i - start), space, newline && list.Count > 0));
                space = newline = false;
            }
            return list;
        }

        private static bool IsHex(string s, int start) => start + 1 < s.Length && s[start] == '0' && (s[start + 1] == 'x' || s[start + 1] == 'X');

        private static bool IsStringPrefix(string p) => p == "L" || p == "u" || p == "U" || p == "u8" || p == "R" || p == "LR" || p == "uR" || p == "UR" || p == "u8R";

        private static int SkipQuoted(string s, int i)
        {
            char q = s[i++];
            while (i < s.Length && s[i] != q)
            {
                if (s[i] == '\\') i++;
                i++;
            }
            return i < s.Length ? i + 1 : -1;
        }

        // ---- parentheses ----

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "return", "sizeof", "alignof", "decltype", "typeid", "noexcept", "if", "while", "for", "switch", "case",
            "throw", "co_return", "co_yield", "co_await", "static_assert", "new", "delete", "and", "or", "not",
        };

        /// <summary>'(' that only groups (not a call, a keyword's operand, a declarator or a function type).</summary>
        private static bool IsGroupingOpen(List<Tok> t, int i)
        {
            if (i == 0) return true;
            var p = t[i - 1];
            if (p.Kind == TK.Ident || p.Kind == TK.Number || p.Kind == TK.Str) return false;
            return p.Text != ")" && p.Text != "]" && p.Text != ">" && p.Text != "#";
        }

        private static int[] Match(List<Tok> t)
        {
            var match = new int[t.Count];
            var stack = new Stack<int>();
            for (int i = 0; i < t.Count; i++)
            {
                match[i] = -1;
                if (t[i].Kind != TK.Punct) continue;
                if (t[i].Text == "(") stack.Push(i);
                else if (t[i].Text == ")")
                {
                    if (stack.Count == 0) return null; // unbalanced: leave the text alone
                    int o = stack.Pop();
                    match[o] = i;
                    match[i] = o;
                }
            }
            return stack.Count == 0 ? match : null;
        }

        private static bool RemoveParens(List<Tok> t)
        {
            var match = Match(t);
            if (match == null) return false;
            for (int o = 0; o < t.Count; o++)
            {
                if (t[o].Text != "(" || t[o].Kind != TK.Punct || !IsGroupingOpen(t, o)) continue;
                int c = match[o];
                if (c <= o + 1) continue; // "()" stays
                bool remove;
                if (c == o + 2)
                {
                    var only = t[o + 1];
                    // (2) / ("x") never change meaning; (o) does unless it is a cast: "(T)x", "(T)(x)", "(T)-x".
                    remove = only.Kind == TK.Number || only.Kind == TK.Str || only.Kind == TK.Ident && !Keywords.Contains(only.Text) && !LooksLikeCastOperand(t, c + 1);
                }
                else if (t[o + 1].Text == "(" && match[o + 1] == c - 1 && IsGroupingOpen(t, o + 1))
                {
                    remove = true; // ((x)) → (x)
                }
                // a type never contains '.' or '->', so only a pure "ns::x" chain could be a cast
                else if (IsMemberChain(t, o + 1, c - 1) && (HasMemberAccess(t, o + 1, c - 1) || !LooksLikeCastOperand(t, c + 1)))
                {
                    remove = true; // "(o->tt_) == 2" → "o->tt_ == 2"
                }
                else
                {
                    // the whole expression in parentheses: "((2) * (3))" → "(2) * (3)"
                    remove = o == 0 && c == t.Count - 1;
                }
                if (!remove) continue;
                bool space = t[o].Space, newline = t[o].NewLine;
                t.RemoveAt(c);
                t.RemoveAt(o);
                if (o < t.Count) { t[o].Space = space || o > 0 && t[o].Space; t[o].NewLine |= newline; }
                return true;
            }
            return false;
        }

        /// <summary>"a->b", "a.b.c", "ns::x", "a->b.c": identifiers joined by member access only (a postfix expression).</summary>
        private static bool IsMemberChain(List<Tok> t, int from, int to)
        {
            if (from > to || t[from].Kind != TK.Ident || (to - from) % 2 != 0) return false;
            for (int i = from + 1; i <= to; i += 2)
            {
                string op = t[i].Text;
                if (t[i].Kind != TK.Punct || op != "->" && op != "." && op != "::" || t[i + 1].Kind != TK.Ident) return false;
            }
            return !Keywords.Contains(t[from].Text);
        }

        private static bool HasMemberAccess(List<Tok> t, int from, int to)
        {
            for (int i = from; i <= to; i++)
                if (t[i].Kind == TK.Punct && (t[i].Text == "." || t[i].Text == "->")) return true;
            return false;
        }

        private static bool LooksLikeCastOperand(List<Tok> t, int next)
        {
            if (next >= t.Count) return false;
            var n = t[next];
            if (n.Kind != TK.Punct) return true;
            switch (n.Text)
            {
                case "(": case "~": case "!": case "&": case "*": case "-": case "+": case "++": case "--":
                    return true;
                default:
                    return false;
            }
        }

        // ---- constant folding ----

        private static bool FoldConstants(List<Tok> t)
        {
            var match = Match(t);
            if (match == null) return false;
            for (int o = 0; o < t.Count; o++)
            {
                if (t[o].Text != "(" || t[o].Kind != TK.Punct || !IsGroupingOpen(t, o)) continue;
                int c = match[o];
                if (c <= o + 1 || !IsConstantRange(t, o + 1, c - 1)) continue;
                if (TryReplace(t, o, c, keepParens: false)) return true;
            }
            // the whole text is a constant expression: "2 * 3 * 2"
            if (t.Count > 1 && IsConstantRange(t, 0, t.Count - 1) && TryReplace(t, 0, t.Count - 1, keepParens: true)) return true;
            return false;
        }

        private static bool IsConstantRange(List<Tok> t, int from, int to)
        {
            bool op = false;
            for (int i = from; i <= to; i++)
            {
                var k = t[i];
                if (k.Kind == TK.Number) continue;
                if (k.Kind != TK.Punct || !Ops.Contains(k.Text)) return false;
                if (k.Text != "(" && k.Text != ")") op = true;
            }
            return op;
        }

        private static readonly HashSet<string> Ops = new HashSet<string>(StringComparer.Ordinal)
        {
            "(", ")", "+", "-", "*", "/", "%", "<<", ">>", "&", "|", "^", "~", "!", "<", ">", "<=", ">=", "==", "!=", "&&", "||",
        };

        private static bool TryReplace(List<Tok> t, int from, int to, bool keepParens)
        {
            int start = keepParens ? from : from + 1, end = keepParens ? to : to - 1;
            var range = t.GetRange(start, end - start + 1);
            int pos = 0;
            if (!TryParse(range, ref pos, 0, out long value) || pos != range.Count) return false;
            bool hex = range.Any(k => k.Kind == TK.Number && IsHex(k.Text, 0)) && value >= 0;
            string text = hex ? "0x" + value.ToString("X", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
            var tok = new Tok(TK.Number, text, t[from].Space);
            t.RemoveRange(from, to - from + 1);
            t.Insert(from, tok);
            return true;
        }

        private static int Precedence(string op)
        {
            switch (op)
            {
                case "*": case "/": case "%": return 10;
                case "+": case "-": return 9;
                case "<<": case ">>": return 8;
                case "<": case ">": case "<=": case ">=": return 7;
                case "==": case "!=": return 6;
                case "&": return 5;
                case "^": return 4;
                case "|": return 3;
                case "&&": return 2;
                case "||": return 1;
                default: return -1;
            }
        }

        private static bool TryParse(List<Tok> t, ref int pos, int minPrec, out long value)
        {
            if (!TryUnary(t, ref pos, out value)) return false;
            while (pos < t.Count)
            {
                string op = t[pos].Text;
                int prec = Precedence(op);
                if (prec < 0 || prec < minPrec) break;
                pos++;
                if (!TryParse(t, ref pos, prec + 1, out long rhs)) return false;
                if (!Apply(op, value, rhs, out value)) return false;
            }
            return true;
        }

        private static bool TryUnary(List<Tok> t, ref int pos, out long value)
        {
            value = 0;
            if (pos >= t.Count) return false;
            var k = t[pos];
            if (k.Kind == TK.Number)
            {
                pos++;
                return TryNumber(k.Text, out value);
            }
            switch (k.Text)
            {
                case "(":
                    pos++;
                    if (!TryParse(t, ref pos, 0, out value) || pos >= t.Count || t[pos].Text != ")") return false;
                    pos++;
                    return true;
                case "-": pos++; if (!TryUnary(t, ref pos, out value)) return false; value = unchecked(-value); return true;
                case "+": pos++; return TryUnary(t, ref pos, out value);
                case "~": pos++; if (!TryUnary(t, ref pos, out value)) return false; value = ~value; return true;
                case "!": pos++; if (!TryUnary(t, ref pos, out value)) return false; value = value == 0 ? 1 : 0; return true;
                default: return false;
            }
        }

        private static bool Apply(string op, long a, long b, out long r)
        {
            r = 0;
            unchecked
            {
                switch (op)
                {
                    case "*": r = a * b; return true;
                    case "/": if (b == 0) return false; r = a / b; return true;
                    case "%": if (b == 0) return false; r = a % b; return true;
                    case "+": r = a + b; return true;
                    case "-": r = a - b; return true;
                    case "<<": if (b < 0 || b > 62) return false; r = a << (int)b; return true;
                    case ">>": if (b < 0 || b > 63) return false; r = a >> (int)b; return true;
                    case "<": r = a < b ? 1 : 0; return true;
                    case ">": r = a > b ? 1 : 0; return true;
                    case "<=": r = a <= b ? 1 : 0; return true;
                    case ">=": r = a >= b ? 1 : 0; return true;
                    case "==": r = a == b ? 1 : 0; return true;
                    case "!=": r = a != b ? 1 : 0; return true;
                    case "&": r = a & b; return true;
                    case "^": r = a ^ b; return true;
                    case "|": r = a | b; return true;
                    case "&&": r = a != 0 && b != 0 ? 1 : 0; return true;
                    case "||": r = a != 0 || b != 0 ? 1 : 0; return true;
                    default: return false;
                }
            }
        }

        /// <summary>Integer literal (decimal, hex, octal, binary; ' separators; u/l suffixes). Floats are not folded.</summary>
        private static bool TryNumber(string s, out long value)
        {
            value = 0;
            s = s.Replace("'", string.Empty).TrimEnd('u', 'U', 'l', 'L');
            if (s.Length == 0) return false;
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return long.TryParse(s.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
                if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                {
                    value = Convert.ToInt64(s.Substring(2), 2);
                    return true;
                }
                if (s.Length > 1 && s[0] == '0')
                {
                    value = Convert.ToInt64(s, 8);
                    return true;
                }
            }
            catch (FormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
            return s.All(char.IsDigit) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        // ---- output ----

        private static string Render(List<Tok> t)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < t.Count; i++)
            {
                if (i > 0 && t[i].NewLine) sb.Append('\n');
                else if (i > 0 && t[i].Space) sb.Append(' ');
                sb.Append(t[i].Text);
            }
            string flat = sb.ToString();
            return flat.Length <= WrapAt || t.Any(k => k.NewLine) ? flat : Wrap(t);
        }

        /// <summary>Long declaration lists: one statement per line, braces indent (parentheses never break).</summary>
        private static string Wrap(List<Tok> t)
        {
            var sb = new StringBuilder();
            int braces = 0, parens = 0;
            bool lineStart = true;
            for (int i = 0; i < t.Count; i++)
            {
                var k = t[i];
                if (k.Kind == TK.Punct)
                {
                    if (k.Text == "(" || k.Text == "[") parens++;
                    else if ((k.Text == ")" || k.Text == "]") && parens > 0) parens--;
                }
                bool close = parens == 0 && k.Kind == TK.Punct && k.Text == "}";
                if (close)
                {
                    braces = Math.Max(0, braces - 1);
                    if (!lineStart) { sb.Append('\n'); lineStart = true; }
                }
                if (lineStart) sb.Append(' ', braces * 4);
                else if (k.Space) sb.Append(' ');
                sb.Append(k.Text);
                lineStart = false;
                if (parens == 0 && k.Kind == TK.Punct && (k.Text == ";" || k.Text == "{" || k.Text == "}"))
                {
                    if (k.Text == "{") braces++;
                    // "};" and "} x;" stay on the brace's line
                    bool joinNext = k.Text == "}" && i + 1 < t.Count && t[i + 1].Text == ";";
                    if (!joinNext && i + 1 < t.Count) { sb.Append('\n'); lineStart = true; }
                }
            }
            return sb.ToString();
        }
    }
}
