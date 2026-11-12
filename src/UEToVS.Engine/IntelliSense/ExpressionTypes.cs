using System;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// Best-effort type (as written: <c>TObjectPtr&lt;UFoo&gt;</c>, <c>AActor*</c>, <c>TArray&lt;AWeapon*&gt;</c>) of the expression
    /// that ends at a token: chains (<c>a-&gt;b.c()</c>), function return types, locals (incl. <c>auto</c> initialisers and range-for),
    /// fields, <c>this</c>, <c>Cast&lt;T&gt;</c>-style factories, indexing of UE containers, UE smart pointer / container APIs
    /// (<c>Get()</c>, <c>Pin()</c>, <c>Last()</c>, <c>Find()</c>...) and common engine getters (<c>GetOwner()</c>, <c>GetWorld()</c>).
    /// Builds on <see cref="SymbolResolver"/> for identifier resolution.
    /// </summary>
    public sealed class ExpressionTypes
    {
        private const int MaxDepth = 8;
        private readonly IsSession _s;
        private readonly DocumentModel _doc;
        private readonly string _cls;

        public ExpressionTypes(IsSession session, string enclosingClass)
        {
            _s = session;
            _doc = session.Doc;
            _cls = enclosingClass;
        }

        /// <summary>Type text of the expression ending at token <paramref name="end"/>, or null.</summary>
        public string TypeOf(int end) => TypeOf(end, 0);

        private int Prev(int i)
        {
            i--;
            while (i >= 0 && _doc.Tokens[i].Kind == TokenKind.DocComment) i--;
            return i;
        }

        private string TypeOf(int end, int depth)
        {
            if (end < 0 || depth > MaxDepth) return null;
            var doc = _doc;
            if (doc.Is(end, Keyword.This)) return _cls != null ? _cls + "*" : null;
            if (doc.Tokens[end].Kind == TokenKind.String) return "const TCHAR*";
            if (doc.Is(end, Punct.RParen))
            {
                int open = doc.MatchBackward(end, Punct.LParen, Punct.RParen);
                if (open <= 0) return null;
                int callee = Prev(open);
                if (doc.Is(callee, Punct.Gt) || doc.Is(callee, Punct.GtGt))
                {
                    int lt = MatchAngle(callee);
                    int nameTok = Prev(lt);
                    if (lt > 0 && doc.IsIdentifier(nameTok))
                    {
                        string arg = doc.TokensText(lt + 1, callee).Trim();
                        string fn = doc.TokenText(nameTok);
                        string factory = FactoryType(fn, arg);
                        if (factory != null) return factory;
                        return CallReturn(nameTok, depth);
                    }
                    return null;
                }
                if (doc.IsIdentifier(callee)) return CallReturn(callee, depth);
                // C-style cast "(AActor*)X" is not an expression ending in ')'; a parenthesised expression is
                int inner = Prev(end);
                return inner > open ? TypeOf(inner, depth + 1) : null;
            }
            if (doc.Is(end, Punct.RBracket))
            {
                int open = doc.MatchBackward(end, Punct.LBracket, Punct.RBracket);
                if (open <= 0) return null;
                return Element(TypeOf(Prev(open), depth + 1));
            }
            if (doc.IsIdentifier(end)) return IdentifierType(end, depth);
            return null;
        }

        /// <summary>Index of the '&lt;' matching the '&gt;' at <paramref name="gt"/>.</summary>
        private int MatchAngle(int gt)
        {
            int d = 0;
            for (int k = gt; k >= 0 && k > gt - 200; k--)
            {
                if (_doc.Is(k, Punct.Gt)) d++;
                else if (_doc.Is(k, Punct.GtGt)) d += 2;
                else if (_doc.Is(k, Punct.Lt) && --d == 0) return k;
                else if (_doc.Is(k, Punct.Semicolon) || _doc.Is(k, Punct.LBrace) || _doc.Is(k, Punct.RBrace)) return -1;
            }
            return -1;
        }

        private static string FactoryType(string fn, string arg)
        {
            if (string.IsNullOrEmpty(arg)) return null;
            switch (fn)
            {
                case "MakeShared":
                case "MakeShareable":
                    return "TSharedRef<" + arg + ">";
                case "MakeUnique":
                    return "TUniquePtr<" + arg + ">";
                case "StaticCastSharedPtr":
                case "ConstCastSharedPtr":
                    return "TSharedPtr<" + arg + ">";
                case "StaticCastSharedRef":
                case "ConstCastSharedRef":
                    return "TSharedRef<" + arg + ">";
                case "static_cast":
                case "reinterpret_cast":
                case "const_cast":
                case "dynamic_cast":
                    return arg;
            }
            return TypeText.TypedFactories.Contains(fn) ? arg.TrimEnd('*', ' ') + "*" : null;
        }

        /// <summary>Return type of the call whose callee name is token <paramref name="callee"/>.</summary>
        private string CallReturn(int callee, int depth)
        {
            var doc = _doc;
            string name = doc.TokenText(callee);
            int before = Prev(callee);
            if (doc.Is(before, Punct.Dot) || doc.Is(before, Punct.Arrow))
            {
                string left = TypeOf(Prev(before), depth + 1);
                return MemberReturn(left, doc.Is(before, Punct.Arrow), name);
            }
            if (!doc.Is(before, Punct.ColonColon))
            {
                // constructor-style temporary: FVector(1, 2, 3)
                var t = _s.FindRecord(name, doc.Tokens[callee].Start);
                if (t != null && !char.IsLower(name[0]) && !_s.Ctx.ByName(name).Any(c => TypeText.IsCallableKind(c.Kind) && c.Kind != SymbolKind.Constructor))
                    return name;
                // a member function of the enclosing class (incl. bases)
                if (_cls != null)
                {
                    string r = MemberReturn(_cls, false, name);
                    if (r != null) return r;
                }
            }
            var res = SymbolResolver.ResolveToken(_s.Ctx, doc, callee, 1);
            var f = res.Candidates.FirstOrDefault(c => TypeText.IsCallableKind(c.Kind));
            string ret = f != null ? Clean(SymbolResolver.ReturnType(f)) : null;
            if (ret != null && !IsTemplateParam(ret)) return ret;
            return UeBuiltins.WellKnownReturns.TryGetValue(name, out var known) ? known : null;
        }

        /// <summary>Return type of member function <paramref name="name"/> called on a value of type <paramref name="left"/>.</summary>
        public string MemberReturn(string left, bool arrow, string name)
        {
            if (left == null) return Fallback(name);
            var shape = TypeShape.Parse(left);
            string target = left;
            if (arrow || shape.Shape == AccessShape.RawPointer || shape.Shape == AccessShape.ObjectPtr)
            {
                if (shape.IsPointerLike) target = shape.Pointee();
            }
            else if (shape.Shape == AccessShape.SmartPointer)
            {
                var api = UeBuiltins.ApiOf(shape.Head);
                var m = api?.FirstOrDefault(x => x.Name == name);
                if (m?.Returns != null) return UeBuiltins.Instantiate(m.Returns, shape.Arg(0), shape.Arg(0), shape.Arg(1));
                target = shape.Pointee();
            }
            if (target == null) return Fallback(name);
            var ts = TypeShape.Parse(target);
            var rec = _s.FindRecord(ts.Head);
            if (rec != null)
            {
                var members = _s.MembersOf(rec);
                foreach (var e in members.Members)
                {
                    var d = e.Decl;
                    if (d.Name != name || !TypeText.IsCallableKind(d.Kind)) continue;
                    string t = Clean(d.Type);
                    if (t != null && !IsTemplateParam(t)) return t;
                }
                // fallback: index symbols of the type and its bases (file not readable)
                var hits = _s.Ctx.MemberLookup(name, ts.Head, out _);
                var f = hits.FirstOrDefault(c => TypeText.IsCallableKind(c.Kind));
                string r = f != null ? Clean(SymbolResolver.ReturnType(f)) : null;
                if (r != null && !IsTemplateParam(r)) return r;
            }
            var builtin = UeBuiltins.ApiOf(ts.Head)?.FirstOrDefault(x => x.Name == name);
            if (builtin?.Returns != null) return UeBuiltins.Instantiate(builtin.Returns, ts.Arg(0), ts.Arg(0), ts.Arg(1));
            return Fallback(name);
        }

        private static string Fallback(string name) => UeBuiltins.WellKnownReturns.TryGetValue(name, out var known) ? known : null;

        /// <summary>Type of field <paramref name="name"/> of a value of type <paramref name="left"/>.</summary>
        public string MemberField(string left, bool arrow, string name)
        {
            if (left == null) return null;
            var shape = TypeShape.Parse(left);
            string target = shape.IsPointerLike && (arrow || shape.Shape != AccessShape.SmartPointer) ? shape.Pointee() : left;
            var ts = TypeShape.Parse(target);
            var rec = _s.FindRecord(ts.Head);
            if (rec == null) return null;
            foreach (var e in _s.MembersOf(rec).Members)
            {
                var d = e.Decl;
                if (d.Name == name && (d.Kind == SymbolKind.Field || d.Kind == SymbolKind.Variable)) return Clean(d.Type) ?? TypeText.TypeFromSignature(d.Signature, d.Name);
            }
            var hits = _s.Ctx.MemberLookup(name, ts.Head, out _);
            var v = hits.FirstOrDefault(c => c.Kind == SymbolKind.Field || c.Kind == SymbolKind.Variable);
            return v != null ? TypeText.TypeFromSignature(v.Signature, v.Name) : null;
        }

        private string IdentifierType(int ti, int depth)
        {
            var doc = _doc;
            string name = doc.TokenText(ti);
            int before = Prev(ti);
            if (doc.Is(before, Punct.Dot) || doc.Is(before, Punct.Arrow))
                return MemberField(TypeOf(Prev(before), depth + 1), doc.Is(before, Punct.Arrow), name);
            int offset = doc.Tokens[ti].Start;
            if (!doc.Is(before, Punct.ColonColon))
            {
                int fn = doc.EnclosingFunction(offset);
                var local = doc.FindLocal(fn, name, offset);
                if (local != null) return LocalType(local, depth);
                if (_cls != null)
                {
                    string f = MemberField(_cls, false, name);
                    if (f != null) return f;
                }
            }
            var res = SymbolResolver.ResolveToken(_s.Ctx, doc, ti, 1);
            if (res.Local != null) return LocalType(res.Local, depth);
            var v = res.Candidates.FirstOrDefault(c => c.Kind == SymbolKind.Field || c.Kind == SymbolKind.Variable);
            if (v != null)
            {
                var (d, i) = _s.DeclOf(v);
                return Clean(d != null ? d.Decls[i].Type : null) ?? TypeText.TypeFromSignature(v.Signature, v.Name);
            }
            var ev = res.Candidates.FirstOrDefault(c => c.Kind == SymbolKind.EnumValue);
            if (ev != null) return ev.Container;
            return null;
        }

        /// <summary>v0.4.5 inlay hints: the type of a local as written, or deduced from its <c>auto</c> initializer / range (null when unknown).</summary>
        public string DeducedType(LocalVariable l) => l == null ? null : LocalType(l, 0);

        private string LocalType(LocalVariable l, int depth)
        {
            string t = l.Type;
            if (t == null) return null;
            bool isAuto = t == "auto" || t.StartsWith("auto", StringComparison.Ordinal) || t.StartsWith("const auto", StringComparison.Ordinal);
            if (!isAuto) return t;
            var doc = _doc;
            int ti = doc.TokenIndexFrom(l.Offset);
            if (doc.Is(ti + 1, Punct.Colon))
            {
                // for (auto* X : Container)
                int k = ti + 2, d = 0, endExpr = -1;
                for (; k < doc.Tokens.Length && k < ti + 200; k++)
                {
                    if (doc.Is(k, Punct.LParen)) d++;
                    else if (doc.Is(k, Punct.RParen)) { if (d-- == 0) { endExpr = k - 1; break; } }
                }
                return endExpr > ti + 1 ? Element(TypeOf(endExpr, depth + 1)) : null;
            }
            if (doc.Is(ti + 1, Punct.Eq))
            {
                // auto X = expr;  → the expression's type (pointer-ness from auto* is already in expr)
                int k = ti + 2, d = 0, endExpr = -1;
                for (; k < doc.Tokens.Length && k < ti + 300; k++)
                {
                    var tk = doc.Tokens[k];
                    if (tk.Is(Punct.LParen) || tk.Is(Punct.LBracket) || tk.Is(Punct.LBrace)) d++;
                    else if (tk.Is(Punct.RParen) || tk.Is(Punct.RBracket) || tk.Is(Punct.RBrace)) d--;
                    else if (d == 0 && (tk.Is(Punct.Semicolon) || tk.Is(Punct.Comma))) { endExpr = k - 1; break; }
                    if (d < 0) break;
                }
                if (endExpr >= ti + 2 && doc.Tokens[endExpr].Start < l.Offset + 4000) return TypeOf(endExpr, depth + 1);
            }
            return null;
        }

        /// <summary>Element type of indexing / iterating a container or pointer.</summary>
        public static string Element(string container)
        {
            if (container == null) return null;
            var s = TypeShape.Parse(container);
            if (s.Shape == AccessShape.RawPointer) return s.Pointee();
            switch (s.Head)
            {
                case "TMap":
                case "TMultiMap":
                case "TSortedMap":
                    return s.Arg(1);
                case null:
                    return null;
            }
            if (s.Shape == AccessShape.SmartPointer || s.Shape == AccessShape.ObjectPtr) return Element(s.Pointee());
            return TypeText.ElementType(container) ?? (s.Args.Count > 0 && UeBuiltins.ApiOf(s.Head) != null ? s.Arg(0) : null);
        }

        private static string Clean(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return null;
            t = t.Trim();
            foreach (var w in new[] { "virtual ", "static ", "inline ", "FORCEINLINE ", "explicit ", "constexpr ", "UE_NODISCARD " })
                if (t.StartsWith(w, StringComparison.Ordinal)) t = t.Substring(w.Length);
            return t == "auto" || t == "void" ? null : t;
        }

        /// <summary><c>T*</c>, <c>T&amp;</c>, <c>ElementType</c>: a template parameter we cannot instantiate.</summary>
        private static bool IsTemplateParam(string t)
        {
            var s = TypeShape.Parse(t);
            return s.Head != null && s.Head.Length == 1 && char.IsUpper(s.Head[0]);
        }
    }
}
