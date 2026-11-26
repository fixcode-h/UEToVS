using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Includes;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>
    /// M3 UE-aware IntelliSense (design §7.6): completion, Quick Info, parameter info and semantic colouring, on top of the M2
    /// navigation layer (<see cref="DocumentCache"/>, <see cref="NavContext"/>, <see cref="SymbolResolver"/>) and the index.
    /// Thread-safe. Shares the navigation service's document cache so a buffer version is parsed once for all features.
    /// </summary>
    public sealed class IntelliSenseService
    {
        private static readonly Logger L = Log.For("IS");

        private readonly ISymbolIndexBackend _backend;
        private readonly FileTable _files;
        private readonly Func<bool> _indexReady;
        private readonly Func<long> _indexStamp;
        private readonly MemberTableCache _members = new MemberTableCache();
        private readonly CompletionEngine _completion = new CompletionEngine();
        private readonly SemanticClassifier _classifier = new SemanticClassifier();
        private readonly DiagnosticsProvider _diagnostics = new DiagnosticsProvider();

        public IntelliSenseService(ISymbolIndexBackend backend, FileTable files, Func<bool> indexReady = null, DocumentCache documents = null, UeMacroKnowledge knowledge = null,
                                   AssetTable assets = null, Func<long> indexStamp = null)
        {
            _indexStamp = indexStamp ?? (() => 0);
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _files = files;
            _indexReady = indexReady ?? (() => true);
            Documents = documents ?? new DocumentCache();
            Knowledge = knowledge ?? UeMacroKnowledge.Default;
            _completion.Strings = new StringCompletionSources(backend, files, assets);
            _diagnostics.Strings = _completion.Strings;
            _diagnostics.IndexReady = _indexReady;
            _diagnostics.GamePackageExists = _completion.GamePackageExists;
        }

        /// <summary>Gameplay tags, ini files and assets behind string context completion.</summary>
        public StringCompletionSources StringSources => _completion.Strings;

        public DocumentCache Documents { get; }
        public UeMacroKnowledge Knowledge { get; }
        public MemberTableCache MemberTables => _members;
        public SemanticClassifier Classifier => _classifier;

        /// <summary>Completion budget (design §7.6: p95 &lt; 50 ms); slower requests are logged at Warn.</summary>
        public double CompletionBudgetMs { get; set; } = 50;

        /// <summary>Classification budget for a viewport (~100 lines): p95 &lt; 15 ms.</summary>
        public double ClassifyBudgetMs { get; set; } = 15;

        public double QuickInfoBudgetMs { get; set; } = 50;

        // ---- v0.4.5 heavy features: generic template instantiation (switch + LRU capacity from the settings window) ----
        private readonly TemplateInstantiator _templates = new TemplateInstantiator();
        private volatile bool _templatesEnabled = true;

        /// <summary>The (template, arguments) binding cache: hit rate in the log as <c>intellisense.templates</c>.</summary>
        public TemplateInstanceCache TemplateCache => _templates.Cache;

        public bool TemplateInstantiationEnabled => _templatesEnabled;

        /// <summary>Turns generic template instantiation on / off and sets the cache capacity. Off drops the cache (no memory kept).</summary>
        public void ConfigureTemplates(bool enabled, int cacheCapacity)
        {
            bool changed = enabled != _templatesEnabled || (cacheCapacity > 0 && cacheCapacity != _templates.Cache.Capacity);
            _templatesEnabled = enabled;
            if (cacheCapacity > 0) _templates.Cache.Capacity = cacheCapacity;
            if (!enabled) _templates.Cache.Clear();
            if (changed)
                L.Info("intellisense.templates.config", null, ("enabled", enabled), ("capacity", _templates.Cache.Capacity), ("maxDepth", _templates.MaxDepth));
        }

        /// <summary>A diagnostics provider configured like the squiggle one (gameplay tags, assets, index readiness), for whole-project inspection.</summary>
        internal DiagnosticsProvider CreateDiagnosticsProvider() => new DiagnosticsProvider
        {
            Strings = _completion.Strings,
            IndexReady = _indexReady,
            GamePackageExists = _completion.GamePackageExists,
        };

        internal ISymbolIndexBackend Backend => _backend;
        internal FileTable Files => _files;

        /// <summary>
        /// A session over an already parsed document with caller-owned caches (whole-project inspection: each run has its own
        /// document / member caches, so it neither evicts the editor's documents nor keeps memory after it ends).
        /// </summary>
        internal IsSession CreateSession(DocumentModel doc, DocumentCache documents, MemberTableCache members, CancellationToken ct) =>
            new IsSession(new NavContext(_backend, _files, doc, ct), doc, documents, members, Knowledge) { Templates = _templatesEnabled ? _templates : null };

        private IsSession Session(CaretRequest caret, CancellationToken ct, out DocumentModel doc)
        {
            doc = null;
            if (caret == null || string.IsNullOrEmpty(caret.FilePath)) return null;
            doc = Documents.Get(caret.FilePath, caret.Text, caret.TextVersion);
            if (doc == null) return null;
            var ctx = new NavContext(_backend, _files, doc, ct);
            return new IsSession(ctx, doc, Documents, _members, Knowledge) { Templates = _templatesEnabled ? _templates : null };
        }

        private static int Offset(DocumentModel doc, CaretRequest caret) => doc.OffsetOf(Math.Max(0, caret.Line), Math.Max(0, caret.Column));

        // ================================================================== completion

        public CompletionResponse GetCompletions(CompletionRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var s = Session(request?.Caret, ct, out var doc);
            if (s == null)
            {
                var empty = new CompletionResponse { IndexReady = _indexReady(), ElapsedMs = Ms(sw) };
                L.Info("is.completion", null, ("context", "none"), ("reason", "no-document"), ("file", FileName(request?.Caret?.FilePath)), ("ms", empty.ElapsedMs));
                return empty;
            }
            int caret = Offset(doc, request.Caret);
            var site = CompletionSiteAnalyzer.Analyze(doc, caret);
            double analyzeMs = Ms(sw);
            var resp = _completion.Complete(s, site, request, sw, out string resolvedType);
            double beforeAuto = Ms(sw);
            int autoIncludes = site.Kind == CompletionContextKind.General ? AutoIncludes(s, resp) : 0;
            double autoIncludeMs = Math.Round(Ms(sw) - beforeAuto, 2);
            resp.IndexReady = _indexReady();
            resp.ElapsedMs = Ms(sw);
            var data = new (string, object)[]
            {
                ("context", CompletionEngine.StringContextName(site.Kind) ?? site.Kind.ToString()), ("prefix", site.Prefix),
                ("candidates", site.StringSite != null ? site.Candidates : (int?)null), ("resolvedType", resolvedType), ("items", resp.Items.Count),
                ("incomplete", resp.IsIncomplete), ("fix", resp.AccessorFix?.Replacement), ("suppressed", site.Suppressed ? site.SuppressReason : null),
                ("trigger", request.TriggerChar == '\0' ? null : request.TriggerChar.ToString()), ("explicit", request.Explicit),
                ("top", resp.Items.Count > 0 ? resp.Items[0].Label : null), ("ms", resp.ElapsedMs), ("analyzeMs", analyzeMs),
                ("indexQueries", s.Ctx.IndexQueries), ("file", FileName(doc.Path)), ("line", request.Caret.Line), ("version", request.Caret.TextVersion),
                ("autoIncludes", autoIncludes == 0 ? (int?)null : autoIncludes), ("autoIncludeMs", autoIncludeMs >= 1 ? autoIncludeMs : (double?)null),
            };
            if (resp.ElapsedMs > CompletionBudgetMs) L.Warn("is.completion", "Completion over budget (" + CompletionBudgetMs + " ms)", data);
            else L.Info("is.completion", null, data);
            return resp;
        }

        /// <summary>Items looked at for <see cref="CompletionItem.AddInclude"/> (the best-scored ones: the list shows only the top anyway).</summary>
        private const int MaxAutoIncludeItems = 150;

        /// <summary>Time the auto include pass may add to one completion request (spellings resolve include paths, some on disk).</summary>
        private const double AutoIncludeBudgetMs = 15;

        private const int MaxSpellings = 20000;

        /// <summary>How a module spells the include of a header (module + "\n" + header → spelling), for the current index revision.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _spellings =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private long _spellingRevision = -1;

        /// <summary>Closure walks queued or running (one per key).</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warming =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        /// <summary>
        /// Runs work off the request path (the engine host passes the scheduler's Background lane). Null: the closure is
        /// walked inline (tests, offline tools), where nobody waits on a list.
        /// </summary>
        public Func<string, Action<CancellationToken>, System.Threading.Tasks.Task> RunInBackground { get; set; }

        /// <summary>Queues the closure walk on the background lane (once per key) and returns <paramref name="stale"/> meanwhile.</summary>
        private IncludeClosure WarmClosure(IncludeModel model, string path, List<string> direct, IncludeClosure stale)
        {
            Background("is.includeClosure\n" + path + "\n" + string.Join("\n", direct), ct => model.Graph.Closure(path, direct, ct));
            return stale;
        }

        /// <summary>Queues <paramref name="work"/> on the background lane unless the same key is already queued or running.</summary>
        private void Background(string key, Action<CancellationToken> work)
        {
            if (!_warming.TryAdd(key, 0)) return;
            Action<CancellationToken> run = ct =>
            {
                try { work(ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { L.Warn("is.completion.autoInclude.warmFailed", "Preparing the auto include data failed", ex, ("work", key.Split('\n')[0])); }
            };
            // removed also when the item is dropped unstarted (cancelled / shut down): the next request queues it again
            RunInBackground(key.Split('\n')[0], run).ContinueWith(_ => _warming.TryRemove(key, out byte _), System.Threading.Tasks.TaskScheduler.Default);
        }

        /// <summary>
        /// v0.11.0 (2.4): types / free functions whose header is outside the file's include closure get the include to add on
        /// commit. Silent when the closure is not known (no module, capped, unknown include lists, the index not ready).
        /// </summary>
        private int AutoIncludes(IsSession s, CompletionResponse resp)
        {
            try
            {
                var model = s.Ctx.Files?.Includes;
                if (model == null || model.Graph.IncludesOf == null || resp.Items.Count == 0 || !_indexReady()) return 0;
                if (RunInBackground != null && !model.IsCurrent)
                {
                    // a file was re-indexed since the maps were built: rebuild them off the request path
                    Background("is.includeModel", ct => { _ = model.Catalog; });
                    return 0;
                }
                if (model.Catalog == null) return 0;
                var doc = s.Doc;
                var module = model.ModuleOf(doc.Path);
                if (module == null) return 0;
                var direct = new List<string>();
                foreach (var inc in doc.Summary.Includes)
                    if (!string.IsNullOrWhiteSpace(inc.Path)) direct.Add(inc.Path);
                string ext = System.IO.Path.GetExtension(doc.Path ?? string.Empty).ToLowerInvariant();
                if (ext != ".h" && ext != ".hpp" && !string.IsNullOrEmpty(module.PrivatePchHeader))
                    direct.Add(System.IO.Path.IsPathRooted(module.PrivatePchHeader) ? module.PrivatePchHeader : System.IO.Path.Combine(module.Directory, module.PrivatePchHeader));
                // v0.11.1: never walk the closure here. A cold walk of a UE file (PCH → thousands of engine headers) took
                // seconds and the list waited for it; use the cached one (even a revision old) and build it in the background.
                IncludeClosure closure;
                if (RunInBackground == null) closure = model.Graph.Closure(doc.Path, direct, s.Ct);
                else
                {
                    closure = model.Graph.Peek(doc.Path, direct, out bool fresh);
                    if (closure == null || !fresh) closure = WarmClosure(model, doc.Path, direct, closure);
                }
                if (closure == null || closure.Capped || closure.UnknownLists > 0) return 0;
                long revision = _indexStamp();
                if (Interlocked.Exchange(ref _spellingRevision, revision) != revision) _spellings.Clear();
                var started = Stopwatch.StartNew();
                int n = 0;
                foreach (var item in resp.Items.OrderByDescending(i => i.Score).Take(MaxAutoIncludeItems))
                {
                    if (started.Elapsed.TotalMilliseconds > AutoIncludeBudgetMs) break; // the rest just get no auto include
                    string file = item.Location?.FilePath;
                    if (file == null || item.IsKeyword || item.IsInherited) continue;
                    bool type = TypeText.IsTypeKind(item.Kind) && item.Kind != SymbolKind.Namespace;
                    bool free = item.Kind == SymbolKind.Function && string.IsNullOrEmpty(item.Container);
                    if (!type && !free) continue;
                    string norm = NavContext.NormalizePath(file);
                    if (!norm.EndsWith(".h", StringComparison.Ordinal) || norm.EndsWith(".generated.h", StringComparison.Ordinal)) continue;
                    if (s.Ctx.IsOverlayPath(file) || closure.Files.Contains(norm)) continue;
                    string spellingKey = module.Name + "\n" + norm;
                    if (!_spellings.TryGetValue(spellingKey, out var spelling))
                    {
                        spelling = model.Spelling(file, doc.Path);
                        if (_spellings.Count >= MaxSpellings) _spellings.Clear();
                        _spellings[spellingKey] = spelling;
                    }
                    if (string.IsNullOrEmpty(spelling)) continue;
                    item.AddInclude = spelling;
                    n++;
                }
                return n;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                L.Warn("is.completion.autoInclude.failed", "Computing the includes to add failed", ex);
                return 0;
            }
        }

        // ================================================================== quick info

        public QuickInfoResponse GetQuickInfo(CaretRequest caret, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var s = Session(caret, ct, out var doc);
            if (s == null) return new QuickInfoResponse { ElapsedMs = Ms(sw) };
            var resp = QuickInfoProvider.Get(s, Offset(doc, caret));
            AddDeprecation(s, resp);
            resp.ElapsedMs = Ms(sw);
            var data = new (string, object)[]
            {
                ("symbol", resp.Symbol?.Name), ("container", resp.Symbol?.Container), ("kind", resp.Symbol?.Kind.ToString()),
                ("confidence", resp.Confidence.ToString()), ("docs", resp.Documentation != null), ("reflection", resp.Reflection != null),
                ("macro", resp.MacroExpansion != null), ("overloads", resp.OverloadCount), ("ms", resp.ElapsedMs), ("file", FileName(doc.Path)), ("line", caret.Line),
            };
            if (resp.ElapsedMs > QuickInfoBudgetMs) L.Warn("is.quickinfo", "Quick Info over budget", data);
            else L.Debug("is.quickinfo", null, data);
            return resp;
        }

        /// <summary>v0.4.5: Quick Info names the deprecation of the hovered symbol (<see cref="DeprecationInfo"/>).</summary>
        private static void AddDeprecation(IsSession s, QuickInfoResponse resp)
        {
            if (resp?.Symbol == null) return;
            try
            {
                var info = DeprecationInfo.Of(s, resp.Symbol);
                if (info == null) return;
                resp.IsDeprecated = true;
                resp.DeprecatedVersion = info.Version;
                resp.DeprecationMessage = info.Message;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                L.Warn("is.quickinfo.deprecation.failed", "Deprecation lookup failed", ex, ("symbol", resp.Symbol.Name));
            }
        }

        // ================================================================== signature help

        public SignatureHelpResponse GetSignatureHelp(CaretRequest caret, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var s = Session(caret, ct, out var doc);
            if (s == null) return new SignatureHelpResponse { ElapsedMs = Ms(sw) };
            var resp = SignatureHelpProvider.Get(s, Offset(doc, caret));
            resp.ElapsedMs = Ms(sw);
            var data = new (string, object)[]
            {
                ("signatures", resp.Signatures.Count), ("active", resp.ActiveSignature), ("parameter", resp.ActiveParameter),
                ("callee", resp.Signatures.Count > 0 ? resp.Signatures[resp.ActiveSignature].Symbol?.Name : null), ("ms", resp.ElapsedMs),
                ("file", FileName(doc.Path)), ("line", caret.Line),
            };
            if (resp.ElapsedMs > CompletionBudgetMs) L.Warn("is.signature", "Parameter info over budget", data);
            else L.Debug("is.signature", null, data);
            return resp;
        }

        // ================================================================== classification

        public ClassifyResponse Classify(ClassifyRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var s = Session(request?.Document, ct, out var doc);
            if (s == null) return new ClassifyResponse { TextVersion = request?.Document?.TextVersion ?? 0, ElapsedMs = Ms(sw) };
            var resp = _classifier.Classify(s, request.StartLine, request.EndLine, _indexStamp(), out int computed, out int cached);
            resp.TextVersion = request.Document.TextVersion != 0 ? request.Document.TextVersion : resp.TextVersion;
            resp.ElapsedMs = Ms(sw);
            var data = new (string, object)[]
            {
                ("lines", request.EndLine - request.StartLine + 1), ("computed", computed), ("cached", cached), ("spans", resp.Spans.Count),
                ("indexQueries", s.Ctx.IndexQueries), ("ms", resp.ElapsedMs), ("file", FileName(doc.Path)), ("version", request.Document.TextVersion),
            };
            // budget scales with the requested range (~100 visible lines → 15 ms)
            double budget = ClassifyBudgetMs * Math.Max(1.0, (request.EndLine - request.StartLine + 1) / 100.0);
            if (resp.ElapsedMs > budget) L.Warn("is.classify", "Classification over budget", data);
            else L.Debug("is.classify", null, data);
            return resp;
        }

        // ================================================================== diagnostics (squiggles)

        /// <summary>Diagnostics budget for a viewport ± 50 lines (runs after an edit pause, not per keystroke).</summary>
        public double DiagnosticsBudgetMs { get; set; } = 50;

        public CodeDiagnosticsResponse GetDiagnostics(CodeDiagnosticsRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var s = Session(request?.Document, ct, out var doc);
            if (s == null) return new CodeDiagnosticsResponse { TextVersion = request?.Document?.TextVersion ?? 0, ElapsedMs = Ms(sw) };
            int lineCount = doc.Lines.LineCount;
            int from = Math.Max(0, request.StartLine);
            int to = Math.Min(lineCount - 1, Math.Max(from, request.EndLine));
            int caret = request.Document.Line > 0 || request.Document.Column > 0 ? Offset(doc, request.Document) : -1;
            var stats = new DiagnosticsStats();
            var options = new DiagnosticsOptions
            {
                CheckDeprecated = request.CheckDeprecated,
                CheckNaming = request.CheckNaming,
                Disabled = request.DisabledRules != null && request.DisabledRules.Count > 0 ? new HashSet<string>(request.DisabledRules, StringComparer.Ordinal) : null,
            };
            var items = _diagnostics.Run(s, from, to, caret, stats, request.UnsavedFiles, options);
            var resp = new CodeDiagnosticsResponse
            {
                TextVersion = request.Document.TextVersion != 0 ? request.Document.TextVersion : doc.Version,
                StartLine = from,
                EndLine = to,
                Items = items,
                Checked = stats.Checked,
                Uncertain = stats.Uncertain,
                ElapsedMs = Ms(sw),
            };
            var data = new (string, object)[]
            {
                ("file", FileName(doc.Path)), ("lines", from + ".." + to), ("version", request.Document.TextVersion), ("items", items.Count),
                ("UE0001", stats.UnknownMember), ("UE0003", stats.MissingGeneratedBody), ("ueRules", stats.UeRuleSummary()), ("checked", stats.Checked), ("uncertain", stats.Uncertain),
                ("uncertainReasons", stats.TopReasons()), ("indexQueries", s.Ctx.IndexQueries), ("ms", resp.ElapsedMs),
            };
            double budget = DiagnosticsBudgetMs * Math.Max(1.0, (to - from + 1) / 200.0);
            if (resp.ElapsedMs > budget) L.Warn("intellisense.diagnostics", "Diagnostics over budget", data);
            else if (items.Count > 0) L.Info("intellisense.diagnostics", null, data);
            else L.Debug("intellisense.diagnostics", null, data);
            return resp;
        }

        // ================================================================== v0.4.5 inlay hints

        /// <summary>Inlay hints budget for a viewport (~100 lines): they run after a scroll / edit pause, never per keystroke.</summary>
        public double InlayHintsBudgetMs { get; set; } = 40;

        public InlayHintsResponse GetInlayHints(InlayHintsRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var s = Session(request?.Document, ct, out var doc);
            if (s == null) return new InlayHintsResponse { TextVersion = request?.Document?.TextVersion ?? 0, ElapsedMs = Ms(sw) };
            int lineCount = doc.Lines.LineCount;
            int from = Math.Max(0, request.StartLine);
            int to = Math.Min(lineCount - 1, Math.Max(from, request.EndLine));
            var stats = new InlayHintsStats();
            var items = InlayHintsProvider.Run(s, from, to, request.ParameterNames, request.AutoTypes, stats);
            var resp = new InlayHintsResponse
            {
                TextVersion = request.Document.TextVersion != 0 ? request.Document.TextVersion : doc.Version,
                StartLine = from,
                EndLine = to,
                Items = items,
                Calls = stats.Calls,
                Resolved = stats.Resolved,
                AutoVariables = stats.AutoVariables,
                ElapsedMs = Ms(sw),
            };
            var data = new (string, object)[]
            {
                ("file", FileName(doc.Path)), ("lines", from + ".." + to), ("version", request.Document.TextVersion), ("items", items.Count),
                ("parameterNames", request.ParameterNames), ("autoTypes", request.AutoTypes), ("calls", stats.Calls), ("resolved", stats.Resolved),
                ("ambiguous", stats.Ambiguous), ("autoVariables", stats.AutoVariables), ("autoDeduced", stats.AutoDeduced), ("indexQueries", s.Ctx.IndexQueries),
                ("ms", resp.ElapsedMs),
            };
            double budget = InlayHintsBudgetMs * Math.Max(1.0, (to - from + 1) / 100.0);
            if (resp.ElapsedMs > budget) L.Warn("editor.inlayHints", "Inlay hints over budget", data);
            else L.Debug("editor.inlayHints", null, data);
            return resp;
        }

        private static double Ms(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 2);

        private static string FileName(string path) => string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path);
    }
}
