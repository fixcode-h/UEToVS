using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.Link
{
    /// <summary>A UE_LOG format string compiled to a regular expression that matches the messages it can produce.</summary>
    public sealed class UeLogFormatPattern
    {
        internal UeLogFormatPattern(string format, Regex regex, int literalChars, int placeholders)
        {
            Format = format;
            Regex = regex;
            LiteralChars = literalChars;
            Placeholders = placeholders;
        }

        public string Format { get; }
        public Regex Regex { get; }

        /// <summary>Non-whitespace literal characters: how specific the format is (0 = "%s" alone, matches anything).</summary>
        public int LiteralChars { get; }

        public int Placeholders { get; }

        public bool IsMatch(string message)
        {
            if (Regex == null || message == null) return false;
            try
            {
                return Regex.IsMatch(message);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Turns UE log format strings into regular expressions: printf style for UE_LOG / UE_CLOG (<c>%s %d %i %u %f %.2f
    /// %lld %x %p %c %%</c>, flags / width / precision / length modifiers) and named placeholders for UE_LOGFMT
    /// (<c>{Name}</c>, <c>{{</c> <c>}}</c>). The whole message must match (anchored); placeholders of %s are lazy and may be
    /// empty. Pure; the patterns are cached per format.
    /// </summary>
    public static class UeLogFormat
    {
        private const string AnyText = @"[\s\S]*?";
        private const string Integer = @"[-+]?\d+";
        private const string Unsigned = @"\d+";
        private const string Hex = @"[0-9a-fA-F]+";
        private const string Float = @"(?:[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?|[-+]?(?:[iI][nN][fF]|[nN][aA][nN])\S*)";
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        public static UeLogFormatPattern Compile(string format, bool structured)
        {
            if (format == null) return new UeLogFormatPattern(null, null, 0, 0);
            var sb = new StringBuilder("^");
            int literal = 0, placeholders = 0;
            var lit = new StringBuilder();

            void Flush()
            {
                if (lit.Length == 0) return;
                foreach (char ch in lit.ToString()) if (!char.IsWhiteSpace(ch)) literal++;
                sb.Append(Regex.Escape(lit.ToString()));
                lit.Clear();
            }

            int i = 0;
            while (i < format.Length)
            {
                char c = format[i];
                if (structured)
                {
                    if ((c == '{' || c == '}') && i + 1 < format.Length && format[i + 1] == c)
                    {
                        lit.Append(c);
                        i += 2;
                        continue;
                    }
                    if (c == '{')
                    {
                        int close = format.IndexOf('}', i + 1);
                        if (close > i)
                        {
                            Flush();
                            sb.Append(AnyText);
                            placeholders++;
                            i = close + 1;
                            continue;
                        }
                    }
                    lit.Append(c);
                    i++;
                    continue;
                }

                if (c != '%')
                {
                    lit.Append(c);
                    i++;
                    continue;
                }
                if (i + 1 < format.Length && format[i + 1] == '%')
                {
                    lit.Append('%');
                    i += 2;
                    continue;
                }
                // %[flags][width][.precision][length]conversion
                int j = i + 1;
                bool leftAlign = false, alternate = false;
                while (j < format.Length && "-+ #0".IndexOf(format[j]) >= 0)
                {
                    if (format[j] == '-') leftAlign = true;
                    if (format[j] == '#') alternate = true;
                    j++;
                }
                bool width = false;
                while (j < format.Length && (char.IsDigit(format[j]) || format[j] == '*'))
                {
                    width = true;
                    j++;
                }
                if (j < format.Length && format[j] == '.')
                {
                    j++;
                    while (j < format.Length && (char.IsDigit(format[j]) || format[j] == '*')) j++;
                }
                // length: hh h l ll L z j t I I32 I64 q
                while (j < format.Length && "hlLzjtqI".IndexOf(format[j]) >= 0)
                {
                    if (format[j] == 'I' && j + 2 < format.Length && ((format[j + 1] == '6' && format[j + 2] == '4') || (format[j + 1] == '3' && format[j + 2] == '2'))) j += 3;
                    else j++;
                }
                if (j >= format.Length)
                {
                    // Dangling '%': UE prints it as is.
                    lit.Append(format, i, format.Length - i);
                    break;
                }
                char conv = format[j];
                string piece;
                switch (conv)
                {
                    case 'd':
                    case 'i': piece = Integer; break;
                    case 'u': piece = Unsigned; break;
                    case 'x':
                    case 'X': piece = (alternate ? "(?:0[xX])?" : "") + Hex; break;
                    case 'o': piece = "[0-7]+"; break;
                    case 'f':
                    case 'F':
                    case 'e':
                    case 'E':
                    case 'g':
                    case 'G':
                    case 'a':
                    case 'A': piece = Float; break;
                    case 'c':
                    case 'C': piece = @"[\s\S]"; break;
                    case 'p': piece = "(?:0[xX])?" + Hex; break;
                    case 'n': piece = string.Empty; break;
                    case 's':
                    case 'S': piece = AnyText; break;
                    default:
                        // Unknown conversion: be permissive rather than miss the line.
                        piece = AnyText;
                        break;
                }
                Flush();
                if (width && conv != 's' && conv != 'S')
                    piece = leftAlign ? "(?:" + piece + ") *" : " *(?:" + piece + ")";
                sb.Append(piece);
                placeholders++;
                i = j + 1;
            }
            Flush();
            sb.Append(@"\s*\z");
            Regex regex;
            try
            {
                regex = new Regex(sb.ToString(), RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException)
            {
                regex = null;
            }
            return new UeLogFormatPattern(format, regex, literal, placeholders);
        }
    }

    /// <summary>Outcome of looking up the UE_LOG that produced a log line.</summary>
    public sealed class UeLogSourceMatch
    {
        /// <summary>The best matching sites (more than one = the same specificity in several places: let the user pick).</summary>
        public List<LogSiteInfo> Sites { get; } = new List<LogSiteInfo>();

        /// <summary>"exact" | "noSites" (no UE_LOG with this category) | "noMatch" | "ambiguousFormat" (only %s-like formats match).</summary>
        public string Outcome { get; set; }

        public int Candidates { get; set; }
        public int Matched { get; set; }
        public int LiteralChars { get; set; }

        public bool IsExact => Outcome == "exact" && Sites.Count > 0;
    }

    /// <summary>
    /// Finds the UE_LOG / UE_CLOG / UE_LOGFMT call that produced a log line: same category, compatible verbosity, and the
    /// format (compiled by <see cref="UeLogFormat"/>) matches the whole message. Only an exact match counts; among several
    /// matching formats the most specific (most literal characters) wins; a format without any literal text ("%s")
    /// is never a match on its own. Keeps a small pattern cache (formats repeat across lookups).
    /// </summary>
    public sealed class UeLogSourceMatcher
    {
        private const int CacheLimit = 2048;
        private readonly Dictionary<string, UeLogFormatPattern> _cache = new Dictionary<string, UeLogFormatPattern>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public UeLogFormatPattern Pattern(string format, bool structured)
        {
            if (format == null) return null;
            string key = (structured ? "F|" : "P|") + format;
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var p)) return p;
                p = UeLogFormat.Compile(format, structured);
                if (_cache.Count >= CacheLimit) _cache.Clear();
                _cache[key] = p;
                return p;
            }
        }

        public UeLogSourceMatch Find(IEnumerable<LogSiteInfo> sites, string category, UeLogVerbosity verbosity, string message)
        {
            var result = new UeLogSourceMatch { Outcome = "noSites" };
            if (sites == null) return result;
            message = (message ?? string.Empty).TrimEnd('\r', '\n');
            int best = -1;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sites)
            {
                if (s == null || s.Kind != LogSiteKind.Usage || !string.Equals(s.Category, category, StringComparison.Ordinal)) continue;
                result.Candidates++;
                if (s.Format == null) continue;
                var written = UeLogVerbosities.Parse(s.Verbosity);
                if (written.HasValue && UeLogVerbosities.LevelOf(written.Value) != UeLogVerbosities.LevelOf(verbosity)) continue;
                var p = Pattern(s.Format, string.Equals(s.Macro, "UE_LOGFMT", StringComparison.Ordinal));
                if (p == null || !p.IsMatch(message)) continue;
                result.Matched++;
                if (p.LiteralChars < best) continue;
                if (p.LiteralChars > best)
                {
                    best = p.LiteralChars;
                    result.Sites.Clear();
                    seen.Clear();
                }
                string key = (s.Location?.FilePath ?? "") + ":" + (s.Location?.Line ?? -1);
                if (seen.Add(key)) result.Sites.Add(s);
            }
            result.LiteralChars = Math.Max(0, best);
            if (result.Candidates == 0) result.Outcome = "noSites";
            else if (result.Sites.Count == 0) result.Outcome = "noMatch";
            else if (best <= 0 && message.Trim().Length > 0)
            {
                result.Outcome = "ambiguousFormat";
                result.Sites.Clear();
            }
            else result.Outcome = "exact";
            return result;
        }
    }

    /// <summary>Log category helpers for the editor (related items: declaration ↔ definition ↔ usages).</summary>
    public static class LogCategoryText
    {
        private static readonly string[] Macros =
        {
            "UE_LOG", "UE_CLOG", "UE_LOGFMT", "DECLARE_LOG_CATEGORY_EXTERN", "DECLARE_LOG_CATEGORY_CLASS",
            "DEFINE_LOG_CATEGORY", "DEFINE_LOG_CATEGORY_STATIC", "DEFINE_LOG_CATEGORY_CLASS",
        };

        /// <summary>
        /// Is <paramref name="word"/> worth asking the engine about? An identifier that starts with "Log" + upper case
        /// (UE naming), or any identifier on a line that uses one of the log macros.
        /// </summary>
        public static bool IsCandidate(string word, string lineText)
        {
            if (string.IsNullOrEmpty(word) || !IsIdentifier(word)) return false;
            if (word.Length > 3 && word.StartsWith("Log", StringComparison.Ordinal) && char.IsUpper(word[3])) return true;
            if (string.IsNullOrEmpty(lineText)) return false;
            foreach (var m in Macros)
                if (lineText.IndexOf(m, StringComparison.Ordinal) >= 0 && !IsMacroName(word)) return true;
            return false;
        }

        private static bool IsMacroName(string word)
        {
            foreach (var m in Macros) if (string.Equals(m, word, StringComparison.Ordinal)) return true;
            return word == "TEXT" || word == "Error" || word == "Warning" || word == "Display" || word == "Log" || word == "Verbose" ||
                   word == "VeryVerbose" || word == "Fatal" || word == "All";
        }

        private static bool IsIdentifier(string s)
        {
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char c in s) if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        /// <summary>Sort key: declarations, definitions, then usages; then file and line.</summary>
        public static int KindRank(LogSiteKind kind)
        {
            switch (kind)
            {
                case LogSiteKind.Declaration: return 0;
                case LogSiteKind.StaticDefinition: return 1;
                case LogSiteKind.Definition: return 2;
                default: return 3;
            }
        }

        /// <summary>Row title for a site: "UE_LOG(LogFoo, Warning, "Loaded %s")" (format shortened).</summary>
        public static string Title(LogSiteInfo s)
        {
            if (s == null) return string.Empty;
            if (s.Kind != LogSiteKind.Usage) return (s.Macro ?? "?") + "(" + s.Category + ")";
            string fmt = s.Format == null ? "…" : "\"" + Shorten(s.Format.Replace("\r", "\\r").Replace("\n", "\\n"), 80) + "\"";
            return (s.Macro ?? "UE_LOG") + "(" + s.Category + ", " + (s.Verbosity ?? "?") + ", " + fmt + ")";
        }

        public static string Shorten(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
    }
}
