using System;
using System.Diagnostics;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
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
        private readonly MemberTableCache _members = new MemberTableCache();
        private readonly CompletionEngine _completion = new CompletionEngine();
        private readonly SemanticClassifier _classifier = new SemanticClassifier();
        private readonly DiagnosticsProvider _diagnostics = new DiagnosticsProvider();

        public IntelliSenseService(ISymbolIndexBackend backend, FileTable files, Func<bool> indexReady = null, DocumentCache documents = null, UeMacroKnowledge knowledge = null,
                                   AssetTable assets = null)
        {
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

        private IsSession Session(CaretRequest caret, CancellationToken ct, out DocumentModel doc)
        {
            doc = null;
            if (caret == null || string.IsNullOrEmpty(caret.FilePath)) return null;
            doc = Documents.Get(caret.FilePath, caret.Text, caret.TextVersion);
            if (doc == null) return null;
            var ctx = new NavContext(_backend, _files, doc, ct);
            return new IsSession(ctx, doc, Documents, _members, Knowledge);
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
            };
            if (resp.ElapsedMs > CompletionBudgetMs) L.Warn("is.completion", "Completion over budget (" + CompletionBudgetMs + " ms)", data);
            else L.Info("is.completion", null, data);
            return resp;
        }

        // ================================================================== quick info

        public QuickInfoResponse GetQuickInfo(CaretRequest caret, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var s = Session(caret, ct, out var doc);
            if (s == null) return new QuickInfoResponse { ElapsedMs = Ms(sw) };
            var resp = QuickInfoProvider.Get(s, Offset(doc, caret));
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
            var resp = _classifier.Classify(s, request.StartLine, request.EndLine, out int computed, out int cached);
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
            var items = _diagnostics.Run(s, from, to, caret, stats, request.UnsavedFiles);
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

        private static double Ms(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 2);

        private static string FileName(string path) => string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path);
    }
}
