using System;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Engine
{
    /// <summary>v0.4.5 编辑体验 RPCs (same contract as the other wrappers: timeout, budget, cancellation, never throws).</summary>
    internal sealed partial class EngineClient
    {
        private static readonly TimeSpan InlayHintsTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Inlay hints for a line range. The tagger logs editor.inlayHints with the merged result; rpc.inlayHints.slow comes from CallAsync.</summary>
        public Task<EngineCallResult<InlayHintsResponse>> GetInlayHintsAsync(InlayHintsRequest request, CancellationToken ct) =>
            CallAsync("inlayHints", (s, c) => s.GetInlayHintsAsync(request, c), ct, InlayHintsTimeout, 100);
    }
}
