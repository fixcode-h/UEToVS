using System;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Navigation;

namespace UEToVS.Host.Engine
{
    /// <summary>
    /// M3 IntelliSense RPCs (completion, Quick Info, parameter info, semantic classification). Same contract as the M2
    /// wrappers: per-call timeout and latency budget (§9.1: completion p95 &lt; 50 ms), caller cancellation honoured,
    /// never throws, <see cref="EngineCallResult{T}"/> status. Logged as rpc.&lt;op&gt; (timing, .slow) plus an is.&lt;op&gt;
    /// summary (context, item counts, engine time, round trip) so slow or empty answers can be diagnosed from the log.
    /// Canceled calls (superseded by typing / scrolling) are not summarized to keep the log readable.
    /// </summary>
    internal sealed partial class EngineClient
    {
        private static readonly Logger IsLog = Log.For("IntelliSense");

        // The user is waiting on completion / signature help; Quick Info and classification are replaced by newer requests anyway.
        private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan QuickInfoTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan SignatureHelpTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ClassifyTimeout = TimeSpan.FromSeconds(5);

        public async Task<EngineCallResult<CompletionResponse>> GetCompletionsAsync(CompletionRequest request, CancellationToken ct)
        {
            var r = await CallAsync("completions", (s, c) => s.GetCompletionsAsync(request, c), ct, CompletionTimeout, 50).ConfigureAwait(false);
            if (r.Status == EngineCallStatus.Canceled) return r;
            var v = r.Value;
            IsLog.Info("is.completion", null, ("caret", CaretRequestBuilder.Describe(request?.Caret)),
                       ("trigger", request == null || request.TriggerChar == '\0' ? null : request.TriggerChar.ToString()), ("explicit", request?.Explicit),
                       ("status", r.Status.ToString()), ("context", v?.Context.ToString()), ("items", v?.Items?.Count ?? 0),
                       ("incomplete", v?.IsIncomplete), ("resolvedType", v?.ResolvedType), ("accessorFix", v?.AccessorFix?.Replacement),
                       ("indexReady", v?.IndexReady), ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<QuickInfoResponse>> GetQuickInfoAsync(CaretRequest caret, CancellationToken ct)
        {
            var r = await CallAsync("quickInfo", (s, c) => s.GetQuickInfoAsync(caret, c), ct, QuickInfoTimeout, 100).ConfigureAwait(false);
            if (r.Status == EngineCallStatus.Canceled) return r;
            var v = r.Value;
            IsLog.Info("is.quickInfo", null, ("caret", CaretRequestBuilder.Describe(caret)), ("status", r.Status.ToString()),
                       ("symbol", v?.Symbol == null ? null : SymbolPresentation.QualifiedName(v.Symbol)), ("kind", v?.Symbol?.Kind.ToString()),
                       ("hasDecl", !string.IsNullOrEmpty(v?.Declaration)), ("docChars", v?.Documentation?.Length ?? 0),
                       ("reflection", !string.IsNullOrEmpty(v?.Reflection)), ("macroChars", v?.MacroExpansion?.Length ?? 0),
                       ("overloads", v?.OverloadCount), ("confidence", v?.Confidence.ToString()), ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<SignatureHelpResponse>> GetSignatureHelpAsync(CaretRequest caret, CancellationToken ct)
        {
            var r = await CallAsync("signatureHelp", (s, c) => s.GetSignatureHelpAsync(caret, c), ct, SignatureHelpTimeout, 80).ConfigureAwait(false);
            if (r.Status == EngineCallStatus.Canceled) return r;
            var v = r.Value;
            IsLog.Info("is.signatureHelp", null, ("caret", CaretRequestBuilder.Describe(caret)), ("status", r.Status.ToString()),
                       ("signatures", v?.Signatures?.Count ?? 0), ("activeSignature", v?.ActiveSignature), ("activeParameter", v?.ActiveParameter),
                       ("openParen", v == null ? null : v.OpenParenLine + ":" + v.OpenParenColumn), ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }

        public async Task<EngineCallResult<ClassifyResponse>> ClassifyAsync(ClassifyRequest request, CancellationToken ct)
        {
            var r = await CallAsync("classify", (s, c) => s.ClassifyAsync(request, c), ct, ClassifyTimeout, 100).ConfigureAwait(false);
            if (r.Status == EngineCallStatus.Canceled) return r;
            var v = r.Value;
            // Debug: runs on every scroll / edit pause; Warn for slow ones comes from rpc.classify.slow.
            IsLog.Debug("is.classify", null, ("file", request?.Document?.FilePath), ("version", request?.Document?.TextVersion),
                        ("lines", request == null ? null : request.StartLine + ".." + request.EndLine), ("dirty", request?.Document?.Text != null),
                        ("status", r.Status.ToString()), ("answerVersion", v?.TextVersion), ("spans", v?.Spans?.Count ?? 0),
                        ("engineMs", v?.ElapsedMs), ("roundTripMs", r.ElapsedMs));
            return r;
        }
    }
}
