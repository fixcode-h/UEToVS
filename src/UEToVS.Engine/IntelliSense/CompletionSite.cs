using System;
using System.Collections.Generic;
using System.Text;
using UEToVS.Contracts;
using UEToVS.Core.Unreal;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>Where completion was requested and what kind of list fits there.</summary>
    public sealed class CompletionSite
    {
        public CompletionContextKind Kind { get; set; }

        /// <summary>Caret is in a comment, string or a directive that takes no completion: answer with an empty list.</summary>
        public bool Suppressed { get; set; }

        public string SuppressReason { get; set; }

        /// <summary>Identifier text before the caret (what the user typed so far).</summary>
        public string Prefix { get; set; } = string.Empty;

        /// <summary>Byte range replaced on commit (the whole word around the caret, or the include path / override statement).</summary>
        public int ReplaceStart { get; set; }

        public int ReplaceEnd { get; set; }

        public int CaretOffset { get; set; }

        /// <summary>Member: the '.' / '-&gt;' token; Scope: the '::' token.</summary>
        public int OperatorToken { get; set; } = -1;

        public bool Arrow { get; set; }

        /// <summary>Specifier: the reflection macro (UCLASS, UPROPERTY...) and whether the caret is inside meta=(...).</summary>
        public string SpecifierMacro { get; set; }

        public bool InMeta { get; set; }

        /// <summary>Specifier: the caret is after '=' (a value, not a specifier name).</summary>
        public bool ValuePosition { get; set; }

        public HashSet<string> UsedSpecifiers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IncludeAngled { get; set; }

        /// <summary>General: only macros make sense (#if / #ifdef lines).</summary>
        public bool MacrosOnly { get; set; }

        /// <summary>Override: byte offset of the 'virtual' keyword starting the declaration.</summary>
        public int OverrideStart { get; set; } = -1;

        /// <summary>General: the identifier is in a position where only types make sense (after 'new', '&lt;', base clause...).</summary>
        public bool TypeExpected { get; set; }

        /// <summary>GameplayTag / AssetPath / UFunctionName / IniKey: the string literal analysis (columns on the caret line).</summary>
        public StringLiteralSite StringSite { get; set; }

        /// <summary>String contexts: line of the caret (columns of <see cref="StringSite"/> refer to it).</summary>
        public int Line { get; set; }

        /// <summary>String contexts: catalog entries considered (tags, folders + assets, UFUNCTIONs, sections / keys), for logs.</summary>
        public int Candidates { get; set; }

        public override string ToString() => $"{Kind} prefix='{Prefix}'" + (Suppressed ? " suppressed:" + SuppressReason : string.Empty);
    }

    /// <summary>Determines the <see cref="CompletionSite"/> at a caret from the document's token stream (tolerates unfinished code).</summary>
    public static class CompletionSiteAnalyzer
    {
        private static readonly HashSet<string> ReflectionMacros = new HashSet<string>(StringComparer.Ordinal)
        {
            "UCLASS", "UPROPERTY", "UFUNCTION", "USTRUCT", "UENUM", "UINTERFACE", "UMETA", "UPARAM", "UDELEGATE",
        };

        public static bool IsReflectionMacro(string name) => name != null && ReflectionMacros.Contains(name);

        private static bool IsIdentByte(byte b) => b == '_' || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b >= 0x80;

        public static CompletionSite Analyze(DocumentModel doc, int caret)
        {
            var text = doc.Text;
            caret = Math.Max(0, Math.Min(caret, text.Length));
            var site = new CompletionSite { CaretOffset = caret, Kind = CompletionContextKind.General };

            // the word around the caret
            int ws = caret;
            while (ws > 0 && IsIdentByte(text[ws - 1])) ws--;
            int we = caret;
            while (we < text.Length && IsIdentByte(text[we])) we++;
            if (ws < caret && text[ws] >= '0' && text[ws] <= '9')
            {
                site.Suppressed = true;
                site.SuppressReason = "number";
                return site;
            }
            site.Prefix = Encoding.UTF8.GetString(text, ws, caret - ws);
            site.ReplaceStart = ws;
            site.ReplaceEnd = we;

            // the last token starting before the word
            int k = doc.TokenIndexFrom(ws) - 1;
            if (k >= 0)
            {
                var t = doc.Tokens[k];
                if (t.Kind == TokenKind.Directive && caret <= t.End + 0 && LineOf(doc, t.Start) == LineOf(doc, caret))
                    return Directive(doc, k, caret, site);
                if (t.Kind == TokenKind.String && caret > t.Start && caret <= t.End && TryString(doc, caret, site))
                    return site;
                if ((t.Kind == TokenKind.String || t.Kind == TokenKind.Char || t.Kind == TokenKind.DocComment) && caret < t.End)
                {
                    site.Suppressed = true;
                    site.SuppressReason = t.Kind == TokenKind.DocComment ? "comment" : "string";
                    return site;
                }
                if (InPlainComment(text, t.End, ws))
                {
                    site.Suppressed = true;
                    site.SuppressReason = "comment";
                    return site;
                }
            }
            else if (InPlainComment(text, 0, ws))
            {
                site.Suppressed = true;
                site.SuppressReason = "comment";
                return site;
            }

            int b = SkipDocs(doc, k);
            if (b >= 0 && (doc.Is(b, Punct.Dot) || doc.Is(b, Punct.Arrow)))
            {
                site.Kind = CompletionContextKind.Member;
                site.OperatorToken = b;
                site.Arrow = doc.Is(b, Punct.Arrow);
                return site;
            }
            if (b >= 0 && doc.Is(b, Punct.ColonColon))
            {
                site.Kind = CompletionContextKind.Scope;
                site.OperatorToken = b;
                return site;
            }
            if (TrySpecifier(doc, b, ws, site)) return site;
            if (TryOverride(doc, b, ws, site)) return site;
            if (b >= 0)
            {
                var bt = doc.Tokens[b];
                // new X, Cast<X>, TArray<X>, class A : public X
                site.TypeExpected = bt.Is(Keyword.New) || bt.Is(Punct.Lt) ||
                                    (bt.Is(Keyword.Public) || bt.Is(Keyword.Protected) || bt.Is(Keyword.Private)) && !doc.Is(b + 1, Punct.Colon);
            }
            return site;
        }

        /// <summary>
        /// Caret in a string literal: a GameplayTag / asset path / UFUNCTION name / ini section or key context (line-local
        /// <see cref="StringLiteralContext"/>, shared with the host so both use the same replace span), or a plain string
        /// (suppressed). False when the caret is not inside the literal after all (right after a closing quote).
        /// </summary>
        private static bool TryString(DocumentModel doc, int caret, CompletionSite site)
        {
            var (line, col) = doc.PositionOf(caret);
            var ss = StringLiteralContext.Analyze(doc.LineText(line), col);
            if (!ss.InString) return false;
            CompletionContextKind kind;
            switch (ss.Kind)
            {
                case StringLiteralKind.GameplayTag: kind = CompletionContextKind.GameplayTag; break;
                case StringLiteralKind.AssetPath: kind = CompletionContextKind.AssetPath; break;
                case StringLiteralKind.UFunctionName: kind = CompletionContextKind.UFunctionName; break;
                case StringLiteralKind.IniSection:
                case StringLiteralKind.IniKey: kind = CompletionContextKind.IniKey; break;
                default:
                    site.Suppressed = true;
                    site.SuppressReason = "string";
                    return true;
            }
            site.Kind = kind;
            site.StringSite = ss;
            site.Line = line;
            site.ReplaceStart = doc.OffsetOf(line, ss.FragmentStart);
            site.ReplaceEnd = doc.OffsetOf(line, ss.FragmentStart + ss.FragmentLength);
            site.Prefix = Encoding.UTF8.GetString(doc.Text, site.ReplaceStart, Math.Max(0, caret - site.ReplaceStart));
            return true;
        }

        private static int LineOf(DocumentModel doc, int offset) => doc.Lines.GetLine(offset);

        private static int SkipDocs(DocumentModel doc, int i)
        {
            while (i >= 0 && doc.Tokens[i].Kind == TokenKind.DocComment) i--;
            return i;
        }

        /// <summary>Whether a '//' or an unclosed '/*' starts in [from, to).</summary>
        private static bool InPlainComment(byte[] text, int from, int to)
        {
            bool line = false, block = false;
            for (int i = Math.Max(0, from); i < to && i < text.Length; i++)
            {
                byte c = text[i];
                if (line)
                {
                    if (c == '\n') line = false;
                    continue;
                }
                if (block)
                {
                    if (c == '*' && i + 1 < to && text[i + 1] == '/') { block = false; i++; }
                    continue;
                }
                if (c == '/' && i + 1 < text.Length)
                {
                    if (text[i + 1] == '/') { line = true; i++; }
                    else if (text[i + 1] == '*') { block = true; i++; }
                }
            }
            return line || block;
        }

        private static CompletionSite Directive(DocumentModel doc, int k, int caret, CompletionSite site)
        {
            var t = doc.Tokens[k];
            if (t.Directive == DirectiveKind.Include || t.Directive == DirectiveKind.IncludeNext || t.Directive == DirectiveKind.Import)
            {
                var text = doc.Text;
                int q = -1;
                for (int i = t.Start; i < caret; i++)
                {
                    if (text[i] == '"' || text[i] == '<')
                    {
                        q = i;
                        break;
                    }
                }
                if (q < 0)
                {
                    site.Suppressed = true;
                    site.SuppressReason = "include-keyword";
                    return site;
                }
                char close = text[q] == '<' ? '>' : '"';
                for (int i = q + 1; i < caret; i++)
                {
                    if (text[i] == close)
                    {
                        site.Suppressed = true;
                        site.SuppressReason = "after-include";
                        return site;
                    }
                }
                int end = caret;
                while (end < t.End && text[end] != close && text[end] != '\n' && text[end] != '\r') end++;
                site.Kind = CompletionContextKind.Include;
                site.IncludeAngled = close == '>';
                site.Prefix = Encoding.UTF8.GetString(text, q + 1, caret - q - 1);
                site.ReplaceStart = q + 1;
                site.ReplaceEnd = end;
                return site;
            }
            if (t.Directive == DirectiveKind.If || t.Directive == DirectiveKind.Ifdef || t.Directive == DirectiveKind.Ifndef ||
                t.Directive == DirectiveKind.Elif || t.Directive == DirectiveKind.Undef || t.Directive == DirectiveKind.Elifdef ||
                t.Directive == DirectiveKind.Elifndef)
            {
                site.MacrosOnly = true;
                return site;
            }
            site.Suppressed = true;
            site.SuppressReason = "directive";
            return site;
        }

        /// <summary>Whether token <paramref name="tokenIndex"/> sits inside UCLASS(...) / UPROPERTY(...) / meta=(...) (for Quick Info and colouring).</summary>
        public static bool SpecifierContextAt(DocumentModel doc, int tokenIndex, out string macro, out bool meta, out bool valuePosition)
        {
            var tmp = new CompletionSite();
            int b = SkipDocs(doc, tokenIndex - 1);
            bool ok = TrySpecifier(doc, b, tokenIndex < doc.Tokens.Length ? doc.Tokens[tokenIndex].Start : doc.Text.Length, tmp);
            macro = tmp.SpecifierMacro;
            meta = tmp.InMeta;
            valuePosition = tmp.ValuePosition;
            return ok;
        }

        /// <summary>Inside UCLASS(...) / UPROPERTY(...) / meta=(...): reflection specifiers.</summary>
        private static bool TrySpecifier(DocumentModel doc, int b, int wordStart, CompletionSite site)
        {
            int open = UnmatchedParen(doc, b, 400);
            if (open < 0) return false;
            int name = SkipDocs(doc, open - 1);
            bool meta = false;
            string macro = null;
            if (doc.Is(name, Punct.Eq) && doc.IsIdentifier(SkipDocs(doc, name - 1)) &&
                string.Equals(doc.TokenText(SkipDocs(doc, name - 1)), "meta", StringComparison.OrdinalIgnoreCase))
            {
                meta = true;
                int outer = UnmatchedParen(doc, SkipDocs(doc, name - 2), 400);
                if (outer < 0) return false;
                int outerName = SkipDocs(doc, outer - 1);
                if (!doc.IsIdentifier(outerName) || !IsReflectionMacro(doc.TokenText(outerName))) return false;
                macro = doc.TokenText(outerName);
                CollectUsed(doc, outer, site, false);
            }
            else if (doc.IsIdentifier(name) && IsReflectionMacro(doc.TokenText(name)))
            {
                macro = doc.TokenText(name);
            }
            if (macro == null) return false;
            site.Kind = CompletionContextKind.Specifier;
            site.SpecifierMacro = macro;
            site.InMeta = meta;
            site.ValuePosition = doc.Is(b, Punct.Eq);
            CollectUsed(doc, open, site, meta);
            site.UsedSpecifiers.Remove(site.Prefix);
            return true;
        }

        /// <summary>The '(' enclosing token <paramref name="from"/> (inclusive scan backwards), or -1 at a statement boundary.</summary>
        private static int UnmatchedParen(DocumentModel doc, int from, int maxTokens)
        {
            int depth = 0;
            for (int k = from; k >= 0 && k > from - maxTokens; k--)
            {
                var t = doc.Tokens[k];
                if (t.Is(Punct.RParen)) depth++;
                else if (t.Is(Punct.LParen))
                {
                    if (depth == 0) return k;
                    depth--;
                }
                else if (t.Is(Punct.Semicolon) || t.Is(Punct.LBrace) || t.Is(Punct.RBrace) || t.Kind == TokenKind.Directive) return -1;
            }
            return -1;
        }

        /// <summary>Identifiers that start an item of the list opened at <paramref name="open"/> (already used specifiers).</summary>
        private static void CollectUsed(DocumentModel doc, int open, CompletionSite site, bool meta)
        {
            int depth = 0;
            for (int k = open; k < doc.Tokens.Length && k < open + 300; k++)
            {
                var t = doc.Tokens[k];
                if (t.Is(Punct.LParen)) depth++;
                else if (t.Is(Punct.RParen)) { if (--depth == 0) break; }
                else if (t.Is(Punct.Semicolon) || t.Is(Punct.LBrace) || t.Is(Punct.RBrace)) break;
                else if (depth == 1 && t.Kind == TokenKind.Identifier && (doc.Is(k - 1, Punct.LParen) || doc.Is(k - 1, Punct.Comma)))
                {
                    string w = doc.TokenText(k);
                    if (!meta && string.Equals(w, "meta", StringComparison.OrdinalIgnoreCase)) continue;
                    site.UsedSpecifiers.Add(w);
                }
            }
        }

        /// <summary>A <c>virtual</c> declaration being typed in a class body: overridable base methods.</summary>
        private static bool TryOverride(DocumentModel doc, int b, int wordStart, CompletionSite site)
        {
            var scopes = doc.EnclosingScopes(wordStart);
            if (scopes.Count == 0) return false;
            var inner = doc.Decls[scopes[scopes.Count - 1]];
            if (!TypeText.IsRecordKind(inner.Kind)) return false;
            int virtualTok = -1;
            for (int k = b; k >= 0 && k > b - 40; k--)
            {
                var t = doc.Tokens[k];
                if (t.Is(Punct.Semicolon) || t.Is(Punct.LBrace) || t.Is(Punct.RBrace) || t.Kind == TokenKind.Directive) break;
                if (t.Is(Punct.Colon) && (doc.Is(k - 1, Keyword.Public) || doc.Is(k - 1, Keyword.Protected) || doc.Is(k - 1, Keyword.Private))) break;
                if (t.Is(Punct.RParen))
                {
                    // UFUNCTION(...) before the declaration ends the scan
                    int open = doc.MatchBackward(k, Punct.LParen, Punct.RParen);
                    if (open > 0 && doc.IsIdentifier(open - 1) && IsReflectionMacro(doc.TokenText(open - 1))) break;
                    if (open < 0) break;
                    k = open;
                    continue;
                }
                if (t.Is(Keyword.Virtual)) virtualTok = k;
            }
            if (virtualTok < 0) return false;
            site.Kind = CompletionContextKind.Override;
            int vStart = doc.Tokens[virtualTok].Start;
            if (LineOf(doc, vStart) == LineOf(doc, wordStart))
            {
                site.OverrideStart = vStart;
                site.ReplaceStart = vStart;
            }
            return true;
        }
    }
}
