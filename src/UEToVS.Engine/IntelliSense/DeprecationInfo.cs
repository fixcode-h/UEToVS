using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using UEToVS.Contracts;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// Whether a declaration is deprecated, and how (v0.4.5). This is the single place that decides "is this symbol
    /// deprecated" — the current-file hints (DiagnosticsProvider UE0011, Quick Info) and the project-wide upgrade assistant
    /// use the same rules:
    /// <list type="bullet">
    /// <item><c>UE_DEPRECATED(5.1, "msg")</c>, <c>UE_DEPRECATED_FORGAME(...)</c>, <c>UE_DEPRECATED_FORENGINE(...)</c>, <c>DEPRECATED(4.x, "msg")</c>
    /// and <c>[[deprecated("msg")]]</c> / <c>[[deprecated]]</c> anywhere in the declaration head (before the name, e.g. above the
    /// function, or between <c>class</c> and the class name);</item>
    /// <item><c>UFUNCTION(meta = (DeprecatedFunction, DeprecationMessage = "msg"))</c> and <c>UPROPERTY(meta = (DeprecatedProperty, ...))</c>;</item>
    /// <item>the UE convention of renaming a removed property to <c>Name_DEPRECATED</c>.</item>
    /// </list>
    /// <see cref="Replacement"/> is the identifier the message points to ("Use X instead", "Please use X", "X should be used
    /// instead", "请使用 X", "改用 X"), stripped of "()" and of a class qualifier; whether it is a mechanical rename (same
    /// container) is up to the caller. Results are cached per parsed document (<see cref="Of(DocumentModel, int)"/>). Pure, thread-safe.
    /// </summary>
    public sealed class DeprecationInfo
    {
        public const string MacroUeDeprecated = "UE_DEPRECATED";
        public const string MacroAttribute = "[[deprecated]]";
        public const string MacroMeta = "meta";
        public const string MacroSuffix = "_DEPRECATED";

        private const int MaxHeadTokens = 160;

        private static readonly HashSet<string> Macros = new HashSet<string>(StringComparer.Ordinal)
        {
            "UE_DEPRECATED", "UE_DEPRECATED_FORGAME", "UE_DEPRECATED_FORENGINE", "DEPRECATED", "UE_DEPRECATED_ALWAYS",
        };

        private static readonly Regex[] ReplacementPatterns =
        {
            new Regex(@"\b(?:use|call)\s+(?:the\s+)?(?:new\s+)?(?:function\s+|method\s+|property\s+|overload\s+)?'?`?(?<id>[A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)*)`?'?(?:\s*\([^)]*\))?\s*(?:,\s*)?instead", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex(@"\bplease\s+use\s+'?`?(?<id>[A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex(@"'?`?(?<id>[A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)*)`?'?(?:\s*\(\))?\s+should\s+be\s+used\s+instead", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex(@"\breplaced\s+(?:by|with)\s+'?`?(?<id>[A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex(@"(?:请使用|请改用|改用|请用)\s*'?`?(?<id>[A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)*)", RegexOptions.CultureInvariant),
        };

        private static readonly ConditionalWeakTable<DocumentModel, Dictionary<int, DeprecationInfo>> Cache =
            new ConditionalWeakTable<DocumentModel, Dictionary<int, DeprecationInfo>>();

        /// <summary>Shared "not deprecated" marker for the cache.</summary>
        private static readonly DeprecationInfo None = new DeprecationInfo();

        /// <summary>Engine version the API was deprecated in ("5.1"), or null.</summary>
        public string Version { get; private set; }

        /// <summary>The deprecation message (unquoted), or null.</summary>
        public string Message { get; private set; }

        /// <summary>How it was marked: <see cref="MacroUeDeprecated"/>-family macro name, <see cref="MacroAttribute"/>, <see cref="MacroMeta"/> or <see cref="MacroSuffix"/>.</summary>
        public string Macro { get; private set; }

        /// <summary>The identifier the message says to use instead (last segment of a qualified name), or null.</summary>
        public string Replacement { get; private set; }

        public override string ToString() => $"{Macro}({Version}, \"{Message}\") → {Replacement}";

        public static DeprecationInfo Create(string macro, string version, string message)
        {
            message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
            return new DeprecationInfo { Macro = macro, Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(), Message = message, Replacement = ReplacementFrom(message) };
        }

        /// <summary>The deprecation of declaration <paramref name="index"/> of <paramref name="doc"/>, or null (cached per document).</summary>
        public static DeprecationInfo Of(DocumentModel doc, int index)
        {
            if (doc == null || index < 0 || index >= doc.Decls.Count) return null;
            var map = Cache.GetValue(doc, _ => new Dictionary<int, DeprecationInfo>());
            lock (map)
            {
                if (map.TryGetValue(index, out var cached)) return ReferenceEquals(cached, None) ? null : cached;
            }
            var info = Compute(doc, index);
            lock (map) map[index] = info ?? None;
            return info;
        }

        /// <summary>The deprecation of an index symbol (through its declaring document), or null.</summary>
        public static DeprecationInfo Of(IsSession s, SymbolInfo symbol)
        {
            if (s == null || symbol == null) return null;
            if (symbol.Name != null && symbol.Name.EndsWith(MacroSuffix, StringComparison.Ordinal) && symbol.Name.Length > MacroSuffix.Length)
                return Create(MacroSuffix, null, null);
            var (doc, i) = s.DeclOf(symbol);
            return doc == null ? null : Of(doc, i);
        }

        private static DeprecationInfo Compute(DocumentModel doc, int index)
        {
            var d = doc.Decls[index];
            if (string.IsNullOrEmpty(d.Name)) return null;
            if (d.Name.EndsWith(MacroSuffix, StringComparison.Ordinal) && d.Name.Length > MacroSuffix.Length) return Create(MacroSuffix, null, null);
            var fromMeta = FromSpecifiers(d.Specifiers);
            int name = doc.TokenIndexFrom(d.Offset);
            if (name < 0 || name >= doc.Tokens.Length) return fromMeta;
            int stop = Math.Max(0, name - MaxHeadTokens);
            for (int k = name - 1; k >= stop; k--)
            {
                var t = doc.Tokens[k];
                if (t.Kind == TokenKind.DocComment) continue;
                if (t.Kind == TokenKind.Directive || t.Is(Punct.Semicolon) || t.Is(Punct.LBrace) || t.Is(Punct.RBrace)) break;
                if (t.Is(Punct.Colon) && (doc.Is(k - 1, Keyword.Public) || doc.Is(k - 1, Keyword.Protected) || doc.Is(k - 1, Keyword.Private))) break;
                if (t.Is(Punct.RParen))
                {
                    int open = doc.MatchBackward(k, Punct.LParen, Punct.RParen);
                    if (open < 0) break;
                    int macro = open - 1;
                    if (doc.IsIdentifier(macro) && Macros.Contains(doc.TokenText(macro)))
                    {
                        var args = Arguments(doc, open, k);
                        string version = args.Count >= 2 ? args[0] : null;
                        string message = args.Count >= 2 ? Unquote(args[1]) : args.Count == 1 ? Unquote(args[0]) : null;
                        if (args.Count == 1 && !LooksQuoted(args[0])) { version = args[0]; message = null; }
                        return Create(doc.TokenText(macro), version, message);
                    }
                    k = open;
                    continue;
                }
                if (t.Is(Punct.RBracket))
                {
                    int open = doc.MatchBackward(k, Punct.LBracket, Punct.RBracket);
                    if (open < 0) break;
                    var attr = Attribute(doc, open, k);
                    if (attr != null) return attr;
                    k = open;
                }
            }
            return fromMeta;
        }

        /// <summary><c>[[deprecated]]</c> / <c>[[deprecated("msg")]]</c> / <c>[[nodiscard, deprecated("msg")]]</c> between the brackets.</summary>
        private static DeprecationInfo Attribute(DocumentModel doc, int open, int close)
        {
            for (int k = open + 1; k < close; k++)
            {
                if (!doc.IsIdentifier(k) || doc.TokenText(k) != "deprecated") continue;
                if (doc.Is(k + 1, Punct.LParen))
                {
                    int end = k + 1, depth = 0;
                    for (; end < close; end++)
                    {
                        if (doc.Is(end, Punct.LParen)) depth++;
                        else if (doc.Is(end, Punct.RParen) && --depth == 0) break;
                    }
                    var args = Arguments(doc, k + 1, end);
                    return Create(MacroAttribute, null, args.Count > 0 ? Unquote(args[0]) : null);
                }
                return Create(MacroAttribute, null, null);
            }
            return null;
        }

        /// <summary>Top-level arguments between '(' at <paramref name="open"/> and ')' at <paramref name="close"/>, as source text (strings unquoted later).</summary>
        private static List<string> Arguments(DocumentModel doc, int open, int close)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            for (int k = open + 1; k < close && k < doc.Tokens.Length; k++)
            {
                var t = doc.Tokens[k];
                if (t.Is(Punct.LParen) || t.Is(Punct.LBracket) || t.Is(Punct.LBrace)) depth++;
                else if (t.Is(Punct.RParen) || t.Is(Punct.RBracket) || t.Is(Punct.RBrace)) depth--;
                if (t.Is(Punct.Comma) && depth == 0)
                {
                    list.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }
                if (sb.Length > 0 && t.Kind == TokenKind.String && sb[sb.Length - 1] == '"') sb.Append(' '); // "a" "b"
                else if (sb.Length > 0 && (t.Flags & TokenFlags.PrecededBySpace) != 0) sb.Append(' ');
                sb.Append(doc.TokenText(k));
            }
            if (sb.Length > 0 || list.Count > 0) list.Add(sb.ToString().Trim());
            return list;
        }

        private static bool LooksQuoted(string s) => s != null && (s.StartsWith("\"", StringComparison.Ordinal) || s.StartsWith("TEXT(", StringComparison.Ordinal));

        /// <summary><c>"a" "b"</c> → <c>ab</c>; <c>TEXT("x")</c> → <c>x</c>; escapes \" \\ \n \t; anything else unchanged.</summary>
        public static string Unquote(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.StartsWith("TEXT(", StringComparison.Ordinal) && s.EndsWith(")", StringComparison.Ordinal)) s = s.Substring(5, s.Length - 6).Trim();
            if (!s.StartsWith("\"", StringComparison.Ordinal)) return s;
            var sb = new StringBuilder(s.Length);
            bool inside = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!inside)
                {
                    if (c == '"') inside = true;
                    continue;
                }
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 'n' ? ' ' : n == 't' ? ' ' : n);
                    continue;
                }
                if (c == '"')
                {
                    inside = false;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary><c>meta = (DeprecatedFunction, DeprecationMessage = "msg")</c> / <c>DeprecatedProperty</c> in a reflection macro's specifiers.</summary>
        public static DeprecationInfo FromSpecifiers(string specifiers)
        {
            if (string.IsNullOrEmpty(specifiers)) return null;
            if (specifiers.IndexOf("DeprecatedFunction", StringComparison.Ordinal) < 0 && specifiers.IndexOf("DeprecatedProperty", StringComparison.Ordinal) < 0) return null;
            var m = Regex.Match(specifiers, "DeprecationMessage\\s*=\\s*\"(?<m>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
            return Create(MacroMeta, null, m.Success ? Unquote("\"" + m.Groups["m"].Value + "\"") : null);
        }

        /// <summary>The identifier a deprecation message points to (see the class remarks), or null.</summary>
        public static string ReplacementFrom(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;
            foreach (var re in ReplacementPatterns)
            {
                var m = re.Match(message);
                if (!m.Success) continue;
                string id = m.Groups["id"].Value;
                int q = id.LastIndexOf("::", StringComparison.Ordinal);
                if (q >= 0) id = id.Substring(q + 2);
                if (id.Length == 0 || IsWord(id)) continue;
                return id;
            }
            return null;
        }

        /// <summary>English filler a pattern may catch ("use this instead", "use it instead").</summary>
        private static bool IsWord(string id)
        {
            switch (id.ToLowerInvariant())
            {
                case "this": case "it": case "that": case "these": case "those": case "the": case "a": case "an": case "one": case "something":
                case "another": case "other": case "others": case "them":
                    return true;
                default:
                    return false;
            }
        }
    }
}
