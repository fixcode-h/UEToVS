using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Unreal;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.Navigation
{
    /// <summary>
    /// M2 core navigation over the index and editor buffers: go to definition / declaration / implementation, find
    /// references, type hierarchy, reference highlighting, navigation bar context and member lists. Thread-safe.
    /// </summary>
    public sealed partial class NavigationService
    {
        private static readonly Logger L = Log.For("Nav");

        private readonly ISymbolIndexBackend _backend;
        private readonly FileTable _files;
        private readonly Func<bool> _indexReady;
        private readonly HighlightCache _highlights = new HighlightCache(64);

        public NavigationService(ISymbolIndexBackend backend, FileTable files, Func<bool> indexReady = null, DocumentCache documents = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _files = files;
            _indexReady = indexReady ?? (() => true);
            Documents = documents ?? new DocumentCache();
        }

        public DocumentCache Documents { get; }

        /// <summary>Upper bound on files scanned by one Find References (largest projects stay responsive).</summary>
        public int MaxReferenceFiles { get; set; } = 20000;

        private DocumentModel Load(CaretRequest caret)
        {
            if (caret == null || string.IsNullOrEmpty(caret.FilePath)) return null;
            return Documents.Get(caret.FilePath, caret.Text, caret.TextVersion);
        }

        private static int CaretOffset(DocumentModel doc, CaretRequest caret) => doc.OffsetOf(Math.Max(0, caret.Line), Math.Max(0, caret.Column));

        // ================================================================== navigate

        public NavigationResponse Navigate(NavigationRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new NavigationResponse { Word = string.Empty, IndexReady = _indexReady(), Reason = "none" };
            var doc = Load(request?.Caret);
            if (doc == null)
            {
                resp.Reason = "no-document";
                return Finish(resp, sw, request);
            }
            var ctx = new NavContext(_backend, _files, doc, ct);
            var r = SymbolResolver.Resolve(ctx, doc, CaretOffset(doc, request.Caret));
            resp.Word = r.Word ?? string.Empty;
            resp.Reason = r.Reason;
            resp.Confidence = r.Confidence;
            if (r.Include != null)
            {
                resp.Resolved = r.Include;
                resp.Targets.Add(new NavigationTarget { Symbol = r.Include, Role = NavigationTargetRole.Definition });
                return Finish(resp, sw, request);
            }
            if (r.Candidates.Count == 0) return Finish(resp, sw, request);
            resp.Resolved = r.Local != null ? r.Candidates[0] : PickResolved(r);
            if (r.Local != null)
            {
                resp.Targets.Add(new NavigationTarget { Symbol = r.Candidates[0], Role = NavigationTargetRole.Declaration });
                return Finish(resp, sw, request);
            }
            resp.Targets.AddRange(BuildTargets(ctx, r, request.Kind));
            if (resp.Targets.Count == 0)
                resp.Targets.Add(new NavigationTarget { Symbol = resp.Resolved, Role = resp.Resolved.IsDefinition ? NavigationTargetRole.Definition : NavigationTargetRole.Declaration });
            return Finish(resp, sw, request);
        }

        private static NavigationResponse Finish(NavigationResponse resp, Stopwatch sw, NavigationRequest request)
        {
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            L.Info("nav.resolve", null, ("word", resp.Word), ("kind", request?.Kind.ToString()), ("reason", resp.Reason),
                ("confidence", resp.Confidence.ToString()), ("candidates", resp.Targets.Count), ("ms", resp.ElapsedMs),
                ("file", System.IO.Path.GetFileName(request?.Caret?.FilePath ?? string.Empty)), ("line", request?.Caret?.Line));
            return resp;
        }

        private static SymbolInfo PickResolved(Resolution r)
        {
            if (r.OnDeclaration != null) return r.OnDeclaration;
            // declarations (headers) describe an entity best; project items first
            return r.Candidates.OrderBy(c => GeneratedFiles.Rank(c.Location?.FilePath))
                .ThenBy(c => TypeText.IsProject(c.Origin) ? 0 : 1)
                .ThenBy(c => TypeText.IsCallableKind(c.Kind) && c.IsDefinition ? 1 : 0).First();
        }

        private static readonly string[] CounterpartSpecifiers = { "BlueprintNativeEvent", "Server", "Client", "NetMulticast" };

        private List<NavigationTarget> BuildTargets(NavContext ctx, Resolution r, NavigationKind kind)
        {
            var targets = new List<NavigationTarget>();
            var self = r.OnDeclaration;
            if (kind == NavigationKind.Implementation)
            {
                targets.AddRange(Implementations(ctx, r));
                if (targets.Count > 0) return Rank(targets);
                kind = NavigationKind.Definition;
            }
            bool wantDefinition = kind == NavigationKind.Definition || kind == NavigationKind.Toggle;
            if (self != null && (kind == NavigationKind.Toggle || kind == NavigationKind.Definition) && TypeText.IsCallableKind(self.Kind))
                wantDefinition = !self.IsDefinition;

            foreach (var group in r.Candidates.GroupBy(SymbolResolver.EntityKey))
            {
                var syms = group.ToList();
                var callable = TypeText.IsCallableKind(syms[0].Kind);
                if (!callable)
                {
                    foreach (var s in syms) targets.Add(new NavigationTarget { Symbol = s, Role = s.IsDefinition ? NavigationTargetRole.Definition : NavigationTargetRole.Declaration });
                    continue;
                }
                var defs = syms.Where(s => s.IsDefinition).ToList();
                var decls = syms.Where(s => !s.IsDefinition).ToList();
                if (self == null && r.CallArguments != null)
                {
                    // a call: the overloads whose parameters fit the arguments
                    var fitting = SymbolResolver.FilterByCall(decls.Count > 0 ? decls : defs, r.CallArguments);
                    var keys = new HashSet<string>(fitting.Select(f => TypeText.ParameterKey(f.Signature)));
                    if (decls.Count > 0) decls = fitting;
                    var matchingDefs = defs.Where(d => keys.Contains(TypeText.ParameterKey(d.Signature))).ToList();
                    if (matchingDefs.Count > 0) defs = matchingDefs;
                }
                if (self != null)
                {
                    // overloads: the definition / declaration with the same parameter list as the caret's
                    string pk = TypeText.ParameterKey(self.Signature);
                    defs = SameParameters(defs, pk);
                    decls = SameParameters(decls, pk);
                }
                var name = syms[0].Name;
                var container = syms[0].Container;
                if (wantDefinition)
                {
                    if (defs.Count > 0) Add(targets, defs, NavigationTargetRole.Definition);
                    // UFUNCTION(BlueprintNativeEvent / Server / Client / NetMulticast) Foo is implemented as Foo_Implementation (+ Foo_Validate)
                    var ufn = decls.FirstOrDefault(d => d.Ue.HasFlag(UeFlags.UFunction) && CounterpartSpecifiers.Any(w => TypeText.HasSpecifier(d.Specifiers, w)));
                    if (ufn != null)
                    {
                        Add(targets, Counterparts(ctx, name + "_Implementation", container), NavigationTargetRole.UeCounterpart);
                        if (TypeText.HasSpecifier(ufn.Specifiers, "WithValidation"))
                            Add(targets, Counterparts(ctx, name + "_Validate", container), NavigationTargetRole.UeCounterpart);
                    }
                    if (targets.Count == 0) Add(targets, decls, NavigationTargetRole.Declaration);
                }
                else
                {
                    if (decls.Count > 0) Add(targets, decls, NavigationTargetRole.Declaration);
                    else if (TryUeBase(name, out string ueBase))
                    {
                        // Foo_Implementation / Foo_Validate are declared by UHT: go to the UFUNCTION Foo
                        var baseDecls = ctx.ByName(ueBase).Where(s => !s.IsDefinition && NavContext.SameScope(s.Container, container)).ToList();
                        if (baseDecls.Count == 0) baseDecls = ctx.MemberLookup(ueBase, container, out _).Where(s => !s.IsDefinition).ToList();
                        Add(targets, baseDecls, NavigationTargetRole.UeCounterpart);
                    }
                    if (targets.Count == 0) Add(targets, defs, NavigationTargetRole.Definition);
                }
            }
            // never "jump" to where the caret already is when there is somewhere else to go
            if (self != null && targets.Count > 1) targets.RemoveAll(t => SymbolResolver.SameLocation(t.Symbol, self));
            if (self != null && targets.Count == 1 && SymbolResolver.SameLocation(targets[0].Symbol, self) && TryUeBase(self.Name, out string ub) &&
                TypeText.IsCallableKind(self.Kind))
            {
                var baseDecls = ctx.MemberLookup(ub, self.Container, out _).Where(s => !s.IsDefinition).ToList();
                if (baseDecls.Count > 0)
                {
                    targets.Clear();
                    Add(targets, baseDecls, NavigationTargetRole.UeCounterpart);
                }
            }
            return Rank(targets);
        }

        private static bool TryUeBase(string name, out string ueBase)
        {
            foreach (var suffix in new[] { "_Implementation", "_Validate" })
            {
                if (name != null && name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    ueBase = name.Substring(0, name.Length - suffix.Length);
                    return true;
                }
            }
            ueBase = null;
            return false;
        }

        private static List<SymbolInfo> Counterparts(NavContext ctx, string name, string container)
        {
            var all = ctx.ByName(name).Where(s => NavContext.SameScope(s.Container, container)).ToList();
            var defs = all.Where(s => s.IsDefinition).ToList();
            return defs.Count > 0 ? defs : all;
        }

        private static List<SymbolInfo> SameParameters(List<SymbolInfo> list, string key)
        {
            if (key == null || list.Count < 2) return list;
            var same = list.Where(s => TypeText.ParameterKey(s.Signature) == key).ToList();
            return same.Count > 0 ? same : list;
        }

        private static void Add(List<NavigationTarget> targets, IEnumerable<SymbolInfo> syms, NavigationTargetRole role)
        {
            foreach (var s in syms)
                if (!targets.Any(t => SymbolResolver.SameLocation(t.Symbol, s))) targets.Add(new NavigationTarget { Symbol = s, Role = role });
        }

        /// <summary>手写文件优先于 UE 生成的文件（X.generated.h 等），然后项目优先于引擎。</summary>
        private static List<NavigationTarget> Rank(List<NavigationTarget> targets) =>
            targets.OrderBy(t => GeneratedFiles.Rank(t.Symbol.Location?.FilePath))
                .ThenBy(t => TypeText.IsProject(t.Symbol.Origin) ? 0 : 1)
                .ThenBy(t => t.Role == NavigationTargetRole.Definition || t.Role == NavigationTargetRole.Declaration ? 0 : 1)
                .ThenBy(t => t.Symbol.Location?.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Symbol.Location?.Line ?? 0).ToList();

        /// <summary>Overrides of a virtual method in derived types, or the derived types of a type.</summary>
        private List<NavigationTarget> Implementations(NavContext ctx, Resolution r)
        {
            var result = new List<NavigationTarget>();
            var resolved = r.OnDeclaration ?? r.Candidates.FirstOrDefault();
            if (resolved == null) return result;
            if (TypeText.IsRecordKind(resolved.Kind))
            {
                var root = ctx.FindType(TypeText.Qualified(resolved.Container, resolved.Name));
                foreach (var t in AllDerived(ctx, root, 2000))
                    result.Add(new NavigationTarget { Symbol = t.Symbol, Role = NavigationTargetRole.DerivedType });
                return result;
            }
            if (!TypeText.IsCallableKind(resolved.Kind) || string.IsNullOrEmpty(resolved.Container)) return result;
            var owner = ctx.FindType(resolved.Container);
            if (owner == null) return result;
            foreach (var t in AllDerived(ctx, owner, 2000))
            {
                string scope = TypeText.Qualified(t.Symbol.Container, t.Symbol.Name);
                foreach (var name in new[] { resolved.Name, resolved.Name + "_Implementation" })
                {
                    var syms = ctx.ByName(name).Where(s => TypeText.IsCallableKind(s.Kind) && string.Equals(s.Container, scope, StringComparison.Ordinal)).ToList();
                    var defs = syms.Where(s => s.IsDefinition).ToList();
                    foreach (var s in defs.Count > 0 ? defs : syms) result.Add(new NavigationTarget { Symbol = s, Role = NavigationTargetRole.Override });
                }
            }
            return result;
        }

        private static List<TypeEntry> AllDerived(NavContext ctx, TypeEntry root, int max)
        {
            var result = new List<TypeEntry>();
            if (root == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal) { TypeText.Qualified(root.Symbol.Container, root.Symbol.Name) };
            var queue = new Queue<TypeEntry>();
            queue.Enqueue(root);
            while (queue.Count > 0 && result.Count < max)
            {
                foreach (var d in ctx.DerivedOf(queue.Dequeue()))
                {
                    if (!seen.Add(TypeText.Qualified(d.Symbol.Container, d.Symbol.Name))) continue;
                    result.Add(d);
                    queue.Enqueue(d);
                }
            }
            return result;
        }

        // ================================================================== references

        public ReferencesResponse FindReferences(ReferencesRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new ReferencesResponse { IndexReady = _indexReady() };
            var caretDoc = Load(request?.Caret);
            var ctx = new NavContext(_backend, _files, caretDoc, ct);

            // what are we looking for?
            var targetKeys = new HashSet<string>(StringComparer.Ordinal);
            string name;
            LocalVariable local = null;
            SymbolInfo symbol = request?.Symbol;
            if (symbol != null)
            {
                name = symbol.Name;
                targetKeys.Add(SymbolResolver.EntityKey(symbol));
            }
            else if (caretDoc != null)
            {
                var r = SymbolResolver.Resolve(ctx, caretDoc, CaretOffset(caretDoc, request.Caret));
                name = r.Word;
                local = r.Local;
                if (r.Candidates.Count == 0 || r.Include != null)
                {
                    LogReferences(resp, name, sw, 0, "unresolved");
                    return resp;
                }
                symbol = r.Local != null ? r.Candidates[0] : PickResolved(r);
                foreach (var c in r.Candidates) targetKeys.Add(SymbolResolver.EntityKey(c));
            }
            else
            {
                LogReferences(resp, null, sw, 0, "no-document");
                return resp;
            }
            resp.Symbol = symbol;
            if (string.IsNullOrEmpty(name)) return resp;

            int max = request.MaxResults > 0 ? request.MaxResults : 5000;
            var hits = new ConcurrentBag<ReferenceHit>();
            int count = 0;
            bool truncated = false;

            if (local != null)
            {
                // a local: only its function in this document
                foreach (var h in ScanDocument(ctx, caretDoc, name, null, local, request.IncludeDeclarations, ct))
                    hits.Add(h);
                resp.FilesScanned = 1;
            }
            else
            {
                bool isType = TypeText.IsTypeKind(symbol.Kind);
                var files = CandidateFiles(ctx, name, request.IncludeNonProject, caretDoc, ct, out bool fileCapHit);
                truncated |= fileCapHit;
                int scanned = 0;
                var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)) };
                try
                {
                    Parallel.ForEach(files, po, (path, state) =>
                    {
                        var doc = caretDoc != null && ctx.IsOverlayPath(path) ? caretDoc : Documents.GetFile(path);
                        Interlocked.Increment(ref scanned);
                        if (doc == null) return;
                        foreach (var h in ScanDocument(ctx, doc, name, targetKeys, null, request.IncludeDeclarations, ct, isType ? symbol : null))
                        {
                            if (Interlocked.Increment(ref count) > max)
                            {
                                truncated = true;
                                state.Stop();
                                return;
                            }
                            hits.Add(h);
                        }
                    });
                }
                catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
                {
                    throw new OperationCanceledException(ct);
                }
                resp.FilesScanned = scanned;
            }

            resp.Hits = hits.OrderBy(h => GeneratedFiles.Rank(h.Location.FilePath))
                .ThenBy(h => TypeText.IsProject(h.Origin) ? 0 : 1)
                .ThenBy(h => h.Location.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Location.Line).ThenBy(h => h.Location.Column).Take(max).ToList();
            resp.Truncated = truncated;
            LogReferences(resp, name, sw, ctx.IndexQueries, local != null ? "local" : "index");
            return resp;
        }

        private static void LogReferences(ReferencesResponse resp, string name, Stopwatch sw, int queries, string mode)
        {
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            L.Info("nav.references", null, ("name", name), ("mode", mode), ("hits", resp.Hits.Count), ("files", resp.FilesScanned),
                ("truncated", resp.Truncated), ("indexQueries", queries), ("ms", resp.ElapsedMs));
        }

        private List<string> CandidateFiles(NavContext ctx, string name, bool includeNonProject, DocumentModel caretDoc, CancellationToken ct, out bool capped)
        {
            capped = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            void Consider(string path)
            {
                if (string.IsNullOrEmpty(path) || !seen.Add(NavContext.NormalizePath(path))) return;
                if (!includeNonProject && !TypeText.IsProject(ctx.FileOrigin(path).Origin)) return;
                list.Add(path);
            }
            if (caretDoc != null) Consider(caretDoc.Path);
            foreach (var s in ctx.ByName(name)) Consider(s.Location?.FilePath);
            foreach (var p in _backend.FindOccurrenceFiles(name, ct))
            {
                Consider(p);
                if (list.Count >= MaxReferenceFiles)
                {
                    capped = true;
                    break;
                }
            }
            return list;
        }

        /// <summary>Identifier tokens named <paramref name="name"/> in a document that resolve to one of <paramref name="targetKeys"/>.</summary>
        private IEnumerable<ReferenceHit> ScanDocument(NavContext ctx, DocumentModel doc, string name, HashSet<string> targetKeys, LocalVariable local,
            bool includeDeclarations, CancellationToken ct, SymbolInfo typeTarget = null)
        {
            var (module, origin) = ctx.FileOrigin(doc.Path);
            for (int i = 0; i < doc.Tokens.Length; i++)
            {
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                var t = doc.Tokens[i];
                if (t.Kind != TokenKind.Identifier || t.Length != System.Text.Encoding.UTF8.GetByteCount(name) || doc.TokenText(i) != name) continue;
                var r = SymbolResolver.ResolveToken(ctx, doc, i, 0);
                ResolutionConfidence conf;
                if (local != null)
                {
                    if (r.Local == null || r.Local.Offset != local.Offset || r.Local.Function != local.Function) continue;
                    conf = ResolutionConfidence.Exact;
                }
                else
                {
                    if (r.Local != null) continue;
                    bool match = r.Candidates.Any(c => targetKeys.Contains(SymbolResolver.EntityKey(c)));
                    if (!match && typeTarget != null)
                    {
                        // a type's constructors / destructor count as references to the type
                        string q = TypeText.Qualified(typeTarget.Container, typeTarget.Name);
                        match = r.Candidates.Any(c => (c.Kind == SymbolKind.Constructor || c.Kind == SymbolKind.Destructor) && c.Name == typeTarget.Name &&
                                                      NavContext.SameScope(c.Container, q));
                    }
                    if (!match && r.Candidates.Count == 0 && r.Reason != "ue-macro") match = true; // unresolvable: keep, NameOnly
                    if (!match) continue;
                    conf = r.Candidates.Count == 0 ? ResolutionConfidence.NameOnly : r.Confidence;
                }
                var kind = KindAt(doc, i, r);
                if (!includeDeclarations && (kind & (ReferenceKind.Declaration | ReferenceKind.Definition)) != 0) continue;
                yield return MakeHit(doc, i, kind, conf, module, origin);
            }
        }

        private static ReferenceKind KindAt(DocumentModel doc, int ti, Resolution r)
        {
            var t = doc.Tokens[ti];
            if (r.OnDeclaration != null)
                return r.OnDeclaration.IsDefinition && TypeText.IsCallableKind(r.OnDeclaration.Kind) ? ReferenceKind.Definition : ReferenceKind.Declaration;
            if (r.Local != null && r.Local.Offset == t.Start) return ReferenceKind.Declaration;
            var role = doc.OccurrenceRoleAt(t.Start);
            var k = ReferenceKind.None;
            if ((role & OccurrenceRole.Read) != 0) k |= ReferenceKind.Read;
            if ((role & OccurrenceRole.Write) != 0) k |= ReferenceKind.Write;
            if ((role & OccurrenceRole.Call) != 0) k |= ReferenceKind.Call;
            if ((role & OccurrenceRole.TypeUse) != 0) k |= ReferenceKind.TypeUse;
            if (k == ReferenceKind.None)
            {
                // outside function bodies: type uses in declarations
                if (doc.IsIdentifier(ti + 1) || doc.Is(ti + 1, Punct.Star) || doc.Is(ti + 1, Punct.Amp) || doc.Is(ti + 1, Punct.Lt) || doc.Is(ti + 1, Punct.ColonColon))
                    k = ReferenceKind.TypeUse;
                else if (doc.Is(ti + 1, Punct.LParen)) k = ReferenceKind.Call;
                else k = ReferenceKind.Read;
            }
            return k;
        }

        private ReferenceHit MakeHit(DocumentModel doc, int ti, ReferenceKind kind, ResolutionConfidence conf, string module, ItemOrigin origin)
        {
            var t = doc.Tokens[ti];
            var (line, col) = doc.PositionOf(t.Start);
            int length = LineMap.Utf16Length(new ReadOnlySpan<byte>(doc.Text, t.Start, t.Length));
            var (text, textCol) = DisplayLine(doc.LineText(line), col);
            return new ReferenceHit
            {
                Location = new SourceLocation { FilePath = doc.Path, Line = line, Column = col },
                Length = length,
                Kind = kind,
                LineText = text,
                LineTextColumn = textCol,
                Context = ContextName(doc, t.Start),
                Confidence = conf,
                Module = module,
                Origin = origin,
            };
        }

        /// <summary>Tabs expanded (4), leading whitespace trimmed, at most ~200 chars around the match.</summary>
        public static (string Text, int Column) DisplayLine(string line, int column)
        {
            var sb = new System.Text.StringBuilder(line.Length + 8);
            int mapped = -1;
            for (int i = 0; i < line.Length; i++)
            {
                if (i == column) mapped = sb.Length;
                if (line[i] == '\t')
                {
                    int spaces = 4 - sb.Length % 4;
                    sb.Append(' ', spaces);
                }
                else sb.Append(line[i]);
            }
            if (mapped < 0) mapped = sb.Length;
            string s = sb.ToString();
            int lead = 0;
            while (lead < s.Length && char.IsWhiteSpace(s[lead])) lead++;
            s = s.Substring(lead).TrimEnd();
            mapped = Math.Max(0, mapped - lead);
            const int Max = 200;
            if (s.Length > Max)
            {
                int start = Math.Max(0, Math.Min(mapped - 80, s.Length - Max));
                s = s.Substring(start, Max);
                mapped -= start;
            }
            return (s, mapped);
        }

        private static string ContextName(DocumentModel doc, int offset)
        {
            var scopes = doc.EnclosingScopes(offset);
            for (int k = scopes.Count - 1; k >= 0; k--)
            {
                var d = doc.Decls[scopes[k]];
                if (d.Kind == SymbolKind.Namespace) continue;
                return d.QualifiedName;
            }
            return null;
        }

        // ================================================================== type hierarchy

        public TypeHierarchyResponse GetTypeHierarchy(TypeHierarchyRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new TypeHierarchyResponse { IndexReady = _indexReady() };
            var caretDoc = request?.Caret != null && string.IsNullOrEmpty(request.TypeName) ? Load(request.Caret) : null;
            var ctx = new NavContext(_backend, _files, caretDoc, ct);
            TypeEntry root = null;
            if (!string.IsNullOrEmpty(request?.TypeName))
            {
                root = ctx.FindType(string.IsNullOrEmpty(request.Container) ? request.TypeName : request.Container + "::" + request.TypeName)
                       ?? ctx.FindType(request.TypeName);
            }
            else if (caretDoc != null)
            {
                int offset = CaretOffset(caretDoc, request.Caret);
                var r = SymbolResolver.Resolve(ctx, caretDoc, offset);
                var typeSym = r.Candidates.FirstOrDefault(c => TypeText.IsRecordKind(c.Kind));
                if (typeSym != null) root = ctx.FindType(TypeText.Qualified(typeSym.Container, typeSym.Name));
                else if (r.Candidates.Count > 0 && !string.IsNullOrEmpty(r.Candidates[0].Container) && r.Local == null)
                    root = ctx.FindType(r.Candidates[0].Container);
                if (root == null)
                {
                    string cls = caretDoc.EnclosingClass(offset, out _);
                    if (cls != null) root = ctx.FindType(cls);
                }
            }
            if (root == null)
            {
                LogHierarchy(resp, sw, request?.TypeName);
                return resp;
            }
            resp.Root = root.Symbol;
            int maxDepth = request.MaxDepth > 0 ? request.MaxDepth : 32;
            int maxNodes = request.MaxNodes > 0 ? request.MaxNodes : 5000;
            int nodes = 0;
            string rootKey = TypeText.Qualified(root.Symbol.Container, root.Symbol.Name);
            if (request.Direction != HierarchyDirection.Derived)
            {
                var path = new HashSet<string>(StringComparer.Ordinal) { rootKey };
                foreach (var b in root.Bases)
                {
                    if (nodes >= maxNodes) break;
                    resp.Bases.Add(BaseNode(ctx, b, 1, maxDepth, maxNodes, ref nodes, path));
                }
            }
            if (request.Direction != HierarchyDirection.Bases)
            {
                var path = new HashSet<string>(StringComparer.Ordinal) { rootKey };
                foreach (var d in ctx.DerivedOf(root))
                {
                    if (nodes >= maxNodes) break;
                    resp.Derived.Add(DerivedNode(ctx, d, 1, maxDepth, maxNodes, ref nodes, path));
                }
            }
            resp.NodeCount = nodes;
            LogHierarchy(resp, sw, rootKey);
            return resp;
        }

        private static void LogHierarchy(TypeHierarchyResponse resp, Stopwatch sw, string root)
        {
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            L.Info("nav.hierarchy", null, ("root", root), ("found", resp.Root != null), ("bases", resp.Bases.Count), ("derived", resp.Derived.Count),
                ("nodes", resp.NodeCount), ("ms", resp.ElapsedMs));
        }

        private static TypeHierarchyNode BaseNode(NavContext ctx, string baseText, int depth, int maxDepth, int maxNodes, ref int nodes, HashSet<string> path)
        {
            nodes++;
            var entry = ctx.FindType(baseText);
            var node = new TypeHierarchyNode { Symbol = entry?.Symbol, DisplayName = baseText?.Trim() };
            if (entry == null) return node;
            string key = TypeText.Qualified(entry.Symbol.Container, entry.Symbol.Name);
            if (entry.Bases.Length == 0) return node;
            if (depth >= maxDepth || nodes >= maxNodes || path.Contains(key))
            {
                node.Truncated = !path.Contains(key) || depth >= maxDepth;
                return node;
            }
            path.Add(key);
            foreach (var b in entry.Bases)
            {
                if (nodes >= maxNodes)
                {
                    node.Truncated = true;
                    break;
                }
                node.Children.Add(BaseNode(ctx, b, depth + 1, maxDepth, maxNodes, ref nodes, path));
            }
            path.Remove(key);
            return node;
        }

        private static TypeHierarchyNode DerivedNode(NavContext ctx, TypeEntry entry, int depth, int maxDepth, int maxNodes, ref int nodes, HashSet<string> path)
        {
            nodes++;
            var node = new TypeHierarchyNode { Symbol = entry.Symbol, DisplayName = entry.Symbol.Name };
            string key = TypeText.Qualified(entry.Symbol.Container, entry.Symbol.Name);
            if (path.Contains(key)) return node; // cycle (A : B, B : A in broken code)
            var derived = ctx.DerivedOf(entry);
            if (derived.Count == 0) return node;
            if (depth >= maxDepth || nodes >= maxNodes)
            {
                node.Truncated = true;
                return node;
            }
            path.Add(key);
            foreach (var d in derived)
            {
                if (nodes >= maxNodes)
                {
                    node.Truncated = true;
                    break;
                }
                node.Children.Add(DerivedNode(ctx, d, depth + 1, maxDepth, maxNodes, ref nodes, path));
            }
            path.Remove(key);
            return node;
        }

        // ================================================================== highlights

        /// <summary>Occurrences of the caret's symbol in this document only (no index access).</summary>
        public HighlightResponse GetHighlights(CaretRequest caret, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new HighlightResponse { Word = string.Empty };
            var doc = Load(caret);
            if (doc == null) return resp;
            int ti = doc.TokenAt(CaretOffset(doc, caret));
            if (ti < 0 || doc.Tokens[ti].Kind != TokenKind.Identifier)
            {
                resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return resp;
            }
            string cacheKey = NavContext.NormalizePath(doc.Path) + "|" + doc.Version + "|" + doc.Tokens[ti].Start + "|" + doc.Text.Length;
            if (_highlights.TryGet(cacheKey, out var cached))
            {
                resp.Word = cached.Word;
                resp.Spans = cached.Spans;
                resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return resp;
            }
            string word = doc.TokenText(ti);
            resp.Word = word;
            var local = LocalAt(doc, ti);
            for (int i = 0; i < doc.Tokens.Length; i++)
            {
                if ((i & 2047) == 0) ct.ThrowIfCancellationRequested();
                var t = doc.Tokens[i];
                if (t.Kind != TokenKind.Identifier || t.Length != doc.Tokens[ti].Length || doc.TokenText(i) != word) continue;
                var other = LocalAt(doc, i);
                if (local != null ? other == null || other.Offset != local.Offset || other.Function != local.Function : other != null) continue;
                var (line, col) = doc.PositionOf(t.Start);
                ReferenceKind kind;
                int di = doc.DeclAtOffset(t.Start);
                if (di >= 0) kind = doc.Decls[di].IsDefinition && TypeText.IsCallableKind(doc.Decls[di].Kind) ? ReferenceKind.Definition : ReferenceKind.Declaration;
                else if (other != null && other.Offset == t.Start) kind = ReferenceKind.Declaration;
                else kind = KindAt(doc, i, new Resolution());
                resp.Spans.Add(new HighlightSpan { Line = line, Column = col, Length = LineMap.Utf16Length(new ReadOnlySpan<byte>(doc.Text, t.Start, t.Length)), Kind = kind });
            }
            _highlights.Add(cacheKey, resp);
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            if (resp.ElapsedMs > 20)
                L.Warn("nav.highlights.slow", "Reference highlighting over its 20 ms budget", ("word", word), ("spans", resp.Spans.Count),
                    ("tokens", doc.Tokens.Length), ("bytes", doc.Text.Length), ("ms", resp.ElapsedMs));
            else
                L.Debug("nav.highlights", null, ("word", word), ("spans", resp.Spans.Count), ("tokens", doc.Tokens.Length), ("ms", resp.ElapsedMs));
            return resp;
        }

        private static LocalVariable LocalAt(DocumentModel doc, int ti)
        {
            if (doc.Is(ti - 1, Punct.ColonColon) || doc.Is(ti - 1, Punct.Dot) || doc.Is(ti - 1, Punct.Arrow)) return null;
            int off = doc.Tokens[ti].Start;
            return doc.FindLocal(doc.EnclosingFunction(off), doc.TokenText(ti), off);
        }

        // ================================================================== caret context

        public CaretContextResponse GetCaretContext(CaretRequest caret, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new CaretContextResponse();
            var doc = Load(caret);
            if (doc == null) return resp;
            var ctx = new NavContext(_backend, _files, doc, ct);
            int offset = CaretOffset(doc, caret);
            var scopes = doc.EnclosingScopes(offset);
            foreach (int di in scopes)
            {
                var d = doc.Decls[di];
                // out-of-line member function: show its class before it (navigation bar "type" drop-down)
                if (TypeText.IsCallableKind(d.Kind) && !string.IsNullOrEmpty(d.Container) && !doc.IsKnownNamespace(d.Container) &&
                    !resp.Scopes.Any(s => TypeText.Qualified(s.Container, s.Name) == d.Container))
                {
                    var owner = ctx.FindType(d.Container);
                    if (owner != null) resp.Scopes.Add(owner.Symbol);
                }
                resp.Scopes.Add(ctx.ToSymbol(doc, di));
            }
            for (int i = 0; i < doc.Decls.Count; i++)
            {
                if (doc.Decls[i].IsImplicit || string.IsNullOrEmpty(doc.Decls[i].Name)) continue;
                resp.FileSymbols.Add(ctx.ToSymbol(doc, i));
            }
            resp.FileSymbols.Sort((a, b) => a.Location.Line != b.Location.Line ? a.Location.Line.CompareTo(b.Location.Line) : a.Location.Column.CompareTo(b.Location.Column));
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            L.Debug("nav.context", null, ("scopes", resp.Scopes.Count), ("symbols", resp.FileSymbols.Count), ("ms", resp.ElapsedMs));
            return resp;
        }

        // ================================================================== members

        public MembersResponse GetMembers(MembersRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new MembersResponse();
            if (string.IsNullOrEmpty(request?.TypeName)) return resp;
            var ctx = new NavContext(_backend, _files, null, ct);
            var root = (string.IsNullOrEmpty(request.Container) ? null : ctx.FindType(request.Container + "::" + request.TypeName)) ?? ctx.FindType(request.TypeName);
            if (root == null)
            {
                L.Info("nav.members", null, ("type", request.TypeName), ("found", false), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 2)));
                return resp;
            }
            resp.Type = root.Symbol;
            int max = request.MaxResults > 0 ? request.MaxResults : 2000;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var level = new List<TypeEntry> { root };
            for (int depth = 0; depth < 32 && level.Count > 0; depth++)
            {
                var next = new List<TypeEntry>();
                foreach (var t in level)
                {
                    ct.ThrowIfCancellationRequested();
                    string q = TypeText.Qualified(t.Symbol.Container, t.Symbol.Name);
                    if (!visited.Add(q)) continue;
                    foreach (var m in DeclaredMembers(ctx, t))
                    {
                        // the most derived declaration wins (overrides, re-declared implicit members)
                        string key = MemberKey(m);
                        if (!keys.Add(key)) continue;
                        if (resp.Members.Count >= max)
                        {
                            resp.Truncated = true;
                            break;
                        }
                        resp.Members.Add(m);
                    }
                    if (!request.IncludeInherited) continue;
                    foreach (var b in t.Bases)
                    {
                        var be = ctx.FindType(b);
                        if (be != null) next.Add(be);
                    }
                }
                if (resp.Truncated || !request.IncludeInherited) break;
                level = next;
            }
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            L.Info("nav.members", null, ("type", request.TypeName), ("found", true), ("members", resp.Members.Count), ("types", visited.Count),
                ("truncated", resp.Truncated), ("ms", resp.ElapsedMs));
            return resp;
        }

        private static string MemberKey(SymbolInfo m)
        {
            if (TypeText.IsCallableKind(m.Kind))
            {
                if (m.Kind == SymbolKind.Constructor || m.Kind == SymbolKind.Destructor) return "c:" + m.Container + "::" + m.Name + "/" + TypeText.ParameterKey(m.Signature);
                return "f:" + m.Name + "/" + TypeText.ParameterCount(m.Signature);
            }
            return "v:" + m.Name;
        }

        /// <summary>Members declared in the type's body (implicit GENERATED_BODY members included), from the file on disk.</summary>
        private List<SymbolInfo> DeclaredMembers(NavContext ctx, TypeEntry t)
        {
            var result = new List<SymbolInfo>();
            string path = t.Symbol.Location?.FilePath;
            string q = TypeText.Qualified(t.Symbol.Container, t.Symbol.Name);
            var doc = string.IsNullOrEmpty(path) ? null : Documents.GetFile(path);
            if (doc != null)
            {
                int cls = -1;
                for (int i = 0; i < doc.Decls.Count; i++)
                {
                    var d = doc.Decls[i];
                    if (TypeText.IsRecordKind(d.Kind) && d.Name == t.Symbol.Name && d.QualifiedName == q && !d.Body.IsNone)
                    {
                        cls = i;
                        if (d.Line == t.Symbol.Location.Line) break;
                    }
                }
                if (cls >= 0)
                {
                    for (int i = 0; i < doc.Decls.Count; i++)
                        if (doc.Decls[i].Parent == cls && !string.IsNullOrEmpty(doc.Decls[i].Name)) result.Add(ctx.ToSymbol(doc, i));
                    return result;
                }
            }
            // file unreadable: the index copy (no implicit members)
            foreach (var s in _backend.GetFileSymbols(path ?? string.Empty))
                if (string.Equals(s.Container, q, StringComparison.Ordinal)) result.Add(s);
            return result;
        }

        // ================================================================== caches

        private sealed class HighlightCache
        {
            private readonly int _max;
            private readonly LinkedList<(string Key, HighlightResponse Value)> _lru = new LinkedList<(string, HighlightResponse)>();
            private readonly Dictionary<string, LinkedListNode<(string Key, HighlightResponse Value)>> _map =
                new Dictionary<string, LinkedListNode<(string Key, HighlightResponse Value)>>(StringComparer.Ordinal);

            public HighlightCache(int max) => _max = max;

            public bool TryGet(string key, out HighlightResponse value)
            {
                lock (_lru)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        value = node.Value.Value;
                        return true;
                    }
                }
                value = null;
                return false;
            }

            public void Add(string key, HighlightResponse value)
            {
                lock (_lru)
                {
                    if (_map.ContainsKey(key)) return;
                    _map[key] = _lru.AddFirst((key, value));
                    while (_lru.Count > _max)
                    {
                        _map.Remove(_lru.Last.Value.Key);
                        _lru.RemoveLast();
                    }
                }
            }
        }
    }
}
