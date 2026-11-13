using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Unreal;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.Navigation
{
    /// <summary>
    /// v0.4.5 call hierarchy (VS's "View Call Hierarchy" needs VS's own C++ IntelliSense, which UEToVS switches off). One call
    /// answers one level, so the host expands lazily:
    /// <list type="bullet">
    /// <item>Callers: the same candidate files and per-token resolution as Find References, keeping call sites and delegate
    /// bindings (<see cref="DelegateBindings"/>: AddDynamic(this, &amp;UFoo::Bar), BindUObject, BindUFunction(this, "Bar"),
    /// BindAction, SetTimer…), grouped by the enclosing function.</item>
    /// <item>Callees: the calls (and member-pointer bindings) inside the function body, resolved token by token.</item>
    /// </list>
    /// A UFUNCTION Foo, its Foo_Implementation and Foo_Validate are one function: callers of any of them, callees of their bodies.
    /// </summary>
    public sealed partial class NavigationService
    {
        /// <summary>Call sites kept per item (a function called 10 000 times from one place does not flood the response).</summary>
        public const int MaxSitesPerItem = 200;

        /// <summary>Files read for name-string bindings (BindUFunction(this, "Foo")) in one request.</summary>
        public const int MaxByNameBindingFiles = 2000;

        private static readonly string[] UeSuffixes = { "_Implementation", "_Validate" };

        public CallHierarchyResponse GetCallHierarchy(CallHierarchyRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new CallHierarchyResponse { IndexReady = _indexReady(), Direction = request?.Direction ?? CallHierarchyDirection.Callers, Reason = "ok" };
            if (request == null) return FinishCalls(resp, sw, "no-request");
            var caretDoc = request.Function == null ? Load(request.Caret) : null;
            if (request.Function == null && caretDoc == null) return FinishCalls(resp, sw, "no-document");
            var ctx = new NavContext(_backend, _files, caretDoc, ct);
            var fn = request.Function ?? FunctionAtCaret(ctx, caretDoc, request.Caret);
            if (fn == null || !TypeText.IsCallableKind(fn.Kind)) return FinishCalls(resp, sw, "no-function");
            var family = FunctionFamily.Of(ctx, fn);
            resp.Root = family.Root;
            resp.Names.AddRange(family.Names);
            int max = request.MaxItems > 0 ? request.MaxItems : 500;
            if (request.Direction == CallHierarchyDirection.Callers) Callers(ctx, family, request.IncludeNonProject, max, resp, ct);
            else Callees(ctx, caretDoc, family, fn, max, resp, ct);
            return FinishCalls(resp, sw, resp.Reason);
        }

        private static CallHierarchyResponse FinishCalls(CallHierarchyResponse resp, Stopwatch sw, string reason)
        {
            resp.Reason = reason;
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            L.Info("navigation.callHierarchy", null, ("direction", resp.Direction.ToString()),
                ("function", resp.Root == null ? null : TypeText.Qualified(resp.Root.Container, resp.Root.Name)), ("names", resp.Names),
                ("items", resp.Items.Count), ("sites", resp.Items.Sum(i => i.Sites.Count)),
                ("bindings", resp.Items.Sum(i => i.Sites.Count(s => s.Kind == CallSiteKind.DelegateBinding))),
                ("files", resp.FilesScanned), ("truncated", resp.Truncated), ("reason", reason), ("indexReady", resp.IndexReady), ("ms", resp.ElapsedMs));
            return resp;
        }

        /// <summary>The callable at the caret (a call, a declaration, a member pointer), else the function whose body holds the caret.</summary>
        private static SymbolInfo FunctionAtCaret(NavContext ctx, DocumentModel doc, CaretRequest caret)
        {
            int offset = CaretOffset(doc, caret);
            var r = SymbolResolver.Resolve(ctx, doc, offset);
            if (r.Local == null && r.Include == null)
            {
                if (r.OnDeclaration != null && TypeText.IsCallableKind(r.OnDeclaration.Kind)) return r.OnDeclaration;
                var callables = r.Candidates.Where(c => TypeText.IsCallableKind(c.Kind)).ToList();
                if (callables.Count > 0)
                {
                    var pool = r.CallArguments != null ? SymbolResolver.FilterByCall(callables, r.CallArguments) : callables;
                    if (pool.Count == 0) pool = callables;
                    return pool.OrderBy(c => GeneratedFiles.Rank(c.Location?.FilePath)).ThenBy(c => TypeText.IsProject(c.Origin) ? 0 : 1).ThenBy(c => c.IsDefinition ? 1 : 0).First();
                }
            }
            int fi = doc.EnclosingFunction(offset);
            return fi >= 0 ? ctx.ToSymbol(doc, fi) : null;
        }

        /// <summary>UFUNCTION Foo + Foo_Implementation + Foo_Validate (the ones that exist), or just the function.</summary>
        internal sealed class FunctionFamily
        {
            public SymbolInfo Root { get; private set; }
            public string Container { get; private set; }
            public List<string> Names { get; } = new List<string>();

            /// <summary>Entity keys of every declaration / definition of the family (what a call must resolve to).</summary>
            public HashSet<string> Keys { get; } = new HashSet<string>(StringComparer.Ordinal);

            public bool IsUFunction { get; private set; }

            public static FunctionFamily Of(NavContext ctx, SymbolInfo fn)
            {
                var f = new FunctionFamily { Root = fn, Container = fn.Container };
                string baseName = fn.Name;
                if (TryUeBase(fn.Name, out string b))
                {
                    var baseDecls = Members(ctx, b, fn.Container);
                    if (baseDecls.Any(s => (s.Ue & UeFlags.UFunction) != 0) || baseDecls.Count > 0 && !string.IsNullOrEmpty(fn.Container))
                    {
                        baseName = b;
                        f.Root = baseDecls.OrderBy(s => GeneratedFiles.Rank(s.Location?.FilePath)).ThenBy(s => s.IsDefinition ? 1 : 0).ThenBy(s => TypeText.IsProject(s.Origin) ? 0 : 1).First();
                    }
                }
                var decls = Members(ctx, baseName, f.Container);
                f.IsUFunction = decls.Any(s => (s.Ue & UeFlags.UFunction) != 0) || (fn.Ue & UeFlags.UFunction) != 0;
                if (baseName == fn.Name && !fn.IsDefinition && (fn.Ue & UeFlags.UFunction) == 0)
                {
                    var ufn = decls.FirstOrDefault(s => !s.IsDefinition && (s.Ue & UeFlags.UFunction) != 0);
                    if (ufn != null) f.Root = ufn;
                }
                else if (baseName == fn.Name && fn.IsDefinition)
                {
                    // report the header declaration (docs, UFUNCTION flags) when there is one
                    var decl = decls.FirstOrDefault(s => !s.IsDefinition && TypeText.ParameterKey(s.Signature) == TypeText.ParameterKey(fn.Signature))
                               ?? decls.FirstOrDefault(s => !s.IsDefinition);
                    if (decl != null) f.Root = decl;
                }
                f.Add(baseName, decls, fn);
                if (f.IsUFunction || baseName != fn.Name)
                    foreach (var suffix in UeSuffixes)
                    {
                        var c = Members(ctx, baseName + suffix, f.Container);
                        if (c.Count > 0) f.Add(baseName + suffix, c, null);
                    }
                return f;
            }

            private void Add(string name, List<SymbolInfo> syms, SymbolInfo self)
            {
                if (!Names.Contains(name)) Names.Add(name);
                foreach (var s in syms) Keys.Add(SymbolResolver.EntityKey(s));
                if (self != null && self.Name == name) Keys.Add(SymbolResolver.EntityKey(self));
                if (syms.Count == 0) Keys.Add(SymbolResolver.EntityKey(new SymbolInfo { Name = name, Container = Container, Kind = SymbolKind.Method }));
            }

            private static List<SymbolInfo> Members(NavContext ctx, string name, string container) =>
                ctx.ByName(name).Where(s => TypeText.IsCallableKind(s.Kind) &&
                                            (string.IsNullOrEmpty(container) ? string.IsNullOrEmpty(s.Container) : NavContext.SameScope(s.Container, container))).ToList();
        }

        // ================================================================== callers

        private void Callers(NavContext ctx, FunctionFamily family, bool includeNonProject, int max, CallHierarchyResponse resp, CancellationToken ct)
        {
            // candidate files of every name of the family, each scanned once
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool capped = false;
            foreach (var name in family.Names)
            {
                var candidates = CandidateFiles(ctx, name, includeNonProject, null, ct, out bool cut);
                foreach (var f in candidates)
                    if (seen.Add(NavContext.NormalizePath(f))) files.Add(f);
                capped |= cut;
            }
            var groups = new ConcurrentDictionary<string, CallHierarchyItem>(StringComparer.Ordinal);
            int scanned = 0;
            var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)) };
            try
            {
                Parallel.ForEach(files, po, path =>
                {
                    var doc = Documents.GetFile(path);
                    Interlocked.Increment(ref scanned);
                    if (doc == null) return;
                    foreach (var name in family.Names)
                        foreach (var (ti, site) in CallerSites(ctx, doc, name, family.Keys, ct))
                            AddCallerSite(ctx, doc, doc.Tokens[ti].Start, site, groups);
                });
            }
            catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
            {
                throw new OperationCanceledException(ct);
            }

            // bindings by name string: BindUFunction(this, "Foo") / AddUFunction / CreateUFunction (only UFUNCTIONs can be bound by name)
            if (family.IsUFunction)
                scanned += ByNameBindings(ctx, family, includeNonProject, groups, ct, ref capped);

            resp.FilesScanned = scanned;
            var items = groups.Values.ToList();
            foreach (var i in items) Finish(i);
            int Rank(CallHierarchyItem i) => TypeText.IsProject(i.Function?.Origin ?? ctx.FileOrigin(i.Sites[0].Location.FilePath).Origin) ? 0 : 1;
            items = items.OrderBy(Rank)
                .ThenBy(i => i.Confidence == ResolutionConfidence.NameOnly ? 1 : 0)
                .ThenBy(i => i.Sites[0].Location.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Sites[0].Location.Line).ToList();
            resp.Truncated = capped || items.Count > max;
            resp.Items = items.Take(max).ToList();
        }

        /// <summary>Calls of <paramref name="name"/> resolving to the family (or not resolvable: NameOnly), and delegate bindings of it.</summary>
        private IEnumerable<(int Token, CallSite Site)> CallerSites(NavContext ctx, DocumentModel doc, string name, HashSet<string> keys, CancellationToken ct)
        {
            int nameBytes = System.Text.Encoding.UTF8.GetByteCount(name);
            for (int i = 0; i < doc.Tokens.Length; i++)
            {
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                var t = doc.Tokens[i];
                if (t.Kind != TokenKind.Identifier || t.Length != nameBytes || doc.TokenText(i) != name) continue;
                var r = SymbolResolver.ResolveToken(ctx, doc, i, 0);
                if (r.Local != null || r.OnDeclaration != null || r.Reason == "ue-macro") continue;
                bool resolved = r.Candidates.Any(c => keys.Contains(SymbolResolver.EntityKey(c)));
                if (!resolved && r.Candidates.Count > 0) continue;
                var conf = resolved ? (r.Confidence == ResolutionConfidence.None ? ResolutionConfidence.Scoped : r.Confidence) : ResolutionConfidence.NameOnly;
                var kind = KindAt(doc, i, r);
                CallSite site;
                if ((kind & ReferenceKind.Call) != 0 && !IsMemberPointer(doc, i)) site = MakeSite(doc, i, CallSiteKind.Call, conf);
                else
                {
                    var b = BindingOnLine(doc, doc.PositionOf(t.Start).Line, name);
                    if (b == null) continue;
                    site = MakeSite(doc, i, CallSiteKind.DelegateBinding, conf);
                    site.BindMethod = b.Method;
                    site.Delegate = b.Delegate;
                }
                yield return (i, site);
            }
        }

        private static bool IsMemberPointer(DocumentModel doc, int ti) =>
            doc.Is(ti - 1, Punct.ColonColon) && doc.IsIdentifier(ti - 2) && doc.Is(ti - 3, Punct.Amp) && !doc.Is(ti + 1, Punct.LParen);

        /// <summary>The binding of <paramref name="name"/> on a line (the bind call may start up to two lines above).</summary>
        private static DelegateBinding BindingOnLine(DocumentModel doc, int line, string name)
        {
            var prev = new List<string>(2);
            for (int k = Math.Max(0, line - 2); k < line; k++) prev.Add(doc.LineText(k));
            return DelegateBindings.Match(doc.LineText(line), name, prev);
        }

        private static CallSite MakeSite(DocumentModel doc, int ti, CallSiteKind kind, ResolutionConfidence conf)
        {
            var t = doc.Tokens[ti];
            var (line, col) = doc.PositionOf(t.Start);
            var (text, textCol) = DisplayLine(doc.LineText(line), col);
            return new CallSite
            {
                Location = new SourceLocation { FilePath = doc.Path, Line = line, Column = col },
                Length = LineMap.Utf16Length(new ReadOnlySpan<byte>(doc.Text, t.Start, t.Length)),
                Kind = kind,
                LineText = text,
                LineTextColumn = textCol,
                Confidence = conf,
            };
        }

        /// <summary>Groups a caller site under its enclosing function (or the file scope).</summary>
        private static void AddCallerSite(NavContext ctx, DocumentModel doc, int offset, CallSite site, ConcurrentDictionary<string, CallHierarchyItem> groups)
        {
            int fi = doc.EnclosingFunction(offset);
            string key = NavContext.NormalizePath(doc.Path) + "|" + (fi >= 0 ? doc.Decls[fi].Offset : -1);
            var item = groups.GetOrAdd(key, _ =>
            {
                if (fi >= 0) return new CallHierarchyItem { Function = ctx.ToSymbol(doc, fi), CanExpand = true };
                return new CallHierarchyItem { DisplayName = ContextName(doc, offset) ?? Path.GetFileName(doc.Path) };
            });
            lock (item)
            {
                if (item.Sites.Any(s => s.Location.Line == site.Location.Line && s.Location.Column == site.Location.Column)) return;
                item.Sites.Add(site);
            }
        }

        private int ByNameBindings(NavContext ctx, FunctionFamily family, bool includeNonProject, ConcurrentDictionary<string, CallHierarchyItem> groups,
            CancellationToken ct, ref bool capped)
        {
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var method in DelegateBindings.ByNameMethods)
                foreach (var p in _backend.FindOccurrenceFiles(method, ct))
                {
                    if (string.IsNullOrEmpty(p) || !seen.Add(NavContext.NormalizePath(p))) continue;
                    if (!includeNonProject && !TypeText.IsProject(ctx.FileOrigin(p).Origin)) continue;
                    if (files.Count >= MaxByNameBindingFiles)
                    {
                        capped = true;
                        break;
                    }
                    files.Add(p);
                }
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var doc = Documents.GetFile(path);
                if (doc == null) continue;
                for (int i = 0; i < doc.Tokens.Length; i++)
                {
                    var t = doc.Tokens[i];
                    if (t.Kind != TokenKind.String) continue;
                    string lit = doc.TokenText(i);
                    if (lit.Length < 3 || lit[0] != '"') continue;
                    string name = lit.Substring(1, lit.Length - 2);
                    if (!family.Names.Contains(name)) continue;
                    var b = BindingOnLine(doc, doc.PositionOf(t.Start).Line, name);
                    if (b == null || !b.Target.StartsWith("\"", StringComparison.Ordinal)) continue;
                    var site = MakeSite(doc, i, CallSiteKind.DelegateBinding, ResolutionConfidence.NameOnly);
                    site.BindMethod = b.Method;
                    site.Delegate = b.Delegate;
                    // the name is a string: say which class it names when the bind call names one (this / a typed pointer is not checked)
                    AddCallerSite(ctx, doc, t.Start, site, groups);
                }
            }
            return files.Count;
        }

        private static void Finish(CallHierarchyItem item)
        {
            item.Sites.Sort((a, b) =>
            {
                int c = string.Compare(a.Location.FilePath, b.Location.FilePath, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = a.Location.Line.CompareTo(b.Location.Line);
                return c != 0 ? c : a.Location.Column.CompareTo(b.Location.Column);
            });
            if (item.Sites.Count > MaxSitesPerItem) item.Sites.RemoveRange(MaxSitesPerItem, item.Sites.Count - MaxSitesPerItem);
            item.Confidence = item.Sites.Count == 0 ? ResolutionConfidence.None : item.Sites.Max(s => s.Confidence);
        }

        // ================================================================== callees

        private void Callees(NavContext ctx, DocumentModel caretDoc, FunctionFamily family, SymbolInfo fn, int max, CallHierarchyResponse resp, CancellationToken ct)
        {
            var bodies = Bodies(ctx, caretDoc, family, fn);
            resp.FilesScanned = bodies.Select(b => b.Doc.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (bodies.Count == 0)
            {
                resp.Reason = "no-body";
                return;
            }
            var groups = new Dictionary<string, CallHierarchyItem>(StringComparer.Ordinal);
            var order = new List<CallHierarchyItem>();
            foreach (var (doc, di) in bodies)
            {
                var body = doc.Decls[di].Body;
                int end = body.End;
                for (int i = doc.TokenIndexFrom(body.Start); i < doc.Tokens.Length && doc.Tokens[i].Start < end; i++)
                {
                    if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                    if (doc.Tokens[i].Kind != TokenKind.Identifier) continue;
                    bool call = doc.Is(i + 1, Punct.LParen);
                    bool memberPointer = !call && doc.Is(i - 1, Punct.ColonColon) && doc.IsIdentifier(i - 2) && doc.Is(i - 3, Punct.Amp);
                    if (!call && !memberPointer) continue;
                    string name = doc.TokenText(i);
                    var r = SymbolResolver.ResolveToken(ctx, doc, i, 0);
                    if (r.Local != null || r.OnDeclaration != null || r.Reason == "ue-macro") continue;
                    if (r.Candidates.Any(c => c.Kind == SymbolKind.Macro)) continue;
                    var callables = r.Candidates.Where(c => TypeText.IsCallableKind(c.Kind)).ToList();
                    if (r.Candidates.Count > 0 && callables.Count == 0) continue; // a type (functional cast / temporary), a variable of functor type...
                    CallSite site;
                    if (memberPointer)
                    {
                        if (callables.Count == 0) continue;
                        var b = BindingOnLine(doc, doc.PositionOf(doc.Tokens[i].Start).Line, name);
                        if (b == null) continue;
                        site = MakeSite(doc, i, CallSiteKind.DelegateBinding, r.Confidence);
                        site.BindMethod = b.Method;
                        site.Delegate = b.Delegate;
                    }
                    else
                    {
                        if (callables.Count == 0 && LooksLikeMacro(name)) continue;
                        site = MakeSite(doc, i, CallSiteKind.Call, callables.Count == 0 ? ResolutionConfidence.NameOnly : r.Confidence);
                    }

                    string key;
                    SymbolInfo target = null;
                    if (callables.Count > 0)
                    {
                        var pool = r.CallArguments != null ? SymbolResolver.FilterByCall(callables, r.CallArguments) : callables;
                        if (pool.Count == 0) pool = callables;
                        target = pool.OrderBy(c => GeneratedFiles.Rank(c.Location?.FilePath)).ThenBy(c => c.IsDefinition ? 1 : 0).ThenBy(c => TypeText.IsProject(c.Origin) ? 0 : 1).First();
                        key = SymbolResolver.EntityKey(target) + "(" + TypeText.ParameterKey(target.Signature) + ")";
                    }
                    else key = "?" + CalleeText(doc, i);
                    if (!groups.TryGetValue(key, out var item))
                    {
                        item = target != null
                            ? new CallHierarchyItem { Function = target, CanExpand = true }
                            : new CallHierarchyItem { DisplayName = CalleeText(doc, i) };
                        groups[key] = item;
                        order.Add(item);
                    }
                    if (item.Sites.Count < MaxSitesPerItem) item.Sites.Add(site);
                }
            }
            foreach (var i in order)
                i.Confidence = i.Sites.Count == 0 ? ResolutionConfidence.None : i.Sites.Max(s => s.Confidence);
            resp.Truncated = order.Count > max;
            resp.Items = order.Take(max).ToList();
        }

        /// <summary>The bodies of the family's definitions (the caret's overload when the function was given as a definition).</summary>
        private List<(DocumentModel Doc, int Decl)> Bodies(NavContext ctx, DocumentModel caretDoc, FunctionFamily family, SymbolInfo fn)
        {
            var result = new List<(DocumentModel, int)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string pk = TypeText.ParameterKey(fn.Signature);
            foreach (var name in family.Names)
            {
                var defs = ctx.ByName(name).Where(s => TypeText.IsCallableKind(s.Kind) && s.IsDefinition && s.Location != null &&
                    (string.IsNullOrEmpty(family.Container) ? string.IsNullOrEmpty(s.Container) : NavContext.SameScope(s.Container, family.Container))).ToList();
                if (name == fn.Name && pk != null && defs.Count > 1) defs = SameParameters(defs, pk);
                foreach (var s in defs)
                {
                    var path = s.Location.FilePath;
                    var doc = caretDoc != null && ctx.IsOverlayPath(path) ? caretDoc : Documents.GetFile(path);
                    if (doc == null) continue;
                    int di = FindDecl(doc, s);
                    if (di < 0 || doc.Decls[di].Body.IsNone) continue;
                    if (seen.Add(NavContext.NormalizePath(path) + "|" + di)) result.Add((doc, di));
                }
            }
            return result;
        }

        private static int FindDecl(DocumentModel doc, SymbolInfo s)
        {
            int di = doc.DeclAtOffset(doc.OffsetOf(s.Location.Line, s.Location.Column));
            if (di >= 0 && doc.Decls[di].Name == s.Name) return di;
            for (int i = 0; i < doc.Decls.Count; i++)
            {
                var d = doc.Decls[i];
                if (d.Line == s.Location.Line && d.Name == s.Name && TypeText.IsCallableKind(d.Kind)) return i;
            }
            return -1;
        }

        /// <summary>An unresolved callee as written: "Obj-&gt;Run", "FMath::Clamp", "Foo".</summary>
        private static string CalleeText(DocumentModel doc, int ti)
        {
            string name = doc.TokenText(ti);
            if ((doc.Is(ti - 1, Punct.ColonColon) || doc.Is(ti - 1, Punct.Arrow) || doc.Is(ti - 1, Punct.Dot)) && doc.IsIdentifier(ti - 2))
                return doc.TokenText(ti - 2) + doc.TokenText(ti - 1) + name;
            return name;
        }

        /// <summary>UE_LOG, TEXT, check… (macros the index may not know): all caps, digits and underscores.</summary>
        internal static bool LooksLikeMacro(string name)
        {
            bool letter = false;
            foreach (char c in name)
            {
                if (char.IsLower(c)) return false;
                if (char.IsUpper(c)) letter = true;
            }
            return letter && name.Length > 1;
        }
    }
}
