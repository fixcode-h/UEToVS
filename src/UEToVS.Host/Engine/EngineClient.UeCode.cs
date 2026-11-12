using System;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Engine
{
    /// <summary>
    /// v0.4.5 UE code queries used by the editor-link windows: automation tests and log-category sites. Scanned on demand
    /// by the engine (per-file cache), never in the background. Same contract as the other wrappers: timeout, never throws,
    /// <see cref="EngineCallResult{T}"/> status; logged as rpc.&lt;op&gt; plus a uecode.* summary.
    /// </summary>
    internal sealed partial class EngineClient
    {
        private static readonly Logger UeCodeLog = Log.For("UeCode");

        // A cold scan reads every project source once (later calls hit the cache).
        private static readonly TimeSpan UeCodeTimeout = TimeSpan.FromSeconds(30);

        public async Task<EngineCallResult<AutomationTestsResponse>> FindAutomationTestsAsync(AutomationTestsRequest request, CancellationToken ct)
        {
            var r = await CallAsync("findAutomationTests", (s, c) => s.FindAutomationTestsAsync(request, c), ct, UeCodeTimeout, 3000).ConfigureAwait(false);
            if (r.Status == EngineCallStatus.Canceled) return r;
            var v = r.Value;
            UeCodeLog.Info("uecode.tests.result", null, ("status", r.Status.ToString()), ("tests", v?.Tests?.Count ?? 0), ("files", v?.FilesScanned),
                           ("read", v?.FilesRead), ("truncated", v?.Truncated), ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<LogSitesResponse>> FindLogSitesAsync(LogSitesRequest request, CancellationToken ct)
        {
            var r = await CallAsync("findLogSites", (s, c) => s.FindLogSitesAsync(request, c), ct, UeCodeTimeout, 3000).ConfigureAwait(false);
            if (r.Status == EngineCallStatus.Canceled) return r;
            var v = r.Value;
            UeCodeLog.Info("uecode.logSites.result", null, ("status", r.Status.ToString()), ("category", request?.Category), ("sites", v?.Sites?.Count ?? 0),
                           ("files", v?.FilesScanned), ("read", v?.FilesRead), ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }
    }
}
