using System;
using System.Text;
using UEToVS.Contracts;

namespace UEToVS.Engine.Index
{
    /// <summary>A type (class/struct/...) with its base classes as written.</summary>
    public sealed class TypeEntry
    {
        public SymbolInfo Symbol { get; set; }

        /// <summary>Base texts as written (template arguments kept); empty when none.</summary>
        public string[] Bases { get; set; } = Array.Empty<string>();

        public override string ToString() => Symbol?.Name + " : " + string.Join(", ", Bases);
    }

    /// <summary>Helpers for base-class texts.</summary>
    public static class BaseNames
    {
        /// <summary>
        /// Key of the base → derived table: template arguments removed and only the last <c>::</c> segment kept,
        /// e.g. <c>UE::Foo::TBase&lt;int&gt;</c> → <c>TBase</c>. Null for empty input.
        /// </summary>
        public static string Simple(string baseText)
        {
            string s = StripTemplateArgs(baseText);
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.LastIndexOf("::", StringComparison.Ordinal);
            if (i >= 0) s = s.Substring(i + 2);
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>
        /// Removes every <c>&lt;...&gt;</c> group (nested aware) and whitespace around <c>::</c>:
        /// <c>UE::TBase&lt;A&lt;B&gt;&gt;</c> → <c>UE::TBase</c>.
        /// </summary>
        public static string StripTemplateArgs(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            int depth = 0;
            foreach (char c in text)
            {
                if (c == '<') { depth++; continue; }
                if (c == '>') { if (depth > 0) depth--; continue; }
                if (depth == 0 && !char.IsWhiteSpace(c)) sb.Append(c);
            }
            string s = sb.ToString();
            if (s.StartsWith("::", StringComparison.Ordinal)) s = s.Substring(2);
            return s;
        }
    }
}
