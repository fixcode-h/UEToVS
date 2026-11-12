using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Contracts
{
    /// <summary>
    /// RPC surface of the engine process (host → engine). Every call is cancellable; the engine must never block
    /// on the host. Methods are added, never changed; see <see cref="ProtocolInfo.Version"/>.
    /// </summary>
    public interface IEngineService
    {
        Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken ct);

        Task<EngineStatus> GetStatusAsync(CancellationToken ct);

        Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct);

        /// <summary>Declarations in one file, in source order (file structure view).</summary>
        Task<List<SymbolInfo>> GetFileSymbolsAsync(string filePath, CancellationToken ct);

        /// <summary>Exact-name lookup, optionally restricted to a container (class / namespace).</summary>
        Task<List<SymbolInfo>> FindSymbolsAsync(string name, string container, CancellationToken ct);

        Task NotifyFilesChangedAsync(List<FileChange> changes, CancellationToken ct);

        /// <summary>Change the engine's log level at runtime (Error/Warn/Info/Debug/Trace).</summary>
        Task SetLogLevelAsync(string level, CancellationToken ct);

        /// <summary>
        /// Data for the performance panel: current resources, a short resource history, index phase timings and
        /// per-operation latency since the engine started. Cheap (served from counters), safe to poll every second.
        /// </summary>
        Task<PerformanceSnapshot> GetPerformanceSnapshotAsync(CancellationToken ct);

        /// <summary>Host reports VS/UBT build start and end; background indexing pauses meanwhile.</summary>
        Task SetBuildInProgressAsync(bool building, CancellationToken ct);

        /// <summary>User pause/resume of background indexing (performance panel button).</summary>
        Task SetBackgroundPausedAsync(bool paused, CancellationToken ct);

        // ---- M2 core navigation (see Navigation.cs) ----

        /// <summary>Go to definition / declaration / implementation for the identifier at the caret.</summary>
        Task<NavigationResponse> NavigateAsync(NavigationRequest request, CancellationToken ct);

        /// <summary>Find references of the caret's symbol (or of request.Symbol) across the index.</summary>
        Task<ReferencesResponse> FindReferencesAsync(ReferencesRequest request, CancellationToken ct);

        Task<TypeHierarchyResponse> GetTypeHierarchyAsync(TypeHierarchyRequest request, CancellationToken ct);

        /// <summary>Occurrences of the caret's symbol in the same document (reference highlighting). Budget: p95 &lt; 20 ms.</summary>
        Task<HighlightResponse> GetHighlightsAsync(CaretRequest caret, CancellationToken ct);

        /// <summary>Enclosing scopes at the caret plus the document's declarations (navigation bar, Alt+M).</summary>
        Task<CaretContextResponse> GetCaretContextAsync(CaretRequest caret, CancellationToken ct);

        /// <summary>Members of a type, optionally including inherited ones (member list).</summary>
        Task<MembersResponse> GetMembersAsync(MembersRequest request, CancellationToken ct);

        // ---- indexing settings (settings window) ----

        /// <summary>Apply indexing settings at runtime (thread counts, build behaviour). Takes effect without restarting the engine.</summary>
        Task SetIndexingSettingsAsync(IndexingSettings settings, CancellationToken ct);

        /// <summary>Drop and rebuild the C++ index, the asset (Blueprint) index, or both. Returns immediately; progress via status.</summary>
        Task RebuildIndexAsync(RebuildScope scope, CancellationToken ct);

        // ---- M3 UE-aware IntelliSense (see IntelliSense.cs) ----

        /// <summary>Completion list at the caret. Budget: p95 &lt; 50 ms.</summary>
        Task<CompletionResponse> GetCompletionsAsync(CompletionRequest request, CancellationToken ct);

        /// <summary>Hover tooltip for the identifier at the caret (declaration, docs, UE reflection, macro expansion).</summary>
        Task<QuickInfoResponse> GetQuickInfoAsync(CaretRequest caret, CancellationToken ct);

        /// <summary>Parameter info for the call enclosing the caret.</summary>
        Task<SignatureHelpResponse> GetSignatureHelpAsync(CaretRequest caret, CancellationToken ct);

        /// <summary>Semantic colouring for a line range of a document (VA-style colours).</summary>
        Task<ClassifyResponse> ClassifyAsync(ClassifyRequest request, CancellationToken ct);

        /// <summary>High-confidence diagnostics (squiggles) for a line range of a document (see CodeDiagnostics.cs).</summary>
        Task<CodeDiagnosticsResponse> GetDiagnosticsAsync(CodeDiagnosticsRequest request, CancellationToken ct);

        Task ShutdownAsync(CancellationToken ct);

        // ---- M4 refactoring / code generation (see Refactoring.cs); the engine answers, the host edits ----

        /// <summary>What is at the caret for Alt+Enter: function signature + declaration/definition status, class, type and include state.</summary>
        Task<RefactoringContextResponse> GetRefactoringContextAsync(RefactoringContextRequest request, CancellationToken ct);

        /// <summary>Virtual functions of a class's bases (engine classes included) that it does not override yet.</summary>
        Task<OverridableMethodsResponse> GetOverridableMethodsAsync(OverridableMethodsRequest request, CancellationToken ct);
    }

    /// <summary>Notifications engine → host.</summary>
    public interface IEngineCallbacks
    {
        Task OnStatusChangedAsync(EngineStatus status);
    }
}
