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

        /// <summary>For a call <c>name(args)</c>: a rough class of each argument (for picking among overloads); null otherwise.</summary>
        public List<ArgClass> CallArguments { get; set; }

        public bool IsEmpty => Candidates.Count == 0 && Include == null;
    }

    /// <summary>Rough argument / parameter category used to choose among overloads.</summary>
    public enum ArgClass : byte
    {
        Unknown = 0,
        Number = 1,
        Text = 2,
        Pointer = 3,
        Object = 4,
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
                // The macro's #define (e.g. GENERATED_BODY in ObjectMacros.h), then — for GENERATED_BODY and friends — the
                // per-class expansion UHT wrote into X.generated.h, when the project has been built.
                foreach (var m in ctx.ByName(word))
                    if (m.Kind == SymbolKind.Macro) r.Candidates.Add(m);
                if (GeneratedBodyLocator.IsBodyMacro(word))
                {
                    try
                    {
                        var gen = GeneratedBodyLocator.Find(doc.Path, System.Text.Encoding.UTF8.GetString(doc.Text), word, doc.PositionOf(offset).Line);
                        if (gen != null) r.Candidates.Add(gen);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
                }
                if (r.Candidates.Count > 0) r.Confidence = ResolutionConfidence.Exact;
                return r;
            }

            bool typePosition = doc.Is(ti + 1, Punct.ColonColon) || (doc.OccurrenceRoleAt(offset) & OccurrenceRole.TypeUse) != 0;
            string cls = r.ClassContext;
            if (doc.Is(ti + 1, Punct.LParen) && depth == 0) r.CallArguments = CallArguments(ctx, doc, ti + 1, cls);

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
                // a class name inside a class (own or base) means the type, not its constructor
                var hits = ctx.MemberLookup(word, cls, out int level).Where(s => s.Kind != SymbolKind.Constructor).ToList();
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

        // ------------------------------------------------------------------ overloads

        private static List<ArgClass> CallArguments(NavContext ctx, DocumentModel doc, int open, string cls)
        {
            var args = new List<ArgClass>();
            int depth = 0, start = open + 1;
            for (int k = open; k < doc.Tokens.Length; k++)
            {
                var t = doc.Tokens[k];
                if (t.Is(Punct.LParen) || t.Is(Punct.LBracket) || t.Is(Punct.LBrace)) depth++;
                else if (t.Is(Punct.RParen) || t.Is(Punct.RBracket) || t.Is(Punct.RBrace))
                {
                    if (--depth == 0)
                    {
                        if (k > start) args.Add(Classify(doc, start, k));
                        return args;
                    }
                }
                else if (t.Is(Punct.Comma) && depth == 1)
                {
                    args.Add(Classify(doc, start, k));
                    start = k + 1;
                }
                else if (t.Is(Punct.Semicolon)) break;
            }
            return null;
        }

        private static ArgClass Classify(DocumentModel doc, int from, int to)
        {
            if (to - from == 1)
            {
                var t = doc.Tokens[from];
                if (t.Kind == TokenKind.Number) return ArgClass.Number;
                if (t.Kind == TokenKind.String) return ArgClass.Text;
                if (t.Is(Keyword.This) || t.Is(Keyword.Nullptr)) return ArgClass.Pointer;
                if (t.Is(Keyword.True) || t.Is(Keyword.False)) return ArgClass.Number;
                if (t.Kind == TokenKind.Identifier)
                {
                    int fn = doc.EnclosingFunction(t.Start);
                    var l = doc.FindLocal(fn, doc.TokenText(from), t.Start);
                    if (l != null) return ParamClass(l.Type);
                }
            }
            if (doc.Is(from, Punct.Amp)) return ArgClass.Pointer;
            if (doc.Is(from, Punct.Minus) && to - from == 2 && doc.Tokens[from + 1].Kind == TokenKind.Number) return ArgClass.Number;
            return ArgClass.Unknown;
        }

        /// <summary>Category of a parameter / variable type text.</summary>
        public static ArgClass ParamClass(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return ArgClass.Unknown;
            if (type.Contains('*')) return ArgClass.Pointer;
            string core = LastWord(type);
            if (TypeText.IsBuiltin(core) || core == "unsigned" || core == "signed") return ArgClass.Number;
            if (core == "FString" || core == "FName" || core == "FText" || core == "TCHAR") return ArgClass.Text;
            if (core.StartsWith("TObjectPtr", StringComparison.Ordinal) || core.StartsWith("TWeakObjectPtr", StringComparison.Ordinal)) return ArgClass.Pointer;
            return ArgClass.Object;
        }

        private static string LastWord(string type)
        {
            string t = type.Replace("const ", " ").Replace("&", " ").Trim();
            int sp = t.LastIndexOf(' ');
            return sp >= 0 ? t.Substring(sp + 1) : t;
        }

        /// <summary>Keeps the overloads whose parameter list fits the call's arguments best (arity, then argument categories).</summary>
        public static List<SymbolInfo> FilterByCall(List<SymbolInfo> candidates, List<ArgClass> args)
        {
            if (args == null || candidates.Count < 2) return candidates;
            var scored = new List<(SymbolInfo S, int Score)>();
            foreach (var c in candidates)
            {
                var ps = ParameterTypes(c.Signature, out int required);
                if (ps == null) { scored.Add((c, 0)); continue; }
                if (args.Count < required || args.Count > ps.Count) { scored.Add((c, -100)); continue; }
                int score = 10;
                for (int i = 0; i < args.Count; i++)
                {
                    if (args[i] == ArgClass.Unknown) continue;
                    var pc = ParamClass(ps[i]);
                    if (pc == args[i]) score += 3;
                    else if (pc != ArgClass.Unknown && !(pc == ArgClass.Object && args[i] == ArgClass.Pointer)) score -= 3;
                }
                scored.Add((c, score));
            }
            int best = scored.Max(x => x.Score);
            return scored.Where(x => x.Score == best).Select(x => x.S).ToList();
        }

        /// <summary>Parameter type texts of a signature (names and defaults removed); <paramref name="required"/> = parameters without defaults.</summary>
        private static List<string> ParameterTypes(string signature, out int required)
        {
            required = 0;
            string key = TypeText.ParameterKey(signature);
            if (key == null) return null;
            var list = key.Length == 0 ? new List<string>() : key.Split('|').ToList();
            int open = signature.IndexOf('(');
            string inner = open >= 0 ? signature.Substring(open) : string.Empty;
            int defaults = 0, depth = 0;
            foreach (char ch in inner)
            {
                if (ch == '(' || ch == '<') depth++;
                else if (ch == ')' || ch == '>') depth--;
                else if (ch == '=' && depth == 1) defaults++;
            }
            required = Math.Max(0, list.Count - defaults);
            return list;
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
            // X.generated.h 在 Intermediate 下，Intermediate 默认不进索引：按模块目录直接去 UHT 输出里找（多份时取最近编译的）
            string path = GeneratedBodyLocator.ResolveGeneratedInclude(inc.Path, doc.Path) ?? ctx.ResolveInclude(inc.Path, doc.Path);
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
