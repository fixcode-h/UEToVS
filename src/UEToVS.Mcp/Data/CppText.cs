using System;
using System.Collections.Generic;
using System.Text;

namespace UEToVS.Mcp.Data
{
    /// <summary>Small lexical helpers for the index-only fallbacks (no parser needed).</summary>
    public static class CppText
    {
        public static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// Identifier at (or just before) a UTF-16 column of a line, with its "A::B::" qualifier if written. Returns
        /// null when the column is not on an identifier.
        /// </summary>
        public static (string Name, string Qualifier, int Start)? IdentifierAt(string line, int column)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (column >= line.Length) column = line.Length - 1;
            if (column < 0) column = 0;
            if (!IsIdentChar(line[column]) && column > 0 && IsIdentChar(line[column - 1])) column--;
            if (!IsIdentChar(line[column])) return null;
            int s = column, e = column;
            while (s > 0 && IsIdentChar(line[s - 1])) s--;
            while (e + 1 < line.Length && IsIdentChar(line[e + 1])) e++;
            string name = line.Substring(s, e - s + 1);
            if (char.IsDigit(name[0])) return null;
            // qualifier: walk back over "Ident::" pairs
            var quals = new List<string>();
            int q = s;
            while (q >= 2 && line[q - 1] == ':' && line[q - 2] == ':')
            {
                int qe = q - 3, qs = qe;
                while (qs >= 0 && IsIdentChar(line[qs])) qs--;
                if (qe < 0 || qs == qe) break;
                quals.Insert(0, line.Substring(qs + 1, qe - qs));
                q = qs + 1;
            }
            return (name, quals.Count > 0 ? string.Join("::", quals) : null, s);
        }

        /// <summary>Splits "Outer::Inner::Name" into (Name, "Outer::Inner").</summary>
        public static (string Name, string Container) SplitQualified(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return (null, null);
            symbol = symbol.Trim();
            int idx = symbol.LastIndexOf("::", StringComparison.Ordinal);
            if (idx < 0) return (symbol, null);
            string container = symbol.Substring(0, idx).Trim();
            return (symbol.Substring(idx + 2).Trim(), container.Length == 0 ? null : container);
        }

        private static readonly HashSet<string> BaseNoise = new HashSet<string>(StringComparer.Ordinal) { "public", "protected", "private", "virtual" };

        /// <summary>
        /// Base classes from a class signature as rendered by the parser, e.g.
        /// "class MYGAME_API AHero : public ACharacter, public IInteractable" → [ACharacter, IInteractable].
        /// Template arguments are kept ("TSharedFromThis&lt;FFoo&gt;").
        /// </summary>
        public static List<string> ParseBases(string signature)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(signature)) return result;
            int colon = -1, depth = 0;
            for (int i = 0; i < signature.Length; i++)
            {
                char c = signature[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == ':' && depth == 0)
                {
                    if (i + 1 < signature.Length && signature[i + 1] == ':') { i++; continue; }
                    if (i > 0 && signature[i - 1] == ':') continue;
                    colon = i;
                    break;
                }
            }
            if (colon < 0) return result;
            string tail = signature.Substring(colon + 1);
            int brace = tail.IndexOf('{');
            if (brace >= 0) tail = tail.Substring(0, brace);
            var part = new StringBuilder();
            depth = 0;
            void Flush()
            {
                var words = part.ToString().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var kept = new List<string>();
                foreach (var w in words) if (!BaseNoise.Contains(w)) kept.Add(w);
                string name = string.Join(" ", kept).Trim();
                if (name.Length > 0 && name != "…") result.Add(name.TrimEnd('…'));
                part.Clear();
            }
            foreach (char c in tail)
            {
                if (c == '<') depth++;
                else if (c == '>') depth--;
                if (c == ',' && depth == 0) Flush();
                else part.Append(c);
            }
            Flush();
            return result;
        }

        /// <summary>The plain type name of a base as written: "Ns::TBase&lt;X&gt;" → "TBase".</summary>
        public static string BaseTypeName(string written)
        {
            if (string.IsNullOrEmpty(written)) return written;
            string s = written;
            int lt = s.IndexOf('<');
            if (lt >= 0) s = s.Substring(0, lt);
            int q = s.LastIndexOf("::", StringComparison.Ordinal);
            if (q >= 0) s = s.Substring(q + 2);
            return s.Trim();
        }
    }
}
