using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Engine
{
    // ---- v0.4.5 navigation gaps
    /// <summary>Call hierarchy RPC: one level (direct callers or callees) per call; logged as navigation.callHierarchy.</summary>
    internal sealed partial class EngineClient
    {
        private static readonly TimeSpan CallHierarchyTimeout = TimeSpan.FromSeconds(30);

        public async Task<EngineCallResult<CallHierarchyResponse>> GetCallHierarchyAsync(CallHierarchyRequest request, CancellationToken ct)
        {
            var r = await CallAsync("callHierarchy", (s, c) => s.GetCallHierarchyAsync(request, c), ct, CallHierarchyTimeout, 500).ConfigureAwait(false);
            var v = r.Value;
            NavLog.Info("navigation.callHierarchy", null, ("caret", CaretRequestBuilder.Describe(request?.Caret)),
                        ("function", request?.Function == null ? null : SymbolPresentation.QualifiedName(request.Function)),
                        ("direction", request?.Direction.ToString()), ("includeEngine", request?.IncludeNonProject), ("status", r.Status.ToString()),
                        ("root", v?.Root == null ? null : SymbolPresentation.QualifiedName(v.Root)), ("items", v?.Items?.Count ?? 0),
                        ("sites", v?.Items?.Sum(i => i.Sites?.Count ?? 0) ?? 0),
                        ("bindings", v?.Items?.Sum(i => i.Sites?.Count(x => x.Kind == CallSiteKind.DelegateBinding) ?? 0) ?? 0),
                        ("files", v?.FilesScanned), ("truncated", v?.Truncated), ("reason", v?.Reason), ("indexReady", v?.IndexReady),
                        ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }
    }
}
