using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Engine.Includes;
using UEToVS.Engine.Index;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// v0.11.0 name rules (design: 后续规划-代码诊断与提示 §4.4):
    /// <list type="bullet">
    /// <item>UE0022 (warning) a type used where its definition is needed (<c>X::</c>, <c>Cast&lt;X&gt;</c>, <c>X Value;</c>, <c>X(…)</c>,
    /// a base class) whose only definition lives in a header outside the file's include closure (+ the module's private PCH for a
    /// .cpp). Pointer / reference uses are never reported (a forward declaration is enough). Silent when the closure is capped, a
    /// reached file's include list is unknown, or an unsaved file is part of it.</item>
    /// <item>UE0023 (error) a name used in a function body that exists nowhere: no local, parameter, member of a fully known class,
    /// declaration in the file or symbol of any kind in the index. Skipped in templates, macro arguments, inactive code, for
    /// all-lower-case names (C runtime / std), names with '_' or macro spelling, while other files have unsaved changes, and when
    /// the engine is not indexed.</item>
    /// </list>
    /// </summary>
    public sealed partial class DiagnosticsProvider
    {
        /// <summary>Templates whose type argument must be complete (they call StaticClass() or need sizeof).</summary>
        private static readonly HashSet<string> CompleteTypeTemplates = new HashSet<string>(StringComparer.Ordinal)
        {
            "Cast", "CastChecked", "ExactCast", "NewObject", "CreateDefaultSubobject", "SpawnActor", "SpawnActorDeferred", "GetSubsystem",
            "GetGameInstanceSubsystem", "GetWorldSubsystem", "GetLocalPlayerSubsystem", "GetEngineSubsystem", "FindComponentByClass",
            "GetComponentByClass", "LoadObject", "FindObject", "GetMutableDefault", "GetDefault", "MakeShared", "MakeUnique", "CreateWidget",
        };

        /// <summary>Folders of the platform layer: reached through COMPILED_PLATFORM_HEADER / per-platform includes the closure cannot follow exactly.</summary>
        private static readonly string[] PlatformFolders =
        {
            "/windows/", "/microsoft/", "/unix/", "/linux/", "/mac/", "/apple/", "/ios/", "/android/", "/clang/", "/msvc/", "/gnuc/",
        };

        private const int MaxNameChecks = 600;

        private sealed partial class Pass
        {
            // ============================================================ shared

            private Dictionary<string, int> _declLike;

            /// <summary>Names written anywhere in the file in a declarator-like position (after a type, *, &amp;, class / struct / enum…).</summary>
            private bool DeclaredLike(string name)
            {
                if (_declLike == null)
                {
                    var doc = _doc;
                    _declLike = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 1; i < doc.Tokens.Length; i++)
                    {
                        if (!doc.IsIdentifier(i)) continue;
                        int b = Prev(i);
                        if (b < 0) continue;
                        var t = doc.Tokens[b];
                        bool like = t.Kind == TokenKind.Identifier || t.Is(Punct.Star) || t.Is(Punct.Amp) || t.Is(Punct.AmpAmp) || t.Is(Punct.Gt) ||
                                    t.Is(Punct.GtGt) || t.Is(Punct.LBracket) || t.Is(Keyword.Class) || t.Is(Keyword.Struct) || t.Is(Keyword.Enum) ||
                                    t.Is(Keyword.Union) || t.Is(Keyword.Typename) || t.Is(Keyword.Namespace) || t.Is(Keyword.Using) || t.Is(Keyword.Goto) ||
                                    t.Kind == TokenKind.Keyword && TypeWords.Contains(doc.TokenText(b));
                        // enumerators: "{ A, B = 2 }" inside an enum body are declared by the parser already; labels "Name:"
                        if (!like && doc.Is(i + 1, Punct.Colon) && !doc.Is(i + 1, Punct.ColonColon)) like = true;
                        if (!like) continue;
                        string n = doc.TokenText(i);
                        _declLike.TryGetValue(n, out int c);
                        _declLike[n] = c + 1;
                    }
                }
                return _declLike.ContainsKey(name);
            }

            private HashSet<string> _docNames;

            private bool DeclaredInDoc(string name)
            {
                if (_docNames == null)
                {
                    _docNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var d in _doc.Decls)
                        if (!string.IsNullOrEmpty(d.Name)) _docNames.Add(d.Name);
                }
                return _docNames.Contains(name);
            }

            /// <summary>Inside the arguments of any macro call (transparent ones included: their arguments may name log categories, stats…).</summary>
            private bool InAnyMacroArgument(int k, int bodyStart)
            {
                var doc = _doc;
                int depth = 0, steps = 0;
                for (int i = k - 1; i >= 0 && doc.Tokens[i].Start >= bodyStart; i--)
                {
                    if (++steps > MaxMacroScan) return true;
                    var t = doc.Tokens[i];
                    if (t.Is(Punct.RParen)) depth++;
                    else if (t.Is(Punct.LParen))
                    {
                        if (depth > 0) { depth--; continue; }
                        int callee = Prev(i);
                        if (doc.IsIdentifier(callee) && IsMacro(callee, doc.TokenText(callee))) return true;
                    }
                }
                return false;
            }

            private bool OtherFilesUnsaved()
            {
                if (_unsaved.Count == 0) return false;
                string self = NavContext.NormalizePath(_doc.Path);
                return _unsaved.Any(u => u != self);
            }

            private bool? _engineKnown;

            /// <summary>The index holds the engine and every module this one can see (otherwise their names would look unknown / not included).</summary>
            private bool EngineKnown()
            {
                if (_engineKnown.HasValue) return _engineKnown.Value;
                bool ready = _p.IndexReady == null || _p.IndexReady();
                var model = _s.Ctx.Files?.Includes;
                // needs the module catalog: without it (loose files, a solution that is not UE) there is no telling what is indexed
                var module = model?.Catalog != null ? model.ModuleOf(_doc.Path) : null;
                _engineKnown = ready && module != null && model.EngineIndexed && model.DependenciesIndexed(module);
                return _engineKnown.Value;
            }

            // ============================================================ UE0023 unknown identifier

            public void UnknownIdentifiers(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                if (!EngineKnown()) return;
                if (OtherFilesUnsaved()) { _stats.Skip("ue0023:unsaved-files"); return; }
                var verdicts = new Dictionary<string, bool>(StringComparer.Ordinal); // name → known
                int checks = 0;
                for (int k = doc.TokenIndexFrom(start); k < doc.Tokens.Length && doc.Tokens[k].Start < end; k++)
                {
                    if (!doc.IsIdentifier(k)) continue;
                    var t = doc.Tokens[k];
                    if ((t.Flags & TokenFlags.Conditional) != 0) continue;
                    if (_caret >= t.Start && _caret <= t.End) continue;
                    string name = doc.TokenText(k);
                    if (!Candidate(name)) continue;
                    int before = Prev(k);
                    if (doc.Is(before, Punct.Dot) || doc.Is(before, Punct.Arrow) || doc.Is(before, Punct.ColonColon) || doc.Is(before, Punct.Tilde)) continue;
                    if (doc.Is(before, Keyword.Goto) || doc.Is(before, Keyword.Operator)) continue;
                    if (doc.Is(k + 1, Punct.Colon)) continue; // label / bit-field / case
                    int fn = doc.EnclosingFunction(t.Start);
                    if (fn < 0 || t.Start <= doc.Decls[fn].Body.Start) continue;
                    if (verdicts.TryGetValue(name, out bool known) && known) continue;
                    if (InInactive(doc, t.Start)) continue;
                    _s.Ct.ThrowIfCancellationRequested();
                    if (++checks > MaxNameChecks) { _stats.Skip("ue0023:budget"); return; }
                    _stats.Checked++;
                    string why = TemplateContext(t.Start);
                    if (why != null) { _stats.Skip("ue0023:" + why); continue; }
                    if (InAnyMacroArgument(k, doc.Decls[fn].Body.Start)) { _stats.Skip("ue0023:macro-argument"); continue; }
                    if (doc.FindLocal(fn, name, t.Start) != null) continue;
                    if (!verdicts.TryGetValue(name, out known))
                    {
                        known = DeclaredInDoc(name) || DeclaredLike(name) || IsDefinedMacro(name) || _s.Ctx.ByName(name).Count > 0;
                        verdicts[name] = known;
                    }
                    if (known) continue;
                    string cls = doc.EnclosingClass(t.Start, out _);
                    TypeEntry rec = null;
                    if (cls != null)
                    {
                        rec = Record(cls, t.Start, out string r);
                        if (rec == null) { _stats.Skip("ue0023:class:" + r); continue; }
                        r = Certain(rec);
                        if (r != null) { _stats.Skip("ue0023:class:" + r); continue; }
                        if (_s.MembersOf(rec).Members.Any(m => m.Decl.Name == name)) { verdicts[name] = true; continue; }
                    }
                    var suggestions = Suggestions(name, fn, t.Start, rec);
                    var d = Make(t.Start, t.Length, CodeDiagnosticSeverity.Error, CodeDiagnosticCodes.UnknownIdentifier,
                        $"identifier '{name}' is undefined", name, string.Join("|", suggestions));
                    if (suggestions.Count > 0) AddReplaceFix(d, t.Start, t.Length, suggestions[0]);
                    for (int s = 1; s < suggestions.Count; s++) AddReplaceFix(d, t.Start, t.Length, suggestions[s]);
                    Report(result, d);
                }
            }

            /// <summary>Names worth checking: not C runtime / std style (all lower case), not macro-made (TAG_X, STAT_X), not UHT names.</summary>
            private bool Candidate(string name)
            {
                if (name.Length < 2 || name[0] == '_' || name.IndexOf('_') >= 0) return false;
                bool upper = false;
                foreach (char c in name) if (char.IsUpper(c)) { upper = true; break; }
                if (!upper) return false;
                if (IsMacroSpelling(name) || IsGeneratedLooking(name) || name == "Super" || name == "ThisClass" || name == "ThisModule") return false;
                if (TypeText.IsBuiltin(name) || UeBuiltins.ApiOf(name) != null) return false;
                if (_s.Knowledge.Lookup(name) != null || _s.Knowledge.IsExportMacro(name)) return false;
                return true;
            }

            /// <summary>Up to three close spellings: locals and parameters, members of the class, the file's own declarations.</summary>
            private List<string> Suggestions(string name, int fn, int offset, TypeEntry rec)
            {
                var pool = new HashSet<string>(StringComparer.Ordinal);
                foreach (var l in _doc.LocalsOf(fn))
                    if (l.Offset < offset) pool.Add(l.Name);
                if (rec != null)
                    foreach (var m in _s.MembersOf(rec).Members) pool.Add(m.Decl.Name);
                foreach (var d in _doc.Decls)
                    if (!string.IsNullOrEmpty(d.Name) && !d.IsImplicit) pool.Add(d.Name);
                int max = name.Length <= 4 ? 1 : 2;
                return pool.Where(p => !string.IsNullOrEmpty(p) && p != name && Math.Abs(p.Length - name.Length) <= max)
                    .Select(p => (Name: p, D: Osa(name, p, max)))
                    .Where(x => x.D >= 0)
                    .OrderBy(x => x.D).ThenBy(x => x.Name, StringComparer.Ordinal)
                    .Take(3).Select(x => x.Name).ToList();
            }

            /// <summary>Optimal-string-alignment distance (case differences cost 1), -1 above <paramref name="max"/>.</summary>
            internal static int Osa(string a, string b, int max)
            {
                int n = a.Length, m = b.Length;
                if (Math.Abs(n - m) > max) return -1;
                var d = new int[n + 1, m + 1];
                for (int i = 0; i <= n; i++) d[i, 0] = i;
                for (int j = 0; j <= m; j++) d[0, j] = j;
                for (int i = 1; i <= n; i++)
                {
                    int rowMin = int.MaxValue;
                    for (int j = 1; j <= m; j++)
                    {
                        int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                        int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                        if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                        d[i, j] = v;
                        if (v < rowMin) rowMin = v;
                    }
                    if (rowMin > max) return -1;
                }
                return d[n, m] <= max ? d[n, m] : -1;
            }

            // ============================================================ UE0022 missing include

            private IncludeClosure _closure;
            private string _closureSkip;
            private bool _closureDone;

            /// <summary>The file's include closure (+ the module's private PCH for a .cpp), or null with <see cref="_closureSkip"/> set.</summary>
            private IncludeClosure Closure()
            {
                if (_closureDone) return _closure;
                _closureDone = true;
                var doc = _doc;
                var model = _s.Ctx.Files?.Includes;
                if (model?.Catalog == null || model.Graph.IncludesOf == null) { _closureSkip = "no-model"; return null; }
                var module = model.ModuleOf(doc.Path);
                if (module == null) { _closureSkip = "no-module"; return null; }
                var direct = new List<string>();
                foreach (var inc in doc.Summary.Includes)
                    if (!InInactive(doc, inc.Offset) && !string.IsNullOrWhiteSpace(inc.Path)) direct.Add(inc.Path);
                if (!IsHeader(doc.Path) && !string.IsNullOrEmpty(module.PrivatePchHeader))
                {
                    string pch = System.IO.Path.IsPathRooted(module.PrivatePchHeader) ? module.PrivatePchHeader : System.IO.Path.Combine(module.Directory, module.PrivatePchHeader);
                    direct.Add(pch);
                }
                var c = model.Graph.Closure(doc.Path, direct, _s.Ct);
                if (c.Capped) { _closureSkip = "closure-capped"; return null; }
                if (c.UnknownLists > 0) { _closureSkip = "closure-unknown-lists"; return null; }
                string self = NavContext.NormalizePath(doc.Path);
                foreach (var u in _unsaved)
                    if (u != self && c.Files.Contains(u)) { _closureSkip = "closure-unsaved"; return null; }
                _closure = c;
                return c;
            }

            public void MissingIncludes(int start, int end, List<CodeDiagnostic> result)
            {
                var doc = _doc;
                var model = _s.Ctx.Files?.Includes;
                if (model?.Catalog == null || model.Graph.IncludesOf == null) return;
                if (!EngineKnown()) return;
                var done = new Dictionary<string, (string Header, bool Report)>(StringComparer.Ordinal);
                for (int k = doc.TokenIndexFrom(start); k < doc.Tokens.Length && doc.Tokens[k].Start < end; k++)
                {
                    if (!doc.IsIdentifier(k)) continue;
                    var t = doc.Tokens[k];
                    if ((t.Flags & TokenFlags.Conditional) != 0) continue;
                    if (_caret >= t.Start && _caret <= t.End) continue;
                    string name = doc.TokenText(k);
                    if (name.Length < 2 || !char.IsUpper(name[0])) continue;
                    if (done.TryGetValue(name, out var prior) && (!prior.Report || prior.Header == null)) continue;
                    if (!NeedsDefinition(k)) continue;
                    if (InInactive(doc, t.Start)) continue;
                    if (prior.Header != null) continue; // reported once per name
                    _s.Ct.ThrowIfCancellationRequested();
                    _stats.Checked++;
                    var c = Closure();
                    if (c == null) { _stats.Skip("ue0022:" + _closureSkip); return; }
                    string reason = DefiningHeader(name, c, out string header);
                    if (reason != null)
                    {
                        done[name] = (null, false);
                        if (reason != Found) _stats.Skip("ue0022:" + reason);
                        continue;
                    }
                    string spelling = model.Spelling(header, doc.Path);
                    if (string.IsNullOrEmpty(spelling)) { done[name] = (null, false); _stats.Skip("ue0022:no-spelling"); continue; }
                    done[name] = (header, true);
                    string kind = ForwardDeclaredHere(name) ? "incomplete" : "missing";
                    var d = Make(t.Start, t.Length, CodeDiagnosticSeverity.Warning, CodeDiagnosticCodes.MissingInclude,
                        kind == "incomplete"
                            ? $"'{name}' is only forward-declared here; include \"{spelling}\""
                            : $"'{name}' is declared in \"{spelling}\", which this file does not include",
                        name, spelling, kind);
                    (d.Fixes ??= new List<CodeDiagnosticFix>()).Add(new CodeDiagnosticFix { Kind = "add-include", Args = { spelling } });
                    Report(result, d);
                }
            }

            /// <summary>Whether the identifier at <paramref name="k"/> is used in a way that needs the type's definition.</summary>
            private bool NeedsDefinition(int k)
            {
                var doc = _doc;
                int b = Prev(k);
                if (doc.Is(b, Punct.Dot) || doc.Is(b, Punct.Arrow) || doc.Is(b, Punct.ColonColon) || doc.Is(b, Punct.Tilde)) return false;
                // X::Member / X::StaticClass()
                if (doc.Is(k + 1, Punct.ColonColon)) return true;
                // Cast<X>(…), NewObject<X>(…)
                if (doc.Is(b, Punct.Lt) && (doc.Is(k + 1, Punct.Gt) || doc.Is(k + 1, Punct.GtGt)) && doc.IsIdentifier(Prev(b)) &&
                    CompleteTypeTemplates.Contains(doc.TokenText(Prev(b))))
                    return true;
                // class A : public X {  /  , public X
                if ((doc.Is(b, Keyword.Public) || doc.Is(b, Keyword.Protected) || doc.Is(b, Keyword.Private)) &&
                    (doc.Is(k + 1, Punct.LBrace) || doc.Is(k + 1, Punct.Comma)))
                    return true;
                int fn = doc.EnclosingFunction(doc.Tokens[k].Start);
                bool inBody = fn >= 0 && doc.Tokens[k].Start > doc.Decls[fn].Body.Start;
                // X Value; / X Value = … / X Value{…} (and X Value(…) in a body: a construction, not a function declaration)
                bool stmtStart = b < 0 || doc.Is(b, Punct.Semicolon) || doc.Is(b, Punct.LBrace) || doc.Is(b, Punct.RBrace) || doc.Is(b, Keyword.Const) ||
                                 doc.Is(b, Keyword.Static) || doc.Is(b, Keyword.Mutable) || doc.Is(b, Punct.Colon) && !inBody;
                if (stmtStart && doc.IsIdentifier(k + 1))
                {
                    int n = k + 2;
                    if (doc.Is(n, Punct.Semicolon) || doc.Is(n, Punct.Eq) || doc.Is(n, Punct.LBrace) || doc.Is(n, Punct.LBracket)) return true;
                    if (inBody && doc.Is(n, Punct.LParen)) return true;
                    return false;
                }
                // new X / X(…) / X{…} in a body: a construction (only when X is a type: checked by the caller's lookup)
                if (inBody && (doc.Is(b, Keyword.New) || (doc.Is(k + 1, Punct.LParen) || doc.Is(k + 1, Punct.LBrace)) && !doc.IsIdentifier(b) && !doc.Is(b, Punct.Gt)))
                    return true;
                return false;
            }

            /// <summary>
            /// null with <paramref name="header"/> = the single header defining type <paramref name="name"/>, outside the closure;
            /// <see cref="Found"/> = visible (or declared here); anything else = why it is uncertain.
            /// </summary>
            private string DefiningHeader(string name, IncludeClosure closure, out string header)
            {
                header = null;
                if (DeclaredInDoc(name)) return Found;
                var defs = new List<SymbolInfo>();
                foreach (var x in _s.Ctx.ByName(name))
                {
                    var file = x.Location?.FilePath;
                    if (file == null) continue;
                    if (closure.Contains(file) || _s.Ctx.IsOverlayPath(file)) return Found;
                    switch (x.Kind)
                    {
                        case SymbolKind.Namespace:
                        case SymbolKind.Macro: return "namespace-or-macro";
                        case SymbolKind.Constructor:
                        case SymbolKind.Destructor: continue;
                    }
                    if (TypeText.IsRecordKind(x.Kind))
                    {
                        if (x.IsDefinition) defs.Add(x);
                        continue;
                    }
                    if (x.Kind == SymbolKind.Enum || x.Kind == SymbolKind.Typedef || x.Kind == SymbolKind.Delegate) { defs.Add(x); continue; }
                    // a function / variable with the same name at namespace scope: "X(…)" may be a call
                    if (string.IsNullOrEmpty(x.Container)) return "same-name-non-type";
                }
                if (defs.Count == 0) return "not-a-type";
                var files = defs.Select(d => NavContext.NormalizePath(d.Location.FilePath)).Distinct().ToList();
                if (files.Count != 1) return "several-headers";
                string norm = files[0];
                if (!IsHeader(norm) && !norm.EndsWith(".inl", StringComparison.Ordinal)) return "defined-in-source";
                if (norm.EndsWith(".generated.h", StringComparison.Ordinal) || norm.Contains("/intermediate/")) return "generated";
                foreach (var f in PlatformFolders)
                    if (norm.IndexOf(f, StringComparison.Ordinal) >= 0) return "platform-header";
                header = defs[0].Location.FilePath;
                return null;
            }

            /// <summary><c>class X;</c> / <c>struct X;</c> in this file.</summary>
            private bool ForwardDeclaredHere(string name)
            {
                var doc = _doc;
                for (int i = 1; i + 1 < doc.Tokens.Length; i++)
                {
                    if (!doc.IsIdentifier(i) || !doc.Is(i + 1, Punct.Semicolon)) continue;
                    if (!doc.Is(i - 1, Keyword.Class) && !doc.Is(i - 1, Keyword.Struct)) continue;
                    if (doc.TokenText(i) == name) return true;
                }
                return false;
            }
        }
    }
}
