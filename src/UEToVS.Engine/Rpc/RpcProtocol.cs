using System.Collections.Generic;
using System.IO;
using MessagePack;
using MessagePack.Resolvers;
using StreamJsonRpc;
using UEToVS.Contracts;

namespace UEToVS.Engine.Rpc
{
    /// <summary>
    /// Wire configuration shared by both ends. THE HOST MUST USE EXACTLY THIS (it cannot reference this assembly,
    /// so it copies these lines):
    /// <code>
    /// var options = MessagePackSerializerOptions.Standard
    ///     .WithResolver(ContractlessStandardResolver.Instance)
    ///     .WithSecurity(MessagePackSecurity.UntrustedData);
    /// var formatter = new MessagePackFormatter();
    /// formatter.SetMessagePackSerializerOptions(options);
    /// var handler = new LengthHeaderMessageHandler(pipe, pipe, formatter);   // 4-byte big-endian length prefix
    /// var rpc = new JsonRpc(handler);
    /// rpc.ActivityTracingStrategy = new ActivityTracingStrategy();            // optional: W3C traceparent → engine "req"
    /// rpc.AddLocalRpcTarget&lt;IEngineCallbacks&gt;(callbacks, null);         // receives OnStatusChangedAsync notifications
    /// var engine = rpc.Attach&lt;IEngineService&gt;();
    /// rpc.StartListening();
    /// </code>
    /// Why ContractlessStandardResolver: the Contracts DTOs are plain POCOs without [MessagePackObject]/[Key]
    /// attributes (netstandard2.0, shared with net48); contractless serializes them as maps keyed by property name,
    /// so adding a property is backward compatible (§10.1 "契约只增不改"). Both sides must use the same resolver,
    /// otherwise objects arrive as empty/default. Enums travel as integers.
    /// Method names are the C# interface method names (e.g. "SearchAsync"); StreamJsonRpc also accepts them without
    /// the "Async" suffix. Cancellation uses StreamJsonRpc's "$/cancelRequest" (pass a CancellationToken on the proxy).
    /// </summary>
    public static class RpcProtocol
    {
        public static readonly MessagePackSerializerOptions SerializerOptions = MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolver.Instance)
            .WithSecurity(MessagePackSecurity.UntrustedData);

        /// <summary>Notification method engine → host (positional argument: one <see cref="EngineStatus"/>).</summary>
        public const string StatusChangedMethod = nameof(IEngineCallbacks.OnStatusChangedAsync);

        public static IJsonRpcMessageHandler CreateHandler(Stream duplex) => CreateHandler(duplex, duplex);

        public static IJsonRpcMessageHandler CreateHandler(Stream sending, Stream receiving)
        {
            var formatter = new MessagePackFormatter();
            formatter.SetMessagePackSerializerOptions(SerializerOptions);
            return new LengthHeaderMessageHandler(sending, receiving, formatter);
        }
    }

    /// <summary>Per-RPC latency budgets (§9.1 hard limits); over budget → "<c>rpc.x.slow</c>" Warn with parameters.</summary>
    public static class RpcBudgets
    {
        public static readonly IReadOnlyDictionary<string, double> ByEvent = new Dictionary<string, double>
        {
            ["rpc.handshake"] = 500,
            ["rpc.getStatus"] = 50,
            ["rpc.search"] = 100,          // Search Everywhere per keystroke: 30 ms p95 / 100 ms hard
            ["rpc.getFileSymbols"] = 100,  // visible-range work: 30 / 100
            ["rpc.findSymbols"] = 200,     // go to definition: 30 / 200
            ["rpc.notifyFilesChanged"] = 50, // enqueue only
            ["rpc.setLogLevel"] = 50,
            ["rpc.getPerformanceSnapshot"] = 50,
            ["rpc.shutdown"] = 2000,
            ["index.update"] = 500,        // single file saved → index updated: 100 / 500
            ["rpc.navigate"] = 200,        // go to definition / declaration / implementation: 30 / 200
            ["rpc.findReferences"] = 3000, // common project symbol < 300 ms; engine-wide searches are cancellable
            ["rpc.getTypeHierarchy"] = 1000,
            ["rpc.getHighlights"] = 50,    // per caret move: p95 < 20 ms
            ["rpc.getCaretContext"] = 100,
            ["rpc.getMembers"] = 300,
            ["rpc.getCompletions"] = 50,   // M3: completion p95 < 50 ms
            ["rpc.getQuickInfo"] = 100,
            ["rpc.getSignatureHelp"] = 50,
            ["rpc.classify"] = 30,         // viewport (~100 lines) p95 < 15 ms
            ["rpc.getDiagnostics"] = 100,  // viewport ± 50 lines, after an edit pause (squiggles)
            ["rpc.getRefactoringContext"] = 100,   // M4 Alt+Enter / light bulb (include walk bounded, cached)
            ["rpc.getOverridableMethods"] = 500,   // M4 Implement Virtual dialog
        };

        public static double For(string evt) => ByEvent.TryGetValue(evt, out var b) ? b : double.MaxValue;

        /// <summary>Budget for a LatencyStats key ("Module/event"); 0 = none.</summary>
        public static double ForKey(string key)
        {
            int slash = key.IndexOf('/');
            string evt = slash >= 0 ? key.Substring(slash + 1) : key;
            return ByEvent.TryGetValue(evt, out var b) ? b : 0;
        }
    }
}
