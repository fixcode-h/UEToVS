using System;
using System.Collections.Generic;
using System.Text;

namespace UEToVS.Host.Logic.Translation
{
    /// <summary>How a translated comment is shown in Quick Info / the selection popup.</summary>
    public enum TranslationDisplay
    {
        /// <summary>Each paragraph of the original followed by its translation (default).</summary>
        Bilingual = 0,

        /// <summary>Only the translation; the original is shown while it is pending or when it fails.</summary>
        TranslationOnly = 1,
    }

    /// <summary>Which free translation service is used.</summary>
    public enum TranslationProviderKind
    {
        /// <summary>Google Translate (translate.googleapis.com, client=gtx; no key). Default.</summary>
        Google = 0,

        /// <summary>Microsoft Translator through the token Edge uses for page translation (no key).</summary>
        Microsoft = 1,

        /// <summary>MyMemory (api.mymemory.translated.net; anonymous quota about 5000 words a day).</summary>
        MyMemory = 2,
    }

    /// <summary>One piece of a comment: a paragraph or a list item, reflowed to one line.</summary>
    public sealed class TextUnit
    {
        public TextUnit(string text, bool translate, bool gapBefore)
        {
            Text = text;
            Translate = translate;
            GapBefore = gapBefore;
        }

        public string Text { get; }

        /// <summary>False for code-like lines and text already in the target language.</summary>
        public bool Translate { get; }

        /// <summary>A blank line preceded this unit in the original.</summary>
        public bool GapBefore { get; }

        public override string ToString() => (GapBefore ? "¶ " : "") + (Translate ? "" : "[keep] ") + Text;
    }

    /// <summary>
    /// Pure text helpers of the comment translation: split a doc comment into units (paragraphs reflowed, list items and
    /// @tags kept apart, code-like lines left untranslated), resolve the target language and map it per provider, and cut a
    /// long unit into chunks a provider accepts. No I/O.
    /// </summary>
    public static class TranslationText
    {
        /// <summary>Longest text translated at once (Quick Info trims docs at 4000 chars; a selection is cut here).</summary>
        public const int MaxTotalChars = 6000;

        public static readonly IReadOnlyList<string> TargetLanguages = new[] { "zh-CN", "zh-TW", "en", "ja", "ko" };

        /// <summary>"auto" (or empty / unknown) → zh-CN for a Chinese UI, en for an English one.</summary>
        public static string ResolveTarget(string setting, bool englishUi)
        {
            if (!string.IsNullOrWhiteSpace(setting))
                foreach (var t in TargetLanguages)
                    if (string.Equals(t, setting.Trim(), StringComparison.OrdinalIgnoreCase)) return t;
            return englishUi ? "en" : "zh-CN";
        }

