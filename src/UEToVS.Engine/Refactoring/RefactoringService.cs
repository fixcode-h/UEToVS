using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.Refactoring
{
    /// <summary>
    /// M4 questions the host asks before planning a refactoring (§6.6): what is at the caret (function signature, its
    /// declarations / definitions, the class, the type and whether its header is included) and which base-class virtuals a
    /// class can override. Read-only: the engine never edits files. Everything is computed on demand per request from the
    /// document cache and the index (no resident scans); the include walk is bounded and its result cached per file +
    /// include list.
    /// </summary>
    public sealed class RefactoringService
    {
        private static readonly Logger L = Log.For("Refactor");

        /// <summary>Include walk bounds: levels below the file's own includes, and files visited.</summary>
        public const int MaxIncludeDepth = 4;
        public const int MaxIncludeFiles = 600;

        private readonly ISymbolIndexBackend _backend;
        private readonly FileTable _files;
        private readonly DocumentCache _documents;
        private readonly Func<bool> _indexReady;
        private readonly IncludeCache _includeCache = new IncludeCache(32);

        public RefactoringService(ISymbolIndexBackend backend, FileTable files, DocumentCache documents, Func<bool> indexReady = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _files = files;
            _documents = documents ?? new DocumentCache();
            _indexReady = indexReady ?? (() => true);
        }

        /// <summary>Member tables shared with IntelliSense (expression typing for Introduce Variable); a small private cache when not set.</summary>
        public MemberTableCache MemberTables { get; set; }

        /// <summary>Include lists of indexed files (tests may replace it; default: the segmented index's file records, else null).</summary>
        public Func<string, IReadOnlyList<string>> IncludesOf { get; set; }

        // ================================================================== context

        public RefactoringContextResponse GetContext(RefactoringContextRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new RefactoringContextResponse { Word = string.Empty, IndexReady = _indexReady() };
            var caret = request?.Caret;
            if (caret == null || string.IsNullOrEmpty(caret.FilePath)) return Finish(resp, sw, request, "no-caret");
            var doc = _documents.Get(caret.FilePath, caret.Text, caret.TextVersion);
            if (doc == null) return Finish(resp, sw, request, "no-document");
            var ctx = new NavContext(_backend, _files, doc, ct);
            int offset = doc.OffsetOf(Math.Max(0, caret.Line), Math.Max(0, caret.Column));

            var r = SymbolResolver.Resolve(ctx, doc, offset);
            resp.Word = r.Word ?? string.Empty;
            resp.Confidence = r.Confidence;
            resp.OnDeclaration = r.OnDeclaration != null;
            if (r.Include == null && r.Candidates.Count > 0)
                resp.Symbol = r.OnDeclaration ?? r.Candidates.OrderBy(c => TypeText.IsProject(c.Origin) ? 0 : 1).ThenBy(c => c.IsDefinition ? 1 : 0).First();

            // function at the caret: on its name, or anywhere on its signature lines
            int fi = FunctionAt(doc, offset, caret.Line);
            if (fi >= 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = doc.Decls[fi];
                var sig = ToSignature(ctx, doc, fi);
                resp.Function = sig;
                string container = OwnerOf(doc, fi);
                sig.Container = container;
                int count = TypeText.ParameterCount(d.Signature);
                foreach (var s in ctx.ByName(d.Name))
                {
                    if (!TypeText.IsCallableKind(s.Kind) || !NavContext.SameScope(s.Container, container)) continue;
                    if (count >= 0 && TypeText.ParameterCount(s.Signature) != count) continue;
                    (s.IsDefinition ? resp.Definitions : resp.Declarations).Add(s);
                }
                foreach (var suffix in new[] { "_Implementation", "_Validate" })
                    foreach (var s in ctx.ByName(d.Name + suffix))
                        if (s.IsDefinition && TypeText.IsCallableKind(s.Kind) && NavContext.SameScope(s.Container, container)) resp.UeCounterpartDefinitions.Add(s);
                if (resp.Symbol == null) resp.Symbol = ctx.ToSymbol(doc, fi);
            }

            resp.Class = ClassAt(ctx, doc, offset, resp.Function);
            if (resp.Symbol != null && IsTypeLike(resp.Symbol.Kind) && !resp.OnDeclaration)
                resp.Type = TypeInfo(ctx, doc, resp.Symbol, r, request.CheckIncludes, ct);

            // ---- v0.4.5 refactoring batch 2 (all opt-in: nothing extra is computed for a plain Alt+Enter)
            if (request.SelectionEndLine >= 0) resp.ExpressionType = ExpressionTypeAt(ctx, doc, request);
            if (request.IncludeLocals) FillLocals(ctx, doc, offset, resp);
            if (request.IncludeSignatureFamily && fi >= 0) FillFamily(ctx, doc, fi, resp, ct);
            return Finish(resp, sw, request, null);
        }

        private static RefactoringContextResponse Finish(RefactoringContextResponse resp, Stopwatch sw, RefactoringContextRequest request, string reason)
        {
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            L.Info("refactor.context", null, ("word", resp.Word), ("reason", reason), ("function", resp.Function?.Name),
                ("decls", resp.Declarations.Count), ("defs", resp.Definitions.Count), ("class", resp.Class?.Symbol?.Name),
                ("type", resp.Type?.Symbol?.Name), ("include", resp.Type?.Include.ToString()), ("visited", resp.Type?.FilesVisited),
                ("checkIncludes", request?.CheckIncludes), ("exprType", resp.ExpressionType), ("locals", resp.Locals.Count),
                ("bases", resp.BaseDeclarations.Count), ("overrides", resp.Overrides.Count), ("ms", resp.ElapsedMs),
                ("file", Path.GetFileName(request?.Caret?.FilePath ?? string.Empty)), ("line", request?.Caret?.Line));
            return resp;
        }

        // ================================================================== v0.4.5: expression type, locals, override chain

        /// <summary>Type of the expression ending at the selection end (ExpressionTypes), or null.</summary>
        private string ExpressionTypeAt(NavContext ctx, DocumentModel doc, RefactoringContextRequest request)
        {
            try
            {
                int end = doc.OffsetOf(Math.Max(0, request.SelectionEndLine), Math.Max(0, request.SelectionEndColumn));
                int ti = doc.TokenIndexFrom(end) - 1;
                if (ti < 0 || doc.Tokens[ti].End > end) return null;
                if (request.SelectionStartLine >= 0)
                {
                    int start = doc.OffsetOf(Math.Max(0, request.SelectionStartLine), Math.Max(0, request.SelectionStartColumn));
                    if (doc.Tokens[ti].Start < start) return null;
                }
                var session = new IsSession(ctx, doc, _documents, MemberTables ?? (MemberTables = new MemberTableCache(16)), null);
                string type = new ExpressionTypes(session, doc.EnclosingClass(doc.Tokens[ti].Start, out _)).TypeOf(ti);
                return string.IsNullOrWhiteSpace(type) ? null : type.Trim();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                L.Warn("refactor.expressionType.failed", "Typing the selected expression failed", ex, ("file", Path.GetFileName(doc.Path)));
                return null;
            }
        }

        /// <summary>The function whose body contains the caret and its locals / parameters (positions as line / UTF-16 column).</summary>
        private static void FillLocals(NavContext ctx, DocumentModel doc, int offset, RefactoringContextResponse resp)
        {
            int fi = doc.EnclosingFunction(offset);
            if (fi < 0) return;
            var sig = ToSignature(ctx, doc, fi);
            sig.Container = OwnerOf(doc, fi);
            resp.EnclosingFunction = sig;
            foreach (var l in doc.LocalsOf(fi))
            {
                var (line, col) = doc.PositionOf(l.Offset);
                int scopeEnd = Math.Min(l.ScopeEnd, doc.Text.Length);
                var (eLine, eCol) = doc.PositionOf(scopeEnd);
                resp.Locals.Add(new LocalSymbol
                {
                    Name = l.Name, Type = l.Type, Line = line, Column = col, IsParameter = l.IsParameter, ScopeEndLine = eLine, ScopeEndColumn = eCol,
                });
            }
        }

        /// <summary>
        /// The override chain of a function (Change Signature): same name and parameter count in base types (nearest first) and
        /// overrides in derived types (Name and Name_Implementation). Bounded: 64 base types, 2000 derived types.
        /// </summary>
        private static void FillFamily(NavContext ctx, DocumentModel doc, int fi, RefactoringContextResponse resp, CancellationToken ct)
        {
            var d = doc.Decls[fi];
            string owner = OwnerOf(doc, fi);
            if (string.IsNullOrEmpty(owner) || doc.IsKnownNamespace(owner)) return;
            var root = ctx.FindType(owner);
            if (root == null) return;
            int count = TypeText.ParameterCount(d.Signature);
            bool SameShape(SymbolInfo s) => TypeText.IsCallableKind(s.Kind) && (count < 0 || TypeText.ParameterCount(s.Signature) == count);

            var seen = new HashSet<string>(StringComparer.Ordinal) { TypeText.Qualified(root.Symbol.Container, root.Symbol.Name) };
            var level = root.Bases.ToList();
            for (int depth = 0; depth < 32 && level.Count > 0 && seen.Count < 64; depth++)
            {
                var next = new List<string>();
                foreach (var baseText in level)
                {
                    ct.ThrowIfCancellationRequested();
                    var be = ctx.FindType(baseText);
                    if (be?.Symbol == null) continue;
                    string q = TypeText.Qualified(be.Symbol.Container, be.Symbol.Name);
                    if (!seen.Add(q)) continue;
                    foreach (var s in ctx.ByName(d.Name))
                        if (SameShape(s) && string.Equals(s.Container, q, StringComparison.Ordinal)) resp.BaseDeclarations.Add(s);
                    next.AddRange(be.Bases);
                }
                level = next;
            }

            var queue = new Queue<TypeEntry>();
            queue.Enqueue(root);
            int visited = 0;
            while (queue.Count > 0 && visited < 2000)
            {
                foreach (var t in ctx.DerivedOf(queue.Dequeue()))
                {
                    ct.ThrowIfCancellationRequested();
                    string q = TypeText.Qualified(t.Symbol.Container, t.Symbol.Name);
                    if (!seen.Add(q)) continue;
                    visited++;
                    queue.Enqueue(t);
                    foreach (var name in new[] { d.Name, d.Name + "_Implementation" })
                        foreach (var s in ctx.ByName(name))
                            if (SameShape(s) && string.Equals(s.Container, q, StringComparison.Ordinal)) resp.Overrides.Add(s);
                }
            }
            L.Debug("refactor.family", null, ("name", d.Name), ("owner", owner), ("bases", resp.BaseDeclarations.Count), ("overrides", resp.Overrides.Count),
                ("types", seen.Count));
        }

        private static bool IsTypeLike(SymbolKind k) => TypeText.IsRecordKind(k) || k == SymbolKind.Enum || k == SymbolKind.Typedef || k == SymbolKind.Delegate;

        /// <summary>Callable declaration whose name is at the caret, else one whose signature spans the caret line (before its body).</summary>
        internal static int FunctionAt(DocumentModel doc, int offset, int caretLine)
        {
            int ti = doc.TokenAt(offset);
            if (ti >= 0)
            {
                int di = doc.DeclAtOffset(doc.Tokens[ti].Start);
                if (di >= 0 && TypeText.IsCallableKind(doc.Decls[di].Kind) && !doc.Decls[di].IsImplicit) return di;
                if (di >= 0) return -1; // on another declaration's name (class, field)
            }
            int best = -1;
            for (int i = 0; i < doc.Decls.Count; i++)
            {
                var d = doc.Decls[i];
                if (d.IsImplicit || !TypeText.IsCallableKind(d.Kind) || string.IsNullOrEmpty(d.Name)) continue;
                int last = d.Line;
                if (!d.Body.IsNone) last = Math.Max(d.Line, doc.PositionOf(d.Body.Start).Line);
                else last = Math.Max(d.Line, EndLineOfDeclaration(doc, d));
                if (caretLine >= d.Line && caretLine <= last && (d.Body.IsNone || offset <= d.Body.Start)) best = i;
            }
            return best;
        }

        /// <summary>Qualified owner of a function: the enclosing class for in-class declarations, the written qualifier otherwise.</summary>
        private static string OwnerOf(DocumentModel doc, int fi)
        {
            var d = doc.Decls[fi];
            if (d.Parent >= 0 && TypeText.IsRecordKind(doc.Decls[d.Parent].Kind)) return doc.Decls[d.Parent].QualifiedName;
            return string.IsNullOrEmpty(d.Container) ? null : d.Container;
        }

        internal static FunctionSignature ToSignature(NavContext ctx, DocumentModel doc, int i)
        {
            var d = doc.Decls[i];
            var (module, origin) = ctx.FileOrigin(doc.Path);
            int end = !d.Body.IsNone ? doc.PositionOf(Math.Max(d.Body.Start, d.Body.End - 1)).Line : EndLineOfDeclaration(doc, d);
            return new FunctionSignature
            {
                Name = d.Name,
                Container = string.IsNullOrEmpty(d.Container) ? null : d.Container,
                Kind = d.Kind,
                ReturnType = string.IsNullOrEmpty(d.Type) ? null : d.Type,
                Parameters = d.Parameters ?? string.Empty,
                TemplateParameters = string.IsNullOrEmpty(d.TemplateParameters) ? null : d.TemplateParameters,
                Modifiers = MapModifiers(d.Modifiers),
                Access = (MemberAccess)(byte)d.Access,
                Specifiers = string.IsNullOrEmpty(d.Specifiers) ? null : d.Specifiers,
                Ue = d.Ue,
                IsDefinition = d.IsDefinition && !d.Body.IsNone,
                Location = new SourceLocation { FilePath = doc.Path, Line = d.Line, Column = d.Column },
                Module = module,
                Origin = origin,
                EndLine = end,
            };
        }

        public static FunctionModifiers MapModifiers(DeclModifiers m)
        {
            var r = FunctionModifiers.None;
            if ((m & DeclModifiers.Virtual) != 0) r |= FunctionModifiers.Virtual;
            if ((m & DeclModifiers.Static) != 0) r |= FunctionModifiers.Static;
            if ((m & DeclModifiers.Const) != 0) r |= FunctionModifiers.Const;
            if ((m & DeclModifiers.Override) != 0) r |= FunctionModifiers.Override;
            if ((m & DeclModifiers.Final) != 0) r |= FunctionModifiers.Final;
            if ((m & DeclModifiers.Pure) != 0) r |= FunctionModifiers.Pure;
            if ((m & DeclModifiers.Inline) != 0) r |= FunctionModifiers.Inline;
            if ((m & DeclModifiers.Explicit) != 0) r |= FunctionModifiers.Explicit;
            if ((m & DeclModifiers.Constexpr) != 0) r |= FunctionModifiers.Constexpr;
            if ((m & DeclModifiers.Noexcept) != 0) r |= FunctionModifiers.Noexcept;
            if ((m & DeclModifiers.Template) != 0) r |= FunctionModifiers.Template;
            return r;
        }

        /// <summary>Line of the ';' (or '{') ending a bodiless declaration (parentheses / brackets balanced).</summary>
        private static int EndLineOfDeclaration(DocumentModel doc, Declaration d)
        {
            int depth = 0;
            int start = doc.TokenIndexFrom(d.Offset);
            for (int k = start; k < doc.Tokens.Length && k < start + 4000; k++)
            {
                var t = doc.Tokens[k];
                if (t.Is(Punct.LParen) || t.Is(Punct.LBracket)) depth++;
                else if (t.Is(Punct.RParen) || t.Is(Punct.RBracket)) depth--;
                else if (depth <= 0 && (t.Is(Punct.Semicolon) || t.Is(Punct.LBrace) || t.Is(Punct.RBrace))) return doc.PositionOf(t.Start).Line;
            }
            return d.Line;
        }

        // ================================================================== class

        private ClassAtCaret ClassAt(NavContext ctx, DocumentModel doc, int offset, FunctionSignature function)
        {
            string name = doc.EnclosingClass(offset, out int cls);
            if (cls >= 0) return ClassInfo(ctx, doc, cls);
            if (name == null && function != null && !string.IsNullOrEmpty(function.Container) && !doc.IsKnownNamespace(function.Container)) name = function.Container;
            if (string.IsNullOrEmpty(name)) return null;
            var t = ctx.FindType(name);
            if (t?.Symbol?.Location?.FilePath == null) return null;
            var tdoc = ctx.IsOverlayPath(t.Symbol.Location.FilePath) ? doc : _documents.GetFile(t.Symbol.Location.FilePath);
            int ci = tdoc == null ? -1 : FindRecord(tdoc, t.Symbol);
            if (ci >= 0) return ClassInfo(ctx, tdoc, ci);
            return new ClassAtCaret { Symbol = t.Symbol, Bases = t.Bases.ToList() };
        }

        private static ClassAtCaret ClassInfo(NavContext ctx, DocumentModel doc, int ci)
        {
            var d = doc.Decls[ci];
            var info = new ClassAtCaret { Symbol = ctx.ToSymbol(doc, ci), Bases = (d.Bases ?? Array.Empty<string>()).ToList() };
            if (!d.Body.IsNone)
            {
                info.BodyStartLine = doc.PositionOf(d.Body.Start).Line;
                info.BodyEndLine = doc.PositionOf(Math.Max(d.Body.Start, d.Body.End - 1)).Line;
            }
            return info;
        }

        /// <summary>Index of the record declaration (with a body) for a type symbol in its document; -1 when absent.</summary>
        internal static int FindRecord(DocumentModel doc, SymbolInfo type)
        {
            string q = TypeText.Qualified(type.Container, type.Name);
            int found = -1;
            for (int i = 0; i < doc.Decls.Count; i++)
            {
                var d = doc.Decls[i];
                if (!TypeText.IsRecordKind(d.Kind) || d.Name != type.Name || d.Body.IsNone || d.IsImplicit) continue;
                if (d.QualifiedName != q && !TypeText.ContainerMatches(d.QualifiedName, q)) continue;
                found = i;
                if (type.Location != null && d.Line == type.Location.Line) break;
            }
            return found;
        }

        // ================================================================== type + include state

        private TypeAtCaret TypeInfo(NavContext ctx, DocumentModel doc, SymbolInfo symbol, Resolution r, bool checkIncludes, CancellationToken ct)
        {
            // the declaring header: a definition in a header first, then any header, then whatever we have
            var all = r.Candidates.Where(c => IsTypeLike(c.Kind) && c.Location?.FilePath != null).ToList();
            if (all.Count == 0) all.Add(symbol);
            var best = all.OrderBy(c => IsHeader(c.Location?.FilePath) ? 0 : 1).ThenBy(c => c.IsDefinition ? 0 : 1)
                          .ThenBy(c => TypeText.IsProject(c.Origin) ? 0 : 1).First();
            var info = new TypeAtCaret { Symbol = best, HeaderPath = best.Location?.FilePath };
            if (string.IsNullOrEmpty(info.HeaderPath)) return info;
            if (all.Any(c => ctx.IsOverlayPath(c.Location?.FilePath)))
            {
                info.Include = IncludeState.SameFile;
                return info;
            }
            if (!checkIncludes) return info;
            var includes = doc.Summary.Includes.Where(i => !i.IsMacro).Select(i => i.Path).ToList();
            string key = NavContext.NormalizePath(doc.Path) + "|" + string.Join(";", includes) + "|" + NavContext.NormalizePath(info.HeaderPath);
            if (_includeCache.TryGet(key, out var cached))
            {
                info.Include = cached.State;
                info.IncludedVia = cached.Via;
                info.FilesVisited = cached.Visited;
                return info;
            }
            var (state, via, visited) = IncludeWalk(includes, info.HeaderPath, ct);
            _includeCache.Add(key, (state, via, visited));
            info.Include = state;
            info.IncludedVia = via;
            info.FilesVisited = visited;
            return info;
        }

        private static bool IsHeader(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".inl" || ext == ".hxx";
        }

        /// <summary>
        /// Whether <paramref name="target"/> is reachable from the include texts of a file: direct match, then breadth-first
        /// through the include lists the index stored for each resolved header (no file reads), at most
        /// <see cref="MaxIncludeDepth"/> levels / <see cref="MaxIncludeFiles"/> files. Hitting a bound without a match → Unknown.
        /// </summary>
        internal (IncludeState State, string Via, int Visited) IncludeWalk(IReadOnlyList<string> includes, string target, CancellationToken ct)
        {
            string t = NavContext.NormalizePath(target);
            foreach (var inc in includes)
                if (IncludeMatches(inc, t)) return (IncludeState.Direct, inc, 0);
            var includesOf = IncludesOf ?? DefaultIncludesOf();
            if (includesOf == null || _files == null) return (IncludeState.Unknown, null, 0);

            var snapshot = _files.Snapshot();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            // (include text, the direct include it came through)
            var level = includes.Select(i => (Text: i, Via: i)).ToList();
            bool capped = false;
            for (int depth = 0; depth < MaxIncludeDepth && level.Count > 0; depth++)
            {
                ct.ThrowIfCancellationRequested();
                var resolved = ResolveAll(snapshot, level.Select(x => x.Text));
                var next = new List<(string Text, string Via)>();
                foreach (var (text, via) in level)
                {
                    if (!resolved.TryGetValue(text, out var path)) continue;
                    if (!visited.Add(NavContext.NormalizePath(path))) continue;
                    if (visited.Count > MaxIncludeFiles) { capped = true; break; }
                    var sub = includesOf(path);
                    if (sub == null) continue;
                    foreach (var s in sub)
                    {
                        if (string.IsNullOrEmpty(s)) continue;
                        if (IncludeMatches(s, t)) return (IncludeState.Transitive, via, visited.Count);
                        next.Add((s, via));
                    }
                }
                if (capped) break;
                level = next;
                if (depth == MaxIncludeDepth - 1 && level.Count > 0) capped = true;
            }
            return (capped ? IncludeState.Unknown : IncludeState.Missing, null, visited.Count);
        }

        private Func<string, IReadOnlyList<string>> DefaultIncludesOf()
        {
            if (_backend is SegmentedIndexBackend seg) return p => seg.Store?.GetFile(p)?.Includes;
            return null;
        }

        /// <summary>One pass over the file table for every include text of a level: the best file whose path ends with it (project first, shortest).</summary>
        private static Dictionary<string, string> ResolveAll(FileEntry[] snapshot, IEnumerable<string> texts)
        {
            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in texts)
            {
                if (string.IsNullOrEmpty(text)) continue;
                string name = Path.GetFileName(text.Replace('\\', '/'));
                if (!byName.TryGetValue(name, out var list)) byName[name] = list = new List<string>();
                if (!list.Contains(text)) list.Add(text);
            }
            var best = new Dictionary<string, (string Path, int Score)>(StringComparer.Ordinal);
            foreach (var e in snapshot)
            {
                var f = e.File;
                string name = f.Name ?? Path.GetFileName(f.Path);
                if (name == null || !byName.TryGetValue(name, out var wanted)) continue;
                string norm = NavContext.NormalizePath(f.Path);
                foreach (var text in wanted)
                {
                    if (!IncludeMatches(text, norm)) continue;
                    int score = (TypeText.IsProject(f.Origin) ? 1000 : 0) - norm.Length;
                    if (!best.TryGetValue(text, out var b) || score > b.Score) best[text] = (f.Path, score);
                }
            }
            return best.ToDictionary(kv => kv.Key, kv => kv.Value.Path, StringComparer.Ordinal);
        }

        /// <summary>Include text (as written) names the normalized path: equal, or a "/"-bounded suffix of it.</summary>
        public static bool IncludeMatches(string includeText, string normalizedPath)
        {
            if (string.IsNullOrEmpty(includeText) || string.IsNullOrEmpty(normalizedPath)) return false;
            string inc = includeText.Replace('\\', '/').ToLowerInvariant();
            while (inc.StartsWith("./", StringComparison.Ordinal)) inc = inc.Substring(2);
            if (inc.StartsWith("../", StringComparison.Ordinal)) inc = Path.GetFileName(inc);
            return normalizedPath == inc || normalizedPath.EndsWith("/" + inc, StringComparison.Ordinal);
        }

        // ================================================================== overridable methods

        public OverridableMethodsResponse GetOverridableMethods(OverridableMethodsRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var resp = new OverridableMethodsResponse();
            DocumentModel caretDoc = null;
            if (request?.Caret != null && !string.IsNullOrEmpty(request.Caret.FilePath))
                caretDoc = _documents.Get(request.Caret.FilePath, request.Caret.Text, request.Caret.TextVersion);
            var ctx = new NavContext(_backend, _files, caretDoc, ct);

            // the class and its own members
            DocumentModel classDoc = null;
            int classIndex = -1;
            TypeEntry root = null;
            if (!string.IsNullOrEmpty(request?.TypeName))
                root = (string.IsNullOrEmpty(request.Container) ? null : ctx.FindType(request.Container + "::" + request.TypeName)) ?? ctx.FindType(request.TypeName);
            else if (caretDoc != null)
            {
                int offset = caretDoc.OffsetOf(Math.Max(0, request.Caret.Line), Math.Max(0, request.Caret.Column));
                string name = caretDoc.EnclosingClass(offset, out int ci);
                if (ci >= 0)
                {
                    classDoc = caretDoc;
                    classIndex = ci;
                    root = new TypeEntry { Symbol = ctx.ToSymbol(caretDoc, ci), Bases = caretDoc.Decls[ci].Bases ?? Array.Empty<string>() };
                }
                else if (name != null) root = ctx.FindType(name);
            }
            if (root == null)
            {
                LogOverridable(resp, sw, request?.TypeName, "no-class");
                return resp;
            }
            resp.Type = root.Symbol;
            if (classDoc == null && root.Symbol.Location?.FilePath != null)
            {
                classDoc = ctx.IsOverlayPath(root.Symbol.Location.FilePath) ? caretDoc : _documents.GetFile(root.Symbol.Location.FilePath);
                classIndex = classDoc == null ? -1 : FindRecord(classDoc, root.Symbol);
            }

            var own = new HashSet<string>(StringComparer.Ordinal);
            if (classIndex >= 0)
                foreach (var m in Members(classDoc, classIndex))
                    own.Add(Key(m.Name, m.Signature));
            var seen = new HashSet<string>(own, StringComparer.Ordinal);
            int max = request.MaxResults > 0 ? request.MaxResults : 500;

            var visitedTypes = new HashSet<string>(StringComparer.Ordinal) { TypeText.Qualified(root.Symbol.Container, root.Symbol.Name) };
            var level = root.Bases.ToList();
            for (int depth = 0; depth < 32 && level.Count > 0 && !resp.Truncated; depth++)
            {
                var next = new List<string>();
                foreach (var baseText in level)
                {
                    ct.ThrowIfCancellationRequested();
                    var be = ctx.FindType(baseText);
                    if (be?.Symbol?.Location?.FilePath == null) continue;
                    string q = TypeText.Qualified(be.Symbol.Container, be.Symbol.Name);
                    if (!visitedTypes.Add(q) || visitedTypes.Count > 64) continue;
                    resp.TypesVisited++;
                    var bdoc = ctx.IsOverlayPath(be.Symbol.Location.FilePath) ? caretDoc : _documents.GetFile(be.Symbol.Location.FilePath);
                    int bi = bdoc == null ? -1 : FindRecord(bdoc, be.Symbol);
                    if (bi < 0) { next.AddRange(be.Bases); continue; }
                    foreach (var mi in MemberIndices(bdoc, bi))
                    {
                        var d = bdoc.Decls[mi];
                        bool isVirtual = (d.Modifiers & (DeclModifiers.Virtual | DeclModifiers.Override)) != 0;
                        bool nativeEvent = (d.Ue & UeFlags.UFunction) != 0 && TypeText.HasSpecifier(d.Specifiers, "BlueprintNativeEvent");
                        string key = Key(d.Name, d.Signature);
                        if (d.Kind == SymbolKind.Method && isVirtual && (d.Modifiers & DeclModifiers.Final) == 0)
                        {
                            if (own.Contains(key)) { resp.AlreadyOverridden++; seen.Add(key); continue; }
                            if (!seen.Add(key)) continue;
                            var sig = ToSignature(ctx, bdoc, mi);
                            sig.Container = q;
                            sig.DeclaringType = q;
                            if (!Add(resp, sig, max)) break;
                        }
                        else if (nativeEvent)
                        {
                            // BlueprintNativeEvent: UHT declares virtual Name_Implementation; derived C++ classes override that.
                            string implKey = Key(d.Name + "_Implementation", d.Signature);
                            if (own.Contains(implKey)) { resp.AlreadyOverridden++; seen.Add(implKey); continue; }
                            if (!seen.Add(implKey)) continue;
                            var sig = ToSignature(ctx, bdoc, mi);
                            sig.Name = d.Name + "_Implementation";
                            sig.Container = q;
                            sig.DeclaringType = q;
                            sig.Modifiers |= FunctionModifiers.Virtual;
                            sig.Specifiers = null;
                            sig.Ue = UeFlags.None;
                            if (!Add(resp, sig, max)) break;
                        }
                        else seen.Add(key); // a non-virtual redeclaration hides base virtuals of the same name/arity
                    }
                    next.AddRange(be.Bases);
                }
                level = next;
            }
            LogOverridable(resp, sw, root.Symbol.Name, "ok");
            return resp;
        }

        private static bool Add(OverridableMethodsResponse resp, FunctionSignature sig, int max)
        {
            if (resp.Methods.Count >= max)
            {
                resp.Truncated = true;
                return false;
            }
            resp.Methods.Add(sig);
            return true;
        }

        private static void LogOverridable(OverridableMethodsResponse resp, Stopwatch sw, string type, string reason)
        {
            resp.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            L.Info("refactor.overridable", null, ("type", type), ("reason", reason), ("methods", resp.Methods.Count), ("alreadyOverridden", resp.AlreadyOverridden),
                ("types", resp.TypesVisited), ("truncated", resp.Truncated), ("ms", resp.ElapsedMs));
        }

        private static IEnumerable<int> MemberIndices(DocumentModel doc, int cls)
        {
            for (int i = 0; i < doc.Decls.Count; i++)
            {
                var d = doc.Decls[i];
                if (d.Parent == cls && !d.IsImplicit && !string.IsNullOrEmpty(d.Name) && TypeText.IsCallableKind(d.Kind)) yield return i;
            }
        }

        private static IEnumerable<Declaration> Members(DocumentModel doc, int cls) => MemberIndices(doc, cls).Select(i => doc.Decls[i]);

        /// <summary>Override identity: name + parameter types (whitespace and parameter names ignored).</summary>
        public static string Key(string name, string signature) => name + "/" + (TypeText.ParameterKey(signature) ?? string.Empty);

        // ================================================================== caches

        private sealed class IncludeCache
        {
            private readonly int _max;
            private readonly LinkedList<(string Key, (IncludeState State, string Via, int Visited) Value)> _lru = new LinkedList<(string, (IncludeState, string, int))>();
            private readonly Dictionary<string, LinkedListNode<(string Key, (IncludeState State, string Via, int Visited) Value)>> _map =
                new Dictionary<string, LinkedListNode<(string, (IncludeState, string, int))>>(StringComparer.Ordinal);

            public IncludeCache(int max) => _max = max;

            public bool TryGet(string key, out (IncludeState State, string Via, int Visited) value)
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
                value = default;
                return false;
            }

            public void Add(string key, (IncludeState State, string Via, int Visited) value)
            {
                lock (_lru)
                {
                    if (_map.TryGetValue(key, out var old)) _lru.Remove(old);
                    var node = _lru.AddFirst((key, value));
                    _map[key] = node;
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
