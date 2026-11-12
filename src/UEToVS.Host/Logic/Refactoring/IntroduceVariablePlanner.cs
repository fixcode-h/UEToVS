using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEToVS.Core.Matching;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>What Introduce Variable found for a selection (offsets into the text).</summary>
    public sealed class IntroduceVariableAnalysis
    {
        /// <summary>Why the selection cannot become a variable, or null.</summary>
        public string Problem { get; set; }

        /// <summary>The expression as written (trimmed selection).</summary>
        public string Expression { get; set; }

        public int Start { get; set; }
        public int End { get; set; }

        /// <summary>Where the declaration is inserted: the start of the statement that contains the first replaced occurrence.</summary>
        public int InsertOffset { get; set; }

        public string Indent { get; set; }

        /// <summary>The selected occurrence.</summary>
        public (int Start, int End) Selected { get; set; }

        /// <summary>Every identical occurrence that can be replaced too (the selected one included), in text order.</summary>
        public List<(int Start, int End)> Occurrences { get; } = new List<(int, int)>();

        /// <summary>The expression is a postfix chain (a.b->c(), x[i]) or a literal: the engine type of its last token is its type.</summary>
        public bool IsChain { get; set; }

        /// <summary>Type of a literal expression (int32, float, bool…), or null.</summary>
        public string LiteralType { get; set; }

        /// <summary>Identifiers of the enclosing body (name collisions).</summary>
        public HashSet<string> Identifiers { get; } = new HashSet<string>(StringComparer.Ordinal);

        public bool Ok => Problem == null;
    }

    /// <summary>
    /// Introduce Variable (pure, v0.4.5): the selected expression becomes <c>Type Name = expr;</c> before its statement and is
    /// replaced by Name (optionally every identical occurrence in the same block after it). Not offered when hoisting could change
    /// what runs: inside a loop / else-if / braceless head, after a short-circuit operator, assignment targets, address-of,
    /// partial operands (<c>a + b</c> out of <c>a + b * c</c>), switch case bodies without braces.
    /// Problem codes: "empty", "not-expression", "not-in-function", "lvalue", "partial", "loop", "short-circuit", "braceless",
    /// "case", "identifier", "string-only".
    /// </summary>
    public static class IntroduceVariablePlanner
    {
        public static IntroduceVariableAnalysis Analyze(string text, int selStart, int selEnd)
        {
            var a = new IntroduceVariableAnalysis();
            text = text ?? string.Empty;
            if (selStart < 0 || selEnd > text.Length || selEnd <= selStart) return Fail(a, "empty");
            var t = CppTextScan.Tokenize(text);
            int first = CodeScan.IndexAtOrAfter(t, selStart);
            int last = CodeScan.IndexEndingAtOrBefore(t, selEnd);
            if (first >= t.Count || last < first) return Fail(a, "empty");
            // the selection must cover whole tokens and nothing but blanks / comments outside them
            if (!CodeScan.IsBlankOrComment(text, selStart, t[first].Offset) || !CodeScan.IsBlankOrComment(text, CodeScan.End(t[last]), selEnd))
                return Fail(a, "not-expression");
            if (first > 0 && CodeScan.End(t[first - 1]) > selStart) return Fail(a, "not-expression");
            a.Start = t[first].Offset;
            a.End = CodeScan.End(t[last]);
            a.Expression = text.Substring(a.Start, a.End - a.Start);
            a.Selected = (a.Start, a.End);
            string why = ExpressionProblem(text, t, first, last);
            if (why != null) return Fail(a, why);

            var body = CodeScan.EnclosingBody(t, a.Start);
            if (body == null || first <= body.OpenToken || last >= body.CloseToken) return Fail(a, "not-in-function");
            for (int k = body.OpenToken + 1; k < body.CloseToken; k++) if (t[k].IsIdent) a.Identifiers.Add(t[k].Text);

            int block = CodeScan.InnermostBlock(t, first, body.OpenToken - 1);
            if (block < body.OpenToken) block = body.OpenToken;
            int stmt = CodeScan.StatementStart(t, first, block);
            why = StatementProblem(t, stmt, first, block);
            if (why != null) return Fail(a, why);
            a.InsertOffset = t[stmt].Offset;
            a.Indent = CodeScan.LineIndent(text, t[stmt].Offset);
            if (!CodeScan.IsBlank(text, CodeScan.LineStart(text, t[stmt].Offset), t[stmt].Offset)) a.Indent = string.Empty;
            a.IsChain = IsPostfixChain(t, first, last);
            a.LiteralType = LiteralType(text, t, first, last);

            // identical occurrences in the same block, from the insertion statement on, not inside lambdas
            a.Occurrences.Add(a.Selected);
            string key = CodeScan.Squash(a.Expression);
            int blockClose = CppTextScan.MatchClose(t, block);
            int count = last - first + 1;
            int stopAt = FirstWriteAfter(t, stmt, blockClose, first, last);
            for (int k = stmt; k + count - 1 < blockClose; k++)
            {
                if (k == first) { k = last; continue; }
                if (!t[k].Is(t[first].Text) || !t[k + count - 1].Is(t[last].Text)) continue;
                if (k >= stopAt) break;
                int s = t[k].Offset, e = CodeScan.End(t[k + count - 1]);
                if (CodeScan.Squash(text.Substring(s, e - s)) != key) continue;
                if (ExpressionProblem(text, t, k, k + count - 1) != null) continue;
                if (CodeScan.InsideLambda(t, stmt, blockClose, k)) continue;
                a.Occurrences.Add((s, e));
                k += count - 1;
            }
            a.Occurrences.Sort((x, y) => x.Start.CompareTo(y.Start));
            return a;
        }

        private static IntroduceVariableAnalysis Fail(IntroduceVariableAnalysis a, string why)
        {
            a.Problem = why;
            return a;
        }

        /// <summary>Why tokens [first, last] are not a complete, hoistable expression; null when they are.</summary>
        internal static string ExpressionProblem(string text, List<ScanToken> t, int first, int last)
        {
            int depth = 0;
            bool topLevelOperator = false;
            for (int k = first; k <= last; k++)
            {
                var tok = t[k];
                if (tok.Is(";") || tok.Is("{") || tok.Is("}")) return "not-expression";
                if (tok.Is("(") || tok.Is("[")) depth++;
                else if (tok.Is(")") || tok.Is("]")) { if (--depth < 0) return "not-expression"; }
                else if (depth == 0 && IsBinaryOperatorAt(t, k, first)) topLevelOperator = true;
                if (depth == 0 && tok.Is("=") && !(k + 1 <= last && t[k + 1].Is("=")) && !(k > first && "=!<>".IndexOf(t[k - 1].Text[0]) >= 0 && CodeScan.End(t[k - 1]) == tok.Offset))
                    return "lvalue"; // an assignment inside the selection
                if (depth == 0 && tok.Is(",")) return "not-expression";
            }
            if (depth != 0) return "not-expression";
            var lastTok = t[last];
            if (!(lastTok.IsIdent || lastTok.Is(")") || lastTok.Is("]") || char.IsDigit(lastTok.Text[0]))) return "not-expression";
            if (lastTok.IsIdent && IsKeyword(lastTok.Text) && lastTok.Text != "this" && lastTok.Text != "true" && lastTok.Text != "false" && lastTok.Text != "nullptr")
                return "not-expression";
            var firstTok = t[first];
            if (firstTok.IsIdent && IsDeclarationKeyword(firstTok.Text)) return "not-expression";
            if (first == last && firstTok.IsIdent && firstTok.Text != "this") return "identifier";
            // part of a longer postfix chain / qualified name / call
            if (first > 0 && (t[first - 1].Is(".") || t[first - 1].Is("::") || (t[first - 1].Is(">") && first > 1 && t[first - 2].Is("-") && CodeScan.End(t[first - 2]) == t[first - 1].Offset)))
                return "partial";
            if (last + 1 < t.Count)
            {
                var n = t[last + 1];
                if (n.Is("(") || n.Is("::") || (n.Is("<") && lastTok.IsIdent)) return "partial";
                if (n.Is("=") && !(last + 2 < t.Count && t[last + 2].Is("=") && CodeScan.End(n) == t[last + 2].Offset)) return "lvalue";
                if ((n.Is("+") || n.Is("-")) && last + 2 < t.Count && t[last + 2].Is(n.Text) && CodeScan.End(n) == t[last + 2].Offset) return "lvalue"; // x++
                if ("+-*/%&|^".IndexOf(n.Text[0]) >= 0 && n.Text.Length == 1 && last + 2 < t.Count && t[last + 2].Is("=") && CodeScan.End(n) == t[last + 2].Offset) return "lvalue"; // x += 1
                if (n.Is("<") && last + 3 < t.Count && t[last + 2].Is("<") && t[last + 3].Is("=")) return "lvalue";
            }
            if (first > 0)
            {
                var p = t[first - 1];
                if ((p.Is("+") || p.Is("-")) && first > 1 && t[first - 2].Is(p.Text) && CodeScan.End(t[first - 2]) == p.Offset) return "lvalue"; // ++x
                if (IsAddressOf(t, first - 1)) return "lvalue"; // &x: the address of the original
            }
            if (topLevelOperator)
            {
                // a binary expression must be a whole operand: bounded by ( , = return ; ? : on the left and ) , ; ] : ? on the right
                bool leftOk = first == 0 || IsExpressionBoundaryLeft(t, first - 1);
                bool rightOk = last + 1 >= t.Count || IsExpressionBoundaryRight(t, last + 1);
                if (!leftOk || !rightOk) return "partial";
            }
            return null;
        }

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "for", "while", "do", "switch", "case", "default", "return", "break", "continue", "goto", "new", "delete", "sizeof", "const",
            "static", "auto", "class", "struct", "enum", "using", "typedef", "template", "typename", "virtual", "override", "public", "private", "protected",
            "throw", "try", "catch", "co_return", "co_await", "co_yield", "static_cast", "const_cast", "reinterpret_cast", "dynamic_cast", "operator",
            "this", "true", "false", "nullptr", "void", "int", "float", "double", "bool", "char", "unsigned", "signed", "long", "short",
        };

        private static bool IsKeyword(string s) => Keywords.Contains(s);

        private static bool IsDeclarationKeyword(string s) =>
            s == "const" || s == "static" || s == "auto" || s == "class" || s == "struct" || s == "enum" || s == "using" || s == "typedef" || s == "return" ||
            s == "if" || s == "for" || s == "while" || s == "switch" || s == "case" || s == "default" || s == "else" || s == "do" || s == "break" ||
            s == "continue" || s == "goto" || s == "template" || s == "throw" || s == "delete";

        /// <summary>A unary '&amp;' (not one half of '&amp;&amp;').</summary>
        private static bool IsAddressOf(List<ScanToken> t, int k)
        {
            if (!t[k].Is("&")) return false;
            if (k > 0 && t[k - 1].Is("&") && CodeScan.End(t[k - 1]) == t[k].Offset) return false;
            if (k + 1 < t.Count && t[k + 1].Is("&") && CodeScan.End(t[k]) == t[k + 1].Offset) return false;
            return IsUnaryPosition(t, k);
        }

        private static int MatchSquare(List<ScanToken> t, int open, int limit)
        {
            int depth = 0;
            for (int k = open; k < limit; k++)
            {
                if (t[k].Is("[")) depth++;
                else if (t[k].Is("]") && --depth == 0) return k;
            }
            return -1;
        }

        private static bool IsUnaryPosition(List<ScanToken> t, int op)
        {
            if (op == 0) return true;
            var p = t[op - 1];
            return !(p.IsIdent && !IsKeyword(p.Text) || p.Is(")") || p.Is("]") || char.IsDigit(p.Text[0])) || p.Is("return");
        }

        /// <summary>A binary operator token at top level of [first, …] (unary + - * &amp; ! ~ excluded).</summary>
        private static bool IsBinaryOperatorAt(List<ScanToken> t, int k, int first)
        {
            var tok = t[k];
            if (tok.Text.Length != 1 || "+-*/%<>=!&|^?:".IndexOf(tok.Text[0]) < 0) return false;
            if (tok.Is(">") && k > 0 && t[k - 1].Is("-") && CodeScan.End(t[k - 1]) == tok.Offset) return false; // ->
            if (tok.Is("-") && k + 1 < t.Count && t[k + 1].Is(">") && CodeScan.End(tok) == t[k + 1].Offset) return false;
            if (k == first) return false;
            return !IsUnaryPosition(t, k) || tok.Is("?") || tok.Is(":") || tok.Is("=");
        }

        private static bool IsExpressionBoundaryLeft(List<ScanToken> t, int k)
        {
            var p = t[k];
            if (p.Is("(") || p.Is(",") || p.Is(";") || p.Is("{") || p.Is("}") || p.Is("[") || p.Is("?") || p.Is(":") || p.Is("return")) return true;
            if (p.Is("=")) return !(k > 0 && "=!<>".IndexOf(t[k - 1].Text[0]) >= 0 && CodeScan.End(t[k - 1]) == p.Offset) || (k > 0 && t[k - 1].Is("="));
            return false;
        }

        private static bool IsExpressionBoundaryRight(List<ScanToken> t, int k)
        {
            var n = t[k];
            return n.Is(")") || n.Is(",") || n.Is(";") || n.Is("]") || n.Is("}") || n.Is("?") || (n.Is(":") && !(k + 1 < t.Count && t[k + 1].Is(":")));
        }

        /// <summary>Why the declaration cannot go before the statement starting at <paramref name="stmt"/>; null when it can.</summary>
        private static string StatementProblem(List<ScanToken> t, int stmt, int first, int block)
        {
            var head = t[stmt];
            if (head.Is("else")) return "braceless";
            if (head.Is("for") || head.Is("while") || head.Is("do")) return "loop";
            if (head.Is("case") || head.Is("default")) return "case";
            // block of a switch: declarations would cross case labels
            if (block > 0 && t[block - 1].Is(")") && CodeScan.ControlHeadOf(t, block - 1) == "switch") return "case";
            // a braceless body: "if (c) Foo(x);" with the selection after the head
            if (head.Is("if"))
            {
                int open = stmt + 1;
                if (open < t.Count && t[open].Is("constexpr")) open++;
                if (open >= t.Count || !t[open].Is("(")) return "braceless";
                int close = CppTextScan.MatchClose(t, open);
                if (first > close) return "braceless";
            }
            // label "Name:" before the statement
            if (head.IsIdent && stmt + 1 < t.Count && t[stmt + 1].Is(":") && !(stmt + 2 < t.Count && t[stmt + 2].Is(":"))) return "case";
            // short-circuit / conditional before the selection in the same statement
            for (int k = stmt; k < first; k++)
            {
                if (t[k].Is("?")) return "short-circuit";
                if ((t[k].Is("&") || t[k].Is("|")) && k + 1 < first && t[k + 1].Is(t[k].Text) && CodeScan.End(t[k]) == t[k + 1].Offset) return "short-circuit";
            }
            return null;
        }

        /// <summary>Index of the first token after the selection that may change a value the expression reads (assignment, ++/--, a call on it).</summary>
        private static int FirstWriteAfter(List<ScanToken> t, int from, int to, int first, int last)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int k = first; k <= last; k++) if (t[k].IsIdent && !IsKeyword(t[k].Text)) names.Add(t[k].Text);
            for (int k = last + 1; k < to; k++)
            {
                if (!t[k].IsIdent || !names.Contains(t[k].Text)) continue;
                // members count too (A.X = 3 writes the selected A.X); conservative: any same-named member stops replacing
                int j = k;
                while (j + 1 < to && t[j + 1].Is("["))
                {
                    int close = MatchSquare(t, j + 1, to);
                    if (close < 0) break;
                    j = close;
                }
                var n = j + 1 < t.Count ? t[j + 1] : default;
                bool next2 = j + 2 < t.Count;
                if (n.Text == null) continue;
                bool assign = n.Is("=") && !(next2 && t[j + 2].Is("="));
                bool compound = n.Text.Length == 1 && "+-*/%&|^".IndexOf(n.Text[0]) >= 0 && next2 && t[j + 2].Is("=") && CodeScan.End(n) == t[j + 2].Offset;
                bool incdec = (n.Is("+") || n.Is("-")) && next2 && t[j + 2].Is(n.Text) && CodeScan.End(n) == t[j + 2].Offset;
                bool pre = k >= 2 && (t[k - 1].Is("+") || t[k - 1].Is("-")) && t[k - 2].Is(t[k - 1].Text) && CodeScan.End(t[k - 2]) == t[k - 1].Offset;
                bool addr = k >= 1 && IsAddressOf(t, k - 1);
                if (assign || compound || incdec || pre || addr) return StatementStartOf(t, k, from);
            }
            return int.MaxValue;
        }

        private static int StatementStartOf(List<ScanToken> t, int k, int floor)
        {
            for (int m = k - 1; m >= floor; m--) if (t[m].Is(";") || t[m].Is("{") || t[m].Is("}")) return m + 1;
            return floor;
        }

        /// <summary>Identifiers, '.', '-&gt;', '::', calls and subscripts only (no operators at top level): the engine types its last token.</summary>
        public static bool IsPostfixChain(List<ScanToken> t, int first, int last)
        {
            int depth = 0;
            for (int k = first; k <= last; k++)
            {
                var tok = t[k];
                if (tok.Is("(") || tok.Is("[")) { depth++; continue; }
                if (tok.Is(")") || tok.Is("]")) { depth--; continue; }
                if (depth > 0) continue;
                if (tok.IsIdent || tok.Is(".") || tok.Is("::")) continue;
                if (tok.Is("-") && k + 1 <= last && t[k + 1].Is(">")) { k++; continue; }
                if (tok.Is("<") || tok.Is(">") || tok.Is(",") || tok.Is("*")) continue; // template arguments Cast<UFoo>(…)
                return false;
            }
            return t[first].IsIdent;
        }

        /// <summary>Type of a numeric / boolean literal expression; null otherwise.</summary>
        public static string LiteralType(string text, List<ScanToken> t, int first, int last)
        {
            int k = first;
            if (t[k].Is("-") && k < last) k++;
            if (k != last) return null;
            string s = t[k].Text;
            if (s == "true" || s == "false") return "bool";
            if (!char.IsDigit(s[0])) return null;
            string lower = s.ToLowerInvariant();
            bool hex = lower.StartsWith("0x", StringComparison.Ordinal);
            if (!hex && (lower.EndsWith("f", StringComparison.Ordinal) && (lower.IndexOf('.') >= 0 || lower.IndexOf('e') >= 0 || lower.Length > 1))) return "float";
            if (!hex && (lower.IndexOf('.') >= 0 || lower.IndexOf('e') >= 0)) return "double";
            if (lower.EndsWith("ull", StringComparison.Ordinal) || lower.EndsWith("llu", StringComparison.Ordinal)) return "uint64";
            if (lower.EndsWith("ll", StringComparison.Ordinal)) return "int64";
            if (lower.EndsWith("u", StringComparison.Ordinal)) return "uint32";
            return "int32";
        }

        /// <summary>The variable type: the engine's type for a chain, the literal's type, else <c>auto</c>.</summary>
        public static string ChooseType(IntroduceVariableAnalysis a, string engineType)
        {
            if (a.LiteralType != null) return a.LiteralType;
            if (a.IsChain && !string.IsNullOrWhiteSpace(engineType) && engineType.IndexOf("auto", StringComparison.Ordinal) < 0 &&
                engineType.IndexOf('?') < 0 && engineType != "void") return engineType.Trim();
            return "auto";
        }

        /// <summary>A name from the expression (<c>GetOwner()</c> → Owner, <c>Foo-&gt;Health</c> → Health) or the type (<c>AActor*</c> → Actor); unique in the body.</summary>
        public static string SuggestName(string expression, string type, ICollection<string> taken)
        {
            string name = null;
            var t = CppTextScan.Tokenize(expression ?? string.Empty);
            // last identifier at top level of the chain
            int depth = 0;
            for (int k = t.Count - 1; k >= 0 && name == null; k--)
            {
                if (t[k].Is(")") || t[k].Is("]")) depth++;
                else if (t[k].Is("(") || t[k].Is("[")) depth--;
                else if (depth == 0 && t[k].IsIdent && !IsKeyword(t[k].Text)) name = t[k].Text;
            }
            if (name != null)
            {
                foreach (var prefix in new[] { "Get", "Find", "Make", "Create", "Calc", "Calculate", "Compute" })
                    if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal) && char.IsUpper(name[prefix.Length])) { name = name.Substring(prefix.Length); break; }
            }
            if (name == null && !string.IsNullOrEmpty(type) && type != "auto")
            {
                var tt = CppTextScan.Tokenize(type);
                var ident = tt.LastOrDefault(x => x.IsIdent && !IsKeyword(x.Text) && x.Text != "TObjectPtr" && x.Text != "TArray");
                if (ident.Text != null) name = UnrealNaming.StripTypePrefix(ident.Text) ?? ident.Text;
            }
            if (string.IsNullOrEmpty(name) || !RenamePlanner.IsValidIdentifier(name)) name = "Value";
            if (char.IsLower(name[0]) && !(name.Length > 1 && name[0] == 'b' && char.IsUpper(name[1]))) name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            if (type == "bool" && !(name.Length > 1 && name[0] == 'b' && char.IsUpper(name[1]))) name = "b" + name;
            string unique = name;
            for (int i = 2; taken != null && taken.Contains(unique); i++) unique = name + i;
            return unique;
        }

        /// <summary>Edits: the declaration before the statement, and Name in place of the selected (or every) occurrence.</summary>
        public static List<TextEdit> Edits(string filePath, string text, IntroduceVariableAnalysis a, string type, string name, bool replaceAll)
        {
            string nl = CppTextScan.NewlineOf(text);
            var structured = new List<StructuredEdit>();
            string decl = (type ?? "auto").Trim() + " " + name + " = " + a.Expression + ";" + nl + a.Indent;
            if (a.Indent.Length == 0 && !CodeScan.IsBlank(text, CodeScan.LineStart(text, a.InsertOffset), a.InsertOffset))
                decl = (type ?? "auto").Trim() + " " + name + " = " + a.Expression + "; ";
            bool inserted = false;
            foreach (var o in replaceAll ? a.Occurrences : new List<(int Start, int End)> { a.Selected })
            {
                // the statement starts with the expression itself: one edit "Decl\n\tName"
                if (o.Start == a.InsertOffset) { structured.Add(StructuredEdit.Replace(o.Start, o.End, decl + name)); inserted = true; }
                else structured.Add(StructuredEdit.Replace(o.Start, o.End, name));
            }
            if (!inserted) structured.Add(StructuredEdit.Replace(a.InsertOffset, a.InsertOffset, decl));
            return StructuredEditComposer.Compose(filePath, text, structured, out _);
        }
    }
}
