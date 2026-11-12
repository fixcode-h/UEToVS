using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UEToVS.Host.Logic.Agent
{
    /// <summary>
    /// Filters the model ids an API returned by what the user types in the "add model" box (Agent / API page).
    /// Case-insensitive; every space-separated word must occur in the id, either as typed or with separators
    /// ('-', '_', '.', '/', ':') ignored on both sides, so "v41" finds "deepseek-v4.1-flash".
    /// </summary>
    public static class ModelFilter
    {
        public static bool Matches(string id, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            if (string.IsNullOrEmpty(id)) return false;
            string compactId = Compact(id);
            foreach (var word in query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (id.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string w = Compact(word);
                if (w.Length > 0 && compactId.IndexOf(w, StringComparison.Ordinal) >= 0) continue;
                return false;
            }
            return true;
        }

        public static IReadOnlyList<string> Apply(IEnumerable<string> ids, string query) =>
            (ids ?? Enumerable.Empty<string>()).Where(id => Matches(id, query)).ToList();

        /// <summary>
        /// What Enter in the box adds: the only matching returned model when exactly one matches (and the text is not
        /// already an exact id), otherwise the text as typed.
        /// </summary>
        public static string ResolveEnter(IReadOnlyList<string> pool, string text)
        {
            string typed = text?.Trim() ?? string.Empty;
            if (typed.Length == 0) return null;
            if (pool == null || pool.Any(id => string.Equals(id, typed, StringComparison.OrdinalIgnoreCase)))
                return pool?.First(id => string.Equals(id, typed, StringComparison.OrdinalIgnoreCase)) ?? typed;
            var hits = Apply(pool, typed);
            return hits.Count == 1 ? hits[0] : typed;
        }

        private static string Compact(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '-' || c == '_' || c == '.' || c == '/' || c == ':' || char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
