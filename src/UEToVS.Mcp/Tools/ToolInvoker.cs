using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Mcp.Data;

namespace UEToVS.Mcp.Tools
{
    /// <summary>Result of one tools/call, transport-independent.</summary>
    public sealed class ToolCallResult
    {
        public string Text { get; set; }
        public bool IsError { get; set; }
        public int ResultCount { get; set; }
        public string Source { get; set; }
        public double ElapsedMs { get; set; }
    }

    /// <summary>
    /// Runs a tool by name with validation, error mapping and logging. Every call writes one line
    /// "mcp.tool.&lt;name&gt;" (Info; Warn when slow or the agent's arguments were wrong; Error on a bug) with the argument
    /// summary, result count, data source and elapsed ms, so the logs show which tools agents use and how fast they are.
    /// </summary>
    public sealed class ToolInvoker
    {
        private static readonly Logger L = Log.For("Mcp.Tools");

        /// <summary>Above this a call is logged as Warn ("mcp.tool.x.slow"); agents feel anything slower.</summary>
        public const double SlowMs = 1000;

        private readonly ToolCatalog _catalog;
        private long _calls;

        public ToolInvoker(ToolCatalog catalog) => _catalog = catalog;

        public ToolCatalog Catalog => _catalog;

        public long Calls => Interlocked.Read(ref _calls);

        public async Task<ToolCallResult> InvokeAsync(string name, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var sw = Stopwatch.StartNew();
            Log.RequestId = "mcp-" + Interlocked.Read(ref _calls);
            var tool = _catalog.Get(name);
            string evt = "mcp.tool." + (tool != null ? name : "unknown");
            string argSummary = null;
            try
            {
                if (tool == null)
                    throw new ToolException("unknown_tool", $"Unknown tool '{name}'. Call tools/list for the available tools.");
                var args = new ToolArgs(arguments, tool.Arguments);
                argSummary = args.Summary();
                var output = await tool.Handler(args, ct).ConfigureAwait(false);
                double ms = sw.Elapsed.TotalMilliseconds;
                var level = ms > SlowMs ? LogLevel.Warn : LogLevel.Info;
                L.Write(level, ms > SlowMs ? evt + ".slow" : evt, null, null, ms, new (string, object)[]
                {
                    ("args", argSummary), ("results", output.ResultCount), ("source", output.Source),
                });
                return new ToolCallResult
                {
                    Text = ResultJson.Serialize(output.Payload),
                    ResultCount = output.ResultCount,
                    Source = output.Source,
                    ElapsedMs = ms,
                };
            }
            catch (ToolException ex)
            {
                double ms = sw.Elapsed.TotalMilliseconds;
                L.Write(LogLevel.Warn, evt + ".rejected", ex.Message, null, ms, new (string, object)[] { ("args", argSummary ?? Summarize(arguments)), ("code", ex.Code) });
                return Error(ex.Code, ex.Message, ms);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                L.Write(LogLevel.Info, evt + ".cancelled", null, null, sw.Elapsed.TotalMilliseconds, new (string, object)[] { ("args", argSummary) });
                throw;
            }
            catch (Exception ex)
            {
                double ms = sw.Elapsed.TotalMilliseconds;
                L.Write(LogLevel.Error, evt + ".failed", "Tool failed", ex, ms, new (string, object)[] { ("args", argSummary ?? Summarize(arguments)) });
                return Error("internal_error", ex.GetType().Name + ": " + ex.Message + " (details in .vs/UEToVS/Logs/mcp-*.log)", ms);
            }
        }

        private static ToolCallResult Error(string code, string message, double ms) => new ToolCallResult
        {
            IsError = true,
            Text = ResultJson.Serialize(new JsonObject { ["error"] = code, ["message"] = message }),
            ElapsedMs = ms,
        };

        private static string Summarize(IReadOnlyDictionary<string, JsonElement> args)
        {
            if (args == null) return "";
            try
            {
                return JsonSerializer.Serialize(args);
            }
            catch (Exception)
            {
                return "(unserializable)";
            }
        }
    }
}