        /// <summary>The provider's code for a target language.</summary>
        public static string ProviderCode(TranslationProviderKind provider, string target)
        {
            if (provider == TranslationProviderKind.Microsoft)
            {
                if (string.Equals(target, "zh-CN", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
                if (string.Equals(target, "zh-TW", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
            }
            return target;
        }

        /// <summary>Splits a (cleaned) doc comment into units. Blank lines separate paragraphs; hard-wrapped lines are joined.</summary>
        public static List<TextUnit> Split(string text, string target)
        {
            var units = new List<TextUnit>();
            if (string.IsNullOrWhiteSpace(text)) return units;
            if (text.Length > MaxTotalChars) text = text.Substring(0, MaxTotalChars);
            var current = new StringBuilder();
            bool currentCode = false, gap = false, pendingGap = false;

            void Flush()
            {
                if (current.Length == 0) return;
                string s = current.ToString().Trim();
                current.Clear();
                if (s.Length == 0) return;
                units.Add(new TextUnit(s, !currentCode && NeedsTranslation(s, target), gap));
                gap = false;
            }

            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.TrimEnd();
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    Flush();
                    pendingGap = units.Count > 0;
                    continue;
                }
                bool code = LooksLikeCode(trimmed) || line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal);
                bool startsItem = StartsListItem(trimmed);
                if (current.Length > 0 && (startsItem || code || currentCode)) Flush();
                if (current.Length == 0)
                {
                    if (pendingGap) gap = true;
                    pendingGap = false;
                    currentCode = code;
                }
                else current.Append(code ? '\n' : ' ');
                current.Append(code ? line.TrimStart() : trimmed);
            }
            Flush();
            return units;
        }

        /// <summary>"- item", "* item", "1. item", "@param X", "Note:" start a new unit.</summary>
        public static bool StartsListItem(string trimmed)
        {
            if (trimmed.Length == 0) return false;
            char c = trimmed[0];
            if ((c == '-' || c == '*' || c == '•' || c == '+') && trimmed.Length > 1 && trimmed[1] == ' ') return true;
            if (c == '@' || c == '\\') return trimmed.Length > 1 && char.IsLetter(trimmed[1]);
            int i = 0;
            while (i < trimmed.Length && char.IsDigit(trimmed[i])) i++;
            return i > 0 && i < trimmed.Length - 1 && (trimmed[i] == '.' || trimmed[i] == ')') && trimmed[i + 1] == ' ';
        }

        private static readonly char[] CodeChars = { '(', ')', '=', '<', '>', '[', ']', '{', '}' };

        /// <summary>A line of code inside a comment (ends with ; { }, or is mostly symbols).</summary>
        public static bool LooksLikeCode(string trimmed)
        {
            if (trimmed.Length == 0) return false;
            char last = trimmed[trimmed.Length - 1];
            if (last == '{' || last == '}') return true;
            // "Foo(5);" / "TArray<int32> A;" are code, "… are trivially relocatable;" (a wrapped sentence) is not
            if (last == ';' && (trimmed.IndexOfAny(CodeChars) >= 0 || trimmed.Split(' ').Length <= 3)) return true;
            if (trimmed.StartsWith("#include", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal)) return true;
            int letters = 0, symbols = 0;
            foreach (char ch in trimmed)
            {
                if (char.IsLetter(ch)) letters++;
                else if (!char.IsWhiteSpace(ch) && !char.IsDigit(ch) && ch != '.' && ch != ',' && ch != '\'' && ch != '"') symbols++;
            }
            return trimmed.Length >= 8 && symbols > letters;
        }

        /// <summary>Worth sending: has words, and is not already written in the target language.</summary>
        public static bool NeedsTranslation(string text, string target)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            int latinWords = 0, cjk = 0, letters = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                if (IsCjk(c)) { cjk++; letters++; inWord = false; continue; }
                bool latin = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (latin) { letters++; if (!inWord) latinWords++; }
                inWord = latin;
            }
            bool toEnglish = string.Equals(target, "en", StringComparison.OrdinalIgnoreCase);
            if (toEnglish) return cjk > 0;
            if (letters == 0) return false;
            if (cjk * 3 >= letters) return false; // already (mostly) CJK
            return latinWords >= 2;
        }

        public static bool IsCjk(char c) =>
            (c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿') || (c >= '぀' && c <= 'ヿ') ||
            (c >= '가' && c <= '힯') || (c >= '豈' && c <= '﫿');

        /// <summary>Cuts <paramref name="text"/> into pieces of at most <paramref name="max"/> chars at sentence, then word boundaries.</summary>
        public static List<string> Chunk(string text, int max)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            if (max <= 0 || text.Length <= max) { list.Add(text); return list; }
            int start = 0;
            while (start < text.Length)
            {
                int remaining = text.Length - start;
                if (remaining <= max) { list.Add(text.Substring(start)); break; }
                int cut = -1;
                for (int i = start + max - 1; i > start + max / 3; i--)
                {
                    char c = text[i];
                    if ((c == '.' || c == '!' || c == '?' || c == ';' || c == '。') && (i + 1 >= text.Length || text[i + 1] == ' ' || text[i + 1] == '\n'))
                    {
                        cut = i + 1;
                        break;
                    }
                }
                if (cut < 0)
                    for (int i = start + max - 1; i > start; i--)
                        if (text[i] == ' ') { cut = i; break; }
                if (cut <= start) cut = start + max;
                list.Add(text.Substring(start, cut - start));
                start = cut;
                while (start < text.Length && text[start] == ' ') start++;
            }
            return list;
        }

        /// <summary>Joins translated chunks: no space between CJK pieces, one space otherwise.</summary>
        public static string JoinChunks(IReadOnlyList<string> chunks)
        {
            var sb = new StringBuilder();
            foreach (var c in chunks)
            {
                string t = c?.Trim() ?? string.Empty;
                if (t.Length == 0) continue;
                if (sb.Length > 0 && !(IsCjk(sb[sb.Length - 1]) || IsCjkPunct(sb[sb.Length - 1])) && !IsCjk(t[0])) sb.Append(' ');
                sb.Append(t);
            }
            return sb.ToString();
        }

        private static bool IsCjkPunct(char c) => c >= '　' && c <= '〿' || c >= '＀' && c <= '￯';
    }
}
