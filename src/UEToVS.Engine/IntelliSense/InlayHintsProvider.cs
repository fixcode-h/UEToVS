using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>Per-request counters of <see cref="InlayHintsProvider"/> (logged as editor.inlayHints).</summary>
    public sealed class InlayHintsStats
    {
        public int Calls;
        public int Resolved;
        public int Ambiguous;
        public int AutoVariables;
        public int AutoDeduced;
    }

    /// <summary>
    /// Inlay hints for a line range (v0.4.5, Rider rules): the parameter name before an argument that is a literal (number,
    /// string, char, TEXT("..."), nullptr, true, false; a leading sign is allowed), and the deduced type after the name of an
    /// <c>auto</c> variable. Only calls with at least one such argument are resolved (through <see cref="SignatureHelpProvider"/>,
    /// once per call); a hint is shown only when every overload that fits the argument count agrees on the name. Macros
    /// (ALL_CAPS callees or macro symbols) get no hints. Pure on top of the request's <see cref="IsSession"/>.
    /// </summary>
    public static class InlayHintsProvider
    {
        public const int MaxHints = 2000;
        private const int MaxArgTokens = 4000;

        public static List<InlayHint> Run(IsSession s, int startLine, int endLine, bool parameterNames, bool autoTypes, InlayHintsStats stats)
        {
            var result = new List<InlayHint>();
            var doc = s.Doc;
            int lineCount = doc.Lines.LineCount;
            if (lineCount == 0 || startLine > endLine) return result;
            startLine = Math.Max(0, startLine);
            endLine = Math.Min(lineCount - 1, endLine);
            int start = doc.Lines.GetLineStart(startLine);
            int end = endLine + 1 < lineCount ? doc.Lines.GetLineStart(endLine + 1) : doc.Text.Length;
            if (parameterNames) ParameterNames(s, start, end, result, stats);
            if (autoTypes) AutoTypes(s, start, end, result, stats);
            if (result.Count > MaxHints) result.RemoveRange(MaxHints, result.Count - MaxHints);
            result.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
            return result;
        }

        // ================================================================ parameter names

        private static void ParameterNames(IsSession s, int start, int end, List<InlayHint> result, InlayHintsStats stats)
        {
            var doc = s.Doc;
            var args = new List<(int First, int Last)>();
            for (int k = doc.TokenIndexFrom(start); k < doc.Tokens.Length && doc.Tokens[k].Start < end && result.Count < MaxHints; k++)
            {
                if (!doc.Is(k, Punct.LParen)) continue;
                int callee = Prev(doc, k);
                if (callee < 0) continue;
                bool templ = doc.Is(callee, Punct.Gt) || doc.Is(callee, Punct.GtGt);
                if (!templ && !doc.IsIdentifier(callee)) continue;
                if (doc.IsIdentifier(callee) && IsMacroName(doc.TokenText(callee))) continue;
                int offset = doc.Tokens[k].Start;
                int fn = doc.EnclosingFunction(offset);
                if (fn < 0 || offset < doc.Decls[fn].Body.Start) continue;
                if (!SplitArguments(doc, k, args)) continue;
                bool any = false;
                foreach (var a in args)
                    if (IsLiteral(doc, a.First, a.Last)) { any = true; break; }
                if (!any) continue;
                s.Ct.ThrowIfCancellationRequested();
                stats.Calls++;
                var help = SignatureHelpProvider.Get(s, doc.Tokens[k + 1].Start);
                var fits = Fitting(help.Signatures, args.Count);
                if (fits.Count == 0) continue;
                stats.Resolved++;
                for (int i = 0; i < args.Count; i++)
                {
                    var a = args[i];
                    if (!IsLiteral(doc, a.First, a.Last)) continue;
                    string name = AgreedName(fits, i, out string detail);
                    if (name == null)
                    {
                        stats.Ambiguous++;
                        continue;
                    }
                    if (string.Equals(name, doc.TokenText(a.First).Trim('"'), StringComparison.OrdinalIgnoreCase)) continue;
                    var (line, col) = doc.PositionOf(doc.Tokens[a.First].Start);
                    result.Add(new InlayHint { Line = line, Column = col, Kind = InlayHintKind.ParameterName, Text = name, Detail = detail });
                }
            }
        }

        private static int Prev(DocumentModel doc, int i)
        {
            i--;
            while (i >= 0 && doc.Tokens[i].Kind == TokenKind.DocComment) i--;
            return i;
        }

        /// <summary>UE / C convention: ALL_CAPS (with digits / underscores) callees are macros (TEXT, UE_LOG, check is lower but a macro symbol).</summary>
        public static bool IsMacroName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 2) return false;
            bool letter = false;
            foreach (char c in name)
            {
                if (char.IsLower(c)) return false;
                if (char.IsUpper(c)) letter = true;
            }
            return letter;
        }

        /// <summary>Top-level argument token ranges of the call whose '(' is <paramref name="open"/>; false when unbalanced or too long.</summary>
        public static bool SplitArguments(DocumentModel doc, int open, List<(int First, int Last)> args)
        {
            args.Clear();
            int depth = 0, first = open + 1;
            for (int k = open + 1; k < doc.Tokens.Length && k < open + MaxArgTokens; k++)
            {
                var t = doc.Tokens[k];
                if (t.Kind == TokenKind.Directive) return false;
                if (t.Is(Punct.LParen) || t.Is(Punct.LBracket) || t.Is(Punct.LBrace)) depth++;
                else if (t.Is(Punct.RParen) || t.Is(Punct.RBracket) || t.Is(Punct.RBrace))
                {
                    if (depth == 0)
                    {
                        if (!t.Is(Punct.RParen)) return false;
                        if (k > first) args.Add((first, k - 1));
                        else if (args.Count > 0) return false; // "f(a, )"
                        return true;
                    }
                    depth--;
                }
                else if (t.Is(Punct.Semicolon) && depth == 0) return false;
                else if (t.Is(Punct.Comma) && depth == 0)
                {
                    if (k == first) return false;
                    args.Add((first, k - 1));
                    first = k + 1;
                }
            }
            return false;
        }

        /// <summary>Number / string / char literal, TEXT("..."), nullptr, true, false, with an optional leading sign.</summary>
        public static bool IsLiteral(DocumentModel doc, int first, int last)
        {
            if (first > last) return false;
            if (first < last && (doc.Is(first, Punct.Minus) || doc.Is(first, Punct.Plus))) first++;
            if (first == last)
            {
                var t = doc.Tokens[first];
                return t.Kind == TokenKind.Number || t.Kind == TokenKind.String || t.Kind == TokenKind.Char ||
                       t.Is(Keyword.Nullptr) || t.Is(Keyword.True) || t.Is(Keyword.False);
            }
            // "a" "b" (adjacent string literals)
            bool strings = true;
            for (int k = first; k <= last; k++)
                if (doc.Tokens[k].Kind != TokenKind.String) { strings = false; break; }
            if (strings) return true;
            // TEXT("...")
            if (last - first >= 3 && doc.IsIdentifier(first) && doc.TokenText(first) == "TEXT" && doc.Is(first + 1, Punct.LParen) && doc.Is(last, Punct.RParen))
            {
                for (int k = first + 2; k < last; k++)
                    if (doc.Tokens[k].Kind != TokenKind.String) return false;
                return true;
            }
            return false;
        }

        /// <summary>Signatures (not macros) whose parameter list accepts <paramref name="argCount"/> arguments.</summary>
        public static List<SignatureInfo> Fitting(List<SignatureInfo> sigs, int argCount)
        {
            var list = new List<SignatureInfo>();
            if (sigs == null) return list;
            foreach (var sig in sigs)
            {
                if (sig.Symbol != null && sig.Symbol.Kind == SymbolKind.Macro) continue;
                int count = sig.Parameters.Count, required = 0;
                bool variadic = false;
                foreach (var p in sig.Parameters)
                {
                    string text = ParamText(sig, p);
                    if (text.Contains("...")) variadic = true;
                    else if (text.IndexOf('=') < 0) required++;
                }
                if (argCount < required) continue;
                if (argCount > count && !variadic) continue;
                list.Add(sig);
            }
            return list;
        }

        private static string ParamText(SignatureInfo sig, SignatureParameter p) =>
            p.LabelStart >= 0 && p.LabelStart + p.LabelLength <= (sig.Label?.Length ?? 0) ? sig.Label.Substring(p.LabelStart, p.LabelLength) : string.Empty;

        /// <summary>The name every fitting signature gives argument <paramref name="index"/>, or null when they differ / have none.</summary>
        public static string AgreedName(List<SignatureInfo> fits, int index, out string detail)
        {
            detail = null;
            string name = null;
            foreach (var sig in fits)
            {
                if (index >= sig.Parameters.Count) return null; // a variadic tail has no name
                var p = sig.Parameters[index];
                if (string.IsNullOrEmpty(p.Name)) return null;
                if (name == null)
                {
                    name = p.Name;
                    detail = ParamText(sig, p);
                }
                else if (!string.Equals(name, p.Name, StringComparison.Ordinal)) return null;
            }
            return name;
        }

        // ================================================================ auto types

        private static void AutoTypes(IsSession s, int start, int end, List<InlayHint> result, InlayHintsStats stats)
        {
            var doc = s.Doc;
            for (int fn = 0; fn < doc.Decls.Count && result.Count < MaxHints; fn++)
            {
                var d = doc.Decls[fn];
                if (!TypeText.IsCallableKind(d.Kind) || d.Body.IsNone || d.Body.End <= start || d.Body.Start >= end) continue;
                ExpressionTypes types = null;
                foreach (var l in doc.LocalsOf(fn))
                {
                    if (l.IsParameter || l.Offset < start || l.Offset >= end) continue;
                    int ti = doc.TokenIndexFrom(l.Offset);
                    if (ti < 0 || ti >= doc.Tokens.Length || doc.Tokens[ti].Start != l.Offset || !IsAutoDeclared(doc, ti)) continue;
                    s.Ct.ThrowIfCancellationRequested();
                    stats.AutoVariables++;
                    string type = l.Type;
                    if (IsAutoText(type))
                    {
                        types ??= new ExpressionTypes(s, doc.EnclosingClass(l.Offset, out _));
                        type = types.DeducedType(l);
                    }
                    type = CleanType(type);
                    if (type == null || IsAutoText(type)) continue;
                    stats.AutoDeduced++;
                    if (InitializerNames(doc, ti, type)) continue; // auto X = FVector(...) / Cast<AFoo>(...): the type is already written there
                    var (line, col) = doc.PositionOf(doc.Tokens[ti].End);
                    result.Add(new InlayHint { Line = line, Column = col, Kind = InlayHintKind.Type, Text = type, Detail = type });
                }
            }
        }

        /// <summary>The variable at <paramref name="nameToken"/> is declared with <c>auto</c> (<c>auto</c>, <c>const auto&amp;</c>, <c>auto*</c> …).</summary>
        public static bool IsAutoDeclared(DocumentModel doc, int nameToken)
        {
            int p = nameToken - 1;
            while (p >= 0 && (doc.Is(p, Punct.Star) || doc.Is(p, Punct.Amp) || doc.Is(p, Punct.AmpAmp) || doc.Is(p, Keyword.Const))) p--;
            return p >= 0 && doc.Is(p, Keyword.Auto);
        }

        private static bool IsAutoText(string t) =>
            t == null || t == "auto" || t.StartsWith("auto", StringComparison.Ordinal) || t.StartsWith("const auto", StringComparison.Ordinal);

        private static string CleanType(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return null;
            t = t.Trim();
            if (t.Length > 120) return null; // a deduction that long is noise in the editor
            return t;
        }

        /// <summary>The initializer of the variable mentions the head type name (e.g. <c>auto V = FVector(…)</c>, <c>auto* A = Cast&lt;AFoo&gt;(…)</c>).</summary>
        private static bool InitializerNames(DocumentModel doc, int nameToken, string type)
        {
            var shape = TypeShape.Parse(type);
            string head = shape.Shape == AccessShape.RawPointer ? TypeShape.Parse(shape.Pointee() ?? type).Head : shape.Head;
            if (string.IsNullOrEmpty(head)) return false;
            string last = TypeText.LastSegment(head);
            int k = nameToken + 1;
            if (!doc.Is(k, Punct.Eq) && !doc.Is(k, Punct.LBrace) && !doc.Is(k, Punct.LParen)) return false;
            for (k++; k < doc.Tokens.Length && k < nameToken + 60; k++)
            {
                if (doc.Is(k, Punct.Semicolon)) break;
                if (doc.IsIdentifier(k) && doc.TokenText(k) == last) return true;
            }
            return false;
        }
    }
}
