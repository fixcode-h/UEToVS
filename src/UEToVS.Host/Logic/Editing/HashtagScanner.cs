using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>One #tag in a comment.</summary>
    public sealed class HashtagHit
    {
        /// <summary>Lower-case tag without '#'.</summary>
        public string Tag { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        /// <summary>The comment text after the tag (trimmed, one line).</summary>
        public string Text { get; set; }

        public override string ToString() => $"#{Tag} {Line + 1}:{Column + 1} {Text}";
    }

    /// <summary>
    /// Comment #tag bookmarks (// #todo fix the spawn, /* #perf … */): a lexical C++ scan of one file's text, comments only
    /// (string / char literals and code skipped; preprocessor words such as #include / #if inside commented-out code are not
    /// tags). Used on demand over the open documents only.
    /// </summary>
    public static class HashtagScanner
    {
        public const int MaxTagLength = 40;
        public const int MaxTextLength = 120;

        private static readonly HashSet<string> Preprocessor = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "include", "if", "ifdef", "ifndef", "else", "elif", "endif", "define", "undef", "pragma", "error", "warning", "line", "region", "endregion", "import",
        };

        public static List<HashtagHit> Scan(string text, string filePath = null)
        {
            var hits = new List<HashtagHit>();
            if (string.IsNullOrEmpty(text) || text.IndexOf('#') < 0) return hits;
            int line = 0, lineStart = 0, i = 0, n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '\n')
                {
                    line++;
                    lineStart = ++i;
                    continue;
                }
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    int end = text.IndexOf('\n', i);
                    if (end < 0) end = n;
                    ScanComment(text, i + 2, end, filePath, ref line, ref lineStart, hits);
                    i = end;
                    continue;
                }
                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) end = n;
                    ScanComment(text, i + 2, end, filePath, ref line, ref lineStart, hits);
                    i = Math.Min(n, end + 2);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    if (c == '"' && i > 0 && text[i - 1] == 'R') // raw string R"d( … )d"
                    {
                        int open = text.IndexOf('(', i);
                        if (open > 0 && open - i <= 17)
                        {
                            string close = ")" + text.Substring(i + 1, open - i - 1) + "\"";
                            int end = text.IndexOf(close, open, StringComparison.Ordinal);
                            end = end < 0 ? n : end + close.Length;
                            CountLines(text, i, end, ref line, ref lineStart);
                            i = end;
                            continue;
                        }
                    }
                    i++;
                    while (i < n && text[i] != c && text[i] != '\n')
                    {
                        if (text[i] == '\\') i++;
                        i++;
                    }
                    if (i < n && text[i] == c) i++;
                    continue;
                }
                i++;
            }
            return hits;
        }

        /// <summary>Groups by tag (most hits first, then name), hits in file / line order.</summary>
        public static List<(string Tag, List<HashtagHit> Hits)> Group(IEnumerable<HashtagHit> hits) =>
            (hits ?? Enumerable.Empty<HashtagHit>())
                .GroupBy(h => h.Tag, StringComparer.Ordinal)
                .Select(g => (g.Key, g.OrderBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Line).ThenBy(h => h.Column).ToList()))
                .OrderByDescending(g => g.Item2.Count).ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

        private static void ScanComment(string text, int start, int end, string filePath, ref int line, ref int lineStart, List<HashtagHit> hits)
        {
            for (int i = start; i < end; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    line++;
                    lineStart = i + 1;
                    continue;
                }
                if (c != '#') continue;
                if (i > start && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_' || text[i - 1] == '#' || text[i - 1] == '&')) continue; // a#b, ##, &#x
                int j = i + 1;
                if (j >= end || !char.IsLetter(text[j])) continue;
                while (j < end && (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '-')) j++;
                string tag = text.Substring(i + 1, j - i - 1).TrimEnd('-');
                if (tag.Length == 0 || tag.Length > MaxTagLength || Preprocessor.Contains(tag)) continue;
                int eol = text.IndexOf('\n', j);
                if (eol < 0 || eol > end) eol = end;
                string rest = text.Substring(j, eol - j).Trim().TrimEnd('*', '/').Trim().TrimStart(':', '：', '-').Trim();
                if (rest.Length > MaxTextLength) rest = rest.Substring(0, MaxTextLength) + "…";
                hits.Add(new HashtagHit { Tag = tag.ToLowerInvariant(), FilePath = filePath, Line = line, Column = i - lineStart, Text = rest });
                i = j - 1;
            }
        }

        private static void CountLines(string text, int start, int end, ref int line, ref int lineStart)
        {
            for (int i = start; i < end && i < text.Length; i++)
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
        }
    }
}
