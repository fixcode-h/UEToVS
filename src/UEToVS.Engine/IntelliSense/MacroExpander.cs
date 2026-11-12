using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>A #define as seen by the expander.</summary>
    public sealed class MacroDefinition
    {
        public string Name { get; set; }

        /// <summary>Parameter names (null for object-like macros); "..." for variadic.</summary>
        public List<string> Parameters { get; set; }

        public string Body { get; set; } = string.Empty;
        public SymbolInfo Symbol { get; set; }
        public bool IsFunctionLike => Parameters != null;
    }

    /// <summary>
    /// Macro expansion for Quick Info: one level (the invocation's arguments substituted into the body) and the full
    /// rescan (nested macros expanded recursively, self-references blocked as the preprocessor does), with size and depth caps.
    /// Definitions come from the index and the declaring files' directive text (so long bodies are not truncated).
    /// </summary>
    public sealed class MacroExpander
    {
        public const int MaxChars = 4000;
        private const int MaxDepth = 24;
        private const int MaxExpansions = 400;

        private readonly IsSession _s;
        private readonly Dictionary<string, MacroDefinition> _defs = new Dictionary<string, MacroDefinition>(StringComparer.Ordinal);
        private int _expansions;

        public MacroExpander(IsSession session) => _s = session;

        /// <summary>The definition of macro <paramref name="name"/> (project / buffer definitions first), or null.</summary>
        public MacroDefinition Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_defs.TryGetValue(name, out var cached)) return cached;
            MacroDefinition def = null;
            var syms = _s.Ctx.ByName(name).Where(x => x.Kind == SymbolKind.Macro)
                .OrderBy(x => _s.Ctx.IsOverlayPath(x.Location?.FilePath) ? 0 : TypeText.IsProject(x.Origin) ? 1 : 2).ToList();
            foreach (var sym in syms)
            {
                var (doc, i) = _s.DeclOf(sym);
                def = doc != null ? FromDecl(doc, i, sym) : FromSignature(sym);
                if (def != null) break;
            }
            _defs[name] = def;
            return def;
        }

        private static MacroDefinition FromDecl(DocumentModel doc, int index, SymbolInfo sym)
        {
            var d = doc.Decls[index];
            var def = new MacroDefinition { Name = d.Name, Symbol = sym };
            if ((d.Modifiers & DeclModifiers.FunctionLike) != 0)
                def.Parameters = (d.Parameters ?? string.Empty).Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            // the full directive text (Value is capped at 200 characters)
            int ti = doc.TokenIndexFrom(d.Offset) - 1;
            string body = null;
            if (ti >= 0 && doc.Tokens[ti].Kind == TokenKind.Directive && doc.Tokens[ti].End > d.Offset)
            {
                var t = doc.Tokens[ti];
                string text = Encoding.UTF8.GetString(doc.Text, d.Offset, t.End - d.Offset);
                text = text.Replace("\\\r\n", " ").Replace("\\\n", " ");
                int p = d.Name.Length;
                if (def.IsFunctionLike && p < text.Length && text[p] == '(')
                {
                    int close = text.IndexOf(')', p);
                    p = close < 0 ? text.Length : close + 1;
                }
                body = StripComments(text.Substring(Math.Min(p, text.Length)));
            }
            def.Body = Collapse(body ?? d.Value ?? string.Empty);
            return def;
        }

        private static MacroDefinition FromSignature(SymbolInfo sym)
        {
            string sig = sym.Signature;
            if (sig == null || !sig.StartsWith("#define ", StringComparison.Ordinal)) return null;
            string rest = sig.Substring(8);
            var def = new MacroDefinition { Name = sym.Name, Symbol = sym };
            int p = sym.Name.Length;
            if (p < rest.Length && rest[p] == '(')
            {
                int close = rest.IndexOf(')', p);
                if (close < 0) close = rest.Length - 1;
                def.Parameters = rest.Substring(p + 1, close - p - 1).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                p = close + 1;
            }
            def.Body = Collapse(p < rest.Length ? rest.Substring(p) : string.Empty);
            return def;
        }

        private static string StripComments(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"' || s[i] == '\'')
                {
                    char q = s[i];
                    sb.Append(q);
                    for (i++; i < s.Length; i++)
                    {
                        sb.Append(s[i]);
                        if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                        if (s[i] == q) break;
                    }
                    continue;
                }
                if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/') break;
                if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    sb.Append(' ');
                    i = e < 0 ? s.Length : e + 1;
                    continue;
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static string Collapse(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Expands the invocation of <paramref name="def"/> with <paramref name="args"/> (null for object-like): one level, and fully.
        /// </summary>
        public (string OneLevel, string Full) Expand(MacroDefinition def, List<string> args)
        {
            _expansions = 0;
            string one = Substitute(def, args ?? new List<string>());
            var disabled = new HashSet<string>(StringComparer.Ordinal) { def.Name };
            string full = Rescan(one, disabled, 0);
            return (Cap(one), Cap(full));
        }

        private static string Cap(string s) => s.Length <= MaxChars ? s : s.Substring(0, MaxChars - 1) + "…";

        private static string Substitute(MacroDefinition def, List<string> args)
        {
            if (!def.IsFunctionLike) return def.Body;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < def.Parameters.Count; i++)
            {
                string p = def.Parameters[i];
                if (p == "..." || p.EndsWith("...", StringComparison.Ordinal))
                {
                    string va = string.Join(", ", args.Skip(i).Select(a => a.Trim()));
                    map[p == "..." ? "__VA_ARGS__" : p.Substring(0, p.Length - 3).Trim()] = va;
                    break;
                }
                map[p] = i < args.Count ? args[i].Trim() : string.Empty;
            }
            var toks = Lex(def.Body);
            var sb = new StringBuilder();
            for (int i = 0; i < toks.Count; i++)
            {
                var (text, ident) = toks[i];
                if (text == "#" && i + 1 < toks.Count && toks[i + 1].Ident && map.TryGetValue(toks[i + 1].Text, out var str))
                {
                    sb.Append('"').Append(str.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                    i++;
                    continue;
                }
                if (text == "##")
                {
                    while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                    // skip whitespace token after ##
                    while (i + 1 < toks.Count && toks[i + 1].Text == " ") i++;
                    continue;
                }
                if (ident && map.TryGetValue(text, out var arg)) sb.Append(arg);
                else sb.Append(text);
            }
            return Collapse(sb.ToString());
        }

        private string Rescan(string text, HashSet<string> disabled, int depth)
        {
            if (depth > MaxDepth || text.Length > MaxChars * 4) return text;
            var toks = Lex(text);
            var sb = new StringBuilder(text.Length);
            bool changed = false;
            for (int i = 0; i < toks.Count; i++)
            {
                var (t, ident) = toks[i];
                if (!ident || disabled.Contains(t) || _expansions >= MaxExpansions || sb.Length > MaxChars * 2)
                {
                    sb.Append(t);
                    continue;
                }
                var def = Find(t);
                if (def == null)
                {
                    sb.Append(t);
                    continue;
                }
                List<string> args = null;
                int next = i;
                if (def.IsFunctionLike)
                {
                    int j = i + 1;
                    while (j < toks.Count && toks[j].Text == " ") j++;
                    if (j >= toks.Count || toks[j].Text != "(")
                    {
                        sb.Append(t);
                        continue;
                    }
                    args = new List<string>();
                    int depthP = 0;
                    var cur = new StringBuilder();
                    for (; j < toks.Count; j++)
                    {
                        string x = toks[j].Text;
                        if (x == "(") { if (depthP++ > 0) cur.Append(x); continue; }
                        if (x == ")")
                        {
                            if (--depthP == 0) { args.Add(cur.ToString()); break; }
                            cur.Append(x);
                            continue;
                        }
                        if (x == "," && depthP == 1) { args.Add(cur.ToString()); cur.Clear(); continue; }
                        cur.Append(x);
                    }
                    if (depthP != 0) { sb.Append(t); continue; }
                    if (args.Count == 1 && args[0].Trim().Length == 0 && def.Parameters.Count == 0) args.Clear();
                    next = j;
                }
                _expansions++;
                var inner = new HashSet<string>(disabled, StringComparer.Ordinal) { t };
                sb.Append(Rescan(Substitute(def, args ?? new List<string>()), inner, depth + 1));
                i = next;
                changed = true;
            }
            return changed ? Collapse(sb.ToString()) : text;
        }

        /// <summary>Tiny preprocessing-token lexer over a string: identifiers, numbers, string/char literals, '##', single characters, spaces.</summary>
        private static List<(string Text, bool Ident)> Lex(string s)
        {
            var list = new List<(string, bool)>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                    list.Add((" ", false));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    // L"..." / TEXT-like prefixes stay attached to the literal
                    list.Add((s.Substring(i, j - i), true));
                    i = j;
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int j = i;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '.' || s[j] == '_')) j++;
                    list.Add((s.Substring(i, j - i), false));
                    i = j;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    int j = i + 1;
                    while (j < s.Length && s[j] != c) { if (s[j] == '\\') j++; j++; }
                    j = Math.Min(s.Length, j + 1);
                    list.Add((s.Substring(i, j - i), false));
                    i = j;
                    continue;
                }
                if (c == '#' && i + 1 < s.Length && s[i + 1] == '#')
                {
                    list.Add(("##", false));
                    i += 2;
                    continue;
                }
                list.Add((c.ToString(), false));
                i++;
            }
            return list;
        }

        /// <summary>Raw argument texts of the invocation whose name is token <paramref name="nameToken"/> (null when not followed by '(').</summary>
        public static List<string> InvocationArgs(DocumentModel doc, int nameToken)
        {
            int open = nameToken + 1;
            if (!doc.Is(open, Punct.LParen)) return null;
            var args = new List<string>();
            int depth = 0, start = doc.Tokens[open].End;
            for (int k = open; k < doc.Tokens.Length && k < open + 2000; k++)
            {
                var t = doc.Tokens[k];
                if (t.Is(Punct.LParen) || t.Is(Punct.LBracket) || t.Is(Punct.LBrace)) depth++;
                else if (t.Is(Punct.RParen) || t.Is(Punct.RBracket) || t.Is(Punct.RBrace))
                {
                    if (--depth == 0)
                    {
                        string last = Encoding.UTF8.GetString(doc.Text, start, t.Start - start);
                        if (args.Count > 0 || last.Trim().Length > 0) args.Add(last);
                        return args;
                    }
                }
                else if (t.Is(Punct.Comma) && depth == 1)
                {
                    args.Add(Encoding.UTF8.GetString(doc.Text, start, t.Start - start));
                    start = t.End;
                }
            }
            return args;
        }
    }
}
