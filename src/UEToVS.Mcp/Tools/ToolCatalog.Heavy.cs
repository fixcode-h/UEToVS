using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Mcp.Data;

namespace UEToVS.Mcp.Tools
{
    /// <summary>v0.4.5 heavy features: run_project_inspection (asynchronous whole-project UE rule inspection inside the engine).</summary>
    public sealed partial class ToolCatalog
    {
        private void RegisterHeavyTools()
        {
            Register(ProjectInspectionTool());
        }

        private ToolDefinition ProjectInspectionTool() => Define(
            "run_project_inspection", "Run project inspection",
            ToolGuides.Description("run_project_inspection"),
            Schema(
                "\"action\":{\"type\":\"string\",\"enum\":[\"start\",\"status\",\"results\",\"cancel\"],\"description\":\"start | status (default) | results | cancel.\"}," +
                "\"include_plugins\":{\"type\":\"boolean\",\"description\":\"start: also inspect project plugins (default true).\"}," +
                "\"rule\":{\"type\":\"string\",\"description\":\"results: only this rule id, e.g. UE0004.\"}," +
                "\"file\":{\"type\":\"string\",\"description\":\"results: only files whose path contains this text.\"}," +
                "\"fixable_only\":{\"type\":\"boolean\",\"description\":\"results: only findings with a mechanical fix.\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500,\"description\":\"results: page size (default 50).\"}," +
                "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"results: findings to skip (use nextOffset).\"}"),
            ProjectInspectionAsync);

        private async Task<ToolOutput> ProjectInspectionAsync(ToolArgs a, CancellationToken ct)
        {
            string action = a.Choice("action", "status", "start", "status", "results", "cancel");
            var req = new ProjectInspectionRequest
            {
                Action = action switch { "start" => HeavyJobAction.Start, "results" => HeavyJobAction.Results, "cancel" => HeavyJobAction.Cancel, _ => HeavyJobAction.Status },
                IncludePlugins = a.Bool("include_plugins", true),
                Rule = a.String("rule", maxLength: 16),
                File = a.String("file", maxLength: 512),
                FixableOnly = a.Bool("fixable_only", false),
                Limit = a.Int("limit", 50, 1, 500),
                Offset = a.Int("offset", 0, 0, int.MaxValue),
            };
            var r = await _index.ProjectInspectionAsync(req, ct).ConfigureAwait(false);
            if (r.State == HeavyJobState.Disabled)
                throw new ToolException("feature_disabled", "Project inspection is switched off: enable it in Visual Studio, UEToVS 设置 > 检查 > 全项目检查 (it may use a lot of CPU/memory).");
            if (r.State == HeavyJobState.Failed && r.Error == "index-not-ready")
                throw new ToolException("index_not_ready", "The index is still being built; start the inspection once index_status reports ready.");
            var payload = new JsonObject
            {
                ["source"] = Sources.Engine,
                ["state"] = r.State.ToString(),
                ["filesDone"] = r.FilesDone,
                ["filesTotal"] = r.FilesTotal,
                ["findings"] = r.Findings,
                ["elapsedMs"] = r.ElapsedMs,
            };
            if (r.ResultsPath != null) payload["resultsFile"] = r.ResultsPath;
            if (!string.IsNullOrEmpty(r.Error)) payload["error"] = r.Error;
            if (r.Threads > 0) payload["threads"] = r.Threads;
            if (r.PeakWorkingSetBytes > 0) payload["peakWorkingSetMB"] = r.PeakWorkingSetBytes >> 20;
            payload["rules"] = new JsonArray(r.Rules.Select(x => (JsonNode)new JsonObject { ["rule"] = x.Code, ["count"] = x.Count, ["fixable"] = x.Fixable }).ToArray());
            int count = 0;
            if (req.Action == HeavyJobAction.Results)
            {
                payload["matched"] = r.Matched;
                payload["items"] = new JsonArray(r.Items.Select(f =>
                {
                    var o = new JsonObject
                    {
                        ["rule"] = f.Code, ["file"] = f.FilePath, ["line"] = f.Line + 1, ["col"] = f.Column + 1, ["severity"] = f.Severity.ToString().ToLowerInvariant(),
                        ["message"] = f.Message,
                    };
                    if (f.LineText != null) o["text"] = f.LineText;
                    if (f.Fix != null && f.Fix.Count > 0) o["fixable"] = true;
                    return (JsonNode)o;
                }).ToArray());
                count = r.Items.Count;
                if (req.Offset + count < r.Matched) payload["nextOffset"] = req.Offset + count;
            }
            else if (r.TopFiles.Count > 0)
            {
                payload["topFiles"] = new JsonArray(r.TopFiles.Take(20).Select(x => (JsonNode)new JsonObject { ["file"] = x.FilePath, ["count"] = x.Count }).ToArray());
            }
            if (r.State == HeavyJobState.Running) payload["note"] = "Running: call again with action 'status' (a few seconds apart), then 'results'.";
            return new ToolOutput { Payload = payload, ResultCount = count, Source = Sources.Engine };
        }
    }
}
