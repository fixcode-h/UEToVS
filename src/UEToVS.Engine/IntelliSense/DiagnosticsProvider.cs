using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UEToVS.Contracts;
using UEToVS.Engine.Index;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>Per-request counters of <see cref="DiagnosticsProvider"/> (logged as intellisense.diagnostics).</summary>
    public sealed class DiagnosticsStats
    {
        public int Checked;
        public int Uncertain;
        public int UnknownMember;
        public int MissingGeneratedBody;

        /// <summary>v0.4.2 UE rules (UE0004…UE0010): rule code → reported count.</summary>
        public Dictionary<string, int> UeRuleHits { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        internal void Hit(string code)
        {
            UeRuleHits.TryGetValue(code, out int n);
            UeRuleHits[code] = n + 1;
        }

        /// <summary>"UE0004=1,UE0007=2" for the log (null when nothing was reported).</summary>
        public string UeRuleSummary() => UeRuleHits.Count == 0 ? null : string.Join(",", UeRuleHits.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));

        /// <summary>Why candidates were skipped (reason → count): tells how much is missed and why.</summary>
        public Dictionary<string, int> UncertainReasons { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        internal void Skip(string reason)
        {
            Uncertain++;
            UncertainReasons.TryGetValue(reason, out int n);
            UncertainReasons[reason] = n + 1;
        }

        /// <summary>The most frequent skip reasons, "reason=count", for the log.</summary>
        public string TopReasons(int max = 16) =>
            string.Join(",", UncertainReasons.OrderByDescending(p => p.Value).Take(max).Select(p => p.Key + "=" + p.Value));
    }

    /// <summary>
    /// High-confidence squiggles (design §7.6 "波浪线"). After the IntelliSense takeover these are the user's only error marks, so
    /// every rule reports only when the answer is certain and gives up (counted in <see cref="DiagnosticsStats.Uncertain"/>) on
    /// anything it cannot fully see: templates, macros, unindexed / templated / aliased bases, auto, overloads with different
    /// return types, lambdas, conditional compilation...
    /// <list type="bullet">
    /// <item>UE0001 unknown member: <c>expr.Name</c> / <c>expr-&gt;Name</c> where expr is <c>this</c>, a local / parameter with a written
    /// type, a field, or a member call (chains of those) and the record type and every base are fully parsed project / engine classes.</item>
    /// <item>UE0003 UCLASS / USTRUCT / UINTERFACE without GENERATED_BODY() (UHT error) in a project header.</item>
    /// </list>
    /// UE0002 (missing module dependency) is a host rule (Build.cs files are read there). Engine files are never diagnosed.
    /// Uses the shared per-version document parse and member tables of <see cref="IsSession"/>; the expression resolver is a
    /// strict subset of <see cref="ExpressionTypes"/> (which guesses, fine for completion but not for errors).
    /// </summary>
    public sealed partial class DiagnosticsProvider
    {
        private const int MaxChain = 4;
        private const int MaxLineage = 64;
        private const int MaxMacroScan = 4000;

        /// <summary>Macros whose arguments are ordinary compiled expressions (in development builds).</summary>
        private static readonly HashSet<string> TransparentMacros = new HashSet<string>(StringComparer.Ordinal)
        {
            "check", "checkf", "checkSlow", "checkfSlow", "verify", "verifyf", "ensure", "ensureMsgf", "ensureAlways", "ensureAlwaysMsgf",
            "UE_LOG", "UE_CLOG", "TEXT",
        };

        /// <summary><c>-&gt;</c> on these reaches the first template argument.</summary>
        private static readonly HashSet<string> ArrowSmartPointers = new HashSet<string>(StringComparer.Ordinal)
        {
            "TSharedPtr", "TSharedRef", "TUniquePtr", "TWeakObjectPtr", "TStrongObjectPtr", "TSubclassOf",
        };

        private static readonly HashSet<string> TypeWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "auto", "bool", "char", "short", "int", "long", "float", "double", "unsigned", "signed", "wchar_t", "char16_t", "char32_t", "char8_t", "void", "const",
        };

        /// <summary>Members UHT adds in .generated.h beyond what the knowledge base injects.</summary>
        private static readonly HashSet<string> GeneratedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetLifetimeReplicatedProps", "ValidateGeneratedRepEnums", "ENetFields_Private", "GetPrivateStaticClass", "StaticClassCastFlags",
            "StaticConfigName", "GetVerseVTable", "__DefaultConstructor", "__VTableCtorCaller",
        };

        private readonly ConditionalWeakTable<DocumentModel, Dictionary<int, string>> _bodyScans = new ConditionalWeakTable<DocumentModel, Dictionary<int, string>>();

        /// <summary>Diagnostics for lines [<paramref name="startLine"/>, <paramref name="endLine"/>]; <paramref name="caretOffset"/> (or -1) is the word being typed.</summary>
        public List<CodeDiagnostic> Run(IsSession s, int startLine, int endLine, int caretOffset, DiagnosticsStats stats, IEnumerable<string> unsavedFiles = null)
        {
            var result = new List<CodeDiagnostic>();
            var doc = s.Doc;
            int lineCount = doc.Lines.LineCount;
            if (lineCount == 0 || startLine > endLine) return result;
            var (_, origin) = s.Ctx.FileOrigin(doc.Path);
            if (!TypeText.IsProject(origin)) return result;
            int start = doc.Lines.GetLineStart(startLine);
            int end = endLine + 1 < lineCount ? doc.Lines.GetLineStart(endLine + 1) : doc.Text.Length;
            var run = new Pass(this, s, stats, caretOffset, unsavedFiles);
            run.Members(start, end, result);
            run.GeneratedBody(start, end, result);
            run.UeRules(start, end, result);
            result.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
            return result;
        }

        /// <summary>One request: memoised type certainty and member lookups.</summary>
        private sealed partial class Pass
        {
            private readonly DiagnosticsProvider _p;
            private readonly IsSession _s;
            private readonly DocumentModel _doc;
            private readonly DiagnosticsStats _stats;
            private readonly int _caret;
            private readonly Dictionary<string, string> _typeCertainty = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, (TypeEntry Type, string Reason)> _records = new Dictionary<string, (TypeEntry, string)>(StringComparer.Ordinal);

            private readonly HashSet<string> _unsaved = new HashSet<string>(StringComparer.Ordinal);

            public Pass(DiagnosticsProvider p, IsSession s, DiagnosticsStats stats, int caret, IEnumerable<string> unsaved)
            {
                if (unsaved != null)
                    foreach (var u in unsaved)
                        if (!string.IsNullOrEmpty(u)) _unsaved.Add(NavContext.NormalizePath(u));
                _p = p;
                _s = s;
                _doc = s.Doc;
                _stats = stats;
                _caret = caret;
            }

            // ============================================================ UE0001 unknown member

            public void Members(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                for (int k = doc.TokenIndexFrom(start); k < doc.Tokens.Length && doc.Tokens[k].Start < end; k++)
                {
                    if (!doc.IsIdentifier(k)) continue;
                    int op = Prev(k);
                    if (!doc.Is(op, Punct.Dot) && !doc.Is(op, Punct.Arrow)) continue;
                    _s.Ct.ThrowIfCancellationRequested();
                    var t = doc.Tokens[k];
                    if (_caret >= t.Start && _caret <= t.End) continue; // being typed
                    _stats.Checked++;
                    string reason = CheckMember(k, op, out string name, out string typeName);
                    if (reason == null)
                    {
                        _stats.UnknownMember++;
                        result.Add(Make(t.Start, t.Length, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.UnknownMember,
                            $"'{name}' is not a member of '{typeName}'", name, typeName));
                    }
                    else if (reason != Found) _stats.Skip(reason);
                }
            }

            private const string Found = "found";

            /// <summary>null = certainly unknown member; <see cref="Found"/> = fine; anything else = why it is uncertain.</summary>
            private string CheckMember(int k, int op, out string name, out string typeName)
            {
                var doc = _doc;
                name = doc.TokenText(k);
                typeName = null;
                int offset = doc.Tokens[k].Start;
                if (InInactive(doc, offset)) return "inactive";
                if (doc.Is(k + 1, Punct.ColonColon)) return "qualified-member";
                if (IsGeneratedLooking(name)) return "generated-name";
                int fn = doc.EnclosingFunction(offset);
                if (fn < 0 || offset < doc.Decls[fn].Body.Start) return "not-in-body";
                string tpl = TemplateContext(offset);
                if (tpl != null) return tpl;
                string macro = MacroArgument(k, doc.Decls[fn].Body.Start);
                if (macro != null) return macro;

                bool arrow = doc.Is(op, Punct.Arrow);
                string reason = TypeOf(Prev(op), fn, 0, out string leftType);
                if (reason != null) return reason;
                reason = Target(leftType, arrow, offset, out var root);
                if (reason != null) return reason;
                reason = Certain(root);
                if (reason != null) return reason;
                typeName = TypeText.Qualified(root.Symbol.Container, root.Symbol.Name);
                var members = _s.MembersOf(root);
                string n = name;
                if (members.Members.Any(m => m.Decl.Name == n)) return Found;
                if (_s.Ctx.MemberLookup(name, typeName, out _).Count > 0) return Found;
                return null;
            }

            private static bool IsGeneratedLooking(string name) =>
                GeneratedNames.Contains(name) || name.StartsWith("__", StringComparison.Ordinal) || name.StartsWith("Execute_", StringComparison.Ordinal) ||
                name.StartsWith("exec", StringComparison.Ordinal) && name.Length > 4 && char.IsUpper(name[4]) ||
                name.StartsWith("StaticRegisterNatives", StringComparison.Ordinal) ||
                name.EndsWith("_Implementation", StringComparison.Ordinal) || name.EndsWith("_Validate", StringComparison.Ordinal);

            /// <summary>Inside a template (class or function) everything may depend on the template arguments.</summary>
            private string TemplateContext(int offset)
            {
                foreach (int i in _doc.EnclosingScopes(offset))
                {
                    var d = _doc.Decls[i];
                    if ((d.Modifiers & DeclModifiers.Template) != 0 || !string.IsNullOrEmpty(d.TemplateParameters)) return "template-context";
                }
                return null;
            }

            /// <summary>Inside the arguments of a macro (other than check / ensure / UE_LOG...): the text may never be compiled as written.</summary>
            private string MacroArgument(int k, int bodyStart)
            {
                var doc = _doc;
                int depth = 0, steps = 0;
                for (int i = k - 1; i >= 0 && doc.Tokens[i].Start >= bodyStart; i--)
                {
                    if (++steps > MaxMacroScan) return "long-body";
                    var t = doc.Tokens[i];
                    if (t.Is(Punct.RParen)) depth++;
                    else if (t.Is(Punct.LParen))
                    {
                        if (depth > 0) { depth--; continue; }
                        int callee = Prev(i);
                        if (doc.IsIdentifier(callee))
                        {
                            string c = doc.TokenText(callee);
                            if (!TransparentMacros.Contains(c) && IsMacro(callee, c)) return "macro-argument";
                        }
                    }
                    else if (t.Is(Punct.LBrace) && depth == 0)
                    {
                        // a statement block: macros cannot span it unless it's a lambda in an argument; keep scanning outwards
                    }
                }
                return null;
            }

            private bool IsDefinedMacro(string name) => _s.Knowledge.Lookup(name) != null || _s.Ctx.ByName(name).Any(x => x.Kind == SymbolKind.Macro);

            private bool IsMacro(int token, string name)
            {
                if ((_doc.Tokens[token].Flags & TokenFlags.MacroLike) != 0 || IsMacroSpelling(name)) return true;
                if (_s.Knowledge.Lookup(name) != null) return true;
                return _s.Ctx.ByName(name).Any(x => x.Kind == SymbolKind.Macro);
            }

            // ------------------------------------------------------------ strict expression types

            /// <summary>Type text of the expression ending at <paramref name="end"/>, or the reason it is not certain.</summary>
            private string TypeOf(int end, int fn, int depth, out string type)
            {
                type = null;
                var doc = _doc;
                if (end < 0) return "no-expression";
                if (depth > MaxChain) return "long-chain";
                if (doc.Is(end, Keyword.This))
                {
                    string cls = doc.EnclosingClass(doc.Tokens[end].Start, out _);
                    if (cls == null) return "this-outside-class";
                    type = cls + "*";
                    return null;
                }
                if (doc.IsIdentifier(end)) return IdentifierType(end, fn, depth, out type);
                if (doc.Is(end, Punct.RParen))
                {
                    int open = doc.MatchBackward(end, Punct.LParen, Punct.RParen);
                    if (open <= 0) return "unbalanced";
                    int callee = Prev(open);
                    if (!doc.IsIdentifier(callee)) return "complex-expression";
                    return CallType(callee, fn, depth, out type);
                }
                return "complex-expression";
            }

            private string IdentifierType(int ti, int fn, int depth, out string type)
            {
                type = null;
                var doc = _doc;
                string name = doc.TokenText(ti);
                int before = Prev(ti);
                if (doc.Is(before, Punct.Dot) || doc.Is(before, Punct.Arrow))
                {
                    string r = TypeOf(Prev(before), fn, depth + 1, out string left);
                    if (r != null) return r;
                    r = Target(left, doc.Is(before, Punct.Arrow), doc.Tokens[ti].Start, out var rec);
                    if (r != null) return r;
                    return FieldType(rec, name, out type);
                }
                if (doc.Is(before, Punct.ColonColon)) return "qualified-name";
                if (IsDefinedMacro(name)) return "macro";
                int offset = doc.Tokens[ti].Start;
                int declLike = DeclarationLikeCount(fn, name, ti);
                var local = doc.FindLocal(fn, name, offset);
                if (local == null && IsMacroSpelling(name)) return "macro";
                if (local != null)
                {
                    if (declLike != 1) return "shadowed-local";
                    if (string.IsNullOrEmpty(local.Type) || HasDeducedType(local.Type)) return "auto-local";
                    type = local.Type;
                    return null;
                }
                if (declLike != 0) return "unparsed-local";
                string cls = doc.EnclosingClass(offset, out _);
                if (cls == null) return "unresolved-name";
                var root = Record(cls, offset, out string why);
                if (root == null) return why;
                return FieldType(root, name, out type);
            }

            /// <summary>Type of field <paramref name="name"/> of a fully known record: exactly one declaration, a field.</summary>
            private string FieldType(TypeEntry rec, string name, out string type)
            {
                type = null;
                string r = Certain(rec);
                if (r != null) return r;
                var hits = _s.MembersOf(rec).Members.Where(m => m.Decl.Name == name).ToList();
                if (hits.Count == 0) return "unresolved-name";
                int min = hits.Min(h => h.Depth);
                hits = hits.Where(h => h.Depth == min).ToList();
                if (hits.Count != 1) return "ambiguous-field";
                var d = hits[0].Decl;
                if (d.Kind != SymbolKind.Field && d.Kind != SymbolKind.Variable) return "not-a-field";
                if (string.IsNullOrEmpty(d.Type) || HasDeducedType(d.Type)) return "field-type";
                type = d.Type;
                return null;
            }

            private string CallType(int callee, int fn, int depth, out string type)
            {
                type = null;
                var doc = _doc;
                string name = doc.TokenText(callee);
                int before = Prev(callee);
                TypeEntry rec;
                if (doc.Is(before, Punct.Dot) || doc.Is(before, Punct.Arrow))
                {
                    string r = TypeOf(Prev(before), fn, depth + 1, out string left);
                    if (r != null) return r;
                    r = Target(left, doc.Is(before, Punct.Arrow), doc.Tokens[callee].Start, out rec);
                    if (r != null) return r;
                }
                else
                {
                    if (doc.Is(before, Punct.ColonColon) || doc.Is(before, Punct.Gt)) return "qualified-call";
                    if (IsMacro(callee, name)) return "macro";
                    if (DeclarationLikeCount(fn, name, callee) != 0) return "local-callable";
                    int offset = doc.Tokens[callee].Start;
                    string cls = doc.EnclosingClass(offset, out _);
                    if (cls == null) return "free-function";
                    rec = Record(cls, offset, out string why);
                    if (rec == null) return why;
                }
                string c = Certain(rec);
                if (c != null) return c;
                var hits = _s.MembersOf(rec).Members.Where(m => m.Decl.Name == name).ToList();
                if (hits.Count == 0) return "unresolved-call";
                int min = hits.Min(h => h.Depth);
                string ret = null;
                foreach (var h in hits)
                {
                    if (h.Depth != min) continue;
                    var d = h.Decl;
                    if (!TypeText.IsCallableKind(d.Kind) || d.Kind == SymbolKind.Constructor) return "not-a-method";
                    if ((d.Modifiers & DeclModifiers.Template) != 0 || !string.IsNullOrEmpty(d.TemplateParameters)) return "template-method";
                    string t = CleanReturn(d.Type);
                    if (t == null) return "return-type";
                    if (ret != null && ret != t) return "overloads-differ";
                    ret = t;
                }
                type = ret;
                return ret == null ? "return-type" : null;
            }

            private static string CleanReturn(string t)
            {
                if (string.IsNullOrWhiteSpace(t)) return null;
                t = t.Trim();
                foreach (var w in new[] { "virtual ", "static ", "inline ", "FORCEINLINE ", "explicit ", "constexpr ", "UE_NODISCARD ", "FORCENOINLINE " })
                    while (t.StartsWith(w, StringComparison.Ordinal)) t = t.Substring(w.Length).TrimStart();
                return t.Length == 0 || t == "void" || HasDeducedType(t) ? null : t;
            }

            private static bool HasDeducedType(string t) =>
                ContainsWord(t, "auto") || ContainsWord(t, "decltype") || ContainsWord(t, "typename") || t.IndexOf('(') >= 0 || t.IndexOf('[') >= 0;

            private static bool ContainsWord(string s, string w)
            {
                for (int i = s.IndexOf(w, StringComparison.Ordinal); i >= 0; i = s.IndexOf(w, i + 1, StringComparison.Ordinal))
                {
                    bool l = i == 0 || !TypeText.IsIdentPart(s[i - 1]);
                    bool r = i + w.Length >= s.Length || !TypeText.IsIdentPart(s[i + w.Length]);
                    if (l && r) return true;
                }
                return false;
            }

            /// <summary>
            /// How often <paramref name="name"/> appears in a declarator-like position (after a type name, *, &amp;, &gt; or [) in the function's
            /// parameters and body before <paramref name="upto"/>: catches lambda parameters, structured bindings and declarations the local
            /// scanner does not understand (any such shadowing makes the name uncertain).
            /// </summary>
            private int DeclarationLikeCount(int fn, string name, int upto)
            {
                var doc = _doc;
                int count = 0;
                for (int i = doc.TokenIndexFrom(doc.Decls[fn].Offset) + 1; i < upto && i < doc.Tokens.Length; i++)
                {
                    if (!doc.IsIdentifier(i) || doc.TokenText(i) != name) continue;
                    int b = Prev(i);
                    if (b < 0) continue;
                    var t = doc.Tokens[b];
                    if (t.Kind == TokenKind.Identifier || t.Is(Punct.Star) || t.Is(Punct.Amp) || t.Is(Punct.AmpAmp) || t.Is(Punct.Gt) ||
                        t.Is(Punct.GtGt) || t.Is(Punct.LBracket) || t.Kind == TokenKind.Keyword && TypeWords.Contains(doc.TokenText(b)))
                        count++;
                }
                return count;
            }

            // ------------------------------------------------------------ types

            /// <summary>The record reached by <c>.</c> / <c>-&gt;</c> on a value of <paramref name="typeText"/>, or why that is uncertain.</summary>
            private string Target(string typeText, bool arrow, int offset, out TypeEntry rec)
            {
                rec = null;
                if (string.IsNullOrWhiteSpace(typeText) || HasDeducedType(typeText)) return "deduced-type";
                var shape = TypeShape.Parse(typeText);
                string target;
                if (arrow)
                {
                    switch (shape.Shape)
                    {
                        case AccessShape.RawPointer:
                            if (shape.PointerDepth != 1) return "pointer-to-pointer";
                            target = shape.Pointee();
                            break;
                        case AccessShape.ObjectPtr:
                            target = shape.Arg(0);
                            break;
                        case AccessShape.SmartPointer:
                            if (!ArrowSmartPointers.Contains(shape.Head)) return "smart-pointer";
                            target = shape.Pointee();
                            break;
                        default:
                            return "arrow-on-value"; // operator-> (or a '.'/'->' mistake: not this rule)
                    }
                }
                else
                {
                    if (shape.Shape != AccessShape.Value) return "dot-on-pointer";
                    target = typeText;
                }
                if (string.IsNullOrWhiteSpace(target)) return "no-target";
                var ts = TypeShape.Parse(target);
                if (ts.Head == null) return "no-target";
                if (ts.PointerDepth != 0) return "pointer-to-pointer";
                if (ts.Args.Count > 0) return "template-type";
                if (ts.Head.Length == 1 || TypeText.IsBuiltin(ts.Head) || UeBuiltins.ApiOf(ts.Head) != null) return "builtin-type";
                rec = Record(ts.Head, offset, out string why);
                return rec == null ? why : null;
            }

            /// <summary>
            /// The single record definition a type name denotes: exactly one class / struct definition with that name (and qualifier)
            /// in the index and the buffer, no typedef / delegate / enum of the same name, and the navigation layer agrees.
            /// </summary>
            private TypeEntry Record(string typeName, int offset, out string reason)
            {
                string key = typeName;
                if (_records.TryGetValue(key, out var memo))
                {
                    reason = memo.Reason;
                    return memo.Type;
                }
                var t = ResolveRecord(typeName, out reason);
                _records[key] = (t, reason);
                return t;
            }

            private TypeEntry ResolveRecord(string typeName, out string reason)
            {
                reason = null;
                string name = BaseNames.StripTemplateArgs(typeName)?.Trim();
                if (string.IsNullOrEmpty(name) || name.IndexOf('<') >= 0) { reason = "template-type"; return null; }
                if (name.StartsWith("::", StringComparison.Ordinal)) name = name.Substring(2);
                string simple = TypeText.LastSegment(name);
                string qual = TypeText.Parent(name);
                var all = _s.Ctx.ByName(simple);
                var defs = new List<SymbolInfo>();
                foreach (var x in all)
                {
                    if (x.Kind == SymbolKind.Typedef || x.Kind == SymbolKind.Delegate || x.Kind == SymbolKind.Enum || x.Kind == SymbolKind.Macro || x.Kind == SymbolKind.Concept)
                    {
                        reason = "alias-or-other-type";
                        return null;
                    }
                    if (!TypeText.IsRecordKind(x.Kind) || !x.IsDefinition) continue;
                    if (qual != null && !TypeText.ContainerMatches(x.Container, qual)) continue;
                    defs.Add(x);
                }
                if (defs.Count == 0) { reason = "unknown-type"; return null; }
                var distinct = defs.Select(d => d.Location?.FilePath + "|" + d.Location?.Line + "|" + TypeText.Qualified(d.Container, d.Name)).Distinct().Count();
                if (distinct != 1) { reason = "ambiguous-type"; return null; }
                var t = _s.Ctx.FindType(name);
                var def = defs[0];
                if (t?.Symbol?.Location == null || def.Location == null || t.Symbol.Location.Line != def.Location.Line ||
                    !string.Equals(NavContext.NormalizePath(t.Symbol.Location.FilePath), NavContext.NormalizePath(def.Location.FilePath), StringComparison.Ordinal))
                {
                    reason = "type-mismatch";
                    return null;
                }
                return t;
            }

            /// <summary>
            /// null when the record and all of its bases are fully known: class body parsed, not a template, no templated / unindexed
            /// bases, no unknown macros or #include in the bodies, no inactive / anonymous parts, GENERATED_BODY where reflected.
            /// </summary>
            private string Certain(TypeEntry root)
            {
                string key = TypeText.Qualified(root.Symbol.Container, root.Symbol.Name);
                if (_typeCertainty.TryGetValue(key, out var memo)) return memo;
                string r = ComputeCertain(root);
                _typeCertainty[key] = r;
                return r;
            }

            private string ComputeCertain(TypeEntry root)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<TypeEntry>();
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    _s.Ct.ThrowIfCancellationRequested();
                    var t = queue.Dequeue();
                    string q = TypeText.Qualified(t.Symbol.Container, t.Symbol.Name);
                    if (!seen.Add(q)) continue;
                    if (seen.Count > MaxLineage) return "deep-lineage";
                    var (doc, ci) = _s.ClassDecl(t);
                    if (doc == null) return "class-body-unavailable";
                    if (_unsaved.Count > 0 && !ReferenceEquals(doc, _doc) && _unsaved.Contains(NavContext.NormalizePath(doc.Path))) return "unsaved-declaration";
                    var d = doc.Decls[ci];
                    if ((d.Modifiers & DeclModifiers.Template) != 0 || !string.IsNullOrEmpty(d.TemplateParameters)) return "template-type";
                    if (d.IsConditional) return "conditional-type";
                    if ((d.Ue & (UeFlags.UClass | UeFlags.UStruct | UeFlags.UInterface)) != 0 && (d.Ue & UeFlags.GeneratedBody) == 0) return "no-generated-body";
                    if (TypeText.HasSpecifier(d.Specifiers, "SparseClassDataTypes") || (d.Specifiers ?? string.Empty).IndexOf("SparseClassData", StringComparison.Ordinal) >= 0)
                        return "sparse-class-data";
                    string body = _p.BodyScan(_s, doc, ci);
                    if (body != null) return body;
                    var bases = d.Bases ?? t.Bases ?? Array.Empty<string>();
                    foreach (var raw in bases)
                    {
                        string b = (raw ?? string.Empty).Trim();
                        if (b.Length == 0) continue;
                        if (b.IndexOf('<') >= 0) return "template-base";
                        var be = Record(b, -1, out string why);
                        if (be == null) return "base:" + why;
                        // the member table walks Ctx.FindType(base): it must be the same class
                        var nav = _s.Ctx.FindType(b);
                        if (nav == null || !ReferenceEquals(nav, be) && TypeText.Qualified(nav.Symbol.Container, nav.Symbol.Name) != TypeText.Qualified(be.Symbol.Container, be.Symbol.Name))
                            return "base-mismatch";
                        queue.Enqueue(be);
                    }
                }
                return null;
            }

            // ============================================================ UE0003 GENERATED_BODY missing

            public void GeneratedBody(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                if (!IsHeader(doc.Path)) return;
                for (int i = 0; i < doc.Decls.Count; i++)
                {
                    var d = doc.Decls[i];
                    if (d.IsImplicit || !TypeText.IsRecordKind(d.Kind) || d.Body.IsNone) continue;
                    var reflected = d.Ue & (UeFlags.UClass | UeFlags.UStruct | UeFlags.UInterface);
                    if (reflected == UeFlags.None) continue;
                    if (d.Offset < start || d.Offset >= end) continue;
                    _stats.Checked++;
                    if ((d.Ue & UeFlags.GeneratedBody) != 0) continue;
                    if (_caret >= d.Offset && _caret <= d.Body.End) { _stats.Skip("typing-type"); continue; }
                    if (d.IsConditional || InInactive(doc, d.Offset)) { _stats.Skip("conditional-type"); continue; }
                    if (!string.IsNullOrEmpty(d.TemplateParameters)) { _stats.Skip("template-type"); continue; }
                    // a macro of ours might expand to GENERATED_BODY(): any unknown macro in the body makes it uncertain
                    string scan = _p.BodyScan(_s, doc, i);
                    if (scan != null) { _stats.Skip(scan); continue; }
                    string macro = (reflected & UeFlags.UClass) != 0 ? "UCLASS" : (reflected & UeFlags.UStruct) != 0 ? "USTRUCT" : "UINTERFACE";
                    _stats.MissingGeneratedBody++;
                    int len = d.NameLength > 0 ? d.NameLength : System.Text.Encoding.UTF8.GetByteCount(d.Name);
                    result.Add(Make(d.Offset, len, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.MissingGeneratedBody,
                        $"{macro} type '{d.Name}' is missing GENERATED_BODY()", macro, d.Name));
                }
            }

            private static bool IsHeader(string path)
            {
                string ext = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
                return ext == ".h" || ext == ".hpp";
            }

            // ============================================================ helpers

            private int Prev(int i)
            {
                i--;
                while (i >= 0 && _doc.Tokens[i].Kind == TokenKind.DocComment) i--;
                return i;
            }

            private CodeDiagnostic Make(int offset, int byteLength, CodeDiagnosticSeverity severity, string code, string message, params string[] args)
            {
                var doc = _doc;
                var (line, col) = doc.PositionOf(offset);
                int len = LineMap.Utf16Length(new ReadOnlySpan<byte>(doc.Text, offset, Math.Max(0, Math.Min(byteLength, doc.Text.Length - offset))));
                return new CodeDiagnostic { Line = line, Column = col, Length = Math.Max(1, len), Severity = severity, Code = code, Message = message, Args = args.ToList() };
            }
        }

        /// <summary>No lower-case letters, at least one upper-case letter, two characters or more (UCLASS, MY_MACRO).</summary>
        internal static bool IsMacroSpelling(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 2) return false;
            bool upper = false;
            foreach (char c in name)
            {
                if (char.IsLower(c)) return false;
                if (char.IsUpper(c)) upper = true;
            }
            return upper;
        }

        internal static bool InInactive(DocumentModel doc, int offset)
        {
            foreach (var r in doc.Summary.InactiveRanges)
                if (offset >= r.Start && offset < r.End) return true;
            return false;
        }

        /// <summary>
        /// Body problems that hide members from the parser (cached per parsed document and class): an #include inside the body, a union
        /// or anonymous member record, an inactive preprocessor branch, or an unknown macro at class scope (it might declare members).
        /// Known UE macros (reflection, GENERATED_BODY, attributes, export macros, declaration macros) are understood.
        /// </summary>
        internal string BodyScan(IsSession s, DocumentModel doc, int classDecl)
        {
            var cache = _bodyScans.GetValue(doc, _ => new Dictionary<int, string>());
            lock (cache)
            {
                if (cache.TryGetValue(classDecl, out var r)) return r;
            }
            string result = ComputeBodyScan(s, doc, classDecl);
            lock (cache) cache[classDecl] = result;
            return result;
        }

        private static string ComputeBodyScan(IsSession s, DocumentModel doc, int ci)
        {
            var d = doc.Decls[ci];
            var body = d.Body;
            foreach (var r in doc.Summary.InactiveRanges)
                if (r.Start < body.End && r.End > body.Start) return "inactive-branch";
            for (int i = ci + 1; i < doc.Decls.Count; i++)
            {
                var m = doc.Decls[i];
                if (m.Parent == ci && string.IsNullOrEmpty(m.Name)) return "anonymous-member";
                if (m.Offset >= body.End) break;
            }
            int depth = 0, paren = 0;
            for (int k = doc.TokenIndexFrom(body.Start); k < doc.Tokens.Length && doc.Tokens[k].Start < body.End; k++)
            {
                var t = doc.Tokens[k];
                if (t.Kind == TokenKind.Directive)
                {
                    var dk = t.Directive;
                    if (dk == DirectiveKind.Include || dk == DirectiveKind.IncludeNext || dk == DirectiveKind.Import) return "include-in-body";
                    continue;
                }
                if (t.Is(Keyword.Union)) return "union";
                if (t.Is(Punct.LBrace)) { depth++; continue; }
                if (t.Is(Punct.RBrace)) { depth--; continue; }
                if (t.Is(Punct.LParen)) { paren++; continue; }
                if (t.Is(Punct.RParen)) { paren--; continue; }
                if (depth != 1 || paren != 0 || t.Kind != TokenKind.Identifier) continue;
                string name = doc.TokenText(k);
                if ((t.Flags & TokenFlags.MacroLike) == 0 && !IsMacroSpelling(name)) continue;
                if (s.Knowledge.Lookup(name) != null || s.Knowledge.IsExportMacro(name)) continue;
                // an all-caps type (TCHAR, FVector-like typedefs) is fine; anything else may be a member-declaring macro
                if (TypeText.IsBuiltin(name)) continue;
                bool isType = false, isMacro = false;
                foreach (var x in s.Ctx.ByName(name))
                {
                    if (x.Kind == SymbolKind.Macro) isMacro = true;
                    else if (TypeText.IsTypeKind(x.Kind) || x.Kind == SymbolKind.Typedef) isType = true;
                }
                if (isType && !isMacro) continue;
                return "unknown-macro";
            }
            return null;
        }
    }
}
