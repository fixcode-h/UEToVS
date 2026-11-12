using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>A local variable / parameter of the enclosing function (from the engine; offsets into the text).</summary>
    public sealed class ExtractLocal
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public int Offset { get; set; }
        public bool IsParameter { get; set; }

        /// <summary>Offset where it goes out of scope.</summary>
        public int ScopeEnd { get; set; } = int.MaxValue;

        public override string ToString() => Type + " " + Name + " @" + Offset;
    }

    public enum ExtractPassing
    {
        /// <summary><c>T Name</c> (cheap types, or the variable that is returned).</summary>
        Value,

        /// <summary><c>const T&amp; Name</c>.</summary>
        ConstReference,

        /// <summary><c>T&amp; Name</c> (the selection may change it).</summary>
        Reference,

        /// <summary>The declared type is a reference already: kept as written.</summary>
        AsDeclared,
    }

    public sealed class ExtractParameter
    {
        public string Name { get; set; }
        public string DeclaredType { get; set; }
        public ExtractPassing Passing { get; set; }

        /// <summary>The parameter declaration of the new function.</summary>
        public string Declaration
        {
            get
            {
                string t = DeclaredType.Trim();
                switch (Passing)
                {
                    case ExtractPassing.ConstReference:
                        return (t.StartsWith("const ", StringComparison.Ordinal) ? t : "const " + t) + "& " + Name;
                    case ExtractPassing.Reference:
                        return t + "& " + Name;
                    default:
                        return t + " " + Name;
                }
            }
        }

        public override string ToString() => Declaration;
    }

    /// <summary>What Extract Method found for a selection.</summary>
    public sealed class ExtractMethodAnalysis
    {
        public string Problem { get; set; }

        /// <summary>The variable the problem is about (unknown type, several results…).</summary>
        public string ProblemDetail { get; set; }

        /// <summary>Replaced range (whole lines when the selection covers them).</summary>
        public int Start { get; set; }

        public int End { get; set; }

        /// <summary>Indentation of the call (the first selected line).</summary>
        public string Indent { get; set; }

        /// <summary>Selected statements re-indented by one tab (the new function's body, without the return).</summary>
        public List<string> BodyLines { get; } = new List<string>();

        public List<ExtractParameter> Parameters { get; } = new List<ExtractParameter>();

        /// <summary>The variable returned, or null for void.</summary>
        public string ReturnVariable { get; set; }

        public string ReturnType { get; set; } = "void";

        /// <summary>The returned variable is declared in the selection: the call declares it (<c>T X = F();</c>).</summary>
        public bool ReturnDeclaredInSelection { get; set; }

        /// <summary>Enclosing function: qualifier as written before its name ("AMyActor::" / "N::AFoo::"), its class name and name.</summary>
        public string Qualifier { get; set; }

        public string ClassName { get; set; }
        public string FunctionName { get; set; }
        public bool IsConst { get; set; }

        /// <summary>Offset of the closing brace of the enclosing function (the new definition goes after it).</summary>
        public int FunctionEnd { get; set; }

        /// <summary>Identifiers used anywhere in the text (name collisions in the dialog).</summary>
        public HashSet<string> Identifiers { get; } = new HashSet<string>(StringComparer.Ordinal);

        public bool Ok => Problem == null;
    }

    /// <summary>
    /// Extract Method (pure, v0.4.5). Only whole statements with one entry and one exit: no return / break / continue / goto /
    /// labels / case labels leaving the selection, no preprocessor lines, no partial if-else / do-while / try-catch. Locals
    /// read from outside become parameters (cheap types by value, others by const&amp;; a reference when the selection may change
    /// them), and the one variable assigned in the selection and read after it becomes the return value (more than one: not
    /// offered). The new function is a private member of the enclosing function's class (const when it is const; static when it
    /// is static), declared in the header and defined after the enclosing function.
    /// Problem codes: "empty", "partial-statement", "not-in-function", "lambda", "jump", "label", "preprocessor", "several-results",
    /// "unknown-type", "reference-result", "not-member", "template".
    /// </summary>
    public static class ExtractMethodPlanner
    {
        private static readonly HashSet<string> CheapTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "bool", "char", "short", "int", "long", "float", "double", "unsigned", "signed", "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32",
            "uint64", "TCHAR", "ANSICHAR", "WIDECHAR", "UTF8CHAR", "SIZE_T", "SSIZE_T", "PTRINT", "UPTRINT", "size_t", "FName", "FGuid", "FColor",
            "FLinearColor", "FVector2D", "FIntPoint", "FKey", "wchar_t", "char16_t", "char32_t",
        };

        public static ExtractMethodAnalysis Analyze(string text, int selStart, int selEnd, IReadOnlyList<ExtractLocal> locals)
        {
            var a = new ExtractMethodAnalysis();
            text = text ?? string.Empty;
            if (selEnd <= selStart || selStart < 0 || selEnd > text.Length) return Fail(a, "empty");
            var t = CppTextScan.Tokenize(text);
            int first = CodeScan.IndexAtOrAfter(t, selStart);
            int last = CodeScan.IndexEndingAtOrBefore(t, selEnd);
            if (first >= t.Count || last < first) return Fail(a, "empty");
            if (!CodeScan.IsBlankOrComment(text, selStart, t[first].Offset) || !CodeScan.IsBlankOrComment(text, CodeScan.End(t[last]), selEnd))
                return Fail(a, "partial-statement");
            foreach (var tok in t) if (tok.IsIdent) a.Identifiers.Add(tok.Text);

            // ---- the enclosing function
            var body = CodeScan.EnclosingBody(t, t[first].Offset);
            if (body == null) return Fail(a, "not-in-function");
            if (body.Kind == BodyKind.Lambda) return Fail(a, "lambda");
            if (first <= body.OpenToken || last >= body.CloseToken) return Fail(a, "partial-statement");
            if (!FunctionHead(t, body.OpenToken, a)) return Fail(a, a.Problem ?? "not-member");
            a.FunctionEnd = t[body.CloseToken].Offset;

            // ---- whole statements, balanced, one entry / one exit
            int before = first - 1;
            if (!(t[before].Is(";") || t[before].Is("{") || t[before].Is("}"))) return Fail(a, "partial-statement");
            if (t[before].Is("}") && IsInitializerBrace(t, before)) return Fail(a, "partial-statement");
            if (!(t[last].Is(";") || t[last].Is("}"))) return Fail(a, "partial-statement");
            int after = last + 1;
            if (after < t.Count && (t[after].Is("else") || t[after].Is("while") || t[after].Is("catch"))) return Fail(a, "partial-statement");
            if (t[first].Is("else") || t[first].Is("catch") || t[first].Is("case") || t[first].Is("default")) return Fail(a, "partial-statement");
            // a statement that is the braceless body of an if / for / while just before the selection
            if (t[before].Is(")") && CodeScan.ControlHeadOf(t, before) != null) return Fail(a, "partial-statement");
            if (before > 0 && t[before].Is("else")) return Fail(a, "partial-statement");
            string jump = JumpProblem(t, first, last);
            if (jump != null) return Fail(a, jump);
            // preprocessor lines inside the selection
            string selected = text.Substring(t[first].Offset, CodeScan.End(t[last]) - t[first].Offset);
            foreach (var l in selected.Replace("\r\n", "\n").Split('\n'))
                if (l.TrimStart().StartsWith("#", StringComparison.Ordinal)) return Fail(a, "preprocessor");

            // ---- replaced range: whole lines when nothing else shares them
            int s = t[first].Offset, e = CodeScan.End(t[last]);
            int ls = CodeScan.LineStart(text, s), le = CodeScan.LineEnd(text, e);
            a.Indent = CodeScan.LineIndent(text, s);
            if (CodeScan.IsBlank(text, ls, s) && CodeScan.IsBlankOrComment(text, e, le))
            {
                a.Start = ls;
                a.End = le;
                selected = text.Substring(ls, le - ls);
            }
            else
            {
                a.Start = s;
                a.End = e;
            }
            a.BodyLines.AddRange(CodeScan.Reindent(selected.Replace("\r\n", "\n").Split('\n'), "\t"));
            while (a.BodyLines.Count > 0 && a.BodyLines[a.BodyLines.Count - 1].Length == 0) a.BodyLines.RemoveAt(a.BodyLines.Count - 1);

            // ---- data flow
            var all = (locals ?? Array.Empty<ExtractLocal>()).Where(l => l != null && !string.IsNullOrEmpty(l.Name)).ToList();
            ExtractLocal Resolve(string name, int offset) =>
                all.Where(l => l.Name == name && l.Offset <= offset && offset < l.ScopeEnd).OrderByDescending(l => l.Offset).FirstOrDefault();

            var used = new List<ExtractLocal>();
            var modified = new HashSet<ExtractLocal>();
            var assigned = new HashSet<ExtractLocal>();
            var seenAssign = new HashSet<ExtractLocal>();
            for (int k = first; k <= last; k++)
            {
                if (!t[k].IsIdent || IsMemberAccess(t, k)) continue;
                var l = Resolve(t[k].Text, t[k].Offset);
                if (l == null) continue;
                bool declaredHere = l.Offset >= s && l.Offset < e;
                if (declaredHere) continue;
                if (!used.Contains(l)) used.Add(l);
                var w = WriteKind(t, k);
                if (w == Write.Assign)
                {
                    assigned.Add(l);
                    seenAssign.Add(l);
                }
                else if (w == Write.Modify) { modified.Add(l); assigned.Add(l); }
                else if (w == Write.MaybeModify) modified.Add(l);
            }
            // used after the selection (in the rest of the function)
            bool UsedAfter(ExtractLocal l)
            {
                for (int k = last + 1; k < body.CloseToken; k++)
                {
                    if (t[k].Offset >= l.ScopeEnd) break;
                    if (!t[k].IsIdent || !t[k].Is(l.Name) || IsMemberAccess(t, k)) continue;
                    if (Resolve(l.Name, t[k].Offset) == l) return true;
                }
                return false;
            }
            var declaredInside = all.Where(l => l.Offset >= s && l.Offset < e && !l.IsParameter).ToList();
            var results = new List<(ExtractLocal Local, bool Inside)>();
            foreach (var l in declaredInside) if (UsedAfter(l)) results.Add((l, true));
            foreach (var l in used) if (assigned.Contains(l) && UsedAfter(l) && !IsReferenceType(l.Type)) results.Add((l, false));
            if (results.Count > 1)
            {
                a.ProblemDetail = string.Join(", ", results.Select(r => r.Local.Name));
                return Fail(a, "several-results");
            }
            ExtractLocal ret = results.Count == 1 ? results[0].Local : null;
            if (ret != null)
            {
                if (!KnownType(ret.Type)) { a.ProblemDetail = ret.Name; return Fail(a, "unknown-type"); }
                if (IsReferenceType(ret.Type)) { a.ProblemDetail = ret.Name; return Fail(a, "reference-result"); }
                a.ReturnVariable = ret.Name;
                a.ReturnDeclaredInSelection = results[0].Inside;
                a.ReturnType = StripTopConst(ret.Type);
            }
            foreach (var l in used.OrderBy(x => x.Offset))
            {
                if (!KnownType(l.Type)) { a.ProblemDetail = l.Name; return Fail(a, "unknown-type"); }
                ExtractPassing passing;
                if (IsReferenceType(l.Type)) passing = ExtractPassing.AsDeclared;
                else if (l == ret) passing = ExtractPassing.Value;
                else if (modified.Contains(l) || assigned.Contains(l)) passing = ExtractPassing.Reference;
                else if (IsCheap(l.Type)) passing = ExtractPassing.Value;
                else passing = ExtractPassing.ConstReference;
                if (passing == ExtractPassing.Reference && l.Type.TrimStart().StartsWith("const ", StringComparison.Ordinal) && !IsPointer(l.Type))
                    passing = IsCheap(l.Type) ? ExtractPassing.Value : ExtractPassing.ConstReference; // a const variable is not modified
                a.Parameters.Add(new ExtractParameter { Name = l.Name, DeclaredType = l.Type, Passing = passing });
            }
            return a;
        }

        private static ExtractMethodAnalysis Fail(ExtractMethodAnalysis a, string why)
        {
            a.Problem = why;
            return a;
        }

        /// <summary>Head of the function whose body opens at <paramref name="open"/>: <c>[Ret] A::B::Name(…) [const] [: init]</c>.</summary>
        private static bool FunctionHead(List<ScanToken> t, int open, ExtractMethodAnalysis a)
        {
            int from = open - 1;
            int depth = 0;
            // back to the previous statement end at depth 0
            for (; from >= 0; from--)
            {
                var tok = t[from];
                if (tok.Is(")") || tok.Is("]")) depth++;
                else if (tok.Is("(") || tok.Is("[")) depth--;
                else if (tok.Is("}"))
                {
                    if (depth == 0)
                    {
                        // a brace initializer in the ctor init list "X{1}" is part of the head
                        int o = CodeScan.MatchOpen(t, from);
                        if (o > 0 && t[o - 1].IsIdent && o > 1 && (t[o - 2].Is(",") || t[o - 2].Is(":"))) { from = o; continue; }
                        break;
                    }
                }
                else if (depth == 0 && (tok.Is(";") || tok.Is("{"))) break;
            }
            from++;
            for (int k = from; k < open; k++) if (t[k].Is("template")) { a.Problem = "template"; return false; }
            for (int k = from; k < open; k++)
            {
                if (!t[k].Is("(") || k < from + 2 || !t[k - 1].IsIdent) { if (t[k].Is("(")) k = CppTextScan.MatchClose(t, k); continue; }
                if (!t[k - 2].Is("::")) return false; // a free function
                int q = k - 2;
                var parts = new List<string>();
                while (q >= from + 1 && t[q].Is("::") && t[q - 1].IsIdent)
                {
                    parts.Insert(0, t[q - 1].Text);
                    q -= 2;
                }
                if (parts.Count == 0) return false;
                a.ClassName = parts[parts.Count - 1];
                a.Qualifier = string.Concat(parts.Select(p => p + "::"));
                a.FunctionName = t[k - 1].Text;
                int close = CppTextScan.MatchClose(t, k);
                for (int m = close + 1; m < open && !t[m].Is(":"); m++) if (t[m].Is("const")) a.IsConst = true;
                return true;
            }
            return false;
        }

        private static bool IsInitializerBrace(List<ScanToken> t, int close)
        {
            int open = CodeScan.MatchOpen(t, close);
            return open > 0 && (t[open - 1].Is("=") || t[open - 1].Is("(") || t[open - 1].Is(",") || t[open - 1].IsIdent);
        }

        /// <summary>return / goto / labels anywhere, break / continue outside a loop or switch of the selection (lambda bodies skipped).</summary>
        private static string JumpProblem(List<ScanToken> t, int first, int last)
        {
            // stack of braces opened in the selection: what they belong to
            var stack = new List<string>();
            string pending = null; // a loop / switch head whose body is next
            int depth = 0;
            for (int k = first; k <= last; k++)
            {
                var tok = t[k];
                if (tok.Is("(")) depth++;
                else if (tok.Is(")"))
                {
                    depth--;
                    string head = CodeScan.ControlHeadOf(t, k);
                    if (head == "for" || head == "while" || head == "switch") pending = head;
                    continue;
                }
                if (tok.Is("do")) { pending = "do"; continue; }
                if (tok.Is("{"))
                {
                    var kind = CodeScan.Classify(t, k, out _);
                    if (kind == BodyKind.Lambda)
                    {
                        k = CppTextScan.MatchClose(t, k); // anything goes inside a lambda
                        pending = null;
                        continue;
                    }
                    stack.Add(pending ?? "block");
                    pending = null;
                    continue;
                }
                if (tok.Is("}")) { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); else return "partial-statement"; continue; }
                if (tok.Is(";") && depth == 0) pending = null;
                if (!tok.IsIdent) continue;
                if (tok.Is("return") || tok.Is("co_return") || tok.Is("co_yield") || tok.Is("co_await") || tok.Is("goto")) return "jump";
                if (tok.Is("break") && !stack.Any(x => x == "for" || x == "while" || x == "do" || x == "switch") && pending == null) return "jump";
                if (tok.Is("continue") && !stack.Any(x => x == "for" || x == "while" || x == "do") && pending == null) return "jump";
                if ((tok.Is("case") || tok.Is("default")) && !stack.Contains("switch")) return "label";
                // "Label:" at statement start
                if (k + 1 <= last && t[k + 1].Is(":") && !(k + 2 <= last && t[k + 2].Is(":")) && k > first && (t[k - 1].Is(";") || t[k - 1].Is("{") || t[k - 1].Is("}")) &&
                    !tok.Is("public") && !tok.Is("private") && !tok.Is("protected") && !tok.Is("default"))
                    return "label";
            }
            return stack.Count == 0 ? null : "partial-statement";
        }

        private static bool IsMemberAccess(List<ScanToken> t, int k) =>
            (k > 0 && (t[k - 1].Is(".") || t[k - 1].Is("::") || (t[k - 1].Is(">") && k > 1 && t[k - 2].Is("-") && CodeScan.End(t[k - 2]) == t[k - 1].Offset))) ||
            (k + 1 < t.Count && t[k + 1].Is("::"));

        private enum Write
        {
            None,

            /// <summary><c>X = …</c></summary>
            Assign,

            /// <summary><c>X += …</c>, <c>++X</c>, <c>X++</c></summary>
            Modify,

            /// <summary>Method call on it, subscript, address, passed to a call, range-for over it: may change it.</summary>
            MaybeModify,
        }

        private static Write WriteKind(List<ScanToken> t, int k)
        {
            ScanToken N(int i) => i < t.Count ? t[i] : default;
            bool Adjacent(int i) => i + 1 < t.Count && CodeScan.End(t[i]) == t[i + 1].Offset;
            var n = N(k + 1);
            if (n.Text != null)
            {
                if (n.Is("=") && !N(k + 2).Is("=")) return Write.Assign;
                if (n.Text.Length == 1 && "+-*/%&|^".IndexOf(n.Text[0]) >= 0 && N(k + 2).Is("=") && Adjacent(k + 1)) return Write.Modify;
                if ((n.Is("+") || n.Is("-")) && N(k + 2).Is(n.Text) && Adjacent(k + 1)) return Write.Modify;
                if ((n.Is("<") || n.Is(">")) && N(k + 2).Is(n.Text) && N(k + 3).Is("=")) return Write.Modify;
                if (n.Is("[")) return Write.MaybeModify;
                if (n.Is(".") && N(k + 2).IsIdent && N(k + 3).Is("(")) return Write.MaybeModify;
                if (n.Is(".") && N(k + 2).IsIdent && (N(k + 3).Is("=") && !N(k + 4).Is("="))) return Write.Modify; // S.Field = 1
                if (n.Is(".") && N(k + 2).IsIdent) return Write.MaybeModify; // S.Field op= / S.Field passed on: be safe
            }
            if (k >= 2 && (t[k - 1].Is("+") || t[k - 1].Is("-")) && t[k - 2].Is(t[k - 1].Text) && Adjacent(k - 2)) return Write.Modify;
            if (k >= 1 && t[k - 1].Is("&"))
            {
                var p = k >= 2 ? t[k - 2] : default;
                if (p.Text == null || !(p.IsIdent && !p.Is("return") || p.Is(")") || p.Is("]"))) return Write.MaybeModify; // &X
            }
            // passed to a call: "(X," "(X)" ", X," ", X)"
            if (k >= 1 && (t[k - 1].Is("(") || t[k - 1].Is(",")) && n.Text != null && (n.Is(",") || n.Is(")")))
            {
                int open = k - 1, depth = 0;
                for (int m = k - 1; m >= 0; m--)
                {
                    if (t[m].Is(")")) depth++;
                    else if (t[m].Is("(")) { if (depth == 0) { open = m; break; } depth--; }
                    else if (t[m].Is(";") || t[m].Is("{") || t[m].Is("}")) { open = -1; break; }
                }
                if (open > 0 && t[open].Is("(") && (t[open - 1].IsIdent || t[open - 1].Is(">")) && !CodeScan.ControlKeywords.Contains(t[open - 1].Text)) return Write.MaybeModify;
            }
            // range-for "for (auto& E : X)"
            if (k >= 1 && t[k - 1].Is(":") && n.Text != null && n.Is(")") && CodeScan.ControlHeadOf(t, k + 1) == "for") return Write.MaybeModify;
            return Write.None;
        }

        private static bool KnownType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return false;
            if (type.IndexOf("auto", StringComparison.Ordinal) >= 0 || type.IndexOf("decltype", StringComparison.Ordinal) >= 0) return false;
            if (type.IndexOf('[') >= 0 || type.IndexOf('(') >= 0) return false;
            return true;
        }

        private static bool IsReferenceType(string type) => type != null && type.TrimEnd().EndsWith("&", StringComparison.Ordinal);

        private static bool IsPointer(string type) => type != null && type.TrimEnd().EndsWith("*", StringComparison.Ordinal);

        /// <summary>Scalars, enums (E…), pointers, TObjectPtr / TWeakObjectPtr / TSubclassOf and small UE value types: by value.</summary>
        public static bool IsCheap(string type)
        {
            string t = (type ?? string.Empty).Trim();
            if (t.StartsWith("const ", StringComparison.Ordinal)) t = t.Substring(6).Trim();
            if (t.EndsWith("*", StringComparison.Ordinal)) return true;
            if (t.StartsWith("TObjectPtr<", StringComparison.Ordinal) || t.StartsWith("TWeakObjectPtr<", StringComparison.Ordinal) ||
                t.StartsWith("TSubclassOf<", StringComparison.Ordinal) || t.StartsWith("TEnumAsByte<", StringComparison.Ordinal)) return true;
            var words = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && words.All(w => CheapTypes.Contains(w))) return true;
            string last = words.Length == 0 ? string.Empty : words[words.Length - 1];
            int sep = last.LastIndexOf("::", StringComparison.Ordinal);
            string simple = sep >= 0 ? last.Substring(sep + 2) : last;
            if (sep >= 0 && simple == "Type") return true; // EFoo::Type
            return simple.Length > 1 && simple[0] == 'E' && char.IsUpper(simple[1]) && simple.IndexOf('<') < 0;
        }

        private static string StripTopConst(string type)
        {
            string t = (type ?? string.Empty).Trim();
            if (t.StartsWith("const ", StringComparison.Ordinal) && !IsPointer(t)) t = t.Substring(6).Trim();
            return t;
        }

        // ---------------------------------------------------------------- generation

        /// <summary>Generated pieces: the call (replacing the selection), the definition (.cpp) and the declaration (class).</summary>
        public sealed class Generated
        {
            public string Call { get; set; }
            public string Definition { get; set; }
            public string Declaration { get; set; }
        }

        public static Generated Generate(ExtractMethodAnalysis a, string name, bool isStatic, string nl)
        {
            string pars = string.Join(", ", a.Parameters.Select(p => p.Declaration));
            string args = string.Join(", ", a.Parameters.Select(p => p.Name));
            string constText = a.IsConst && !isStatic ? " const" : string.Empty;
            var g = new Generated();
            string call = name + "(" + args + ");";
            if (a.ReturnVariable != null)
                call = (a.ReturnDeclaredInSelection ? a.ReturnType + " " : string.Empty) + a.ReturnVariable + " = " + call;
            g.Call = call;
            var def = new StringBuilder();
            def.Append(a.ReturnType).Append(' ').Append(a.Qualifier).Append(name).Append('(').Append(pars).Append(')').Append(constText).Append(nl);
            def.Append('{').Append(nl);
            foreach (var l in a.BodyLines) def.Append(l).Append(nl);
            if (a.ReturnVariable != null) def.Append('\t').Append("return ").Append(a.ReturnVariable).Append(';').Append(nl);
            def.Append('}').Append(nl);
            g.Definition = def.ToString();
            g.Declaration = (isStatic ? "static " : string.Empty) + a.ReturnType + " " + name + "(" + pars + ")" + constText + ";";
            return g;
        }

        /// <summary>Edits of the source file: the call in place of the selection, the definition after the enclosing function.</summary>
        public static List<TextEdit> SourceEdits(string filePath, string text, ExtractMethodAnalysis a, Generated g)
        {
            string nl = CppTextScan.NewlineOf(text);
            bool wholeLines = a.Start == CodeScan.LineStart(text, a.Start) && a.Start != a.End && CodeScan.IsBlank(text, a.Start, a.Start + a.Indent.Length);
            string call = wholeLines ? a.Indent + g.Call : g.Call;
            var edits = new List<StructuredEdit> { StructuredEdit.Replace(a.Start, a.End, call) };
            int after = CodeScan.LineEnd(text, a.FunctionEnd);
            edits.Add(StructuredEdit.Replace(after, after, nl + nl + g.Definition.TrimEnd('\r', '\n')));
            return StructuredEditComposer.Compose(filePath, text, edits, out _);
        }

        /// <summary>Whether a name is free (not an identifier of the file, a keyword, or invalid).</summary>
        public static bool IsFreeName(ExtractMethodAnalysis a, string name, string headerText = null)
        {
            if (!RenamePlanner.IsValidIdentifier(name)) return false;
            if (a.Identifiers.Contains(name)) return false;
            if (!string.IsNullOrEmpty(headerText) && CppTextScan.Tokenize(headerText).Any(x => x.IsIdent && x.Is(name))) return false;
            return true;
        }

        /// <summary>Whether the class declares <paramref name="function"/> static (header text; overloads must agree).</summary>
        public static bool? IsStaticInHeader(string headerText, string className, string function)
        {
            var t = CppTextScan.Tokenize(headerText ?? string.Empty);
            var body = CppTextScan.FindClassBody(t, className);
            if (body == null) return null;
            bool? result = null;
            int depth = 0, stmt = body.OpenToken + 1;
            for (int k = body.OpenToken + 1; k < body.CloseToken; k++)
            {
                var tok = t[k];
                if (tok.Is("{") || tok.Is("(") || tok.Is("[")) depth++;
                else if (tok.Is("}") || tok.Is(")") || tok.Is("]")) { depth--; if (depth == 0 && tok.Is("}")) stmt = k + 1; }
                else if (depth == 0 && tok.Is(";")) stmt = k + 1;
                else if (depth == 0 && tok.Is(":") && k > 0 && (t[k - 1].Is("public") || t[k - 1].Is("private") || t[k - 1].Is("protected"))) stmt = k + 1;
                if (depth != 0 || !tok.Is(function) || k + 1 >= body.CloseToken || !t[k + 1].Is("(")) continue;
                bool isStatic = false;
                for (int m = stmt; m < k; m++) if (t[m].Is("static")) isStatic = true;
                if (result.HasValue && result.Value != isStatic) return null;
                result = isStatic;
            }
            return result;
        }
    }
}
