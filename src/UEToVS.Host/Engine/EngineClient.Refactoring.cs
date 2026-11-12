using System;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Engine
{
    /// <summary>
    /// M4 refactoring RPCs (Alt+Enter context, overridable methods). Same contract as the other wrappers: timeout, budget,
    /// caller cancellation, never throws. Logged as rpc.&lt;op&gt; plus a refactor.&lt;op&gt; summary.
    /// </summary>
    internal sealed partial class EngineClient
    {
        private static readonly Logger RefactorLog = Log.For("Refactor");
        private static readonly TimeSpan RefactorContextTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan OverridableTimeout = TimeSpan.FromSeconds(15);

        public async Task<EngineCallResult<RefactoringContextResponse>> GetRefactoringContextAsync(RefactoringContextRequest request, CancellationToken ct)
        {
            var r = await CallAsync("refactoringContext", (s, c) => s.GetRefactoringContextAsync(request, c), ct, RefactorContextTimeout, 150).ConfigureAwait(false);
            if (r.Status == EngineCallStatus.Canceled) return r;
            var v = r.Value;
            RefactorLog.Debug("refactor.context", null, ("caret", CaretRequestBuilder.Describe(request?.Caret)), ("status", r.Status.ToString()),
                ("word", v?.Word), ("function", v?.Function?.Name), ("decls", v?.Declarations?.Count ?? 0), ("defs", v?.Definitions?.Count ?? 0),
                ("class", v?.Class?.Symbol?.Name), ("type", v?.Type?.Symbol?.Name), ("include", v?.Type?.Include.ToString()),
                ("checkIncludes", request?.CheckIncludes), ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<OverridableMethodsResponse>> GetOverridableMethodsAsync(OverridableMethodsRequest request, CancellationToken ct)
        {
            var r = await CallAsync("overridableMethods", (s, c) => s.GetOverridableMethodsAsync(request, c), ct, OverridableTimeout, 500).ConfigureAwait(false);
            var v = r.Value;
            RefactorLog.Info("refactor.overridable", null, ("caret", CaretRequestBuilder.Describe(request?.Caret)), ("type", request?.TypeName ?? v?.Type?.Name),
                ("status", r.Status.ToString()), ("methods", v?.Methods?.Count ?? 0), ("alreadyOverridden", v?.AlreadyOverridden),
                ("types", v?.TypesVisited), ("truncated", v?.Truncated), ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }
    }
}
