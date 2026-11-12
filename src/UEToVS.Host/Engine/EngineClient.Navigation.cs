using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Engine
{
    /// <summary>
    /// M2 navigation RPCs (go to definition, references, type hierarchy, highlights, navigation bar, members).
    /// Every call has its own timeout and latency budget (§9.1), honours the caller's cancellation, never throws and
    /// returns an <see cref="EngineCallResult{T}"/> whose status lets the feature degrade with a precise message.
    /// Each call is logged: rpc.&lt;op&gt; timing (budget → .slow warning) plus a nav.&lt;op&gt; summary with result sizes,
    /// engine-side time, confidence and reason, so wrong or slow jumps can be diagnosed from the log alone.
    /// </summary>
    internal sealed partial class EngineClient
    {
        private static readonly Logger NavLog = Log.For("Navigation");

        // Timeouts: the user waits on navigate / references / hierarchy / members; highlights and the navigation bar are
        // background refreshes that must give up quickly (a newer request replaces them anyway).
        private static readonly TimeSpan NavigateTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ReferencesTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan HierarchyTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan HighlightTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CaretContextTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MembersTimeout = TimeSpan.FromSeconds(10);

        public async Task<EngineCallResult<NavigationResponse>> NavigateAsync(NavigationRequest request, CancellationToken ct)
        {
            var r = await CallAsync("navigate", (s, c) => s.NavigateAsync(request, c), ct, NavigateTimeout, 200).ConfigureAwait(false);
            var v = r.Value;
            NavLog.Info("nav.navigate", null, ("kind", request?.Kind.ToString()), ("caret", CaretRequestBuilder.Describe(request?.Caret)),
                        ("status", r.Status.ToString()), ("word", v?.Word), ("targets", v?.Targets?.Count ?? 0), ("confidence", v?.Confidence.ToString()),
                        ("reason", v?.Reason), ("resolved", v?.Resolved == null ? null : SymbolPresentation.QualifiedName(v.Resolved)),
                        ("indexReady", v?.IndexReady), ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<ReferencesResponse>> FindReferencesAsync(ReferencesRequest request, CancellationToken ct)
        {
            var r = await CallAsync("findReferences", (s, c) => s.FindReferencesAsync(request, c), ct, ReferencesTimeout, 1000).ConfigureAwait(false);
            var v = r.Value;
            NavLog.Info("nav.references", null, ("caret", CaretRequestBuilder.Describe(request?.Caret)),
                        ("symbol", request?.Symbol == null ? null : SymbolPresentation.QualifiedName(request.Symbol)),
                        ("includeNonProject", request?.IncludeNonProject), ("status", r.Status.ToString()),
                        ("resolved", v?.Symbol == null ? null : SymbolPresentation.QualifiedName(v.Symbol)), ("hits", v?.Hits?.Count ?? 0),
                        ("filesScanned", v?.FilesScanned), ("truncated", v?.Truncated), ("indexReady", v?.IndexReady),
                        ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<TypeHierarchyResponse>> GetTypeHierarchyAsync(TypeHierarchyRequest request, CancellationToken ct)
        {
            var r = await CallAsync("typeHierarchy", (s, c) => s.GetTypeHierarchyAsync(request, c), ct, HierarchyTimeout, 300).ConfigureAwait(false);
            var v = r.Value;
            NavLog.Info("nav.hierarchy", null, ("caret", CaretRequestBuilder.Describe(request?.Caret)), ("typeName", request?.TypeName),
                        ("direction", request?.Direction.ToString()), ("status", r.Status.ToString()),
                        ("root", v?.Root == null ? null : SymbolPresentation.QualifiedName(v.Root)), ("bases", v?.Bases?.Count ?? 0),
                        ("derived", v?.Derived?.Count ?? 0), ("nodes", v?.NodeCount), ("indexReady", v?.IndexReady),
                        ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<HighlightResponse>> GetHighlightsAsync(CaretRequest caret, CancellationToken ct)
        {
            var r = await CallAsync("highlights", (s, c) => s.GetHighlightsAsync(caret, c), ct, HighlightTimeout, 100).ConfigureAwait(false);
            if (r.Status != EngineCallStatus.Canceled)
                NavLog.Debug("nav.highlights", null, ("file", caret?.FilePath), ("line", caret?.Line), ("col", caret?.Column), ("status", r.Status.ToString()),
                             ("word", r.Value?.Word), ("spans", r.Value?.Spans?.Count ?? 0), ("engineMs", r.Value?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<CaretContextResponse>> GetCaretContextAsync(CaretRequest caret, CancellationToken ct)
        {
            var r = await CallAsync("caretContext", (s, c) => s.GetCaretContextAsync(caret, c), ct, CaretContextTimeout, 100).ConfigureAwait(false);
            if (r.Status != EngineCallStatus.Canceled)
                NavLog.Debug("nav.caretContext", null, ("file", caret?.FilePath), ("line", caret?.Line), ("status", r.Status.ToString()),
                             ("scopes", r.Value?.Scopes?.Count ?? 0), ("fileSymbols", r.Value?.FileSymbols?.Count ?? 0),
                             ("engineMs", r.Value?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<MembersResponse>> GetMembersAsync(MembersRequest request, CancellationToken ct)
        {
            var r = await CallAsync("members", (s, c) => s.GetMembersAsync(request, c), ct, MembersTimeout, 150).ConfigureAwait(false);
            var v = r.Value;
            NavLog.Info("nav.members", null, ("type", request?.TypeName), ("container", request?.Container), ("inherited", request?.IncludeInherited),
                        ("status", r.Status.ToString()), ("resolved", v?.Type == null ? null : SymbolPresentation.QualifiedName(v.Type)),
                        ("members", v?.Members?.Count ?? 0), ("truncated", v?.Truncated), ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        /// <summary>
        /// <see cref="InvokeAsync{T}"/> mapped to a status: unavailable / timeout / canceled / failed are logged by
        /// InvokeAsync (or here) and returned, never thrown.
        /// </summary>
        private async Task<EngineCallResult<T>> CallAsync<T>(string op, Func<IEngineService, CancellationToken, Task<T>> call, CancellationToken ct,
                                                             TimeSpan timeout, double budgetMs) where T : class
        {
            var sw = Stopwatch.StartNew();
            double Ms() => sw.Elapsed.TotalMilliseconds;
            try
            {
                var value = await InvokeAsync(op, call, ct, timeout, budgetMs).ConfigureAwait(false);
                return value == null ? EngineCallResult<T>.Fail(EngineCallStatus.Failed, Ms()) : EngineCallResult<T>.Success(value, Ms());
            }
            catch (EngineUnavailableException ex)
            {
                NavLog.Debug("rpc.unavailable", ex.Message, ("op", op), ("state", _state.ToString()));
                return EngineCallResult<T>.Fail(EngineCallStatus.Unavailable, Ms());
            }
            catch (TimeoutException)
            {
                return EngineCallResult<T>.Fail(EngineCallStatus.Timeout, Ms()); // rpc.timeout logged by InvokeAsync
            }
            catch (OperationCanceledException)
            {
                return EngineCallResult<T>.Fail(EngineCallStatus.Canceled, Ms());
            }
            catch (RemoteInvocationException)
            {
                return EngineCallResult<T>.Fail(EngineCallStatus.Failed, Ms()); // rpc.remote.failed logged by InvokeAsync
            }
            catch (Exception ex)
            {
                NavLog.Error("rpc.failed", "Navigation request failed", ex, ("op", op));
                return EngineCallResult<T>.Fail(EngineCallStatus.Failed, Ms());
            }
        }
    }
}
