using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Matching;
using UEToVS.Engine.Index;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// Builds completion lists for every <see cref="CompletionContextKind"/>. Ranking (VA-like): exact &gt; prefix &gt; camel-hump
    /// match, then scope proximity (locals &gt; own members &gt; base members &gt; namespace &gt; global &gt; keywords), project over
    /// engine, UE-reflected symbols slightly boosted, shorter names first. Thread-safe; shared caches are bounded.
    /// </summary>
    public sealed class CompletionEngine
    {
        // proximity bands (added to MatchRank * RankUnit)
        private const int RankUnit = 10000;
        private const int PLocal = 9000;
        private const int PMember = 8000;
        private const int PNamespace = 5000;
        private const int PGlobal = 4000;
        private const int PKeyword = 3500;
        private const int PSnippet = 3400;

        private readonly GlobalSearchCache _globals = new GlobalSearchCache();
        private readonly IncludeTable _includes = new IncludeTable();

        public int GlobalSearchMax { get; set; } = 150;

        public long GlobalCacheHits => _globals.Hits;

        /// <summary>Per-request item collector: dedupes by label + family and keeps the best score.</summary>
        private sealed class Collector
        {
            private readonly Dictionary<string, CompletionItem> _items = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
            private readonly FuzzyMatcher _matcher;
            private readonly string _prefix;

            public Collector(string prefix)
            {
                _prefix = prefix ?? string.Empty;
                _matcher = _prefix.Length > 0 ? new FuzzyMatcher(_prefix) : null;
            }

            public int Count => _items.Count;
            public bool HasPrefix => _matcher != null;

            /// <summary>Match rank: 3 exact, 2 prefix, 1 camel humps (0 when there is no prefix); -1 = filtered out.</summary>
            public int Rank(string name, bool unreal, out bool caseExact)
            {
                caseExact = false;
                if (_matcher == null) return 0;
                if (string.IsNullOrEmpty(name)) return -1;
                var m = unreal ? _matcher.MatchUnreal(name) : _matcher.Match(name);
                switch (m.Kind)
                {
                    case MatchKind.Exact:
                        caseExact = string.Equals(name, _prefix, StringComparison.Ordinal);
                        return 3;
                    case MatchKind.Prefix:
                        caseExact = name.StartsWith(_prefix, StringComparison.Ordinal) || name.Length > 1 && name.Substring(1).StartsWith(_prefix, StringComparison.Ordinal);
                        return 2;
                    case MatchKind.CamelHump:
                        return 1;
                    default:
                        return -1;
                }
            }

            public void Add(CompletionItem item, string family)
            {
                string key = item.Label + "\u0001" + family;
                if (_items.TryGetValue(key, out var old) && old.Score >= item.Score) return;
                _items[key] = item;
            }

            public List<CompletionItem> Sorted()
            {
                var list = _items.Values.ToList();
                list.Sort((a, b) =>
                {
                    int c = b.Score.CompareTo(a.Score);
                    return c != 0 ? c : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
                });
                return list;
            }
        }

        public CompletionResponse Complete(IsSession s, CompletionSite site, CompletionRequest request, Stopwatch sw, out string resolvedType)
        {
            resolvedType = null;
            var resp = new CompletionResponse { Context = site.Kind };
            var doc = s.Doc;
            var (line, col) = doc.PositionOf(site.ReplaceStart);
            resp.ReplaceColumn = col;
            resp.ReplaceLength = Utf16Length(doc, site.ReplaceStart, site.ReplaceEnd);
            if (site.Suppressed) return resp;

            int max = request?.MaxResults > 0 ? request.MaxResults : 500;
            var c = new Collector(site.Prefix);
            bool incomplete = false;
            switch (site.Kind)
            {
                case CompletionContextKind.Member:
                    resolvedType = Member(s, site, c, resp);
                    break;
                case CompletionContextKind.Scope:
                    resolvedType = Scope(s, site, c, ref incomplete);
                    break;
                case CompletionContextKind.Specifier:
                    resolvedType = site.SpecifierMacro + (site.InMeta ? ".meta" : string.Empty);
                    Specifiers(s, site, c);
                    break;
                case CompletionContextKind.Include:
                    Includes(s, site, c);
                    break;
                case CompletionContextKind.Override:
                    resolvedType = Override(s, site, c);
                    break;
                default:
                    General(s, site, c, request?.Explicit == true, ref incomplete);
                    break;
            }
            var sorted = c.Sorted();
            if (sorted.Count > max)
            {
                sorted.RemoveRange(max, sorted.Count - max);
                incomplete = true;
            }
            resp.Items = sorted;
            resp.IsIncomplete = incomplete;
            resp.ResolvedType = resolvedType;
            _ = line;
            _ = sw;
            return resp;
        }

        private static int Utf16Length(DocumentModel doc, int start, int end)
        {
            if (end <= start) return 0;
            return LineMap.Utf16Length(new ReadOnlySpan<byte>(doc.Text, start, end - start));
        }

        // ================================================================== items

        private static int Proximity(int band, string name, ItemOrigin origin, UeFlags ue, bool caseExact, bool projectMatters)
        {
            int p = band;
            if (projectMatters && TypeText.IsProject(origin)) p += 300;
            if (ue != UeFlags.None && ue != UeFlags.GeneratedBody) p += 200;
            if (caseExact) p += 100;
            p -= Math.Min(name?.Length ?? 0, 50) * 2;
            return p;
        }

        private static string Truncate(string s, int max = 160)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        /// <summary>Right-aligned detail of a declaration: signature for functions, type for variables.</summary>
        public static string DetailOf(Declaration d)
        {
            switch (d.Kind)
            {
                case SymbolKind.Function:
                case SymbolKind.Method:
                case SymbolKind.Constructor:
                case SymbolKind.Destructor:
                    {
                        string ret = string.IsNullOrEmpty(d.Type) ? string.Empty : d.Type + " ";
                        string cst = (d.Modifiers & DeclModifiers.Const) != 0 ? " const" : string.Empty;
                        return Truncate(ret + d.Name + "(" + (d.Parameters ?? string.Empty) + ")" + cst);
                    }
                case SymbolKind.Field:
                case SymbolKind.Variable:
                case SymbolKind.Typedef:
                    return Truncate(d.Type ?? d.Signature);
                case SymbolKind.EnumValue:
                    return Truncate(d.Value != null ? d.QualifiedName + " = " + d.Value : d.QualifiedName);
                default:
                    return Truncate(d.Signature);
            }
        }

        private static CompletionItem FromDecl(DocumentModel doc, int index, string container, int depth, int score, NavContext ctx)
        {
            var d = doc.Decls[index];
            return new CompletionItem
            {
                Label = d.Name,
                InsertText = d.Name,
                Kind = d.Kind,
                Ue = d.Ue,
                Detail = DetailOf(d),
                Container = string.IsNullOrEmpty(container) ? null : container,
                IsInherited = depth > 0,
                Score = score,
                Location = new SourceLocation { FilePath = doc.Path, Line = d.Line, Column = d.Column },
            };
        }

        private static CompletionItem FromSymbol(SymbolInfo s, int score) => new CompletionItem
        {
            Label = s.Name,
            InsertText = s.Name,
            Kind = s.Kind,
            Ue = s.Ue,
            Detail = Truncate(s.Signature),
            Container = s.Container,
            Score = score,
            Location = s.Location,
        };

        private static string Family(SymbolKind k) => TypeText.IsCallableKind(k) ? "f" : TypeText.IsTypeKind(k) ? "t" : k == SymbolKind.Macro ? "m" : "v";

        // ================================================================== member: a.b / p->b

        private string Member(IsSession s, CompletionSite site, Collector c, CompletionResponse resp)
        {
            var doc = s.Doc;
            string cls = doc.EnclosingClass(site.ReplaceStart, out _);
            var types = new ExpressionTypes(s, cls);
            int left = site.OperatorToken - 1;
            while (left >= 0 && doc.Tokens[left].Kind == TokenKind.DocComment) left--;
            string leftType = types.TypeOf(left);
            if (leftType == null) return null;
            var shape = Canonical(s, TypeShape.Parse(leftType));

            string listType;
            bool wrapperApi = false;
            var op = doc.Tokens[site.OperatorToken];
            if (site.Arrow)
            {
                if (shape.IsPointerLike) listType = shape.Pointee();
                else
                {
                    listType = leftType;
                    if (shape.Shape == AccessShape.Value && IsKnownValueType(s, shape)) resp.AccessorFix = Fix(doc, op, ".");
                }
            }
            else
            {
                switch (shape.Shape)
                {
                    case AccessShape.RawPointer:
                    case AccessShape.ObjectPtr:
                        listType = shape.Pointee();
                        resp.AccessorFix = Fix(doc, op, "->");
                        break;
                    case AccessShape.SmartPointer:
                        listType = leftType;
                        wrapperApi = true;
                        break;
                    default:
                        listType = leftType;
                        break;
                }
            }
            if (listType == null) return leftType;
            var ls = Canonical(s, TypeShape.Parse(listType));
            if (wrapperApi || ls.Shape == AccessShape.SmartPointer && !site.Arrow)
            {
                AddBuiltins(c, UeBuiltins.ApiOf(shape.Head), shape, null, null, shape.Head);
                return leftType;
            }
            var rec = s.FindRecord(ls.Head, site.ReplaceStart);
            bool any = false;
            if (rec != null)
            {
                var members = s.MembersOf(rec);
                bool related = cls != null && s.IsSameOrDerived(cls, members.QualifiedName);
                foreach (var e in members.Members)
                {
                    var d = e.Decl;
                    if (d.Kind == SymbolKind.Constructor || d.Kind == SymbolKind.Destructor || TypeText.IsTypeKind(d.Kind) || d.Kind == SymbolKind.EnumValue) continue;
                    if (d.Name.StartsWith("operator", StringComparison.Ordinal) || d.Name.StartsWith("~", StringComparison.Ordinal)) continue;
                    if (!Accessible(d, e.Depth, related, cls != null && NavContext.SameScope(cls, members.QualifiedName))) continue;
                    int rank = c.Rank(d.Name, false, out bool exact);
                    if (rank < 0) continue;
                    int band = Math.Max(PMember - 500 * e.Depth, PNamespace + 500);
                    c.Add(FromDecl(e.Doc, e.Index, e.DeclaringType, e.Depth, rank * RankUnit + Proximity(band, d.Name, ItemOrigin.Project, d.Ue, exact, false), s.Ctx), Family(d.Kind));
                    any = true;
                }
            }
            if (!any)
            {
                var api = UeBuiltins.ApiOf(ls.Head);
                if (api != null) AddBuiltins(c, api, ls, null, null, ls.Head);
                else Delegate(s, c, ls.Head);
            }
            return listType;
        }

        /// <summary>Typedef'd heads are replaced by their target ("using FArr = TArray&lt;int&gt;").</summary>
        private static TypeShape Canonical(IsSession s, TypeShape shape)
        {
            for (int i = 0; i < 3 && shape.Head != null; i++)
            {
                if (UeBuiltins.ApiOf(shape.Head) != null || s.Ctx.FindType(shape.Head) != null) break;
                string target = s.ResolveAlias(shape.Head);
                if (target == null) break;
                string text = target + new string('*', shape.PointerDepth);
                shape = TypeShape.Parse(text);
            }
            return shape;
        }

        private static bool IsKnownValueType(IsSession s, TypeShape shape) =>
            s.Ctx.FindType(shape.Head) != null || UeBuiltins.ApiOf(shape.Head) != null;

        private static AccessorFix Fix(DocumentModel doc, Token op, string replacement)
        {
            var (l, c) = doc.PositionOf(op.Start);
            return new AccessorFix { Line = l, Column = c, Length = op.Length, Replacement = replacement };
        }

        /// <summary>public always; protected from the class or a derived one; private only from the declaring class itself.</summary>
        private static bool Accessible(Declaration d, int depth, bool related, bool same)
        {
            switch (d.Access)
            {
                case AccessLevel.Private:
                    return same && depth == 0;
                case AccessLevel.Protected:
                    return related;
                default:
                    return true;
            }
        }

        private static void AddBuiltins(Collector c, IReadOnlyList<BuiltinMember> api, TypeShape shape, string parameters, string ret, string container)
        {
            if (api == null) return;
            foreach (var m in api)
            {
                int rank = c.Rank(m.Name, false, out bool exact);
                if (rank < 0) continue;
                string sig = UeBuiltins.Instantiate(m.Signature, shape?.Arg(0), shape?.Arg(0), shape?.Arg(1), parameters, ret);
                c.Add(new CompletionItem
                {
                    Label = m.Name,
                    InsertText = m.Name,
                    Kind = UeBuiltins.KindOf(m),
                    Detail = sig,
                    Container = container,
                    Score = rank * RankUnit + Proximity(PMember, m.Name, ItemOrigin.Engine, UeFlags.None, exact, false),
                }, "f");
            }
        }

        /// <summary>Delegate API (Broadcast / AddDynamic / BindUObject...) for a delegate type declared with a DECLARE_*_DELEGATE macro.</summary>
        private static void Delegate(IsSession s, Collector c, string typeName)
        {
            var del = FindDelegate(s, typeName, out var decl);
            if (del == null) return;
            bool dyn = (decl?.Modifiers & DeclModifiers.Dynamic) != 0 || (del.Ue & UeFlags.UDelegate) != 0;
            bool multi = (decl?.Modifiers & DeclModifiers.Multicast) != 0;
            if (decl == null) multi = (del.Signature ?? string.Empty).Contains("MULTICAST", StringComparison.Ordinal) || (del.Signature ?? string.Empty).Contains("EVENT", StringComparison.Ordinal);
            AddBuiltins(c, UeBuiltins.DelegateApi(dyn, multi), null, DelegateParameters(decl, dyn), decl?.Type, del.Name);
        }

        public static SymbolInfo FindDelegate(IsSession s, string typeName, out Declaration decl)
        {
            decl = null;
            if (string.IsNullOrEmpty(typeName)) return null;
            var del = s.Ctx.ByName(TypeText.LastSegment(typeName)).FirstOrDefault(x => x.Kind == SymbolKind.Delegate);
            if (del == null) return null;
            var (d, i) = s.DeclOf(del);
            decl = d != null ? d.Decls[i] : null;
            return del;
        }

        /// <summary>
        /// Parameter list of a delegate's Broadcast / Execute from its macro arguments: dynamic delegates pass (Type, Name) pairs,
        /// native ones only types.
        /// </summary>
        public static string DelegateParameters(Declaration decl, bool dynamic)
        {
            if (decl == null || string.IsNullOrWhiteSpace(decl.Parameters)) return string.Empty;
            var parts = SignatureHelpProvider.SplitTopLevel(decl.Parameters).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (!dynamic) return string.Join(", ", parts);
            var list = new List<string>();
            for (int i = 0; i + 1 < parts.Count; i += 2) list.Add(parts[i] + " " + parts[i + 1]);
            if (parts.Count % 2 == 1) list.Add(parts[parts.Count - 1]);
            return string.Join(", ", list);
        }

        // ================================================================== scope: X::b

        private string Scope(IsSession s, CompletionSite site, Collector c, ref bool incomplete)
        {
            var doc = s.Doc;
            var parts = new List<string>();
            int k = site.OperatorToken;
            while (doc.Is(k, Punct.ColonColon))
            {
                int q = k - 1;
                if (doc.Is(q, Punct.Gt))
                {
                    int depth = 0;
                    for (; q >= 0; q--)
                    {
                        if (doc.Is(q, Punct.Gt)) depth++;
                        else if (doc.Is(q, Punct.Lt) && --depth == 0) break;
                    }
                    q--;
                }
                if (!doc.IsIdentifier(q)) break;
                parts.Insert(0, doc.TokenText(q));
                k = q - 1;
            }
            int offset = site.ReplaceStart;
            string cls = doc.EnclosingClass(offset, out _);
            bool inFunction = doc.EnclosingFunction(offset) >= 0;
            if (parts.Count == 0)
            {
                // ::name
                Globals(s, site, c, ref incomplete, null);
                return "::";
            }
            string qualifier = string.Join("::", parts);
            if (parts[0] == "Super" && cls != null)
            {
                string sup = s.Ctx.FirstBase(cls);
                if (sup == null) return null;
                qualifier = parts.Count == 1 ? sup : sup + "::" + string.Join("::", parts.Skip(1));
            }
            else if (parts[0] == "ThisClass" && cls != null) qualifier = parts.Count == 1 ? cls : cls + "::" + string.Join("::", parts.Skip(1));

            // a class / struct (incl. inherited members)
            var rec = s.FindRecord(qualifier, offset);
            if (rec != null)
            {
                var members = s.MembersOf(rec);
                foreach (var e in members.Members)
                {
                    var d = e.Decl;
                    if ((d.Kind == SymbolKind.Constructor || d.Kind == SymbolKind.Destructor) && (inFunction || e.Depth > 0)) continue;
                    if (d.IsImplicit && d.Kind == SymbolKind.Typedef && e.Depth > 0) continue;
                    int rank = c.Rank(d.Name, false, out bool exact);
                    if (rank < 0) continue;
                    int band = Math.Max(PMember - 500 * e.Depth, PNamespace + 500);
                    c.Add(FromDecl(e.Doc, e.Index, e.DeclaringType, e.Depth, rank * RankUnit + Proximity(band, d.Name, ItemOrigin.Project, d.Ue, exact, false), s.Ctx), Family(d.Kind));
                }
                return members.QualifiedName;
            }

            // an enum: its values
            string last = parts[parts.Count - 1];
            string qual = parts.Count > 1 ? string.Join("::", parts.Take(parts.Count - 1)) : null;
            foreach (var sym in s.Ctx.ByName(last))
            {
                if (sym.Kind != SymbolKind.Enum) continue;
                if (qual != null && !TypeText.ContainerMatches(sym.Container, qual)) continue;
                var (d, i) = s.DeclOf(sym);
                if (d == null) continue;
                for (int j = i + 1; j < d.Decls.Count; j++)
                {
                    var v = d.Decls[j];
                    if (v.Parent != i || v.Kind != SymbolKind.EnumValue) continue;
                    int rank = c.Rank(v.Name, false, out bool exact);
                    if (rank < 0) continue;
                    // declaration order is meaningful for enums: keep it as a tiebreaker
                    c.Add(FromDecl(d, j, d.Decls[i].QualifiedName, 0, rank * RankUnit + PMember - Math.Min(j - i, 400), s.Ctx), "v");
                }
                return TypeText.Qualified(sym.Container, sym.Name);
            }

            // a namespace: declarations from every file that opens it (bounded), plus the index for a typed prefix
            var nsSyms = s.Ctx.ByName(last).Where(x => x.Kind == SymbolKind.Namespace && (qual == null || TypeText.ContainerMatches(x.Container, qual))).ToList();
            if (nsSyms.Count > 0)
            {
                string full = TypeText.Qualified(nsSyms[0].Container, nsSyms[0].Name);
                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ns in nsSyms)
                {
                    if (seenFiles.Count >= 24) { incomplete = true; break; }
                    var path = ns.Location?.FilePath;
                    if (path == null || !seenFiles.Add(path)) continue;
                    var d = s.DocFor(path);
                    if (d == null) continue;
                    for (int j = 0; j < d.Decls.Count; j++)
                    {
                        var x = d.Decls[j];
                        if (x.IsImplicit || string.IsNullOrEmpty(x.Name) || !string.Equals(x.Container, full, StringComparison.Ordinal)) continue;
                        if (x.Kind == SymbolKind.Method || x.Kind == SymbolKind.Field) continue;
                        int rank = c.Rank(x.Name, TypeText.IsTypeKind(x.Kind), out bool exact);
                        if (rank < 0) continue;
                        var (_, origin) = s.Ctx.FileOrigin(d.Path);
                        c.Add(FromDecl(d, j, full, 0, rank * RankUnit + Proximity(PNamespace + 1000, x.Name, origin, x.Ue, exact, true), s.Ctx), Family(x.Kind));
                    }
                }
                if (c.HasPrefix) Globals(s, site, c, ref incomplete, full);
                return full;
            }

            // an alias of something we know
            string alias = s.ResolveAlias(qualifier);
            if (alias != null)
            {
                var ar = s.FindRecord(alias, offset);
                if (ar != null)
                {
                    foreach (var e in s.MembersOf(ar).Members)
                    {
                        var d = e.Decl;
                        int rank = c.Rank(d.Name, false, out bool exact);
                        if (rank < 0) continue;
                        c.Add(FromDecl(e.Doc, e.Index, e.DeclaringType, e.Depth, rank * RankUnit + Proximity(PMember - 500 * e.Depth, d.Name, ItemOrigin.Project, d.Ue, exact, false), s.Ctx), Family(d.Kind));
                    }
                    return alias;
                }
            }
            return null;
        }

        // ================================================================== specifiers

        private static void Specifiers(IsSession s, CompletionSite site, Collector c)
        {
            if (site.ValuePosition) return;
            var kb = s.Knowledge;
            var list = site.InMeta ? kb.GetMetaKeys(site.SpecifierMacro) : kb.GetSpecifiers(site.SpecifierMacro);
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!site.InMeta)
                foreach (var used in site.UsedSpecifiers)
                {
                    var u = kb.FindSpecifier(site.SpecifierMacro, used, false);
                    if (u != null) foreach (var x in u.Conflicts) conflicts.Add(x);
                }
            int order = 0;
            foreach (var spec in list)
            {
                order++;
                if (site.UsedSpecifiers.Contains(spec.Name) || conflicts.Contains(spec.Name)) continue;
                int rank = c.Rank(spec.Name, false, out bool exact);
                if (rank < 0) continue;
                string insert = spec.Name;
                if (spec.Name == "meta") insert = "meta = ()";
                else if (spec.Value != null && spec.Value.StartsWith("=", StringComparison.Ordinal)) insert = spec.Name + " = ";
                c.Add(new CompletionItem
                {
                    Label = spec.Name,
                    InsertText = insert,
                    Kind = SymbolKind.Unknown,
                    Detail = spec.Doc + (spec.Value != null ? "  " + spec.Name + spec.Value : string.Empty) +
                             (spec.Conflicts.Count > 0 ? "  (excludes " + string.Join(", ", spec.Conflicts) + ")" : string.Empty),
                    Container = site.InMeta ? site.SpecifierMacro + " meta" : site.SpecifierMacro,
                    IsKeyword = true,
                    Ue = UeFlags.UMeta,
                    Score = rank * RankUnit + PMember - Math.Min(spec.Name.Length, 50) * 2 + (exact ? 100 : 0) - (spec.Name == "meta" ? 50 : 0),
                }, "s");
            }
        }

        // ================================================================== includes

        private void Includes(IsSession s, CompletionSite site, Collector c)
        {
            var files = s.Ctx.Files;
            if (files == null) return;
            string prefix = (site.Prefix ?? string.Empty).Replace('\\', '/');
            string from = s.Doc.Path;
            string fromDir = NavContext.NormalizePath(Path.GetDirectoryName(from) ?? string.Empty);
            var (fromModule, _) = s.Ctx.FileOrigin(from);
            var matcher = prefix.Length > 0 ? new FuzzyMatcher(Path.GetFileName(prefix)) : null;
            string prefixLower = prefix.ToLowerInvariant();
            int count = 0;
            foreach (var e in _includes.Get(files))
            {
                if ((++count & 1023) == 0) s.Ct.ThrowIfCancellationRequested();
                if (string.Equals(e.PathKey, NavContext.NormalizePath(from), StringComparison.Ordinal)) continue;
                string inc = e.IncludePath;
                // same directory: the bare file name works
                if (e.DirKey == fromDir) inc = e.FileName;
                int rank;
                bool exact = false;
                if (prefix.Length == 0) rank = 0;
                else if (inc.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    rank = 3;
                    exact = inc.StartsWith(prefix, StringComparison.Ordinal);
                }
                else if (prefix.IndexOf('/') < 0 && e.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rank = 2;
                else if (prefix.IndexOf('/') < 0 && matcher != null && matcher.Match(e.FileName).Kind == MatchKind.CamelHump) rank = 1;
                else if (prefix.IndexOf('/') >= 0 && e.IncludeLower.Contains(prefixLower, StringComparison.Ordinal)) rank = 1;
                else continue;
                int p = TypeText.IsProject(e.Origin) ? PNamespace : PGlobal;
                if (fromModule != null && string.Equals(e.Module, fromModule, StringComparison.Ordinal)) p += 500;
                if (exact) p += 100;
                p -= Math.Min(inc.Length, 120);
                c.Add(new CompletionItem
                {
                    Label = inc,
                    InsertText = inc,
                    Kind = SymbolKind.Unknown,
                    Detail = e.Module,
                    Container = e.Module,
                    Score = rank * RankUnit + p,
                    Location = new SourceLocation { FilePath = e.Path, Line = 0, Column = 0 },
                }, "i");
            }
        }

        /// <summary>Header list with include paths, rebuilt only when the file table changes.</summary>
        private sealed class IncludeTable
        {
            internal sealed class Entry
            {
                public string Path, PathKey, DirKey, FileName, IncludePath, IncludeLower, Module;
                public ItemOrigin Origin;
            }

            private FileEntry[] _source;
            private Entry[] _entries = Array.Empty<Entry>();
            private readonly object _gate = new object();

            public Entry[] Get(FileTable files)
            {
                var snap = files.Snapshot();
                lock (_gate)
                {
                    if (ReferenceEquals(snap, _source)) return _entries;
                    var list = new List<Entry>(snap.Length / 2);
                    foreach (var fe in snap)
                    {
                        var f = fe.File;
                        if (f == null || f.Kind != UEToVS.Engine.Unreal.SourceFileKind.Header) continue;
                        string norm = f.Path.Replace('\\', '/');
                        string inc = IncludePathOf(norm);
                        list.Add(new Entry
                        {
                            Path = f.Path,
                            PathKey = NavContext.NormalizePath(f.Path),
                            DirKey = NavContext.NormalizePath(System.IO.Path.GetDirectoryName(f.Path) ?? string.Empty),
                            FileName = f.Name ?? System.IO.Path.GetFileName(f.Path),
                            IncludePath = inc,
                            IncludeLower = inc.ToLowerInvariant(),
                            Module = f.Module,
                            Origin = f.Origin,
                        });
                    }
                    _entries = list.ToArray();
                    _source = snap;
                    return _entries;
                }
            }

            /// <summary>Path relative to the module's Public / Classes / Private folder (UBT include roots), else the file name.</summary>
            public static string IncludePathOf(string normalized)
            {
                foreach (var root in new[] { "/Public/", "/Classes/", "/Private/", "/Internal/" })
                {
                    int i = normalized.LastIndexOf(root, StringComparison.OrdinalIgnoreCase);
                    if (i >= 0) return normalized.Substring(i + root.Length);
                }
                int slash = normalized.LastIndexOf('/');
                return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            }
        }

        public static string IncludePathOf(string path) => IncludeTable.IncludePathOf((path ?? string.Empty).Replace('\\', '/'));

        // ================================================================== override

        private static string Override(IsSession s, CompletionSite site, Collector c)
        {
            var doc = s.Doc;
            string cls = doc.EnclosingClass(site.ReplaceStart, out int clsDecl);
            if (cls == null || clsDecl < 0) return null;
            var self = doc.Decls[clsDecl];
            var own = new HashSet<string>(StringComparer.Ordinal);
            for (int i = clsDecl + 1; i < doc.Decls.Count; i++)
                if (doc.Decls[i].Parent == clsDecl && TypeText.IsCallableKind(doc.Decls[i].Kind)) own.Add(IsSession.MemberKey(doc.Decls[i]));
            bool replaceStatement = site.OverrideStart >= 0;
            foreach (var b in self.Bases ?? Array.Empty<string>())
            {
                var be = s.Ctx.FindType(b);
                if (be == null) continue;
                foreach (var e in s.MembersOf(be).Members)
                {
                    var d = e.Decl;
                    if (d.Kind != SymbolKind.Method || d.IsImplicit && (d.Modifiers & DeclModifiers.Virtual) == 0) continue;
                    bool isVirtual = (d.Modifiers & (DeclModifiers.Virtual | DeclModifiers.Override | DeclModifiers.Pure)) != 0;
                    bool nativeEvent = (d.Ue & UeFlags.UFunction) != 0 && TypeText.HasSpecifier(d.Specifiers, "BlueprintNativeEvent");
                    if ((d.Modifiers & DeclModifiers.Final) != 0) continue;
                    string name = d.Name;
                    if (!isVirtual && nativeEvent) name += "_Implementation";
                    else if (!isVirtual) continue;
                    var probe = new Declaration { Name = name, Kind = d.Kind, Parameters = d.Parameters, Modifiers = d.Modifiers };
                    if (own.Contains(IsSession.MemberKey(probe))) continue;
                    int rank = c.Rank(name, false, out bool exact);
                    if (rank < 0) continue;
                    string sig = OverrideSignature(d, name);
                    c.Add(new CompletionItem
                    {
                        Label = name,
                        InsertText = replaceStatement ? sig : sig.Substring("virtual ".Length),
                        Kind = SymbolKind.Method,
                        Ue = d.Ue,
                        Detail = sig,
                        Container = e.DeclaringType,
                        IsInherited = true,
                        Score = rank * RankUnit + Proximity(PMember - 300 * e.Depth, name, ItemOrigin.Project, d.Ue, exact, false) +
                                ((d.Modifiers & DeclModifiers.Pure) != 0 ? 150 : 0),
                        Location = new SourceLocation { FilePath = e.Doc.Path, Line = d.Line, Column = d.Column },
                    }, "o:" + TypeText.ParameterKey("(" + d.Parameters + ")"));
                }
            }
            return cls;
        }

        /// <summary><c>virtual void Tick(float DeltaSeconds) override;</c> (defaults stripped).</summary>
        public static string OverrideSignature(Declaration d, string name)
        {
            var ps = SignatureHelpProvider.SplitTopLevel(d.Parameters ?? string.Empty)
                .Select(p => SignatureHelpProvider.StripDefault(p).Trim()).Where(p => p.Length > 0);
            string ret = string.IsNullOrEmpty(d.Type) ? "void" : d.Type;
            string cst = (d.Modifiers & DeclModifiers.Const) != 0 ? " const" : string.Empty;
            return "virtual " + ret + " " + name + "(" + string.Join(", ", ps) + ")" + cst + " override;";
        }

        // ================================================================== general

        private void General(IsSession s, CompletionSite site, Collector c, bool explicitInvoke, ref bool incomplete)
        {
            var doc = s.Doc;
            int offset = site.ReplaceStart;
            if (site.MacrosOnly)
            {
                Globals(s, site, c, ref incomplete, null, macrosOnly: true);
                return;
            }

            // 1. locals and parameters
            int fn = doc.EnclosingFunction(offset);
            if (fn >= 0)
            {
                foreach (var l in doc.LocalsOf(fn))
                {
                    if (l.Offset >= offset || offset >= l.ScopeEnd) continue;
                    int rank = c.Rank(l.Name, false, out bool exact);
                    if (rank < 0) continue;
                    var (ll, lc) = doc.PositionOf(l.Offset);
                    c.Add(new CompletionItem
                    {
                        Label = l.Name,
                        InsertText = l.Name,
                        Kind = SymbolKind.Variable,
                        Detail = Truncate(l.Type),
                        Container = l.IsParameter ? "parameter" : "local",
                        // later declarations shadow earlier ones
                        Score = rank * RankUnit + Proximity(PLocal + (l.IsParameter ? 0 : 10), l.Name, ItemOrigin.Project, UeFlags.None, exact, false) + Math.Min(l.Offset / 1000, 50),
                        Location = new SourceLocation { FilePath = doc.Path, Line = ll, Column = lc },
                    }, "v");
                }
            }

            // 2. members of the enclosing class and its bases
            string cls = doc.EnclosingClass(offset, out _);
            if (cls != null)
            {
                var rec = s.FindRecord(cls, offset);
                if (rec != null)
                {
                    foreach (var e in s.MembersOf(rec).Members)
                    {
                        var d = e.Decl;
                        if (d.Kind == SymbolKind.Constructor || d.Kind == SymbolKind.Destructor || d.Name.StartsWith("operator", StringComparison.Ordinal)) continue;
                        if (d.Access == AccessLevel.Private && e.Depth > 0) continue;
                        int rank = c.Rank(d.Name, false, out bool exact);
                        if (rank < 0) continue;
                        int band = Math.Max(PMember - 500 * e.Depth, PNamespace + 500);
                        c.Add(FromDecl(e.Doc, e.Index, e.DeclaringType, e.Depth, rank * RankUnit + Proximity(band, d.Name, ItemOrigin.Project, d.Ue, exact, false), s.Ctx), Family(d.Kind));
                    }
                }
            }

            // 3. this document's namespace-level declarations (the buffer may be newer than the index)
            for (int i = 0; i < doc.Decls.Count; i++)
            {
                var d = doc.Decls[i];
                if (d.IsImplicit || string.IsNullOrEmpty(d.Name) || d.Kind == SymbolKind.Namespace && string.IsNullOrEmpty(d.Name)) continue;
                if (!string.IsNullOrEmpty(d.Container) && !doc.IsKnownNamespace(d.Container) && d.Kind != SymbolKind.EnumValue) continue;
                if (d.Kind == SymbolKind.Method || d.Kind == SymbolKind.Field || d.Kind == SymbolKind.Constructor || d.Kind == SymbolKind.Destructor) continue;
                if (d.Kind == SymbolKind.EnumValue && !UnscopedEnumValue(doc, d)) continue;
                if (d.Offset >= site.ReplaceStart && d.Offset < site.ReplaceEnd) continue;
                int rank = c.Rank(d.Name, TypeText.IsTypeKind(d.Kind), out bool exact);
                if (rank < 0) continue;
                c.Add(FromDecl(doc, i, d.Container, 0, rank * RankUnit + Proximity(PNamespace, d.Name, ItemOrigin.Project, d.Ue, exact, false), s.Ctx), Family(d.Kind));
            }

            // 3b. namespaces this buffer qualifies with (the index's prefix search does not list namespaces)
            if (c.HasPrefix) QualifierNamespaces(s, site, c);

            // 4. index: types / functions / macros / variables at namespace scope
            if (c.HasPrefix) Globals(s, site, c, ref incomplete, null);
            else incomplete = true; // nothing typed: globals are only listed once there is a prefix

            // 5. keywords and UE snippets
            if (c.HasPrefix || explicitInvoke)
            {
                foreach (var kw in UeBuiltins.Keywords)
                {
                    int rank = c.Rank(kw, false, out bool exact);
                    if (rank < 2 && c.HasPrefix) continue; // keywords: prefix only
                    c.Add(new CompletionItem
                    {
                        Label = kw,
                        InsertText = kw,
                        Kind = SymbolKind.Unknown,
                        IsKeyword = true,
                        Detail = "keyword",
                        Score = Math.Max(0, rank) * RankUnit + Proximity(PKeyword, kw, ItemOrigin.Project, UeFlags.None, exact, false),
                    }, "k");
                }
                foreach (var (label, insert, detail) in UeBuiltins.Snippets)
                {
                    int rank = c.Rank(label, false, out bool exact);
                    if (rank < 1 && c.HasPrefix) continue;
                    var info = s.Knowledge.Lookup(label);
                    c.Add(new CompletionItem
                    {
                        Label = label,
                        InsertText = insert,
                        Kind = SymbolKind.Macro,
                        IsKeyword = true,
                        Ue = info != null && (info.Role == MacroRole.Reflection || info.Role == MacroRole.GeneratedBody) ? info.Flag : UeFlags.None,
                        Detail = detail,
                        Score = Math.Max(0, rank) * RankUnit + Proximity(PSnippet, label, ItemOrigin.Project, UeFlags.None, exact, false),
                    }, "m");
                }
            }
        }

        /// <summary>Outermost <c>X::</c> qualifiers of the buffer that the index knows as namespaces and that match the prefix.</summary>
        private static void QualifierNamespaces(IsSession s, CompletionSite site, Collector c)
        {
            var doc = s.Doc;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int k = 0; k + 1 < doc.Tokens.Length; k++)
            {
                if (!doc.Is(k + 1, Punct.ColonColon) || !doc.IsIdentifier(k) || doc.Is(k - 1, Punct.ColonColon)) continue;
                if (doc.Tokens[k].Start >= site.ReplaceStart && doc.Tokens[k].Start < site.ReplaceEnd) continue;
                string name = doc.TokenText(k);
                if (!seen.Add(name)) continue;
                int rank = c.Rank(name, false, out bool exact);
                if (rank < 0 || doc.IsKnownNamespace(name) && doc.Decls.Any(d => d.Kind == SymbolKind.Namespace && d.Name == name)) continue;
                var ns = s.Ctx.ByName(name).FirstOrDefault(x => x.Kind == SymbolKind.Namespace && string.IsNullOrEmpty(x.Container));
                if (ns == null) continue;
                c.Add(new CompletionItem
                {
                    Label = name,
                    InsertText = name,
                    Kind = SymbolKind.Namespace,
                    Detail = "namespace " + name,
                    Score = rank * RankUnit + Proximity(PGlobal, name, ns.Origin, UeFlags.None, exact, true),
                }, Family(SymbolKind.Namespace));
            }
        }

        private static bool UnscopedEnumValue(DocumentModel doc, Declaration v)
        {
            if (v.Parent < 0) return false;
            var e = doc.Decls[v.Parent];
            return e.Kind == SymbolKind.Enum && (e.Modifiers & DeclModifiers.Scoped) == 0;
        }

        /// <summary>Index search for the prefix: namespace-level types, functions, variables, macros and delegates.</summary>
        private void Globals(IsSession s, CompletionSite site, Collector c, ref bool incomplete, string container, bool macrosOnly = false)
        {
            string prefix = site.Prefix ?? string.Empty;
            if (prefix.Length == 0) return;
            var hits = _globals.Search(s.Ctx.Backend, prefix, GlobalSearchMax, s.Ct, out bool truncated);
            if (truncated) incomplete = true;
            var nsCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            string offsetNs = s.Doc.EnclosingNamespace(site.ReplaceStart);
            foreach (var sym in hits)
            {
                if (s.Ctx.IsOverlayPath(sym.Location?.FilePath)) continue; // the buffer's own declarations come from the document
                switch (sym.Kind)
                {
                    case SymbolKind.Method:
                    case SymbolKind.Field:
                    case SymbolKind.Constructor:
                    case SymbolKind.Destructor:
                    case SymbolKind.EnumValue:
                    case SymbolKind.Unknown:
                        continue;
                }
                if (macrosOnly && sym.Kind != SymbolKind.Macro) continue;
                if (container != null)
                {
                    if (!TypeText.ContainerMatches(sym.Container, container)) continue;
                }
                else if (!string.IsNullOrEmpty(sym.Container) && !IsNamespace(s, sym.Container, nsCache)) continue;
                int rank = c.Rank(sym.Name, TypeText.IsTypeKind(sym.Kind), out bool exact);
                if (rank < 0) continue;
                int band = PGlobal;
                if (!string.IsNullOrEmpty(sym.Container) && offsetNs != null && (offsetNs == sym.Container || offsetNs.StartsWith(sym.Container + "::", StringComparison.Ordinal)))
                    band = PNamespace;
                if (site.TypeExpected && TypeText.IsTypeKind(sym.Kind)) band += 600;
                c.Add(FromSymbol(sym, rank * RankUnit + Proximity(band, sym.Name, sym.Origin, sym.Ue, exact, true)), Family(sym.Kind));
            }
        }

        private static bool IsNamespace(IsSession s, string container, Dictionary<string, bool> cache)
        {
            if (cache.TryGetValue(container, out bool b)) return b;
            b = s.Ctx.FindType(container) == null && !s.Ctx.ByName(TypeText.LastSegment(container)).Any(x => x.Kind == SymbolKind.Enum);
            cache[container] = b;
            return b;
        }

        /// <summary>
        /// Index searches by prefix, reused while the user keeps typing: a complete (untruncated) result for "Hea" also answers
        /// "Heal". Entries expire after a few seconds so index updates show up.
        /// </summary>
        private sealed class GlobalSearchCache
        {
            private sealed class Entry
            {
                public string Prefix;
                public List<SymbolInfo> Hits;
                public bool Truncated;
                public long Ticks;
                public ISymbolIndexBackend Backend;
            }

            private readonly LinkedList<Entry> _entries = new LinkedList<Entry>();
            private long _hits;

            public long Hits => Interlocked.Read(ref _hits);

            public List<SymbolInfo> Search(ISymbolIndexBackend backend, string prefix, int max, CancellationToken ct, out bool truncated)
            {
                long now = Stopwatch.GetTimestamp();
                long ttl = Stopwatch.Frequency * 3;
                lock (_entries)
                {
                    for (var n = _entries.First; n != null; n = n.Next)
                    {
                        var e = n.Value;
                        if (now - e.Ticks > ttl || !ReferenceEquals(e.Backend, backend)) continue;
                        if (string.Equals(e.Prefix, prefix, StringComparison.OrdinalIgnoreCase) ||
                            !e.Truncated && prefix.StartsWith(e.Prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            Interlocked.Increment(ref _hits);
                            truncated = e.Truncated; // a longer prefix is only answered from an untruncated entry
                            return e.Hits;
                        }
                    }
                }
                var result = backend.Search(new SymbolQuery
                {
                    Pattern = prefix,
                    IncludeTypes = true,
                    IncludeSymbols = true,
                    IncludeNonProject = true,
                    MaxResults = max,
                }, ct);
                var hits = new List<SymbolInfo>(result.Matches.Count);
                int types = 0, syms = 0;
                foreach (var m in result.Matches)
                {
                    hits.Add(m.Symbol);
                    if (m.Category == ResultCategory.Type) types++;
                    else syms++;
                }
                truncated = types >= max || syms >= max;
                lock (_entries)
                {
                    _entries.AddFirst(new Entry { Prefix = prefix, Hits = hits, Truncated = truncated, Ticks = now, Backend = backend });
                    while (_entries.Count > 32) _entries.RemoveLast();
                }
                return hits;
            }
        }
    }
}
