using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Snippets
{
    /// <summary>A postfix template: <c>expr.key</c> → <see cref="Body"/> with <c>$EXPR$</c> standing for the expression.</summary>
    public sealed class PostfixTemplate
    {
        public PostfixTemplate(string key, bool statement, string titleZh, string titleEn, string body)
        {
            Key = key;
            Statement = statement;
            TitleZh = titleZh;
            TitleEn = titleEn;
            Body = body;
        }

        public string Key { get; }

        /// <summary>Statement templates (if / for / return …) only apply when the expression starts the line.</summary>
        public bool Statement { get; }

        public string TitleZh { get; }
        public string TitleEn { get; }
        public string Body { get; }
        public string Title => Loc.T(TitleZh, TitleEn);
    }

    /// <summary>Where a postfix template applies: <c>expr.key</c> on a line.</summary>
    public sealed class PostfixSite
    {
        /// <summary>Column of the expression's first character.</summary>
        public int ExpressionStart { get; set; }

        public string Expression { get; set; }

        /// <summary>Column of the '.' before the key.</summary>
        public int Dot { get; set; }

        /// <summary>The key typed so far (may be empty right after the '.').</summary>
        public string Typed { get; set; }

        /// <summary>The expression is the first thing on the line (statement templates allowed).</summary>
        public bool AtLineStart { get; set; }
    }

    /// <summary>Rider-style postfix templates (expr.if, expr.notnull, expr.cast, expr.isvalid, expr.for …). Pure.</summary>
    public static class PostfixTemplates
    {
        public static IReadOnlyList<PostfixTemplate> All { get; } = new List<PostfixTemplate>
        {
            new PostfixTemplate("if", true, "if (expr)", "if (expr)", "if ($EXPR$)\n{\n\t$END$\n}"),
            new PostfixTemplate("notnull", true, "if (expr != nullptr)", "if (expr != nullptr)", "if ($EXPR$ != nullptr)\n{\n\t$END$\n}"),
            new PostfixTemplate("null", true, "if (expr == nullptr)", "if (expr == nullptr)", "if ($EXPR$ == nullptr)\n{\n\t$END$\n}"),
            new PostfixTemplate("ifvalid", true, "if (IsValid(expr))", "if (IsValid(expr))", "if (IsValid($EXPR$))\n{\n\t$END$\n}"),
            new PostfixTemplate("for", true, "for (auto& Item : expr)", "for (auto& Item : expr)", "for ($Type=auto$& $Item=Item$ : $EXPR$)\n{\n\t$END$\n}"),
            new PostfixTemplate("return", true, "return expr;", "return expr;", "return $EXPR$;$END$"),
            new PostfixTemplate("var", true, "auto Name = expr;", "auto Name = expr;", "$Type=auto$ $Name=Value$ = $EXPR$;$END$"),
            new PostfixTemplate("check", true, "check(expr);", "check(expr);", "check($EXPR$);$END$"),
            new PostfixTemplate("ensure", true, "if (ensure(expr))", "if (ensure(expr))", "if (ensure($EXPR$))\n{\n\t$END$\n}"),
            new PostfixTemplate("cast", false, "Cast<T>(expr)", "Cast<T>(expr)", "Cast<$Type=UObject$>($EXPR$)$END$"),
            new PostfixTemplate("isvalid", false, "IsValid(expr)", "IsValid(expr)", "IsValid($EXPR$)$END$"),
            new PostfixTemplate("valid", false, "IsValid(expr)", "IsValid(expr)", "IsValid($EXPR$)$END$"),
            new PostfixTemplate("not", false, "!expr", "!expr", "!$EXPR$$END$"),
            new PostfixTemplate("par", false, "(expr)", "(expr)", "($EXPR$)$END$"),
            new PostfixTemplate("text", false, "TEXT(expr)", "TEXT(expr)", "TEXT($EXPR$)$END$"),
        };

        public static PostfixTemplate Find(string key) => All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));

        /// <summary>Templates usable at <paramref name="site"/> whose key starts with the typed text.</summary>
        public static IEnumerable<PostfixTemplate> For(PostfixSite site) =>
            site == null ? Enumerable.Empty<PostfixTemplate>()
                : All.Where(t => (site.AtLineStart || !t.Statement) && t.Key.StartsWith(site.Typed ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// <c>expr.key|</c> with the caret after the key: finds the expression before the '.'. The expression is a chain of
        /// identifiers, <c>::</c>, <c>.</c>, <c>-&gt;</c>, calls / subscripts / template arguments with balanced brackets, string
        /// literals and numbers, optionally preceded by unary <c>*</c> / <c>!</c> / <c>&amp;</c>. Null in comments and strings.
        /// </summary>
        public static PostfixSite Analyze(string line, int caret)
        {
            if (string.IsNullOrEmpty(line) || caret <= 0 || caret > line.Length) return null;
            int k = caret;
            while (k > 0 && (char.IsLetterOrDigit(line[k - 1]) || line[k - 1] == '_')) k--;
            if (k == 0 || line[k - 1] != '.') return null;
            if (caret < line.Length && (char.IsLetterOrDigit(line[caret]) || line[caret] == '_')) return null;
            int dot = k - 1;
            if (InCommentOrString(line, dot)) return null;
            int i = dot - 1;
            int start = dot;
            while (true)
            {
                bool progressed = false;
                // trailing call / subscript / template argument groups: Foo<int>(x)[0]
                while (i >= 0 && (line[i] == ')' || line[i] == ']' || line[i] == '>' && !(i > 0 && line[i - 1] == '-')))
                {
                    char c = line[i];
                    int m = Match(line, i, c, c == ')' ? '(' : c == ']' ? '[' : '<');
                    if (m < 0) return null;
                    i = m - 1;
                    start = m;
                    progressed = true;
                }
                if (i >= 0 && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                {
                    while (i >= 0 && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i--;
                    start = i + 1;
                    progressed = true;
                }
                else if (i >= 0 && line[i] == '"' && !progressed)
                {
                    int q = i > 0 ? line.LastIndexOf('"', i - 1) : -1;
                    if (q < 0) return null;
                    i = q - 1;
                    while (i >= 0 && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i--; // L"…", u8"…"
                    start = i + 1;
                    progressed = true;
                }
                if (!progressed) break;
                // connectors that continue the chain to the left
                if (i >= 1 && line[i] == ':' && line[i - 1] == ':') { i -= 2; continue; }
                if (i >= 1 && line[i] == '>' && line[i - 1] == '-') { i -= 2; continue; }
                if (i >= 0 && line[i] == '.') { i--; continue; }
                break;
            }
            if (start >= dot) return null;
            // unary prefix
            while (start > 0 && (line[start - 1] == '*' || line[start - 1] == '!' || line[start - 1] == '&') &&
                   (start - 1 == 0 || line[start - 2] == ' ' || line[start - 2] == '\t' || line[start - 2] == '(' || line[start - 2] == '='))
                start--;
            string expr = line.Substring(start, dot - start).Trim();
            if (expr.Length == 0 || expr == "this" || char.IsDigit(expr[0]) && expr.All(ch => char.IsDigit(ch))) return null;
            bool atStart = true;
            for (int j = 0; j < start; j++)
                if (!char.IsWhiteSpace(line[j])) { atStart = false; break; }
            return new PostfixSite { ExpressionStart = start, Expression = expr, Dot = dot, Typed = line.Substring(k, caret - k), AtLineStart = atStart };
        }

        /// <summary>The template body with the expression substituted (for <see cref="SnippetExpander"/>).</summary>
        public static string Body(PostfixTemplate t, string expression) => t.Body.Replace("$EXPR$", (expression ?? string.Empty).Replace("$", "$$"));

        private static int Match(string line, int close, char c, char open)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                if (line[i] == c) depth++;
                else if (line[i] == open && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Whether column <paramref name="col"/> is inside a // comment or a string / char literal (line-local).</summary>
        public static bool InCommentOrString(string line, int col)
        {
            bool str = false, chr = false;
            for (int i = 0; i < col && i < line.Length; i++)
            {
                char c = line[i];
                if (str || chr)
                {
                    if (c == '\\') { i++; continue; }
                    if (str && c == '"') str = false;
                    else if (chr && c == '\'') chr = false;
                    continue;
                }
                if (c == '"') str = true;
                else if (c == '\'') chr = true;
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return true;
            }
            return str || chr;
        }
    }
}
