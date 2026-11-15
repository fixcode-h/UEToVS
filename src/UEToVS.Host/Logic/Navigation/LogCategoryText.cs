using System;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.Navigation
{
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
