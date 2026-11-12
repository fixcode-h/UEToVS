using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.Navigation
{
    /// <summary>What the identifier at a position denotes.</summary>
    public sealed class Resolution
    {
        public string Word { get; set; } = string.Empty;
        public int TokenIndex { get; set; } = -1;

        /// <summary>Symbols of the entities the identifier may denote (declarations and definitions), best scope first.</summary>
        public List<SymbolInfo> Candidates { get; set; } = new List<SymbolInfo>();

        public ResolutionConfidence Confidence { get; set; }
        public string Reason { get; set; } = "none";

        /// <summary>Set when the identifier is the name of a declaration in this document.</summary>
        public SymbolInfo OnDeclaration { get; set; }

        /// <summary>Set when the identifier denotes a local variable or parameter.</summary>
        public LocalVariable Local { get; set; }

        /// <summary>Set for <c>#include</c> lines: the header to open.</summary>
        public SymbolInfo Include { get; set; }

        /// <summary>Enclosing class at the position (qualified), or null.</summary>
        public string ClassContext { get; set; }

        public bool IsEmpty => Candidates.Count == 0 && Include == null;
    }

    /// <summary>
    /// Scope-aware resolution of an identifier in a <see cref="DocumentModel"/> against the index (VA / Rider-like rules,
    /// heuristics where C++ semantics would need a compiler):
    /// <list type="number">
    /// <item>on a declaration's name → that declaration (Exact);</item>
    /// <item>locals and parameters of the enclosing function;</item>
    /// <item><c>X::name</c> → members of X and its bases; <c>Super::</c> → the enclosing class's first base;</item>
    /// <item><c>obj.name</c> / <c>p-&gt;name</c> → members of the object's type when it can be inferred (this, locals, fields,
    /// Cast&lt;T&gt;), otherwise every member with that name (NameOnly);</item>
    /// <item>unqualified → enclosing class and its bases → enclosing namespaces → global → any (NameOnly).</item>
    /// </list>
    /// </summary>
    public static class SymbolResolver
    {
        private const int MaxInferenceDepth = 4;

        public static Resolution Resolve(NavContext ctx, DocumentModel doc, int offset)
        {
            int ti = doc.TokenAt(offset);
            if (ti < 0) return new Resolution { Reason = "no-token" };
            return ResolveToken(ctx, doc, ti, 0);
        }

        public static Resolution ResolveToken(NavContext ctx, DocumentModel doc, int ti, int depth)
        {
            var r = new Resolution { TokenIndex = ti };
            var tok = doc.Tokens[ti];
            if (tok.Kind == TokenKind.Directive)
            {
                if (tok.Directive == DirectiveKind.Include || tok.Directive == DirectiveKind.IncludeNext || tok.Directive == DirectiveKind.Import)
                    ResolveInclude(ctx, doc, ti, r);
                else r.Reason = "directive";
                return r;
            }
            if (tok.Kind != TokenKind.Identifier)
            {
                r.Reason = "not-identifier";
                return r;
            }
            string word = doc.TokenText(ti);
            r.Word = word;
            int offset = tok.Start;
            r.ClassContext = doc.EnclosingClass(offset, out _);

            // 1. the name of a declaration in this document
            int di = doc.DeclAtOffset(offset);
            if (di >= 0)
            {
                var self = ctx.ToSymbol(doc, di);
                r.OnDeclaration = self;
                r.Candidates = SameEntity(ctx.ByName(word), self);
                if (!r.Candidates.Any(c => SameLocation(c, self))) r.Candidates.Insert(0, self);
                r.Confidence = ResolutionConfidence.Exact;
                r.Reason = "declaration";
                return r;
            }

            // 2. locals / parameters
            int fn = doc.EnclosingFunction(offset);
            bool qualified = doc.Is(ti - 1, Punct.ColonColon) || doc.Is(ti - 1, Punct.Dot) || doc.Is(ti - 1, Punct.Arrow);
            if (!qualified)
            {
                var local = doc.FindLocal(fn, word, offset);
                if (local != null)
                {
                    r.Local = local;
                    r.Candidates.Add(ctx.LocalSymbol(doc, local));
                    r.Confidence = ResolutionConfidence.Exact;
                    r.Reason = local.Offset == offset ? "local-declaration" : local.IsParameter ? "parameter" : "local";
                    return r;
                }
            }

            if (DeclarationContextOccurrences.IsUeBoilerplateMacro(word))
            {
                r.Reason = "ue-macro";
                return r;
            }

            bool typePosition = doc.Is(ti + 1, Punct.ColonColon) || (doc.OccurrenceRoleAt(offset) & OccurrenceRole.TypeUse) != 0;
            string cls = r.ClassContext;

            if (doc.Is(ti - 1, Punct.ColonColon))
            {
                ResolveQualified(ctx, doc, ti, word, cls, typePosition, r);
            }
            else if (doc.Is(ti - 1, Punct.Dot) || doc.Is(ti - 1, Punct.Arrow))
            {
                string type = depth < MaxInferenceDepth ? InferExpressionType(ctx, doc, ti - 2, cls, depth + 1) : null;
                if (type != null)
                {
                    var hits = ctx.MemberLookup(word, type, out int level);
                    if (hits.Count > 0) Set(r, hits, level == 0 ? "member-access" : "member-access-base");
                }
                if (r.Candidates.Count == 0)
                {
                    var members = ctx.ByName(word).Where(s => !string.IsNullOrEmpty(s.Container) && !TypeText.IsTypeKind(s.Kind) || s.Kind == SymbolKind.Delegate).ToList();
                    if (members.Count == 0) members = ctx.ByName(word);
                    SetNameOnly(r, members, type == null ? "member-access-unknown" : "member-access-not-found");
                }
            }
            else
            {
                ResolveUnqualified(ctx, doc, offset, word, cls, typePosition, r);
            }
            return r;
        }

        // ------------------------------------------------------------------ qualified

        private static void ResolveQualified(NavContext ctx, DocumentModel doc, int ti, string word, string cls, bool typePosition, Resolution r)
        {
            var parts = new List<string>();
            int k = ti - 1;
            bool global = false;
            while (doc.Is(k, Punct.ColonColon))
            {
                int q = k - 1;
                if (doc.Is(q, Punct.Gt))
                {
                    // TFoo<int>::Bar → skip the template arguments
                    int depthT = 0;
                    for (; q >= 0; q--)
                    {
                        if (doc.Is(q, Punct.Gt)) depthT++;
                        else if (doc.Is(q, Punct.Lt) && --depthT == 0) break;
                    }
                    q--;
                }
                if (!doc.IsIdentifier(q))
                {
                    global = true;
                    break;
                }
                parts.Insert(0, doc.TokenText(q));
                k = q - 1;
            }
            if (parts.Count == 0)
            {
                // ::name → global scope
                var g = Prefer(ctx.ByName(word).Where(s => string.IsNullOrEmpty(s.Container)).ToList(), typePosition);
                if (g.Count > 0) Set(r, g, "global-qualified");
                else SetNameOnly(r, ctx.ByName(word), "name-only");
                return;
            }
            _ = global;

            string qualifier = string.Join("::", parts);
            if (parts[0] == "Super" && cls != null)
            {
                string super = ctx.FirstBase(cls);
                qualifier = parts.Count == 1 ? super : super + "::" + string.Join("::", parts.Skip(1));
                if (qualifier != null)
                {
                    var hits = ctx.MemberLookup(word, qualifier, out _);
                    if (hits.Count > 0)
                    {
                        Set(r, hits, "super");
                        return;
                    }
                }
                // base not indexed (e.g. engine skipped): any member with that name outside the current class
                var others = ctx.ByName(word).Where(s => !string.IsNullOrEmpty(s.Container) && !NavContext.SameScope(s.Container, cls)).ToList();
                SetNameOnly(r, others.Count > 0 ? others : ctx.ByName(word), "super-unresolved");
                return;
            }
            if (parts[0] == "ThisClass" && cls != null) qualifier = parts.Count == 1 ? cls : cls + "::" + string.Join("::", parts.Skip(1));

            var found = Prefer(ctx.MemberLookup(word, qualifier, out int level), typePosition);
            if (found.Count == 0)
            {
                // relative to the enclosing namespace: Detail::FHelper inside namespace Game
                string ns = doc.EnclosingNamespace(doc.Tokens[ti].Start);
                for (string p = ns; found.Count == 0 && !string.IsNullOrEmpty(p); p = TypeText.Parent(p))
                    found = Prefer(ctx.MemberLookup(word, p + "::" + qualifier, out level), typePosition);
            }
            if (found.Count > 0)
            {
                Set(r, found, level == 0 ? "qualified" : "qualified-base");
                return;
            }
            // the qualifier may be a typedef / alias / enum we do not know: fall back to the last qualifier segment
            string last = parts[parts.Count - 1];
            var loose = ctx.ByName(word).Where(s => TypeText.LastSegment(s.Container) == last).ToList();
            if (loose.Count > 0) SetNameOnly(r, loose, "qualified-loose");
            else SetNameOnly(r, ctx.ByName(word), "name-only");
        }

        // ------------------------------------------------------------------ unqualified

        private static void ResolveUnqualified(NavContext ctx, DocumentModel doc, int offset, string word, string cls, bool typePosition, Resolution r)
        {
            if (word == "Super" && cls != null)
            {
                string b = ctx.FirstBase(cls);
                var t = b != null ? ctx.FindType(b) : null;
                if (t != null)
                {
                    Set(r, Prefer(SameEntity(ctx.ByName(t.Symbol.Name), t.Symbol), true), "super");
                    if (r.Candidates.Count == 0) Set(r, new List<SymbolInfo> { t.Symbol }, "super");
                    return;
                }
            }
            if (word == "ThisClass" && cls != null)
            {
                var t = ctx.FindType(cls);
                if (t != null)
                {
                    Set(r, new List<SymbolInfo> { t.Symbol }, "this-class");
                    return;
                }
            }

            var all = ctx.ByName(word);
            if (all.Count == 0)
            {
                r.Reason = "not-found";
                return;
            }

            // enclosing class and its bases
            if (cls != null)
            {
                var hits = ctx.MemberLookup(word, cls, out int level);
                var preferred = Prefer(hits, typePosition);
                if (typePosition) preferred = preferred.Where(s => TypeText.IsTypeKind(s.Kind)).ToList();
                if (preferred.Count > 0)
                {
                    Set(r, preferred, level == 0 ? "member-of-enclosing" : "member-of-base");
                    return;
                }
                // the class itself and nested types of its containers
                for (string p = TypeText.Parent(cls); !string.IsNullOrEmpty(p); p = TypeText.Parent(p))
                {
                    var nested = Prefer(all.Where(s => s.Container == p).ToList(), typePosition);
                    if (nested.Count > 0)
                    {
                        Set(r, nested, "namespace");
                        return;
                    }
                }
            }

            // enclosing namespaces, innermost first
            string ns = doc.EnclosingNamespace(offset);
            for (string p = ns; !string.IsNullOrEmpty(p); p = TypeText.Parent(p))
            {
                var hits = Prefer(all.Where(s => string.Equals(s.Container, p, StringComparison.Ordinal)).ToList(), typePosition);
                if (hits.Count > 0)
                {
                    Set(r, hits, "namespace");
                    return;
                }
            }

            // global
            var globals = Prefer(all.Where(s => string.IsNullOrEmpty(s.Container)).ToList(), typePosition);
            if (globals.Count > 0)
            {
                Set(r, globals, "global");
                return;
            }

            // anything with that name; types first when used as a type
            var any = Prefer(all.ToList(), typePosition);
            // a member of an unrelated class is unlikely for an unqualified use inside a known class: keep it, but NameOnly
            SetNameOnly(r, any, "name-only");
        }

        // ------------------------------------------------------------------ expression types

        /// <summary>Best-effort class name of the expression ending at token <paramref name="end"/> (the left side of . or -&gt;).</summary>
        public static string InferExpressionType(NavContext ctx, DocumentModel doc, int end, string cls, int depth)
        {
            string text = InferExpressionTypeText(ctx, doc, end, cls, depth);
            return text == null ? null : TypeText.CoreType(text);
        }

        private static string InferExpressionTypeText(NavContext ctx, DocumentModel doc, int end, string cls, int depth)
        {
            if (end < 0 || depth > MaxInferenceDepth) return null;
            if (doc.Is(end, Keyword.This)) return cls;
            if (doc.Is(end, Punct.RParen))
            {
                int open = doc.MatchBackward(end, Punct.LParen, Punct.RParen);
                if (open <= 0) return null;
                int callee = open - 1;
                if (doc.Is(callee, Punct.Gt))
                {
                    int lt = doc.MatchBackward(callee, Punct.Lt, Punct.Gt);
                    if (lt > 0 && doc.IsIdentifier(lt - 1) && TypeText.TypedFactories.Contains(doc.TokenText(lt - 1)))
                    {
                        int t = lt + 1;
                        while (doc.IsIdentifier(t) && doc.Is(t + 1, Punct.ColonColon)) t += 2;
                        if (doc.IsIdentifier(t)) return doc.TokenText(t) + "*";
                    }
                    return null;
                }
                if (doc.IsIdentifier(callee))
                {
                    // a call: the return type of the function
                    var res = ResolveToken(ctx, doc, callee, depth);
                    var f = res.Candidates.FirstOrDefault(c => TypeText.IsCallableKind(c.Kind));
                    return f == null ? null : ReturnType(f);
                }
                // (expr) → the inner expression
                return InferExpressionTypeText(ctx, doc, end - 1, cls, depth + 1);
            }
            if (doc.Is(end, Punct.RBracket))
            {
                int open = doc.MatchBackward(end, Punct.LBracket, Punct.RBracket);
                if (open <= 0) return null;
                return TypeText.ElementType(InferExpressionTypeText(ctx, doc, open - 1, cls, depth + 1));
            }
            if (doc.IsIdentifier(end))
            {
                var res = ResolveToken(ctx, doc, end, depth);
                if (res.Local != null) return LocalType(ctx, doc, res.Local, cls, depth);
                var v = res.Candidates.FirstOrDefault(c => c.Kind == SymbolKind.Field || c.Kind == SymbolKind.Variable);
                if (v != null) return TypeText.TypeFromSignature(v.Signature, v.Name);
                return null;
            }
            return null;
        }

        private static string LocalType(NavContext ctx, DocumentModel doc, LocalVariable l, string cls, int depth)
        {
            string t = l.Type;
            if (t != null && (t == "auto" || t.StartsWith("auto", StringComparison.Ordinal) || t.StartsWith("const auto", StringComparison.Ordinal)))
            {
                // for (auto* X : Container) → element type of the container
                int ti = doc.TokenIndexFrom(l.Offset);
                if (doc.Is(ti + 1, Punct.Colon))
                {
                    int k = ti + 2;
                    int endExpr = k;
                    while (endExpr + 1 < doc.Tokens.Length && !doc.Is(endExpr + 1, Punct.RParen)) endExpr++;
                    return TypeText.ElementType(InferExpressionTypeText(ctx, doc, endExpr, cls, depth + 1));
                }
                return null;
            }
            return t;
        }

        /// <summary>Return type text of a function symbol from its signature.</summary>
        public static string ReturnType(SymbolInfo f)
        {
            string t = TypeText.TypeFromSignature(f.Signature, f.Name);
            if (t == null) return null;
            // "UFoo* AMyActor::" → "UFoo*"
            while (t.EndsWith("::", StringComparison.Ordinal))
            {
                t = t.Substring(0, t.Length - 2).TrimEnd();
                int s = t.Length;
                while (s > 0 && TypeText.IsIdentPart(t[s - 1])) s--;
                t = t.Substring(0, s).TrimEnd();
            }
            foreach (var w in new[] { "virtual ", "static ", "inline ", "FORCEINLINE ", "explicit ", "constexpr " })
                if (t.StartsWith(w, StringComparison.Ordinal)) t = t.Substring(w.Length);
            return t.Trim();
        }

        // ------------------------------------------------------------------ includes

        private static void ResolveInclude(NavContext ctx, DocumentModel doc, int ti, Resolution r)
        {
            int line = doc.PositionOf(doc.Tokens[ti].Start).Line;
            var inc = doc.Summary.Includes.FirstOrDefault(i => i.Line == line && !i.IsMacro);
            r.Word = inc?.Path ?? string.Empty;
            if (inc == null)
            {
                r.Reason = "include-unparsed";
                return;
            }
            string path = ctx.ResolveInclude(inc.Path, doc.Path);
            if (path == null)
            {
                r.Reason = "include-not-found";
                return;
            }
            var (module, origin) = ctx.FileOrigin(path);
            r.Include = new SymbolInfo
            {
                Name = System.IO.Path.GetFileName(path),
                Kind = SymbolKind.Unknown,
                Signature = "#include " + (inc.IsAngled ? "<" + inc.Path + ">" : "\"" + inc.Path + "\""),
                Location = new SourceLocation { FilePath = path, Line = 0, Column = 0 },
                Module = module,
                Origin = origin,
                IsDefinition = true,
            };
            r.Candidates.Add(r.Include);
            r.Confidence = ResolutionConfidence.Exact;
            r.Reason = "include";
        }

        // ------------------------------------------------------------------ entities

        /// <summary>Identity of the entity a declaration belongs to (declaration and definition share it; overloads too).</summary>
        public static string EntityKey(SymbolInfo s)
        {
            string family = TypeText.IsCallableKind(s.Kind) ? "f" : TypeText.IsTypeKind(s.Kind) ? "t" : s.Kind == SymbolKind.Macro ? "m" : "v";
            return family + ":" + (s.Container ?? string.Empty) + "::" + s.Name;
        }

        /// <summary>Symbols of the same entity as <paramref name="self"/> (same name, same container, same kind family).</summary>
        public static List<SymbolInfo> SameEntity(List<SymbolInfo> all, SymbolInfo self)
        {
            string key = EntityKey(self);
            var list = new List<SymbolInfo>();
            foreach (var s in all)
                if (EntityKey(s) == key) list.Add(s);
            return list;
        }

        public static bool SameLocation(SymbolInfo a, SymbolInfo b) =>
            a.Location != null && b.Location != null && a.Location.Line == b.Location.Line && a.Location.Column == b.Location.Column &&
            string.Equals(NavContext.NormalizePath(a.Location.FilePath), NavContext.NormalizePath(b.Location.FilePath), StringComparison.Ordinal);

        /// <summary>Types first when the identifier is used as a type (or qualifier), when there are any.</summary>
        private static List<SymbolInfo> Prefer(List<SymbolInfo> hits, bool typePosition)
        {
            if (!typePosition || hits.Count < 2) return hits;
            var types = hits.Where(s => TypeText.IsTypeKind(s.Kind)).ToList();
            return types.Count > 0 ? types : hits;
        }

        private static void Set(Resolution r, List<SymbolInfo> hits, string reason)
        {
            r.Candidates = hits;
            r.Reason = reason;
            int entities = hits.Select(EntityKey).Distinct().Count();
            r.Confidence = entities <= 1 ? ResolutionConfidence.Exact : ResolutionConfidence.Scoped;
        }

        private static void SetNameOnly(Resolution r, List<SymbolInfo> hits, string reason)
        {
            r.Candidates = hits;
            r.Reason = hits.Count == 0 ? "not-found" : reason;
            r.Confidence = hits.Count == 0 ? ResolutionConfidence.None : ResolutionConfidence.NameOnly;
        }
    }
}
