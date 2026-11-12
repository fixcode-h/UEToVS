using System.Collections.Generic;

namespace UEToVS.Indexer.Parsing
{
    internal enum TokKind : byte
    {
        Ident,
        Number,
        Punct,
        String,
        Define, // "#define NAME": Text = NAME
    }

    internal struct Token
    {
        public TokKind Kind;
        public string Text;
        public int Line;
        public int Col;

        public bool Is(string s) => Text == s;
        public override string ToString() => Text;
    }

    /// <summary>
    /// Small C++ tokenizer for declaration scanning. Drops comments and preprocessor lines
    /// (except "#define NAME"), keeps strings as a single String token.
    /// </summary>
    internal static class CppLexer
    {
        public static List<Token> Tokenize(string src)
        {
            var toks = new List<Token>(src.Length / 6);
            int i = 0, line = 0, lineStart = 0, n = src.Length;
            bool atLineStart = true;

            while (i < n)
            {
                char c = src[i];
                if (c == '\n')
                {
                    line++;
                    i++;
                    lineStart = i;
                    atLineStart = true;
                    continue;
                }
                if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v')
                {
                    i++;
                    continue;
                }

                if (c == '#' && atLineStart)
                {
                    int start = i;
                    int dline = line, dcol = i - lineStart;
                    // read directive name
                    int j = i + 1;
                    while (j < n && (src[j] == ' ' || src[j] == '\t')) j++;
                    int ns = j;
                    while (j < n && char.IsLetter(src[j])) j++;
                    string directive = src.Substring(ns, j - ns);
                    if (directive == "define")
                    {
                        while (j < n && (src[j] == ' ' || src[j] == '\t')) j++;
                        int ids = j;
                        while (j < n && IsIdentChar(src[j])) j++;
                        if (j > ids)
                        {
                            toks.Add(new Token { Kind = TokKind.Define, Text = src.Substring(ids, j - ids), Line = dline, Col = ids - lineStart });
                        }
                    }
                    // skip to end of logical line (handle continuations and comments)
                    i = j;
                    while (i < n)
                    {
                        if (src[i] == '\\' && i + 1 < n && (src[i + 1] == '\n' || (src[i + 1] == '\r' && i + 2 < n && src[i + 2] == '\n')))
                        {
                            i += src[i + 1] == '\r' ? 3 : 2;
                            line++;
                            lineStart = i;
                            continue;
                        }
                        if (src[i] == '/' && i + 1 < n && src[i + 1] == '*')
                        {
                            i += 2;
                            while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == '/'))
                            {
                                if (src[i] == '\n') { line++; lineStart = i + 1; }
                                i++;
                            }
                            i += 2;
                            continue;
                        }
                        if (src[i] == '\n') break;
                        i++;
                    }
                    continue;
                }
                atLineStart = false;

                if (c == '/' && i + 1 < n)
                {
                    if (src[i + 1] == '/')
                    {
                        while (i < n && src[i] != '\n') i++;
                        continue;
                    }
                    if (src[i + 1] == '*')
                    {
                        i += 2;
                        while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == '/'))
                        {
                            if (src[i] == '\n') { line++; lineStart = i + 1; }
                            i++;
                        }
                        i += 2;
                        continue;
                    }
                }

                int col = i - lineStart;
                if (IsIdentStart(c))
                {
                    int s = i;
                    while (i < n && IsIdentChar(src[i])) i++;
                    string id = src.Substring(s, i - s);
                    // raw / prefixed string literals: R"(...)", u8"..", L"..", TEXT("") handled as normal call
                    if (i < n && (src[i] == '"' || src[i] == '\'') && IsStringPrefix(id))
                    {
                        if (id.EndsWith("R") && src[i] == '"')
                        {
                            i = SkipRawString(src, i, ref line, ref lineStart);
                        }
                        else
                        {
                            i = SkipQuoted(src, i, ref line, ref lineStart);
                        }
                        toks.Add(new Token { Kind = TokKind.String, Text = "\"\"", Line = line, Col = col });
                        continue;
                    }
                    toks.Add(new Token { Kind = TokKind.Ident, Text = Intern(id), Line = line, Col = col });
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int s = i;
                    while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '.' || src[i] == '\'' ||
                           ((src[i] == '+' || src[i] == '-') && (src[i - 1] == 'e' || src[i - 1] == 'E' || src[i - 1] == 'p' || src[i - 1] == 'P')))) i++;
                    toks.Add(new Token { Kind = TokKind.Number, Text = "0", Line = line, Col = col });
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(src, i, ref line, ref lineStart);
                    toks.Add(new Token { Kind = TokKind.String, Text = "\"\"", Line = line, Col = col });
                    continue;
                }
                if (c == ':' && i + 1 < n && src[i + 1] == ':')
                {
                    toks.Add(new Token { Kind = TokKind.Punct, Text = "::", Line = line, Col = col });
                    i += 2;
                    continue;
                }
                if (c == '-' && i + 1 < n && src[i + 1] == '>')
                {
                    toks.Add(new Token { Kind = TokKind.Punct, Text = "->", Line = line, Col = col });
                    i += 2;
                    continue;
                }
                toks.Add(new Token { Kind = TokKind.Punct, Text = PunctString(c), Line = line, Col = col });
                i++;
            }
            return toks;
        }

        private static bool IsStringPrefix(string id) =>
            id == "R" || id == "L" || id == "u" || id == "U" || id == "u8" || id == "LR" || id == "uR" || id == "UR" || id == "u8R";

        private static int SkipQuoted(string src, int i, ref int line, ref int lineStart)
        {
            char q = src[i++];
            while (i < src.Length)
            {
                char c = src[i];
                if (c == '\\') { i += 2; continue; }
                if (c == q) return i + 1;
                if (c == '\n') { line++; lineStart = i + 1; return i + 1; } // unterminated; recover
                i++;
            }
            return i;
        }

        private static int SkipRawString(string src, int i, ref int line, ref int lineStart)
        {
            int open = src.IndexOf('(', i);
            if (open < 0) return src.Length;
            string delim = ")" + src.Substring(i + 1, open - i - 1) + "\"";
            int end = src.IndexOf(delim, open, System.StringComparison.Ordinal);
            if (end < 0) end = src.Length - delim.Length;
            for (int k = i; k < end; k++)
                if (src[k] == '\n') { line++; lineStart = k + 1; }
            return end + delim.Length;
        }

        public static bool IsIdentStart(char c) => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c > 127 && char.IsLetter(c);

        public static bool IsIdentChar(char c) => IsIdentStart(c) || (c >= '0' && c <= '9');

        private static readonly string[] PunctCache = BuildPunct();

        private static string[] BuildPunct()
        {
            var arr = new string[128];
            for (int k = 0; k < 128; k++) arr[k] = ((char)k).ToString();
            return arr;
        }

        private static string PunctString(char c) => c < 128 ? PunctCache[c] : c.ToString();

        private static string Intern(string s) => s.Length <= 32 ? string.Intern(s) : s;
    }
}
