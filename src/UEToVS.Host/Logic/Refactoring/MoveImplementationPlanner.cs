using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>A member function defined inside its class body in a header (offsets into the header text).</summary>
    public sealed class InlineMember
    {
        public string Name { get; set; }
        public string ClassName { get; set; }

        /// <summary>Namespace qualifier of the class ("N1::N2::") or empty.</summary>
        public string NamespacePrefix { get; set; } = string.Empty;

        /// <summary>First token of the declaration (after any UFUNCTION(...) / access label).</summary>
        public int HeadStart { get; set; }

        /// <summary>End of the last token before the body / initializer list (after the qualifiers).</summary>
        public int SignatureEnd { get; set; }

        /// <summary>Offset of ':' of a ctor initializer list, or -1.</summary>
        public int InitializerStart { get; set; } = -1;

        public int BodyOpen { get; set; }
        public int BodyClose { get; set; }
        public int Line { get; set; }

        /// <summary>Text of the definition for the .cpp: <c>Ret Class::Name(params) const</c> (+ initializer list).</summary>
        public string DefinitionHead { get; set; }

        /// <summary>Parameter list text as written (defaults included).</summary>
        public string Parameters { get; set; }

        /// <summary>Tokens to drop from the header declaration (inline / FORCEINLINE): (offset, length).</summary>
        public List<(int Offset, int Length)> DropFromHeader { get; } = new List<(int, int)>();

        public override string ToString() => ClassName + "::" + Name + " @" + Line;
    }

    /// <summary>An out-of-line member definition in a source file (offsets into the source text).</summary>
    public sealed class OutOfLineMember
    {
        public string ClassName { get; set; }
        public string Name { get; set; }
        public int Start { get; set; }
        public int ParamOpen { get; set; }
        public int ParamClose { get; set; }
        public int InitializerStart { get; set; } = -1;
        public int BodyOpen { get; set; }
        public int BodyClose { get; set; }
        public int Line { get; set; }
        public bool IsConst { get; set; }
        public string Parameters { get; set; }
        public override string ToString() => ClassName + "::" + Name + " @" + Line;
    }

    /// <summary>Result of a move: edits for both files, or a problem code.</summary>
    public sealed class MovePlan
    {
        public string Problem { get; set; }
        public List<TextEdit> HeaderEdits { get; } = new List<TextEdit>();
        public List<TextEdit> SourceEdits { get; } = new List<TextEdit>();
        public List<string> Moved { get; } = new List<string>();

        /// <summary>Line of the first moved definition in the target file (after the edits), for the caret.</summary>
        public int TargetLine { get; set; } = -1;

        public string Reason { get; set; }
        public bool Ok => Problem == null && (HeaderEdits.Count > 0 || SourceEdits.Count > 0);
        public static MovePlan Fail(string problem) => new MovePlan { Problem = problem };
    }

    /// <summary>
    /// Move Implementation to Source / Header (pure, v0.4.5). Header → source: an in-class function body (or every inline body
    /// of a class) becomes an out-of-line definition in the paired .cpp, placed next to its neighbours (CodePlacement); the
    /// definition drops virtual / override / final / static / explicit / inline / FORCEINLINE, UFUNCTION(...) and default
    /// arguments and gets the class qualifier (nested types in the return type too); the header keeps the declaration.
    /// Source → header: the reverse into the matching bodiless in-class declaration.
    /// Problem codes: "no-member", "template", "constexpr", "nested-class", "friend", "no-declaration", "ambiguous", "not-found".
    /// </summary>
    public static class MoveImplementationPlanner
    {
        private static readonly HashSet<string> DropKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "virtual", "static", "explicit", "inline", "FORCEINLINE", "FORCENOINLINE", "FORCEINLINE_DEBUGGABLE", "UE_NODISCARD", "UE_NODISCARD_CTOR",
        };

        private static readonly HashSet<string> TrailingDrop = new HashSet<string>(StringComparer.Ordinal) { "override", "final" };

        // ---------------------------------------------------------------- header side

        /// <summary>The in-class member function body at / around the caret line (on its signature or inside its body); null when none.</summary>
        public static InlineMember InlineMemberAt(string headerText, int offset, out string problem)
        {
            problem = "no-member";
            var t = CppTextScan.Tokenize(headerText);
            foreach (var (cls, body, ns) in Classes(t))
            {
                if (offset < t[body.OpenToken].Offset || offset > t[body.CloseToken].Offset) continue;
                foreach (var m in Members(headerText, t, cls, body, ns, out var problems))
                {
                    int lineStart = CodeScan.LineStart(headerText, m.HeadStart);
                    if (offset >= lineStart && offset <= m.BodyClose + 1)
                    {
                        problem = null;
                        return m;
                    }
                }
                // a member that cannot be moved: say why
                foreach (var (start, end, why) in Rejected(headerText, t, body))
                    if (offset >= CodeScan.LineStart(headerText, start) && offset <= end) { problem = why; return null; }
            }
            return null;
        }

        /// <summary>Every movable inline member of <paramref name="className"/> (source order).</summary>
        public static List<InlineMember> InlineMembersOf(string headerText, string className, int hintLine = -1)
        {
            var t = CppTextScan.Tokenize(headerText);
            var body = CppTextScan.FindClassBody(t, className, hintLine);
            if (body == null) return new List<InlineMember>();
            var cls = Classes(t).FirstOrDefault(c => c.Body.OpenToken == body.OpenToken);
            if (cls.Name == null) return new List<InlineMember>();
            return Members(headerText, t, cls.Name, body, cls.Ns, out _);
        }

        /// <summary>Top-level classes (namespaces entered; nested classes are skipped: their members are not offered).</summary>
        private static IEnumerable<(string Name, ClassBody Body, string Ns)> Classes(List<ScanToken> t)
        {
            var nsStack = new List<(string Name, int Close)>();
            for (int k = 0; k < t.Count; k++)
            {
                while (nsStack.Count > 0 && k > nsStack[nsStack.Count - 1].Close) nsStack.RemoveAt(nsStack.Count - 1);
                if (t[k].Is("namespace"))
                {
                    int m = k + 1;
                    var name = new StringBuilder();
                    while (m < t.Count && (t[m].IsIdent || t[m].Is("::"))) name.Append(t[m++].Text);
                    if (m < t.Count && t[m].Is("{"))
                    {
                        nsStack.Add((name.ToString(), CppTextScan.MatchClose(t, m)));
                        k = m;
                    }
                    continue;
                }
                if (!(t[k].Is("class") || t[k].Is("struct")) || (k > 0 && (t[k - 1].Is("enum") || t[k - 1].Is("friend")))) continue;
                int j = k + 1, nameAt = -1;
                while (j < t.Count && j < k + 6 && t[j].IsIdent)
                {
                    if (j + 1 < t.Count && (t[j + 1].Is("{") || t[j + 1].Is(":") && !t[j + 1].Is("::") || t[j + 1].Is("final"))) { nameAt = j; break; }
                    j++;
                }
                if (nameAt < 0) continue;
                int m2 = nameAt + 1;
                while (m2 < t.Count && !t[m2].Is("{") && !t[m2].Is(";") && !t[m2].Is("(")) m2++;
                if (m2 >= t.Count || !t[m2].Is("{")) continue;
                int close = CppTextScan.MatchClose(t, m2);
                var body = new ClassBody
                {
                    IsStruct = t[k].Is("struct"), OpenToken = m2, CloseToken = close, OpenOffset = t[m2].Offset, CloseOffset = t[close].Offset,
                    OpenLine = t[m2].Line, CloseLine = t[close].Line,
                };
                string ns = string.Concat(nsStack.Where(n => n.Name.Length > 0).Select(n => n.Name + "::"));
                yield return (t[nameAt].Text, body, ns);
                k = close; // nested classes are not entered
            }
        }

        /// <summary>Member statements at class depth: (first token, last token) — the last is ';' or the '}' of a body.</summary>
        private static IEnumerable<(int First, int Last)> MemberStatements(List<ScanToken> t, ClassBody body)
        {
            int k = body.OpenToken + 1;
            while (k < body.CloseToken)
            {
                // access labels
                if ((t[k].Is("public") || t[k].Is("protected") || t[k].Is("private")) && k + 1 < body.CloseToken && t[k + 1].Is(":")) { k += 2; continue; }
                if (t[k].Is(";")) { k++; continue; }
                int first = k, depth = 0, last = -1;
                for (; k < body.CloseToken; k++)
                {
                    var tok = t[k];
                    if (tok.Is("(") || tok.Is("[")) depth++;
                    else if (tok.Is(")") || tok.Is("]")) depth--;
                    else if (depth == 0 && tok.Is(";")) { last = k; k++; break; }
                    else if (depth == 0 && tok.Is("{"))
                    {
                        int close = CppTextScan.MatchClose(t, k);
                        // "= { … };" / "{ … };" initializers and nested types continue to their ';'
                        bool function = CodeScan.Classify(t, k, out _) == BodyKind.Function;
                        k = close;
                        if (function)
                        {
                            last = close;
                            k = close + 1;
                            if (k < body.CloseToken && t[k].Is(";")) k++;
                            break;
                        }
                    }
                }
                if (last < 0) yield break;
                yield return (first, last);
            }
        }

        private static List<InlineMember> Members(string text, List<ScanToken> t, string cls, ClassBody body, string ns, out List<string> problems)
        {
            problems = new List<string>();
            var list = new List<InlineMember>();
            var nestedTypes = NestedTypeNames(t, body);
            foreach (var (first, last) in MemberStatements(t, body))
            {
                if (!t[last].Is("}")) continue;
                var m = Parse(text, t, first, last, cls, nestedTypes, out string why);
                if (m == null) { problems.Add(why); continue; }
                m.NamespacePrefix = ns;
                list.Add(m);
            }
            return list;
        }

        private static IEnumerable<(int Start, int End, string Why)> Rejected(string text, List<ScanToken> t, ClassBody body)
        {
            var nested = NestedTypeNames(t, body);
            foreach (var (first, last) in MemberStatements(t, body))
            {
                if (!t[last].Is("}")) continue;
                if (Parse(text, t, first, last, "X", nested, out string why) == null && why != null) yield return (t[first].Offset, CodeScan.End(t[last]), why);
            }
        }

        private static HashSet<string> NestedTypeNames(List<ScanToken> t, ClassBody body)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            int depth = 0;
            for (int k = body.OpenToken + 1; k < body.CloseToken; k++)
            {
                if (t[k].Is("{")) depth++;
                else if (t[k].Is("}")) depth--;
                if (depth != 0) continue;
                if ((t[k].Is("struct") || t[k].Is("class") || t[k].Is("enum") || t[k].Is("union")) && k + 1 < body.CloseToken)
                {
                    int j = k + 1;
                    if (t[j].Is("class") || t[j].Is("struct")) j++;
                    while (j < body.CloseToken && t[j].IsIdent && j + 1 < body.CloseToken && t[j + 1].IsIdent && !t[j + 1].Is("final")) j++; // API macros
                    if (j < body.CloseToken && t[j].IsIdent && j + 1 < body.CloseToken && (t[j + 1].Is("{") || t[j + 1].Is(":") || t[j + 1].Is("final"))) names.Add(t[j].Text);
                }
                if (t[k].Is("using") && k + 2 < body.CloseToken && t[k + 1].IsIdent && t[k + 2].Is("=")) names.Add(t[k + 1].Text);
                if (t[k].Is("typedef"))
                {
                    int j = k + 1;
                    while (j < body.CloseToken && !t[j].Is(";")) j++;
                    if (j - 1 > k && t[j - 1].IsIdent) names.Add(t[j - 1].Text);
                }
            }
            return names;
        }

        /// <summary>Parses one member statement with a body; null with a reason when it cannot (or must not) be moved.</summary>
        private static InlineMember Parse(string text, List<ScanToken> t, int first, int last, string cls, HashSet<string> nestedTypes, out string why)
        {
            why = null;
            int open = -1;
            for (int k = first; k <= last; k++)
                if (t[k].Is("{") && CodeScan.Classify(t, k, out _) == BodyKind.Function) { open = k; break; }
            if (open < 0 || CppTextScan.MatchClose(t, open) != last) return null;
            // head: skip UE macros with arguments (UFUNCTION(...), UE_DEPRECATED(...)) at the start
            int h = first;
            while (h < open && t[h].IsIdent && IsMacroName(t[h].Text) && h + 1 < open && t[h + 1].Is("("))
                h = CppTextScan.MatchClose(t, h + 1) + 1;
            if (h >= open) return null;
            // the parameter list: the first top-level '(' whose preceding token is the name
            int paramOpen = -1;
            for (int k = h; k < open; k++)
            {
                if (t[k].Is("<") && k > h && t[k - 1].Is("template")) { why = "template"; return null; }
                if (t[k].Is("(") && k > h && (t[k - 1].IsIdent || t[k - 1].Is("~")) && !IsMacroName(t[k - 1].Text)) { paramOpen = k; break; }
                if (t[k].Is("(") && k > h && t[k - 1].IsIdent && IsMacroName(t[k - 1].Text)) { k = CppTextScan.MatchClose(t, k); }
            }
            if (paramOpen < 0) return null;
            int paramClose = CppTextScan.MatchClose(t, paramOpen);
            for (int k = h; k < paramOpen; k++)
            {
                if (t[k].Is("template")) { why = "template"; return null; }
                if (t[k].Is("constexpr") || t[k].Is("consteval")) { why = "constexpr"; return null; }
                if (t[k].Is("friend")) { why = "friend"; return null; }
                if (t[k].Is("operator")) { why = "operator"; return null; }
            }
            int nameTok = paramOpen - 1;
            string name = t[nameTok].Text;
            bool dtor = nameTok > 0 && t[nameTok - 1].Is("~");
            bool ctor = !dtor && name == cls;
            if (dtor) name = "~" + name;
            // after the parameters: qualifiers up to '{' or ':' (initializer list)
            int initColon = -1, sigEndTok = paramClose;
            for (int k = paramClose + 1; k < open; k++)
            {
                if (t[k].Is(":") && initColon < 0) { initColon = k; break; }
                if (t[k].Is("=")) { why = "no-member"; return null; } // = default / = 0 with a body?
                sigEndTok = k;
            }
            var m = new InlineMember
            {
                Name = name, ClassName = cls, HeadStart = t[h].Offset, SignatureEnd = CodeScan.End(t[sigEndTok]),
                InitializerStart = initColon >= 0 ? t[initColon].Offset : -1, BodyOpen = t[open].Offset, BodyClose = t[last].Offset, Line = t[h].Line,
            };
            // definition head text
            var head = new StringBuilder();
            var retTokens = new List<int>();
            for (int k = h; k < (dtor ? nameTok - 1 : nameTok); k++)
            {
                var tok = t[k];
                if (DropKeywords.Contains(tok.Text))
                {
                    if (tok.Is("inline") || tok.Is("FORCEINLINE") || tok.Is("FORCENOINLINE") || tok.Is("FORCEINLINE_DEBUGGABLE")) m.DropFromHeader.Add((tok.Offset, tok.Text.Length));
                    continue;
                }
                if (tok.IsIdent && IsMacroName(tok.Text) && k + 1 < nameTok && t[k + 1].Is("("))
                {
                    k = CppTextScan.MatchClose(t, k + 1);
                    continue;
                }
                retTokens.Add(k);
            }
            if (!ctor && !dtor && retTokens.Count == 0) return null;
            string ret = SpanText(text, t, retTokens);
            if (ret.Length > 0)
            {
                // nested type of the class in the return type: qualify it
                int firstIdent = -1;
                foreach (int k in retTokens)
                    if (t[k].IsIdent && !t[k].Is("const") && !t[k].Is("typename") && !t[k].Is("volatile")) { firstIdent = k; break; }
                if (firstIdent >= 0 && nestedTypes.Contains(t[firstIdent].Text) && (firstIdent == 0 || !t[firstIdent - 1].Is("::")))
                {
                    int rel = t[firstIdent].Offset;
                    var sb = new StringBuilder();
                    foreach (int k in retTokens)
                    {
                        if (sb.Length > 0 && NeedsSpace(text, t, k)) sb.Append(' ');
                        if (k == firstIdent) sb.Append(cls).Append("::");
                        sb.Append(t[k].Text);
                    }
                    ret = sb.ToString();
                }
                head.Append(ret).Append(' ');
            }
            head.Append(cls).Append("::").Append(name);
            string pars = text.Substring(CodeScan.End(t[paramOpen]), t[paramClose].Offset - CodeScan.End(t[paramOpen]));
            m.Parameters = pars;
            head.Append('(').Append(StripDefaultsKeepLayout(pars)).Append(')');
            for (int k = paramClose + 1; k <= sigEndTok; k++)
            {
                if (TrailingDrop.Contains(t[k].Text)) continue;
                if (t[k].Is("PURE_VIRTUAL")) { why = "no-member"; return null; }
                head.Append(t[k].Is("&") && t[k - 1].Is("&") ? string.Empty : " ").Append(t[k].Text);
            }
            m.DefinitionHead = head.ToString();
            return m;
        }

        private static bool NeedsSpace(string text, List<ScanToken> t, int k) => k > 0 && t[k].Offset > CodeScan.End(t[k - 1]);

        private static string SpanText(string text, List<ScanToken> t, List<int> toks)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < toks.Count; i++)
            {
                if (i > 0 && (toks[i] != toks[i - 1] + 1 || NeedsSpace(text, t, toks[i]))) sb.Append(' ');
                sb.Append(t[toks[i]].Text);
            }
            return sb.ToString();
        }

        /// <summary>UE / API style macro names: all caps (UFUNCTION, UE_DEPRECATED, MYGAME_API).</summary>
        public static bool IsMacroName(string s) =>
            s != null && s.Length > 2 && s.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c)) && s.Any(char.IsLetter) && s != "TEXT";

        /// <summary>Default arguments removed; a single-line list is normalised, a multi-line one keeps one parameter per line.</summary>
        public static string StripDefaultsKeepLayout(string parameters)
        {
            if (parameters.IndexOf('\n') < 0) return SignatureText.StripDefaults(parameters);
            var parts = SignatureText.SplitParameters(parameters).Select(p =>
            {
                int eq = SignatureText.IndexOfTopLevel(p, '=');
                return ChangeSignaturePlanner.NormalizeSpaces(eq >= 0 ? p.Substring(0, eq) : p);
            });
            return string.Join(", ", parts);
        }

        /// <summary>A body block re-indented for a new place: lines start with <paramref name="indent"/>; a one-line body is opened up.</summary>
        public static List<string> BodyLines(string text, int open, int close, string indent)
        {
            string inner = text.Substring(open + 1, close - open - 1);
            var lines = inner.Replace("\r\n", "\n").Split('\n').ToList();
            var result = new List<string> { indent + "{" };
            if (lines.Count == 1)
            {
                string one = lines[0].Trim();
                if (one.Length > 0) result.Add(indent + "\t" + one);
            }
            else
            {
                // first line (after '{') and last line (before '}') may hold code too
                string head = lines[0].Trim();
                string tail = lines[lines.Count - 1].Trim();
                var middle = lines.Skip(1).Take(lines.Count - 2).ToList();
                if (head.Length > 0) middle.Insert(0, "\t" + head);
                if (tail.Length > 0) middle.Add("\t" + tail);
                while (middle.Count > 0 && middle[0].Trim().Length == 0) middle.RemoveAt(0);
                while (middle.Count > 0 && middle[middle.Count - 1].Trim().Length == 0) middle.RemoveAt(middle.Count - 1);
                result.AddRange(CodeScan.Reindent(middle, indent + "\t"));
            }
            result.Add(indent + "}");
            return result;
        }

        /// <summary>Header → source for <paramref name="members"/> (one class): header bodies become ';', the definitions go into the .cpp.</summary>
        public static MovePlan ToSource(string headerPath, string headerText, string cppPath, string cppText, IReadOnlyList<InlineMember> members)
        {
            if (members == null || members.Count == 0) return MovePlan.Fail("no-member");
            string nl = CppTextScan.NewlineOf(cppText);
            string cls = members[0].ClassName;
            var existing = Definitions(cppText, CppTextScan.Tokenize(cppText)).Where(d => d.ClassName == cls).ToList();
            foreach (var m in members)
                if (existing.Any(d => d.Name == m.Name && ParameterKey(d.Parameters) == ParameterKey(m.Parameters))) return MovePlan.Fail("exists");
            var plan = new MovePlan();
            var blocks = new List<string>();
            var headerEdits = new List<StructuredEdit>();
            foreach (var m in members)
            {
                var block = new StringBuilder();
                string head = m.NamespacePrefix + m.DefinitionHead;
                block.Append(head).Append(nl);
                if (m.InitializerStart >= 0)
                {
                    string init = headerText.Substring(m.InitializerStart, m.BodyOpen - m.InitializerStart).Trim();
                    block.Append('\t').Append(ChangeSignaturePlanner.NormalizeSpaces(init.Replace("\r", " ").Replace("\n", " "))).Append(nl);
                }
                foreach (var l in BodyLines(headerText, m.BodyOpen, m.BodyClose, string.Empty)) block.Append(l).Append(nl);
                blocks.Add(block.ToString());
                plan.Moved.Add(m.Name);
                // header: "Sig {…}" → "Sig;" (initializer list removed); inline / FORCEINLINE dropped
                int removeFrom = m.SignatureEnd;
                headerEdits.Add(StructuredEdit.Replace(removeFrom, m.BodyClose + 1, ";"));
                foreach (var (off, len) in m.DropFromHeader)
                {
                    int end = off + len;
                    while (end < headerText.Length && (headerText[end] == ' ' || headerText[end] == '\t')) end++;
                    headerEdits.Add(StructuredEdit.Replace(off, end, string.Empty));
                }
            }
            plan.HeaderEdits.AddRange(StructuredEditComposer.Compose(headerPath, headerText, headerEdits, out var dropped));
            if (dropped.Count > 0) return MovePlan.Fail("overlap");
            var before = CodePlacement.DeclaredBefore(headerText, cls, -1, LineOfOffset(headerText, members[0].HeadStart));
            string all = string.Join(nl, blocks);
            var point = CodePlacement.Implementation(cppText, cls, before, all);
            plan.SourceEdits.Add(TextEdit.Insert(cppPath, point.Line, point.Text));
            plan.Reason = point.Reason;
            plan.TargetLine = point.Line + LeadingNewlines(point.Text);
            return plan;
        }

        private static int LeadingNewlines(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length && (s[i] == '\r' || s[i] == '\n'); i++) if (s[i] == '\n') n++;
            return n;
        }

        internal static int LineOfOffset(string text, int offset)
        {
            int line = 0;
            for (int i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }

        // ---------------------------------------------------------------- source side

        /// <summary>The out-of-line member definition <c>[Ret] A::B::Name(…) [const] [: init] { … }</c> around <paramref name="offset"/>.</summary>
        public static OutOfLineMember DefinitionAt(string cppText, int offset)
        {
            var t = CppTextScan.Tokenize(cppText);
            foreach (var d in Definitions(cppText, t))
            {
                if (offset >= CodeScan.LineStart(cppText, d.Start) && offset <= d.BodyClose + 1) return d;
            }
            return null;
        }

        /// <summary>Out-of-line member definitions at namespace level (namespace blocks entered).</summary>
        public static List<OutOfLineMember> Definitions(string text, List<ScanToken> t)
        {
            var list = new List<OutOfLineMember>();
            Walk(text, t, 0, t.Count, list);
            return list;
        }

        private static void Walk(string text, List<ScanToken> t, int from, int to, List<OutOfLineMember> list)
        {
            int statement = from;
            for (int k = from; k < to; k++)
            {
                var tok = t[k];
                if (tok.Is("namespace"))
                {
                    int m = k + 1;
                    while (m < to && !t[m].Is("{") && !t[m].Is(";")) m++;
                    if (m < to && t[m].Is("{"))
                    {
                        int close = CppTextScan.MatchClose(t, m);
                        Walk(text, t, m + 1, close, list);
                        k = close;
                        statement = k + 1;
                        continue;
                    }
                }
                if (tok.Is(";")) { statement = k + 1; continue; }
                if (tok.Is("("))
                {
                    k = CppTextScan.MatchClose(t, k);
                    continue;
                }
                if (!tok.Is("{")) continue;
                int closeBrace = CppTextScan.MatchClose(t, k);
                if (CodeScan.Classify(t, k, out _) == BodyKind.Function)
                {
                    var d = Head(text, t, statement, k, closeBrace);
                    if (d != null) list.Add(d);
                }
                k = closeBrace;
                if (k + 1 < to && t[k + 1].Is(";")) k++;
                statement = k + 1;
            }
        }

        private static OutOfLineMember Head(string text, List<ScanToken> t, int from, int open, int close)
        {
            int paramOpen = -1;
            for (int k = from; k < open; k++)
            {
                if (t[k].Is("template")) return null;
                if (t[k].Is("(") && k >= from + 2 && (t[k - 1].IsIdent) && (t[k - 2].Is("::") || (t[k - 2].Is("~") && k >= from + 3 && t[k - 3].Is("::"))))
                {
                    paramOpen = k;
                    break;
                }
                if (t[k].Is("(")) k = CppTextScan.MatchClose(t, k);
            }
            if (paramOpen < 0) return null;
            int nameTok = paramOpen - 1;
            int sep = t[nameTok - 1].Is("~") ? nameTok - 2 : nameTok - 1;
            if (sep < 1 || !t[sep].Is("::") || !t[sep - 1].IsIdent) return null;
            int paramClose = CppTextScan.MatchClose(t, paramOpen);
            var d = new OutOfLineMember
            {
                ClassName = t[sep - 1].Text, Name = (t[nameTok - 1].Is("~") ? "~" : string.Empty) + t[nameTok].Text, Start = t[from].Offset, ParamOpen = t[paramOpen].Offset,
                ParamClose = t[paramClose].Offset, BodyOpen = t[open].Offset, BodyClose = t[close].Offset, Line = t[from].Line,
                Parameters = text.Substring(CodeScan.End(t[paramOpen]), t[paramClose].Offset - CodeScan.End(t[paramOpen])),
            };
            for (int k = paramClose + 1; k < open; k++)
            {
                if (t[k].Is(":")) { d.InitializerStart = t[k].Offset; break; }
                if (t[k].Is("const")) d.IsConst = true;
            }
            return d;
        }

        /// <summary>
        /// Source → header: the definition's body goes onto the matching bodiless declaration in the class (same name, same
        /// parameter types; const must agree), the definition is removed from the .cpp.
        /// </summary>
        public static MovePlan ToHeader(string headerPath, string headerText, string cppPath, string cppText, OutOfLineMember def)
        {
            if (def == null) return MovePlan.Fail("no-member");
            if (def.Name.EndsWith("_Implementation", StringComparison.Ordinal) || def.Name.EndsWith("_Validate", StringComparison.Ordinal)) return MovePlan.Fail("no-declaration");
            var t = CppTextScan.Tokenize(headerText);
            var cls = Classes(t).Where(c => c.Name == def.ClassName).ToList();
            if (cls.Count != 1) return MovePlan.Fail(cls.Count == 0 ? "not-found" : "ambiguous");
            var body = cls[0].Body;
            string wantKey = ParameterKey(def.Parameters);
            var candidates = new List<(int Semi, int ParamClose)>();
            foreach (var (first, last) in MemberStatements(t, body))
            {
                if (!t[last].Is(";")) continue;
                for (int k = first; k < last; k++)
                {
                    if (!t[k].Is("(") || k == first) continue;
                    if (t[k - 1].IsIdent && IsMacroName(t[k - 1].Text)) { k = CppTextScan.MatchClose(t, k); continue; }
                    string n = (k >= 2 && t[k - 2].Is("~") ? "~" : string.Empty) + t[k - 1].Text;
                    if (n != def.Name) break;
                    int pc = CppTextScan.MatchClose(t, k);
                    string pars = headerText.Substring(CodeScan.End(t[k]), t[pc].Offset - CodeScan.End(t[k]));
                    bool isConst = false, bad = false;
                    for (int q = pc + 1; q < last; q++)
                    {
                        if (t[q].Is("const")) isConst = true;
                        if (t[q].Is("=") || t[q].Is("PURE_VIRTUAL")) bad = true; // = 0 / = default / = delete
                    }
                    if (!bad && isConst == def.IsConst && ParameterKey(pars) == wantKey) candidates.Add((last, pc));
                    break;
                }
            }
            if (candidates.Count == 0) return MovePlan.Fail("no-declaration");
            if (candidates.Count > 1) return MovePlan.Fail("ambiguous");
            var (semi, _) = candidates[0];
            string nl = CppTextScan.NewlineOf(headerText);
            string indent = CodeScan.LineIndent(headerText, t[semi].Offset);
            var sb = new StringBuilder();
            if (def.InitializerStart >= 0)
            {
                string init = cppText.Substring(def.InitializerStart, def.BodyOpen - def.InitializerStart).Trim();
                sb.Append(nl).Append(indent).Append('\t').Append(ChangeSignaturePlanner.NormalizeSpaces(init.Replace("\r", " ").Replace("\n", " ")));
            }
            foreach (var l in BodyLines(cppText, def.BodyOpen, def.BodyClose, indent)) sb.Append(nl).Append(l);
            var plan = new MovePlan();
            var hs = TextEditApplier.LineStarts(headerText);
            int semiLine = StructuredEditComposer.LineOf(hs, t[semi].Offset);
            plan.HeaderEdits.Add(new TextEdit
            {
                FilePath = headerPath, Line = semiLine, Column = t[semi].Offset - hs[semiLine], Length = 1, OldText = ";", NewText = sb.ToString(),
            });
            plan.TargetLine = semiLine + 1;
            // source: remove whole lines of the definition (+ one blank line around it)
            int start = CodeScan.LineStart(cppText, def.Start);
            int end = CodeScan.LineEnd(cppText, def.BodyClose);
            if (!CodeScan.IsBlank(cppText, start, def.Start) || !CodeScan.IsBlankOrComment(cppText, def.BodyClose + 1, end)) return MovePlan.Fail("shared-line");
            end = end < cppText.Length ? cppText.IndexOf('\n', end) + 1 : end;
            if (end <= 0) end = cppText.Length;
            // swallow one following blank line, else one preceding one
            int nextEnd = cppText.IndexOf('\n', end);
            if (end < cppText.Length && nextEnd >= 0 && CodeScan.IsBlank(cppText, end, nextEnd)) end = nextEnd + 1;
            else if (start >= 2)
            {
                int prevStart = CodeScan.LineStart(cppText, start - 1);
                if (CodeScan.IsBlank(cppText, prevStart, start)) start = prevStart;
            }
            var cs = TextEditApplier.LineStarts(cppText);
            int sl = StructuredEditComposer.LineOf(cs, start);
            plan.SourceEdits.Add(new TextEdit
            {
                FilePath = cppPath, Line = sl, Column = start - cs[sl], Length = end - start, OldText = cppText.Substring(start, end - start), NewText = string.Empty,
            });
            plan.Moved.Add(def.Name);
            plan.Reason = "declaration:" + semiLine;
            return plan;
        }

        /// <summary>Parameter types only (names and defaults removed, white space normalised) to match a definition to its declaration.</summary>
        public static string ParameterKey(string parameters)
        {
            var parts = new List<string>();
            foreach (var p in SignatureText.SplitParameters(parameters))
            {
                if (!ChangeSignaturePlanner.TrySplit(p, out string type, out _, out _)) parts.Add(ChangeSignaturePlanner.NormalizeSpaces(p));
                else parts.Add(type.Replace(" ", string.Empty));
            }
            return string.Join(",", parts);
        }
    }
}
