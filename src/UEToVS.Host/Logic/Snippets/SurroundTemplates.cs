using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Snippets
{
    /// <summary>
    /// A Surround With template (v0.4.5): a live-template body (<see cref="SnippetTemplate"/> syntax) with one <c>$SELECTION$</c>
    /// placeholder on a line of its own. The leading tabs before <c>$SELECTION$</c> are the extra indent the selected lines get
    /// (<c>\t$SELECTION$</c> inside braces, plain <c>$SELECTION$</c> for #if / #pragma region).
    /// </summary>
    public sealed class SurroundTemplate
    {
        public SurroundTemplate(string key, string titleZh, string titleEn, string body)
        {
            Key = key;
            TitleZh = titleZh;
            TitleEn = titleEn;
            Body = body;
        }

        /// <summary>Stable id (logs, filter text).</summary>
        public string Key { get; }
        public string TitleZh { get; }
        public string TitleEn { get; }
        public string Body { get; }
        public string Title => Loc.T(TitleZh, TitleEn);

        /// <summary>The template with "…" for the selection, one line (popup description).</summary>
        public string Preview => SurroundExpander.Expand(this, "…", "    ", "\n", null).Text.Replace("\n", " ⏎ ");

        public override string ToString() => Key;
    }

    /// <summary>Built-in Surround With templates for UE code. No <c>try</c>: UE builds with exceptions disabled.</summary>
    public static class SurroundCatalog
    {
        public const string Selection = "$SELECTION$";

        public static IReadOnlyList<SurroundTemplate> BuiltIn { get; } = new List<SurroundTemplate>
        {
            new SurroundTemplate("isvalid", "if (IsValid(X))", "if (IsValid(X))",
                "if (IsValid($Object=Object$))\n{\n\t$SELECTION$\n}$END$"),
            new SurroundTemplate("if", "if ()", "if ()",
                "if ($Condition=$)\n{\n\t$SELECTION$\n}$END$"),
            new SurroundTemplate("for", "for（范围 for）", "for (range-based)",
                "for ($Element=auto& Element$ : $Container=Items$)\n{\n\t$SELECTION$\n}$END$"),
            new SurroundTemplate("block", "{ }（代码块）", "{ } (block)",
                "{\n\t$SELECTION$\n}$END$"),
            new SurroundTemplate("witheditor", "#if WITH_EDITOR", "#if WITH_EDITOR",
                "#if WITH_EDITOR\n$SELECTION$\n#endif // WITH_EDITOR$END$"),
            new SurroundTemplate("notshipping", "#if !UE_BUILD_SHIPPING", "#if !UE_BUILD_SHIPPING",
                "#if !UE_BUILD_SHIPPING\n$SELECTION$\n#endif // !UE_BUILD_SHIPPING$END$"),
            new SurroundTemplate("cyclecounter", "SCOPE_CYCLE_COUNTER", "SCOPE_CYCLE_COUNTER",
                "{\n\tSCOPE_CYCLE_COUNTER($Stat=STAT_MyStat$);\n\t$SELECTION$\n}$END$"),
            new SurroundTemplate("cpuprofiler", "TRACE_CPUPROFILER_EVENT_SCOPE", "TRACE_CPUPROFILER_EVENT_SCOPE",
                "{\n\tTRACE_CPUPROFILER_EVENT_SCOPE($Name=MyScope$);\n\t$SELECTION$\n}$END$"),
            new SurroundTemplate("quickcounter", "QUICK_SCOPE_CYCLE_COUNTER", "QUICK_SCOPE_CYCLE_COUNTER",
                "{\n\tQUICK_SCOPE_CYCLE_COUNTER($Stat=STAT_MyScope$);\n\t$SELECTION$\n}$END$"),
            new SurroundTemplate("region", "#pragma region", "#pragma region",
                "#pragma region $Name=Region$\n$SELECTION$\n#pragma endregion$END$"),
        };

        public static SurroundTemplate Find(string key) => BuiltIn.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Expands a <see cref="SurroundTemplate"/> around whole lines of code (pure). The selection is inserted verbatim (a '$' or a
    /// tab in it is never read as template syntax); lines indented at least as deep as the first line are re-indented by the
    /// template's extra indent, shallower lines (a #define at column 0) and blank lines are kept as they are.
    /// </summary>
    public static class SurroundExpander
    {
        private const string Marker = "$__UETOVS_SELECTION__=$";
        private const string MarkerName = "__UETOVS_SELECTION__";

        /// <param name="selection">The selected lines without the first line's indentation and without a trailing line break.</param>
        /// <param name="indent">Leading white space of the first selected line (the template's own indentation).</param>
        public static SnippetExpansion Expand(SurroundTemplate template, string selection, string indent, string indentUnit, string newLine, string filePath = null)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            return Expand(template.Body, selection, indent, indentUnit, newLine, filePath);
        }

        public static SnippetExpansion Expand(string body, string selection, string indent, string indentUnit, string newLine, string filePath = null)
        {
            indent = indent ?? string.Empty;
            indentUnit = indentUnit ?? "\t";
            newLine = string.IsNullOrEmpty(newLine) ? "\r\n" : newLine;
            selection = selection ?? string.Empty;
            body = body ?? string.Empty;
            if (body.IndexOf(SurroundCatalog.Selection, StringComparison.Ordinal) < 0) body += "\n" + SurroundCatalog.Selection;
            var e = SnippetExpander.Expand(body.Replace(SurroundCatalog.Selection, Marker), indent, indentUnit, newLine, filePath);
            var mark = e.Fields.FirstOrDefault(f => f.Name == MarkerName);
            if (mark == null) return e;
            // indentation of the line the selection goes on (template indent + its extra tabs)
            int lineStart = e.Text.LastIndexOf('\n', Math.Max(0, mark.Start - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            string lineIndent = e.Text.Substring(lineStart, mark.Start - lineStart);
            if (lineStart == 0) lineIndent = indent + lineIndent; // the first line's indent is outside the expansion
            string inserted = Reindent(selection, indent, lineIndent, newLine);

            var result = new SnippetExpansion { Text = e.Text.Substring(0, mark.Start) + inserted + e.Text.Substring(mark.Start) };
            foreach (var f in e.Fields)
            {
                if (f.Name == MarkerName) continue;
                result.Fields.Add(new SnippetField { Name = f.Name, Start = f.Start >= mark.Start ? f.Start + inserted.Length : f.Start, Length = f.Length, IsPrimary = f.IsPrimary });
            }
            result.EndOffset = e.EndOffset >= mark.Start ? e.EndOffset + inserted.Length : e.EndOffset;
            return result;
        }

        /// <summary>
        /// Selected lines for the template: the first line is placed as is (the caller removed its indent), the others lose
        /// <paramref name="baseIndent"/> and are put under <paramref name="lineIndent"/>.
        /// </summary>
        public static string Reindent(string selection, string baseIndent, string lineIndent, string newLine)
        {
            var lines = SplitLines(selection);
            var sb = new StringBuilder(selection.Length + lines.Count * (lineIndent.Length + 2));
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                if (i > 0)
                {
                    sb.Append(newLine);
                    if (l.Trim().Length == 0) continue;
                    if (l.StartsWith(baseIndent, StringComparison.Ordinal)) sb.Append(lineIndent).Append(l, baseIndent.Length, l.Length - baseIndent.Length);
                    else sb.Append(l);
                    continue;
                }
                sb.Append(l);
            }
            return sb.ToString();
        }

        private static List<string> SplitLines(string text)
        {
            var list = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n' && text[i] != '\r') continue;
                list.Add(text.Substring(start, i - start));
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            list.Add(text.Substring(start));
            return list;
        }

        /// <summary>
        /// The whole lines a selection covers: [first, last] line numbers given the selection's start / end (line, column).
        /// A selection that ends at column 0 of a later line does not include that line.
        /// </summary>
        public static (int First, int Last) LineRange(int startLine, int endLine, int endColumn)
        {
            if (endLine > startLine && endColumn == 0) endLine--;
            return (startLine, Math.Max(startLine, endLine));
        }
    }
}
