using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Query;

namespace UEToVS.Engine.Cli
{
    /// <summary>--query / --status: index once, answer on stdout, exit. Same code paths as the RPC handlers.</summary>
    public static class CliRunner
    {
        public static async Task<int> RunAsync(EngineHost host, EngineOptions options)
        {
            _ = host.StartIndexing();
            bool ready;
            try
            {
                ready = await host.Index.WaitForReadyAsync(host.ShutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("cancelled: " + host.ShutdownReason);
                return ExitCodes.Fatal;
            }
            if (!ready)
            {
                Console.Error.WriteLine("index failed: " + host.Index.LastError);
                return ExitCodes.Fatal;
            }

            var ws = host.Index.Workspace;
            Console.WriteLine($"project: {ws?.ProjectFile ?? "-"}");
            Console.WriteLine($"engine:  {ws?.EngineRoot ?? "-"} {ws?.EngineVersion}");
            Console.WriteLine($"logs:    {host.Paths.Logs}");

            if (options.Status)
            {
                var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
                Console.WriteLine(JsonSerializer.Serialize(new { status = host.GetStatus(), performance = host.GetPerformanceSnapshot() }, json));
            }

            if (options.Query == null) return ExitCodes.Ok;
            var q = SearchQuery.Parse(options.Query);
            var resp = await host.Scheduler.RunAsync(Scheduling.WorkPriority.Interactive, "cli.search", ct => host.Search.Search(new SearchRequest
            {
                Pattern = q.Pattern,
                Container = q.Container,
                IncludeAssets = true,
                IncludeNonProject = options.QueryAll,
                MaxResultsPerCategory = options.QueryMax,
            }, ct)).ConfigureAwait(false);
            Console.WriteLine($"{resp.Hits.Count} hits in {resp.ElapsedMs:F1} ms (non-project: {resp.NonProjectHitCount})");
            foreach (var h in resp.Hits)
            {
                switch (h.Category)
                {
                    case ResultCategory.File:
                        Console.WriteLine($"  [file  ] {h.File.Name,-40} {h.File.RelativePath} ({h.File.Module}, {h.File.Origin}) {h.Score}");
                        break;
                    case ResultCategory.Asset:
                        Console.WriteLine($"  [asset ] {h.Asset.Name,-40} {h.Asset.ObjectPath} {h.Score}");
                        break;
                    default:
                        var s = h.Symbol;
                        string name = (s.Container == null ? "" : s.Container + "::") + s.Name;
                        Console.WriteLine($"  [{s.Kind,-6}] {name,-40} {Path.GetFileName(s.Location?.FilePath)}:{s.Location?.Line + 1} {s.Ue} {h.Score}");
                        break;
                }
            }
            return resp.Hits.Count > 0 ? ExitCodes.Ok : ExitCodes.CliNoResult;
        }
    }
}
