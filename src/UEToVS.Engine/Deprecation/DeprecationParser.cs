using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UEToVS.Engine.Index;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.Deprecation
{
    /// <summary>Which spelling marked a declaration as deprecated.</summary>
    public enum DeprecationMacro : byte
    {
        /// <summary><c>UE_DEPRECATED(5.1, "Use X instead.")</c>.</summary>
        UeDeprecated = 0,
        /// <summary><c>UE_DEPRECATED_FORGAME(5.1, "…")</c> (deprecated for game code only).</summary>
        UeDeprecatedForGame = 1,
        /// <summary><c>[[deprecated]]</c> / <c>[[deprecated("…")]]</c>.</summary>
        CxxAttribute = 2,
        /// <summary>UE4's <c>DEPRECATED(4.22, "…")</c>.</summary>
        Legacy = 3,
    }

    /// <summary>One deprecation marker found in a file: kind, version (empty for the C++ attribute), message and byte range.</summary>
    public readonly struct DeprecationMark
    {
        public DeprecationMark(DeprecationMacro kind, string version, string message, int start, int end)
        {
            Kind = kind;
            Version = version ?? string.Empty;
            Message = message ?? string.Empty;
            Start = start;
            End = end;
        }

        public DeprecationMacro Kind { get; }
        public string Version { get; }
        public string Message { get; }

        /// <summary>Byte offset of the macro / the first '[' of the attribute.</summary>
        public int Start { get; }

        /// <summary>Byte offset just after the closing ')' / ']]'.</summary>
        public int End { get; }

        public override string ToString() => Kind + "(" + Version + ", \"" + Message + "\")@" + Start;
    }

    /// <summary>
    /// Stand-alone, reusable deprecation parsing (no index, no engine state): finds <c>UE_DEPRECATED(ver, msg)</c>,
    /// <c>UE_DEPRECATED_FORGAME(ver, msg)</c>, <c>DEPRECATED(ver, msg)</c> and <c>[[deprecated("msg")]]</c> markers in UTF-8
    /// source, attaches each one to the declaration it precedes, suggests the replacement named in the message and compares
    /// UE versions. Used by the indexer (markers go into the index) and by the upgrade assistant; the per-file deprecation
    /// hint can use it as is. Thread-safe (stateless).
    /// </summary>
    public static class DeprecationParser
    {
        private static readonly byte[] UpperMarker = Encoding.ASCII.GetBytes("DEPRECATED");
        private static readonly byte[] LowerMarker = Encoding.ASCII.GetBytes("deprecated");

        /// <summary>Cheap pre-check (vectorised byte search): false means the file has no marker and needs no lexing.</summary>
        public static bool MayContain(ReadOnlySpan<byte> utf8) => utf8.IndexOf(UpperMarker) >= 0 || utf8.IndexOf(LowerMarker) >= 0;

        /// <summary>All markers of a file, in source order. Markers inside <c>#define</c> lines and comments are ignored.</summary>
        public static List<DeprecationMark> Scan(ReadOnlySpan<byte> utf8)
        {
            var result = new List<DeprecationMark>();
            if (!MayContain(utf8)) return result;
            var tokens = Lex(utf8);
            ScanTokens(utf8, tokens, result);
            return result;
        }

        /// <summary>
        /// The marker of each declaration (index-aligned with <paramref name="decls"/>, null = not deprecated), or null when the
        /// file has no marker at all (the common case: one byte search, no allocation). A marker belongs to the first
        /// declaration whose name starts after it, unless a <c>;</c>, <c>{</c> or <c>}</c> (outside parentheses) lies in between.
        /// </summary>
        public static DeclDeprecation[] Attach(ReadOnlySpan<byte> utf8, IReadOnlyList<Declaration> decls)
        {
            if (decls == null || decls.Count == 0 || !MayContain(utf8)) return null;
            var tokens = Lex(utf8);
            var marks = new List<DeprecationMark>();
            ScanTokens(utf8, tokens, marks);
            if (marks.Count == 0) return null;
            var order = new List<int>(decls.Count);
            for (int i = 0; i < decls.Count; i++)
                if (!decls[i].IsImplicit && !string.IsNullOrEmpty(decls[i].Name)) order.Add(i);
            order.Sort((a, b) => decls[a].Offset != decls[b].Offset ? decls[a].Offset.CompareTo(decls[b].Offset) : a.CompareTo(b));
            var result = new DeclDeprecation[decls.Count];
            bool any = false;
            int di = 0;
            foreach (var m in marks)
            {
                while (di < order.Count && decls[order[di]].Offset < m.End) di++;
                if (di >= order.Count) break;
                int target = order[di];
                if (result[target] != null || !NothingBetween(tokens, m.End, decls[target].Offset)) continue;
                result[target] = new DeclDeprecation(m.Version, m.Message);
                any = true;
            }
            return any ? result : null;
        }

        private static List<Token> Lex(ReadOnlySpan<byte> utf8)
        {
            var tokens = new List<Token>(Math.Max(64, utf8.Length / 6));
            var lexer = new CppLexer(utf8);
            while (true)
            {
                var t = lexer.Next();
                if (t.Kind == TokenKind.EndOfFile) break;
                if (t.Kind == TokenKind.DocComment || t.Kind == TokenKind.Comment || t.Kind == TokenKind.Directive) continue;
                tokens.Add(t);
            }
            return tokens;
        }

        private static bool NothingBetween(List<Token> tokens, int from, int to)
        {
            int i = FirstTokenAtOrAfter(tokens, from);
            int depth = 0;
            for (; i < tokens.Count && tokens[i].Start < to; i++)
            {
                var t = tokens[i];
                if (t.Kind != TokenKind.Punctuator) continue;
                switch ((Punct)t.Sub)
                {
                    case Punct.LParen: depth++; break;
                    case Punct.RParen: depth--; break;
                    case Punct.Semicolon:
                    case Punct.LBrace:
                    case Punct.RBrace:
                        if (depth <= 0) return false;
                        break;
                }
            }
            return true;
        }

        private static int FirstTokenAtOrAfter(List<Token> tokens, int offset)
        {
            int lo = 0, hi = tokens.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (tokens[mid].Start < offset) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static bool Is(List<Token> tokens, int i, Punct p) =>
            i >= 0 && i < tokens.Count && tokens[i].Kind == TokenKind.Punctuator && tokens[i].Sub == (byte)p;

        private static string Text(ReadOnlySpan<byte> utf8, Token t) => Encoding.UTF8.GetString(utf8.Slice(t.Start, t.Length));

        private static bool IdentIs(ReadOnlySpan<byte> utf8, Token t, byte[] word) =>
            t.Kind == TokenKind.Identifier && t.Length == word.Length && utf8.Slice(t.Start, t.Length).SequenceEqual(word);

        private static readonly byte[] WUeDeprecated = Encoding.ASCII.GetBytes("UE_DEPRECATED");
        private static readonly byte[] WUeDeprecatedForGame = Encoding.ASCII.GetBytes("UE_DEPRECATED_FORGAME");
        private static readonly byte[] WLegacy = Encoding.ASCII.GetBytes("DEPRECATED");
        private static readonly byte[] WAttr = Encoding.ASCII.GetBytes("deprecated");

        private static void ScanTokens(ReadOnlySpan<byte> utf8, List<Token> tokens, List<DeprecationMark> result)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind == TokenKind.Identifier)
                {
                    DeprecationMacro kind;
                    if (IdentIs(utf8, t, WUeDeprecated)) kind = DeprecationMacro.UeDeprecated;
                    else if (IdentIs(utf8, t, WUeDeprecatedForGame)) kind = DeprecationMacro.UeDeprecatedForGame;
                    else if (IdentIs(utf8, t, WLegacy)) kind = DeprecationMacro.Legacy;
                    else continue;
                    if (!Is(tokens, i + 1, Punct.LParen)) continue;
                    var args = Arguments(tokens, i + 1, out int close);
                    if (close < 0) continue;
                    string version = args.Count > 0 ? Joined(utf8, tokens, args[0]) : string.Empty;
                    string message = args.Count > 1 ? Message(utf8, tokens, args[1]) : string.Empty;
                    result.Add(new DeprecationMark(kind, version, message, t.Start, tokens[close].Start + tokens[close].Length));
                    i = close;
                }
                else if (Is(tokens, i, Punct.LBracket) && Is(tokens, i + 1, Punct.LBracket))
                {
                    // [[attr, attr(args), deprecated("msg")]]
                    int depth = 0;
                    int j = i + 2;
                    string message = null;
                    bool found = false;
                    for (; j < tokens.Count; j++)
                    {
                        if (Is(tokens, j, Punct.RBracket) && depth == 0 && Is(tokens, j + 1, Punct.RBracket)) break;
                        if (Is(tokens, j, Punct.LParen)) depth++;
                        else if (Is(tokens, j, Punct.RParen)) depth--;
                        else if (depth == 0 && IdentIs(utf8, tokens[j], WAttr) && !Is(tokens, j - 1, Punct.ColonColon))
                        {
                            found = true;
                            if (Is(tokens, j + 1, Punct.LParen))
                            {
                                var args = Arguments(tokens, j + 1, out int close);
                                if (close > 0)
                                {
                                    message = args.Count > 0 ? Message(utf8, tokens, args[0]) : string.Empty;
                                    j = close;
                                }
                            }
                        }
                        if (j - i > 400) break;
                    }
                    if (found && j + 1 < tokens.Count && Is(tokens, j + 1, Punct.RBracket))
                        result.Add(new DeprecationMark(DeprecationMacro.CxxAttribute, string.Empty, message ?? string.Empty, t.Start, tokens[j + 1].Start + 1));
                    i = Math.Max(i, j);
                }
            }
        }

        /// <summary>Top-level argument token ranges [from, to) of the parenthesis at <paramref name="open"/>; <paramref name="close"/> = matching ')'.</summary>
        private static List<(int From, int To)> Arguments(List<Token> tokens, int open, out int close)
        {
            var args = new List<(int, int)>(2);
            close = -1;
            int depth = 0, start = open + 1;
            for (int k = open; k < tokens.Count && k < open + 2000; k++)
            {
                if (Is(tokens, k, Punct.LParen) || Is(tokens, k, Punct.LBracket) || Is(tokens, k, Punct.LBrace)) depth++;
                else if (Is(tokens, k, Punct.RParen) || Is(tokens, k, Punct.RBracket) || Is(tokens, k, Punct.RBrace))
                {
                    if (--depth == 0)
                    {
                        if (k > start || args.Count > 0) args.Add((start, k));
                        close = k;
                        return args;
                    }
                }
                else if (depth == 1 && Is(tokens, k, Punct.Comma))
                {
                    args.Add((start, k));
                    start = k + 1;
                }
            }
            return args;
        }

        private static string Joined(ReadOnlySpan<byte> utf8, List<Token> tokens, (int From, int To) range)
        {
            var sb = new StringBuilder();
            for (int k = range.From; k < range.To; k++) sb.Append(Text(utf8, tokens[k]));
            return sb.ToString().Trim();
        }

        /// <summary>Concatenated string literals of an argument (adjacent literals, TEXT("…") and prefixes handled); escapes resolved.</summary>
        private static string Message(ReadOnlySpan<byte> utf8, List<Token> tokens, (int From, int To) range)
        {
            var sb = new StringBuilder();
            for (int k = range.From; k < range.To; k++)
            {
                if (tokens[k].Kind != TokenKind.String) continue;
                sb.Append(Unquote(Text(utf8, tokens[k])));
            }
            return sb.ToString().Trim();
        }

        internal static string Unquote(string literal)
        {
            int q = literal.IndexOf('"');
            if (q < 0) return literal;
            int end = literal.LastIndexOf('"');
            if (end <= q) return string.Empty;
            string body = literal.Substring(q + 1, end - q - 1);
            if (q > 0 && literal[q - 1] == 'R')
            {
                // raw string R"delim( … )delim"
                int lp = body.IndexOf('('), rp = body.LastIndexOf(')');
                return lp >= 0 && rp > lp ? body.Substring(lp + 1, rp - lp - 1) : body;
            }
            if (body.IndexOf('\\') < 0) return body;
            var sb = new StringBuilder(body.Length);
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c != '\\' || i + 1 >= body.Length) { sb.Append(c); continue; }
                char n = body[++i];
                switch (n)
                {
                    case 'n': sb.Append(' '); break;
                    case 't': sb.Append(' '); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ replacement suggestion

        private static readonly Regex ReplacementPattern = new Regex(
            @"(?:\buse\b|\bcall\b|\breplaced\s+(?:with|by)\b|\brenamed\s+to\b|\bin\s+favou?r\s+of\b|\bswitch\s+to\b|\bmigrate\s+to\b|\bprefer\b|\binstead\s+use\b)" +
            @"\s+(?:the\s+)?(?:new\s+)?(?:function\s+|method\s+|version\s+|overload\s+|property\s+|class\s+|struct\s+|type\s+|member\s+)?" +
            @"['""`]?(?<name>(?:[A-Za-z_][A-Za-z0-9_]*::)*~?[A-Za-z_][A-Za-z0-9_]*)(?<call>\s*\()?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly HashSet<string> NotIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "this", "that", "these", "those", "it", "instead", "of", "to", "in", "any", "other", "another", "new", "and", "or",
            "with", "for", "when", "if", "is", "are", "be", "no", "not", "one", "version", "overload", "function", "method", "which", "its", "your",
        };

        /// <summary>
        /// The replacement a deprecation message names (<c>"Use GetActorLabel instead."</c> → <c>GetActorLabel</c>,
        /// <c>"Please use UGameplayStatics::Foo()"</c> → <c>UGameplayStatics::Foo</c>), or null. Only identifier-looking words
        /// (an upper-case letter, '_', '::' or a following '(') count, so "use the new system" yields nothing.
        /// </summary>
        public static string SuggestReplacement(string message, string deprecatedName = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;
            foreach (Match m in ReplacementPattern.Matches(message))
            {
                string name = m.Groups["name"].Value;
                if (name.Length == 0 || NotIdentifiers.Contains(name)) continue;
                bool looksLikeCode = m.Groups["call"].Success || name.Contains("::") || name.IndexOf('_') >= 0 || HasUpper(name);
                if (!looksLikeCode) continue;
                if (deprecatedName != null && string.Equals(LastSegment(name), deprecatedName, StringComparison.Ordinal)) continue;
                return name;
            }
            return null;
        }

        private static bool HasUpper(string s)
        {
            foreach (char c in s) if (char.IsUpper(c)) return true;
            return false;
        }

        public static string LastSegment(string qualified)
        {
            if (string.IsNullOrEmpty(qualified)) return qualified;
            int i = qualified.LastIndexOf("::", StringComparison.Ordinal);
            return i < 0 ? qualified : qualified.Substring(i + 2);
        }

        // ------------------------------------------------------------------ versions

        /// <summary>Orders UE versions numerically ("4.27" &lt; "5.0" &lt; "5.10"); empty / unparsable versions sort first.</summary>
        public static int CompareVersions(string a, string b)
        {
            var pa = VersionParts(a);
            var pb = VersionParts(b);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return string.CompareOrdinal(a ?? string.Empty, b ?? string.Empty);
        }

        private static int[] VersionParts(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return Array.Empty<int>();
            var parts = v.Trim().Split('.');
            var r = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                r[i] = int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1;
            return r;
        }
    }
}
