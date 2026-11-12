using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>
    /// Where generated code goes (pure; works on the current buffer text):
    /// <list type="bullet">
    /// <item>implementations in the .cpp: after the definition of the member declared just before it in the class (keeps the
    /// header order), else after the class's last member definition, else at the end of the file;</item>
    /// <item>declarations in the class body: after the declaration of the member defined just before it in the .cpp, else at
    /// the end of the last section with the wanted access, else a new "public:" section before the closing brace.</item>
    /// </list>
    /// </summary>
    public static class CodePlacement
    {
        /// <summary>
        /// Insert point for one or more definitions of <paramref name="className"/> members in a source file.
        /// <paramref name="declaredBefore"/>: names of the members declared before the new one in the header (source order).
        /// <paramref name="block"/> is the generated text (complete lines); a blank separator line is added.
        /// </summary>
        public static InsertPoint Implementation(string cppText, string className, IReadOnlyList<string> declaredBefore, string block)
        {
            string nl = CppTextScan.NewlineOf(cppText);
            var lines = CppTextScan.Lines(cppText);
            var defs = CppTextScan.MemberDefinitions(cppText, className);
            MemberDefinition after = null;
            string reason = null;
            if (declaredBefore != null)
                for (int i = declaredBefore.Count - 1; i >= 0 && after == null; i--)
                {
                    after = defs.LastOrDefault(d => d.Name == declaredBefore[i] || d.Name == declaredBefore[i] + "_Implementation" || d.Name == declaredBefore[i] + "_Validate");
                    if (after != null) reason = "after:" + after.Name;
                }
            if (after == null && defs.Count > 0)
            {
                after = defs[defs.Count - 1];
                reason = "class-last";
            }
            block = Normalize(block, nl);
            if (after != null)
                return new InsertPoint { Line = after.EndLine + 1, Text = nl + block, Reason = reason };
            // end of file (one blank line before)
            int end = lines.Length;
            bool lastBlank = end > 0 && lines[end - 1].Trim().Length == 0;
            if (lastBlank && end >= 2 && lines[end - 2].Trim().Length == 0) end--; // keep at most one blank line
            string prefix = lastBlank ? string.Empty : nl;
            if (lastBlank) return new InsertPoint { Line = end - 1 < 0 ? 0 : end - 1, Text = nl + block, Reason = "end-of-file" };
            return new InsertPoint { Line = end, Text = prefix + block, Reason = "end-of-file" };
        }

        /// <summary>
        /// Insert point for member declarations in a class body. <paramref name="definedBefore"/>: names of the class's members
        /// defined before the new one in the .cpp (source order). <paramref name="declarationLines"/> are indented by this
        /// method (pass them without indentation, one per element, multi-line elements allowed).
        /// Null when the class body is not found.
        /// </summary>
        public static InsertPoint Declaration(string headerText, string className, int hintLine, IReadOnlyList<string> definedBefore,
                                              AccessKind access, IReadOnlyList<string> declarationLines)
        {
            string nl = CppTextScan.NewlineOf(headerText);
            var t = CppTextScan.Tokenize(headerText);
            var body = CppTextScan.FindClassBody(t, className, hintLine);
            if (body == null) return null;
            var lines = CppTextScan.Lines(headerText);
            string indent = CppTextScan.IndentOf(lines, body.OpenLine + 1, body.CloseLine);
            string Block() => string.Concat(declarationLines.Select(d => Indent(d, indent, nl)));

            if (definedBefore != null)
                for (int i = definedBefore.Count - 1; i >= 0; i--)
                {
                    string n = definedBefore[i];
                    if (n.EndsWith("_Implementation", StringComparison.Ordinal)) n = n.Substring(0, n.Length - "_Implementation".Length);
                    else if (n.EndsWith("_Validate", StringComparison.Ordinal)) n = n.Substring(0, n.Length - "_Validate".Length);
                    int end = CppTextScan.MemberDeclarationEndLine(t, body, n);
                    if (end >= 0) return new InsertPoint { Line = end + 1, Text = Block(), Reason = "after:" + n };
                }

            var sections = CppTextScan.Sections(t, body);
            var section = sections.LastOrDefault(s => s.Access == access);
            if (section != null)
            {
                int line = section.LastMemberLine >= 0 ? section.LastMemberLine + 1 : section.LabelLine + 1;
                if (section.Implicit && section.LastMemberLine < 0) line = body.OpenLine + 1;
                return new InsertPoint { Line = line, Text = Block(), Reason = "section:" + access.ToString().ToLowerInvariant() };
            }
            // new section before the closing brace (label at the class's own indentation)
            string closeIndent = lines[body.CloseLine].Substring(0, lines[body.CloseLine].Length - lines[body.CloseLine].TrimStart().Length);
            string label = closeIndent + access.ToString().ToLowerInvariant() + ":" + nl;
            bool closeAlone = lines[body.CloseLine].TrimStart().StartsWith("}", StringComparison.Ordinal);
            return new InsertPoint { Line = closeAlone ? body.CloseLine : body.CloseLine + 1, Text = label + Block(), Reason = "new-section" };
        }

        /// <summary>Names of the functions defined in the .cpp for <paramref name="className"/> before <paramref name="line"/>.</summary>
        public static List<string> DefinedBefore(string cppText, string className, int line) =>
            CppTextScan.MemberDefinitions(cppText, className).Where(d => d.StartLine < line).Select(d => d.Name).ToList();

        /// <summary>Names of the member functions declared in the class body before <paramref name="line"/>.</summary>
        public static List<string> DeclaredBefore(string headerText, string className, int hintLine, int line)
        {
            var t = CppTextScan.Tokenize(headerText);
            var body = CppTextScan.FindClassBody(t, className, hintLine);
            return body == null ? new List<string>() : CppTextScan.MemberFunctionNamesBefore(t, body, line);
        }

        /// <summary>Whether the .cpp already defines <c>className::name</c>.</summary>
        public static bool HasDefinition(string cppText, string className, string name) =>
            CppTextScan.MemberDefinitions(cppText, className).Any(d => d.Name == name);

        private static string Indent(string block, string indent, string nl)
        {
            var sb = new StringBuilder();
            foreach (var l in block.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
                sb.Append(l.Length == 0 ? string.Empty : indent + l.TrimStart('\t')).Append(nl);
            return sb.ToString();
        }

        private static string Normalize(string block, string nl)
        {
            string b = block.Replace("\r\n", "\n");
            if (!b.EndsWith("\n", StringComparison.Ordinal)) b += "\n";
            return nl == "\n" ? b : b.Replace("\n", nl);
        }
    }
}
