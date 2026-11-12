using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// Parameter info: finds the call enclosing the caret (nested parentheses, template angle brackets, string / char literals
    /// and lambdas are skipped), the active argument index, and the signatures of every overload of the callee (functions,
    /// methods through chains, constructors, delegate Broadcast / Execute from their DECLARE_* macro).
    /// </summary>
    public static class SignatureHelpProvider
    {
        /// <summary>The innermost unclosed '(' of a call before the caret and the index of the argument the caret is in.</summary>
        public static bool FindCall(DocumentModel doc, int caret, out int openParen, out int activeArg, out int argCount)
        {
            openParen = -1;
            activeArg = 0;
            argCount = 0;
            int last = doc.TokenIndexFrom(caret) - 1;
            // a caret inside a string / char literal still belongs to the call
            int depth = 0;
            for (int k = last; k >= 0 && k > last - 2000; k--)
            {
                var t = doc.Tokens[k];
                if (t.Kind == TokenKind.Directive) return false;
                if (t.Is(Punct.RParen) || t.Is(Punct.RBracket)) depth++;
                else if (t.Is(Punct.RBrace))
                {
                    // a lambda / init-list body inside the arguments: skip it whole
                    int open = doc.MatchBackward(k, Punct.LBrace, Punct.RBrace);
                    if (open < 0) return false;
                    k = open;
                }
                else if (t.Is(Punct.LBracket))
                {
                    if (depth == 0) return false; // caret inside [...] (subscript / lambda capture)
                    depth--;
                }
                else if (t.Is(Punct.LParen))
                {
                    if (depth == 0)
                    {
                        int callee = k - 1;
                        while (callee >= 0 && doc.Tokens[callee].Kind == TokenKind.DocComment) callee--;
                        if (callee < 0) return false;
                        var ct = doc.Tokens[callee];
                        bool isCall = ct.Kind == TokenKind.Identifier || ct.Is(Punct.Gt) || ct.Is(Punct.GtGt);
                        if (!isCall)
                        {
                            // (expr), if (, for (, while (: keep looking outwards
                            continue;
                        }
                        openParen = k;
                        break;
                    }
                    depth--;
                }
                else if (t.Is(Punct.LBrace) || t.Is(Punct.Semicolon))
                {
                    if (depth == 0) return false;
                }
            }
            if (openParen < 0) return false;
            // count top-level commas between '(' and the caret, and the arguments of the whole call
            int angle = 0, nest = 0;
            for (int k = openParen + 1; k < doc.Tokens.Length && doc.Tokens[k].Start < caret; k++)
            {
                var t = doc.Tokens[k];
                if (t.Is(Punct.LParen) || t.Is(Punct.LBracket) || t.Is(Punct.LBrace)) nest++;
                else if (t.Is(Punct.RParen) || t.Is(Punct.RBracket) || t.Is(Punct.RBrace))
                {
                    if (nest == 0) break;
                    nest--;
                }
                else if (t.Is(Punct.Lt) && nest == 0 && IsTemplateOpen(doc, k)) angle++;
                else if ((t.Is(Punct.Gt) || t.Is(Punct.GtGt)) && angle > 0 && nest == 0) angle = Math.Max(0, angle - (t.Is(Punct.GtGt) ? 2 : 1));
                else if (t.Is(Punct.Semicolon) && nest == 0) break;
                else if (t.Is(Punct.Comma) && nest == 0 && angle == 0) activeArg++;
            }
            // arguments typed so far: commas + 1 when there is anything after '('
            bool hasText = doc.Tokens.Length > openParen + 1 && doc.Tokens[openParen + 1].Start < caret && !doc.Is(openParen + 1, Punct.RParen);
            argCount = activeArg + (hasText || activeArg > 0 ? 1 : 0);
            return true;
        }

        /// <summary><c>Name&lt;</c> without a space where Name looks like a template (UE T-prefix, casts, factories, std::...).</summary>
        private static bool IsTemplateOpen(DocumentModel doc, int lt)
        {
            int n = lt - 1;
            if (!doc.IsIdentifier(n)) return false;
            if ((doc.Tokens[lt].Flags & TokenFlags.PrecededBySpace) != 0) return false;
            string name = doc.TokenText(n);
            if (name.Length > 1 && name[0] == 'T' && char.IsUpper(name[1])) return true;
            return TypeText.TypedFactories.Contains(name) || name.EndsWith("_cast", StringComparison.Ordinal) || doc.Is(n - 1, Punct.ColonColon) ||
                   name is "TFunction" or "TFunctionRef" or "vector" or "map" or "array";
        }

        public static SignatureHelpResponse Get(IsSession s, int caret)
        {
            var resp = new SignatureHelpResponse();
            var doc = s.Doc;
            if (!FindCall(doc, caret, out int open, out int active, out int args)) return resp;
            var (ol, oc) = doc.PositionOf(doc.Tokens[open].Start);
            resp.OpenParenLine = ol;
            resp.OpenParenColumn = oc;
            resp.ActiveParameter = active;

            int callee = open - 1;
            while (callee >= 0 && doc.Tokens[callee].Kind == TokenKind.DocComment) callee--;
            if (doc.Is(callee, Punct.Gt) || doc.Is(callee, Punct.GtGt))
            {
                int d = 0;
                for (int k = callee; k >= 0 && k > callee - 200; k--)
                {
                    if (doc.Is(k, Punct.Gt)) d++;
                    else if (doc.Is(k, Punct.GtGt)) d += 2;
                    else if (doc.Is(k, Punct.Lt) && --d == 0) { callee = k - 1; break; }
                }
            }
            if (!doc.IsIdentifier(callee)) return resp;
            string name = doc.TokenText(callee);
            string cls = doc.EnclosingClass(doc.Tokens[callee].Start, out _);
            var sigs = new List<SignatureInfo>();
            int before = callee - 1;
            while (before >= 0 && doc.Tokens[before].Kind == TokenKind.DocComment) before--;

            if (doc.Is(before, Punct.Dot) || doc.Is(before, Punct.Arrow))
            {
                var types = new ExpressionTypes(s, cls);
                string left = types.TypeOf(before - 1);
                MemberSignatures(s, left, doc.Is(before, Punct.Arrow), name, sigs);
            }
            if (sigs.Count == 0)
            {
                var r = SymbolResolver.ResolveToken(s.Ctx, doc, callee, 0);
                var cands = r.Candidates;
                if (cands.Count > 0 && cands.All(c => TypeText.IsTypeKind(c.Kind)))
                {
                    // FVector(...) / new AFoo(...): constructors
                    var rec = s.FindRecord(cands[0].Container != null ? cands[0].Container + "::" + cands[0].Name : cands[0].Name, doc.Tokens[callee].Start);
                    if (rec != null)
                        foreach (var e in s.MembersOf(rec).Members)
                            if (e.Depth == 0 && e.Decl.Kind == SymbolKind.Constructor) AddDecl(s, e.Doc, e.Index, sigs);
                }
                else
                {
                    foreach (var c in cands)
                    {
                        if (!TypeText.IsCallableKind(c.Kind) && c.Kind != SymbolKind.Macro) continue;
                        var (d, i) = s.DeclOf(c);
                        if (d != null) AddDecl(s, d, i, sigs);
                        else if (c.Signature != null) AddLabel(c.Signature, null, c, sigs);
                    }
                }
            }
            resp.Signatures = Dedupe(sigs);
            resp.ActiveSignature = PickActive(resp.Signatures, args, active);
            return resp;
        }

        private static void MemberSignatures(IsSession s, string left, bool arrow, string name, List<SignatureInfo> sigs)
        {
            if (left == null) return;
            var shape = TypeShape.Parse(left);
            string target = shape.IsPointerLike && (arrow || shape.Shape != AccessShape.SmartPointer) ? shape.Pointee() : left;
            var ts = TypeShape.Parse(target);
            if (!arrow && shape.Shape == AccessShape.SmartPointer)
            {
                AddBuiltin(UeBuiltins.ApiOf(shape.Head), shape, name, null, null, sigs);
                return;
            }
            var rec = s.FindRecord(ts.Head);
            if (rec != null)
            {
                var hidden = new HashSet<int>();
                int firstDepth = -1;
                foreach (var e in s.MembersOf(rec).Members)
                {
                    if (e.Decl.Name != name || !TypeText.IsCallableKind(e.Decl.Kind)) continue;
                    // the most derived level that declares the name hides the base overloads (C++ name hiding)
                    if (firstDepth < 0) firstDepth = e.Depth;
                    if (e.Depth != firstDepth) continue;
                    AddDecl(s, e.Doc, e.Index, sigs);
                }
                if (sigs.Count > 0) return;
            }
            var api = UeBuiltins.ApiOf(ts.Head);
            if (api != null)
            {
                AddBuiltin(api, ts, name, null, null, sigs);
                return;
            }
            var del = CompletionEngine.FindDelegate(s, ts.Head, out var decl);
            if (del != null)
            {
                bool dyn = (decl?.Modifiers & DeclModifiers.Dynamic) != 0 || (del.Ue & UeFlags.UDelegate) != 0;
                bool multi = (decl?.Modifiers & DeclModifiers.Multicast) != 0;
                AddBuiltin(UeBuiltins.DelegateApi(dyn, multi), null, name, CompletionEngine.DelegateParameters(decl, dyn), decl?.Type, sigs);
            }
        }

        private static void AddBuiltin(IReadOnlyList<BuiltinMember> api, TypeShape shape, string name, string parameters, string ret, List<SignatureInfo> sigs)
        {
            if (api == null) return;
            foreach (var m in api)
                if (m.Name == name)
                    AddLabel(UeBuiltins.Instantiate(m.Signature, shape?.Arg(0), shape?.Arg(0), shape?.Arg(1), parameters, ret), null, null, sigs);
        }

        private static void AddDecl(IsSession s, DocumentModel doc, int index, List<SignatureInfo> sigs)
        {
            var d = doc.Decls[index];
            string label;
            if (d.Kind == SymbolKind.Macro) label = d.Name + "(" + (d.Parameters ?? string.Empty) + ")";
            else
            {
                string ret = string.IsNullOrEmpty(d.Type) ? string.Empty : d.Type + " ";
                string cst = (d.Modifiers & DeclModifiers.Const) != 0 ? " const" : string.Empty;
                label = ret + d.Name + "(" + (d.Parameters ?? string.Empty) + ")" + cst;
            }
            string docText = DocComments.Get(doc, index);
            var sym = s.Ctx.ToSymbol(doc, index);
            AddLabel(label, docText, sym, sigs);
        }

        private static void AddLabel(string label, string documentation, SymbolInfo sym, List<SignatureInfo> sigs)
        {
            var info = new SignatureInfo { Label = label, Documentation = documentation, Symbol = sym };
            int open = label.IndexOf('(');
            if (open < 0)
            {
                sigs.Add(info);
                return;
            }
            int close = MatchClose(label, open);
            if (close < 0) close = label.Length;
            int start = open + 1;
            int depth = 0;
            var paramDocs = DocComments.ParamDocs(documentation);
            for (int i = open + 1; i <= close; i++)
            {
                char c = i < label.Length ? label[i] : ')';
                if (i < close && (c == '<' || c == '(' || c == '[' || c == '{')) depth++;
                else if (i < close && (c == '>' || c == ')' || c == ']' || c == '}')) depth--;
                else if (i == close || c == ',' && depth == 0)
                {
                    int s0 = start;
                    while (s0 < i && label[s0] == ' ') s0++;
                    int e0 = i;
                    while (e0 > s0 && label[e0 - 1] == ' ') e0--;
                    if (e0 > s0)
                    {
                        string text = label.Substring(s0, e0 - s0);
                        if (text != "void")
                        {
                            string pname = ParamName(text);
                            info.Parameters.Add(new SignatureParameter
                            {
                                Name = pname,
                                LabelStart = s0,
                                LabelLength = e0 - s0,
                                Documentation = pname != null && paramDocs.TryGetValue(pname, out var pd) ? pd : null,
                            });
                        }
                    }
                    start = i + 1;
                }
            }
            sigs.Add(info);
        }

        private static int MatchClose(string s, int open)
        {
            int d = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') d++;
                else if (s[i] == ')' && --d == 0) return i;
            }
            return -1;
        }

        /// <summary>Name of a parameter declaration: <c>const FVector&amp; Dir = FVector::ZeroVector</c> → Dir.</summary>
        public static string ParamName(string p)
        {
            p = StripDefault(p).Trim();
            int end = p.Length;
            while (end > 0 && p[end - 1] == ']') { int lb = p.LastIndexOf('[', end - 1); if (lb < 0) break; end = lb; }
            int s = end;
            while (s > 0 && TypeText.IsIdentPart(p[s - 1])) s--;
            if (s == end || s == 0) return null;
            string before = p.Substring(0, s).Trim();
            if (before.Length == 0 || before == "const" || before == "unsigned" || before == "signed" || before.EndsWith("::", StringComparison.Ordinal)) return null;
            return p.Substring(s, end - s);
        }

        /// <summary>Removes a top-level <c>= default</c> from a parameter.</summary>
        public static string StripDefault(string p)
        {
            int depth = 0;
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth--;
                else if (c == '=' && depth == 0) return p.Substring(0, i).TrimEnd();
            }
            return p;
        }

        /// <summary>Splits on top-level commas (angle brackets, parentheses, brackets and braces nest).</summary>
        public static List<string> SplitTopLevel(string s)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(s)) return parts;
            int depth = 0, start = 0;
            bool inString = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\')) inString = !inString;
                if (inString) continue;
                if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        private static List<SignatureInfo> Dedupe(List<SignatureInfo> sigs)
        {
            // declaration and definition of the same overload: keep the first (declarations come first from headers)
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<SignatureInfo>();
            foreach (var g in sigs.OrderBy(x => x.Symbol?.IsDefinition == true ? 1 : 0))
            {
                string key = TypeText.ParameterKey(g.Label) ?? g.Label;
                if (seen.Add(key)) list.Add(g);
                else if (g.Documentation != null)
                {
                    var existing = list.First(x => (TypeText.ParameterKey(x.Label) ?? x.Label) == key);
                    existing.Documentation ??= g.Documentation;
                }
            }
            list.Sort((a, b) => a.Parameters.Count.CompareTo(b.Parameters.Count));
            return list;
        }

        /// <summary>Fewest parameters that still fit the arguments typed so far (variadic "..." fits anything).</summary>
        private static int PickActive(List<SignatureInfo> sigs, int args, int active)
        {
            if (sigs.Count == 0) return 0;
            int need = Math.Max(args, active + 1);
            for (int i = 0; i < sigs.Count; i++)
            {
                int n = sigs[i].Parameters.Count;
                bool variadic = sigs[i].Parameters.Any(p => p.Name == null && sigs[i].Label.Substring(p.LabelStart, p.LabelLength).Contains("..."));
                if (n >= need || variadic) return i;
            }
            return sigs.Count - 1;
        }
    }

    /// <summary>Doc comments: <c>/** */</c>, <c>///</c> and plain <c>//</c> lines directly above a declaration (or above its UFUNCTION / UPROPERTY line).</summary>
    public static class DocComments
    {
        public static string Get(DocumentModel doc, int index)
        {
            var d = doc.Decls[index];
            if (!d.DocComment.IsNone)
            {
                string t = CppParser.GetDocCommentText(doc.Text, d.DocComment);
                if (!string.IsNullOrWhiteSpace(t)) return Clean(t);
            }
            // walk up from the declaration's first line: skip reflection macro / template lines, then collect comment lines
            int line = d.Line;
            int ti = doc.TokenIndexFrom(doc.Lines.GetLineStart(line));
            // the declaration may start on an earlier line (return type on its own line, UFUNCTION(...) above)
            int first = line;
            for (int k = ti - 1; k >= 0 && k > ti - 60; k--)
            {
                var t = doc.Tokens[k];
                if (t.Is(Punct.Semicolon) || t.Is(Punct.LBrace) || t.Is(Punct.RBrace) || t.Kind == TokenKind.Directive || t.Kind == TokenKind.DocComment) break;
                if (t.Is(Punct.Colon) && (doc.Is(k - 1, Keyword.Public) || doc.Is(k - 1, Keyword.Protected) || doc.Is(k - 1, Keyword.Private))) break;
                first = doc.Lines.GetLine(t.Start);
            }
            var lines = new List<string>();
            for (int l = first - 1; l >= 0 && lines.Count < 40; l--)
            {
                string text = doc.LineText(l).Trim();
                if (text.StartsWith("//", StringComparison.Ordinal))
                {
                    lines.Add(text);
                    continue;
                }
                if (text.EndsWith("*/", StringComparison.Ordinal))
                {
                    // a block comment ending here
                    var block = new List<string> { text };
                    int m = l;
                    while (!block[block.Count - 1].Contains("/*") && m > 0 && block.Count < 60) block.Add(doc.LineText(--m).Trim());
                    if (!block[block.Count - 1].Contains("/*")) break;
                    lines.AddRange(block); // bottom-up like the // lines
                    break;
                }
                break;
            }
            if (lines.Count == 0) return null;
            lines.Reverse();
            var sb = new StringBuilder();
            foreach (var raw in lines)
            {
                string t = raw;
                if (t.StartsWith("///", StringComparison.Ordinal) || t.StartsWith("//!", StringComparison.Ordinal)) t = t.Substring(3);
                else if (t.StartsWith("//", StringComparison.Ordinal)) t = t.Substring(2);
                if (t.StartsWith("/**", StringComparison.Ordinal) || t.StartsWith("/*!", StringComparison.Ordinal)) t = t.Substring(3);
                else if (t.StartsWith("/*", StringComparison.Ordinal)) t = t.Substring(2);
                if (t.EndsWith("*/", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 2);
                t = t.Trim();
                if (t.StartsWith("*", StringComparison.Ordinal)) t = t.Substring(1).Trim();
                if (t.Length == 0 && sb.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
            string result = Clean(sb.ToString());
            // a commented-out line of code is not documentation
            if (result.Length == 0 || result.EndsWith(";", StringComparison.Ordinal) && !result.Contains(' ')) return null;
            return result;
        }

        /// <summary>Trims, collapses blank runs and removes separator lines (//////, -----).</summary>
        private static string Clean(string s)
        {
            var outLines = new List<string>();
            foreach (var raw in s.Split('\n'))
            {
                string l = raw.TrimEnd();
                if (l.Length > 2 && l.All(ch => ch == '/' || ch == '*' || ch == '-' || ch == '=' || ch == ' ')) continue;
                if (l.Length == 0 && (outLines.Count == 0 || outLines[outLines.Count - 1].Length == 0)) continue;
                outLines.Add(l);
            }
            while (outLines.Count > 0 && outLines[outLines.Count - 1].Length == 0) outLines.RemoveAt(outLines.Count - 1);
            string r = string.Join("\n", outLines);
            return r.Length > 4000 ? r.Substring(0, 3999) + "…" : r;
        }

        /// <summary><c>@param Name text</c> entries of a doc comment.</summary>
        public static Dictionary<string, string> ParamDocs(string doc)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(doc)) return d;
            foreach (var raw in doc.Split('\n'))
            {
                string l = raw.Trim();
                if (!l.StartsWith("@param", StringComparison.Ordinal) && !l.StartsWith("\\param", StringComparison.Ordinal)) continue;
                l = l.Substring(6).Trim();
                if (l.StartsWith("[", StringComparison.Ordinal)) { int rb = l.IndexOf(']'); if (rb > 0) l = l.Substring(rb + 1).Trim(); }
                int sp = l.IndexOfAny(new[] { ' ', '\t' });
                string name = sp < 0 ? l : l.Substring(0, sp);
                string text = sp < 0 ? string.Empty : l.Substring(sp + 1).Trim().TrimStart('-', ':').Trim();
                if (name.Length > 0) d[name] = text;
            }
            return d;
        }
    }
}
