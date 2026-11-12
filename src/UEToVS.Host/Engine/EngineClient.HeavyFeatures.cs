using System;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Engine
{
    /// <summary>
    /// v0.4.5 heavy-feature RPCs (whole-project inspection, upgrade assistant). The work runs in the engine on its own
    /// threads; these calls only start / poll / page, so the timeouts are short except for a results page. Same contract
    /// as the other wrappers: timeout, budget, caller cancellation, never throws.
    /// </summary>
    internal sealed partial class EngineClient
    {
        private static readonly Logger HeavyLog = Log.For("Heavy");
        private static readonly TimeSpan HeavyCallTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan HeavyPageTimeout = TimeSpan.FromSeconds(30);

        public async Task<EngineCallResult<ProjectInspectionResponse>> ProjectInspectionAsync(ProjectInspectionRequest request, CancellationToken ct)
        {
            bool page = request?.Action == HeavyJobAction.Results;
            var r = await CallAsync("projectInspection", (s, c) => s.ProjectInspectionAsync(request, c), ct, page ? HeavyPageTimeout : HeavyCallTimeout, page ? 2000 : 200)
                .ConfigureAwait(false);
            var v = r.Value;
            if (r.Status != EngineCallStatus.Canceled)
                HeavyLog.Debug("inspection.call", null, ("action", request?.Action.ToString()), ("status", r.Status.ToString()), ("state", v?.State.ToString()),
                    ("done", v?.FilesDone), ("total", v?.FilesTotal), ("findings", v?.Findings), ("matched", v?.Matched), ("items", v?.Items?.Count ?? 0),
                    ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<DeprecationScanResponse>> DeprecationScanAsync(DeprecationScanRequest request, CancellationToken ct)
        {
            bool results = request?.Action == HeavyJobAction.Results;
            var r = await CallAsync("deprecationScan", (s, c) => s.DeprecationScanAsync(request, c), ct, results ? HeavyPageTimeout : HeavyCallTimeout, results ? 2000 : 200)
                .ConfigureAwait(false);
            var v = r.Value;
            if (r.Status != EngineCallStatus.Canceled)
                HeavyLog.Debug("upgrade.call", null, ("action", request?.Action.ToString()), ("status", r.Status.ToString()), ("state", v?.State.ToString()),
                    ("done", v?.SymbolsDone), ("total", v?.DeprecatedSymbols), ("usages", v?.Usages), ("apis", v?.Apis?.Count ?? 0), ("roundTripMs", r.ElapsedMs));
            return r;
        }
    }
}
