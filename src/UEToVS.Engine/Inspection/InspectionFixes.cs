using System;
using System.Collections.Generic;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Engine.Navigation;

namespace UEToVS.Engine.Inspection
{
    /// <summary>
    /// Mechanical fixes for whole-project inspection findings, computed on the document the finding came from. Only rules
    /// whose fix is certain get one:
    /// <list type="bullet">
    /// <item>UE0004 — an out-of-line definition (<c>AFoo::ServerDo</c>) of a Server / Client / NetMulticast / BlueprintNativeEvent
    /// UFUNCTION: rename it to <c>ServerDo_Implementation</c> (UHT declares that one). Not for BlueprintImplementableEvent
    /// (it must have no body at all) nor for bodies inside the class (renaming there would rename the UFUNCTION itself).</item>
    /// <item>UE0003 — a reflected type without GENERATED_BODY(): insert it as the first line of the class body, when the
    /// <c>{</c> ends its line.</item>
    /// </list>
    /// Edits carry <see cref="FileTextEdit.OldText"/> so a file changed since the run is skipped instead of corrupted.
    /// </summary>
    public static class InspectionFixes
    {
        private static readonly HashSet<string> RenameSpecifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "Server", "Client", "NetMulticast", "BlueprintNativeEvent",
        };

        /// <summary>Whether findings of <paramref name="code"/> can have a mechanical fix at all (UI chips, MCP).</summary>
        public static bool IsFixableRule(string code) =>
            code == CodeDiagnosticCodes.GeneratedFunctionBody || code == CodeDiagnosticCodes.MissingGeneratedBody;

        public static List<FileTextEdit> For(DocumentModel doc, CodeDiagnostic d)
        {
            if (doc == null || d == null) return null;
            try
            {
                switch (d.Code)
                {
                    case CodeDiagnosticCodes.GeneratedFunctionBody: return RenameToImplementation(doc, d);
                    case CodeDiagnosticCodes.MissingGeneratedBody: return InsertGeneratedBody(doc, d);
                    default: return null;
                }
            }
            catch (ArgumentException)
            {
                return null; // position outside the document: no fix rather than a wrong one
            }
        }

        private static List<FileTextEdit> RenameToImplementation(DocumentModel doc, CodeDiagnostic d)
        {
            if (d.Args == null || d.Args.Count < 2 || !RenameSpecifiers.Contains(d.Args[1])) return null;
            string name = d.Args[0];
            string line = doc.LineText(d.Line);
            if (line == null || d.Column + d.Length > line.Length || d.Column < 2) return null;
            if (!string.Equals(line.Substring(d.Column, d.Length), name, StringComparison.Ordinal)) return null;
            // out-of-line definition only: "Owner::Name"
            int k = d.Column - 1;
            while (k >= 0 && line[k] == ' ') k--;
            if (k < 1 || line[k] != ':' || line[k - 1] != ':') return null;
            string impl = name + "_Implementation";
            foreach (var decl in doc.Decls)
                if (string.Equals(decl.Name, impl, StringComparison.Ordinal)) return null; // already there: renaming would duplicate it
            return new List<FileTextEdit>
            {
                new FileTextEdit { FilePath = doc.Path, Line = d.Line, Column = d.Column, Length = d.Length, OldText = name, NewText = impl },
            };
        }

        private static List<FileTextEdit> InsertGeneratedBody(DocumentModel doc, CodeDiagnostic d)
        {
            int offset = doc.OffsetOf(d.Line, d.Column);
            int di = doc.DeclAtOffset(offset);
            if (di < 0) return null;
            var decl = doc.Decls[di];
            if (decl.Body.IsNone || decl.Body.Start < 0 || decl.Body.Start >= doc.Text.Length || doc.Text[decl.Body.Start] != (byte)'{') return null;
            var (braceLine, braceCol) = doc.PositionOf(decl.Body.Start);
            string line = doc.LineText(braceLine);
            if (line == null || braceCol >= line.Length || line.Substring(braceCol + 1).Trim().Length != 0) return null;
            int indentEnd = 0;
            while (indentEnd < line.Length && (line[indentEnd] == ' ' || line[indentEnd] == '\t')) indentEnd++;
            string indent = line.Substring(0, indentEnd);
            string newline = UsesCrLf(doc.Text) ? "\r\n" : "\n";
            if (braceLine + 1 >= doc.Lines.LineCount) return null;
            return new List<FileTextEdit>
            {
                new FileTextEdit { FilePath = doc.Path, Line = braceLine + 1, Column = 0, Length = 0, OldText = string.Empty, NewText = indent + "\tGENERATED_BODY()" + newline },
            };
        }

        private static bool UsesCrLf(byte[] text)
        {
            int n = Math.Min(text.Length, 64 * 1024);
            int i = Array.IndexOf(text, (byte)'\n', 0, n);
            return i > 0 && text[i - 1] == (byte)'\r';
        }

        /// <summary>The finding's source line for the result list (trimmed, at most 240 characters).</summary>
        public static string LinePreview(DocumentModel doc, int line)
        {
            string text = doc?.LineText(line);
            if (text == null) return null;
            text = text.Trim();
            if (text.Length <= 240) return text;
            var sb = new StringBuilder(text, 0, 239, 240);
            sb.Append('…');
            return sb.ToString();
        }
    }
}
