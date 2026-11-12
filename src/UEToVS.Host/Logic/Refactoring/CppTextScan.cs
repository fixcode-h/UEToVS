using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>A token of <see cref="CppTextScan"/>: identifier, number or single punctuation character ("::" is one token).</summary>
    public readonly struct ScanToken
    {
        public ScanToken(string text, int offset, int line, bool isIdent)
        {
            Text = text;
            Offset = offset;
            Line = line;
            IsIdent = isIdent;
        }

        public string Text { get; }
        public int Offset { get; }
        public int Line { get; }
        public bool IsIdent { get; }
        public bool Is(string s) => string.Equals(Text, s, StringComparison.Ordinal);
        public override string ToString() => $"{Text}@{Line}";
    }

    public enum AccessKind
    {
        Public,
        Protected,
        Private,
    }

    /// <summary>A class body in a text: offsets of '{' and '}' and their lines.</summary>
    public sealed class ClassBody
    {
        public bool IsStruct { get; set; }
        public int OpenOffset { get; set; }
        public int CloseOffset { get; set; }
        public int OpenLine { get; set; }
        public int CloseLine { get; set; }
        public int OpenToken { get; set; }
        public int CloseToken { get; set; }
    }

    public sealed class AccessSection
    {
        public AccessKind Access { get; set; }

        /// <summary>Line of the label (the '{' line for the implicit first section).</summary>
        public int LabelLine { get; set; }

        public bool Implicit { get; set; }

        /// <summary>Line of the last token of the last member in the section, or -1 when empty.</summary>
        public int LastMemberLine { get; set; } = -1;

        /// <summary>Token range [first, end) of the section's contents.</summary>
        public int FirstToken { get; set; }
        public int EndToken { get; set; }
    }

    /// <summary>An out-of-line member function definition found in a source file.</summary>
    public sealed class MemberDefinition
    {
        public string ClassName { get; set; }
        public string Name { get; set; }
        public int StartLine { get; set; }

        /// <summary>Line of the closing brace of the body.</summary>
        public int EndLine { get; set; }

        public override string ToString() => $"{ClassName}::{Name} {StartLine}-{EndLine}";
    }

    /// <summary>
    /// Lightweight, error-tolerant text scanner for the host's edit planning (where to put a declaration / implementation /
    /// include). Works on the current buffer text (which may be unsaved), skips comments, strings and preprocessor lines.
    /// It does not need to understand C++: only braces, class heads, access labels and <c>A::B(</c> heads.
    /// </summary>
    public static class CppTextScan
    {
        public static List<ScanToken> Tokenize(string text)
        {
            var list = new List<ScanToken>();
            if (string.IsNullOrEmpty(text)) return list;
            int i = 0, line = 0, n = text.Length;
            bool lineStart = true;
            while (i < n)
            {
                char c = text[i];
                if (c == '\n') { line++; i++; lineStart = true; continue; }
                if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v') { i++; continue; }
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    while (i < n && text[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    i += 2;
                    while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/')) { if (text[i] == '\n') line++; i++; }
                    i = Math.Min(n, i + 2);
                    continue;
                }
                if (c == '#' && lineStart)
                {
                    // preprocessor line (with continuations)
                    while (i < n && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < n && (text[i + 1] == '\n' || (text[i + 1] == '\r' && i + 2 < n && text[i + 2] == '\n')))
                        {
                            i += text[i + 1] == '\r' ? 3 : 2;
                            line++;
                            continue;
                        }
                        if (text[i] == '/' && i + 1 < n && text[i + 1] == '*') break;
                        i++;
                    }
                    continue;
                }
                lineStart = false;
                if (c == '"' || c == '\'')
                {
                    // raw strings R"x(...)x"
                    if (c == '"' && i > 0 && text[i - 1] == 'R')
                    {
                        int open = text.IndexOf('(', i);
                        if (open > 0)
                        {
                            string delim = ")" + text.Substring(i + 1, open - i - 1) + "\"";
                            int close = text.IndexOf(delim, open, StringComparison.Ordinal);
                            int end = close < 0 ? n : close + delim.Length;
                            for (int k = i; k < end; k++) if (text[k] == '\n') line++;
                            i = end;
                            continue;
                        }
                    }
                    i++;
                    while (i < n && text[i] != c && text[i] != '\n') { if (text[i] == '\\') i++; i++; }
                    i++;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    if (i < n && (text[i] == '"' || text[i] == '\'') && IsStringPrefix(text.Substring(s, i - s))) continue; // u8"..", L'x', R"(..)"
                    list.Add(new ScanToken(text.Substring(s, i - s), s, line, true));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int s = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '\'')) i++;
                    list.Add(new ScanToken(text.Substring(s, i - s), s, line, false));
                    continue;
                }
                if (c == ':' && i + 1 < n && text[i + 1] == ':')
                {
                    list.Add(new ScanToken("::", i, line, false));
                    i += 2;
                    continue;
                }
                list.Add(new ScanToken(c.ToString(), i, line, false));
                i++;
            }
            return list;
        }

        private static bool IsStringPrefix(string s) => s == "L" || s == "u" || s == "U" || s == "u8" || s == "R" || s == "LR" || s == "uR" || s == "UR" || s == "u8R";

        /// <summary>Index of the token closing the bracket opened at <paramref name="open"/> (or the last token).</summary>
        public static int MatchClose(List<ScanToken> t, int open)
        {
            string o = t[open].Text, c = o == "{" ? "}" : o == "(" ? ")" : o == "[" ? "]" : null;
            if (c == null) return open;
            int depth = 0;
            for (int k = open; k < t.Count; k++)
            {
                if (t[k].Is(o)) depth++;
                else if (t[k].Is(c) && --depth == 0) return k;
            }
            return t.Count - 1;
        }

        /// <summary>
        /// The body of <c>class|struct [API] Name [final] [: bases] { … }</c>; when there are several (forward declarations,
        /// same name in two namespaces), the one whose head is nearest <paramref name="hintLine"/>.
        /// </summary>
        public static ClassBody FindClassBody(string text, string className, int hintLine = -1) => FindClassBody(Tokenize(text), className, hintLine);

        public static ClassBody FindClassBody(List<ScanToken> t, string className, int hintLine = -1)
        {
            ClassBody best = null;
            int bestDistance = int.MaxValue;
            for (int k = 0; k < t.Count; k++)
            {
                if (!(t[k].Is("class") || t[k].Is("struct")) || k + 1 >= t.Count) continue;
                if (k > 0 && t[k - 1].Is("enum")) continue;
                // head: identifiers (API macro, the name) up to the name
                int j = k + 1, nameAt = -1;
                while (j < t.Count && j < k + 6 && t[j].IsIdent)
                {
                    if (t[j].Is(className)) { nameAt = j; break; }
                    j++;
                }
                if (nameAt < 0) continue;
                // to '{' (body) or ';' (forward declaration) / '(' (function returning class X)
                int m = nameAt + 1, angle = 0;
                for (; m < t.Count; m++)
                {
                    if (t[m].Is("<")) angle++;
                    else if (t[m].Is(">")) angle--;
                    else if (angle <= 0 && (t[m].Is("{") || t[m].Is(";") || t[m].Is("(") || t[m].Is(")") || t[m].Is("}"))) break;
                }
                if (m >= t.Count || !t[m].Is("{")) continue;
                int close = MatchClose(t, m);
                var body = new ClassBody
                {
                    IsStruct = t[k].Is("struct"), OpenToken = m, CloseToken = close, OpenOffset = t[m].Offset, CloseOffset = t[close].Offset,
                    OpenLine = t[m].Line, CloseLine = t[close].Line,
                };
                int distance = hintLine < 0 ? 0 : Math.Abs(t[nameAt].Line - hintLine);
                if (distance < bestDistance)
                {
                    best = body;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>Access sections of a class body, in order (the implicit first one included).</summary>
        public static List<AccessSection> Sections(List<ScanToken> t, ClassBody body)
        {
            var list = new List<AccessSection>();
            var cur = new AccessSection
            {
                Access = body.IsStruct ? AccessKind.Public : AccessKind.Private, LabelLine = body.OpenLine, Implicit = true, FirstToken = body.OpenToken + 1,
            };
            int depth = 0;
            for (int k = body.OpenToken + 1; k < body.CloseToken; k++)
            {
                var tok = t[k];
                if (tok.Is("{") || tok.Is("(") || tok.Is("[")) depth++;
                else if (tok.Is("}") || tok.Is(")") || tok.Is("]")) depth--;
                if (depth == 0 && tok.IsIdent && (tok.Is("public") || tok.Is("protected") || tok.Is("private")) && k + 1 < body.CloseToken && t[k + 1].Is(":"))
                {
                    cur.EndToken = k;
                    list.Add(cur);
                    cur = new AccessSection
                    {
                        Access = tok.Is("public") ? AccessKind.Public : tok.Is("protected") ? AccessKind.Protected : AccessKind.Private,
                        LabelLine = tok.Line, FirstToken = k + 2,
                    };
                    k++;
                    continue;
                }
                if (depth == 0 && (tok.Is(";") || tok.Is("}"))) cur.LastMemberLine = tok.Line;
                else if (depth < 0) depth = 0;
                if (depth == 0 && tok.Is(")") && cur.LastMemberLine < tok.Line) { /* GENERATED_BODY() etc.: a member */ cur.LastMemberLine = tok.Line; }
            }
            cur.EndToken = body.CloseToken;
            list.Add(cur);
            // the implicit first section is only interesting when it has members
            return list.Where(s => !s.Implicit || s.LastMemberLine >= 0 || list.Count == 1).ToList();
        }

        /// <summary>Line of the ';' / closing '}' ending the first member declaration named <paramref name="name"/> in the body; -1 when absent.</summary>
        public static int MemberDeclarationEndLine(List<ScanToken> t, ClassBody body, string name)
        {
            int depth = 0;
            for (int k = body.OpenToken + 1; k < body.CloseToken; k++)
            {
                var tok = t[k];
                if (tok.Is("{") || tok.Is("(") || tok.Is("[")) depth++;
                else if (tok.Is("}") || tok.Is(")") || tok.Is("]")) depth--;
                if (depth != 0 || !tok.Is(name) || k + 1 >= body.CloseToken || !t[k + 1].Is("(")) continue;
                int d = 0;
                for (int m = k + 1; m < body.CloseToken; m++)
                {
                    if (t[m].Is("(") || t[m].Is("[")) d++;
                    else if (t[m].Is(")") || t[m].Is("]")) d--;
                    else if (d == 0 && t[m].Is(";")) return t[m].Line;
                    else if (d == 0 && t[m].Is("{")) return t[MatchClose(t, m)].Line;
                }
            }
            return -1;
        }

        /// <summary>Names of member functions declared in the body before <paramref name="beforeLine"/> (source order).</summary>
        public static List<string> MemberFunctionNamesBefore(List<ScanToken> t, ClassBody body, int beforeLine)
        {
            var names = new List<string>();
            int depth = 0;
            for (int k = body.OpenToken + 1; k < body.CloseToken && t[k].Line < beforeLine; k++)
            {
                var tok = t[k];
                if (tok.Is("{") || tok.Is("(") || tok.Is("[")) { if (depth == 0 && tok.Is("(") && k > 0 && t[k - 1].IsIdent && !IsMacroLike(t[k - 1].Text)) names.Add(t[k - 1].Text); depth++; }
                else if (tok.Is("}") || tok.Is(")") || tok.Is("]")) depth--;
            }
            return names;
        }

        private static bool IsMacroLike(string s) =>
            s == "UFUNCTION" || s == "UPROPERTY" || s == "GENERATED_BODY" || s == "GENERATED_UCLASS_BODY" || s == "GENERATED_USTRUCT_BODY" ||
            s == "UE_DEPRECATED" || s == "DECLARE_DYNAMIC_MULTICAST_DELEGATE" || (s.Length > 2 && s.All(ch => char.IsUpper(ch) || ch == '_' || char.IsDigit(ch)));

        /// <summary>
        /// Out-of-line definitions <c>[ret] Class::Name(...) [qualifiers] { … }</c> in a source file (namespace blocks are
        /// entered; bodies are skipped), in source order. Constructors and destructors included (Name = Class / ~Class).
        /// </summary>
        public static List<MemberDefinition> MemberDefinitions(string text, string className = null)
        {
            var t = Tokenize(text);
            var list = new List<MemberDefinition>();
            int k = 0;
            Scan(t, ref k, t.Count, className, list);
            return list;
        }

        private static void Scan(List<ScanToken> t, ref int k, int end, string className, List<MemberDefinition> list)
        {
            int statementStart = k;
            while (k < end)
            {
                var tok = t[k];
                if (tok.Is("namespace"))
                {
                    int m = k + 1;
                    while (m < end && !t[m].Is("{") && !t[m].Is(";")) m++;
                    if (m < end && t[m].Is("{"))
                    {
                        int close = MatchClose(t, m);
                        int inner = m + 1;
                        Scan(t, ref inner, close, className, list);
                        k = close + 1;
                        statementStart = k;
                        continue;
                    }
                }
                if (tok.Is(";")) { k++; statementStart = k; continue; }
                if (tok.Is("{"))
                {
                    int close = MatchClose(t, k);
                    // a function body? look back for ')' [qualifiers] and a qualified head "X :: Name ("
                    var def = HeadBefore(t, statementStart, k);
                    if (def != null && (className == null || def.ClassName == className))
                    {
                        def.EndLine = t[close].Line;
                        list.Add(def);
                    }
                    k = close + 1;
                    if (k < end && t[k].Is(";")) k++;
                    statementStart = k;
                    continue;
                }
                k++;
            }
        }

        private static MemberDefinition HeadBefore(List<ScanToken> t, int from, int brace)
        {
            // find the parameter list: the last top-level '(' ... ')' before the brace (skip a ctor initializer list)
            int depth = 0;
            int paramOpen = -1;
            for (int k = from; k < brace; k++)
            {
                if (t[k].Is("(")) { if (depth == 0 && paramOpen < 0) paramOpen = k; depth++; }
                else if (t[k].Is(")")) depth--;
                else if (depth == 0 && t[k].Is(":") && paramOpen >= 0) break; // initializer list
            }
            if (paramOpen < 2) return null;
            int nameAt = paramOpen - 1;
            if (!t[nameAt].IsIdent) return null;
            string name = t[nameAt].Text;
            int sep = nameAt - 1;
            if (sep >= 0 && t[sep].Is("~")) { name = "~" + name; sep--; }
            if (sep < 1 || !t[sep].Is("::") || !t[sep - 1].IsIdent) return null;
            string cls = t[sep - 1].Text;
            int start = sep - 1;
            while (start - 2 >= from && t[start - 1].Is("::") && t[start - 2].IsIdent) start -= 2;
            return new MemberDefinition { ClassName = cls, Name = name, StartLine = t[from].Line };
        }

        // ---------------------------------------------------------------- lines

        /// <summary>Text lines (line breaks removed); keeps a trailing empty line.</summary>
        public static string[] Lines(string text) => (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        /// <summary>Line break used by a text ("\r\n" when it has any).</summary>
        public static string NewlineOf(string text) => text != null && text.Contains("\r\n") ? "\r\n" : "\n";

        /// <summary>Leading whitespace of the first non-empty line in [from, to), or a tab.</summary>
        public static string IndentOf(string[] lines, int from, int to, string fallback = "\t")
        {
            for (int i = Math.Max(0, from); i < Math.Min(lines.Length, to); i++)
            {
                string l = lines[i];
                if (l.Trim().Length == 0) continue;
                string trimmed = l.TrimStart();
                if (trimmed.StartsWith("public:", StringComparison.Ordinal) || trimmed.StartsWith("protected:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("private:", StringComparison.Ordinal)) continue;
                return l.Substring(0, l.Length - trimmed.Length);
            }
            return fallback;
        }
    }
}
