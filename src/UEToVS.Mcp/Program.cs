using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using UEToVS.Mcp.Data;
using UEToVS.Mcp.Server;
using UEToVS.Mcp.Tools;

namespace UEToVS.Mcp
{
    public static class Program
    {
        private static readonly Logger L = Log.For("Mcp");

        public static async Task<int> Main(string[] args)
        {
            var options = McpOptions.Parse(args, out string error);
            if (options == null || options.Help)
            {
                // stderr only: stdout belongs to the MCP protocol
                if (error != null) Console.Error.WriteLine("error: " + error);
                Console.Error.WriteLine(McpOptions.Usage);
                return options?.Help == true ? 0 : 2;
            }

            var started = Stopwatch.StartNew();
            var paths = StoragePaths.ForSolution(options.SolutionDir);
            JsonLinesFileSink sink = null;
            try
            {
                sink = LogSetup.Initialize(paths, "mcp", options.LogLevel);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("warning: file logging unavailable: " + ex.Message);
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            int exitCode = 0;
            string reason = "stdin-closed";
            try
            {
                L.Info("mcp.start", "UEToVS MCP server starting", ("version", EngineConnection.Version), ("pid", Environment.ProcessId),
                    ("solutionDir", options.SolutionDir), ("solutionDirMethod", options.SolutionDirMethod), ("source", options.Source.ToString()),
                    ("protocol", ProtocolInfo.Version), ("logFile", sink?.CurrentFile), ("indexDir", paths.Index), ("cwd", Environment.CurrentDirectory));

                using var index = new CodeIndex(options.SolutionDir, options.Source);
                var invoker = new ToolInvoker(new ToolCatalog(index));
                var serverOptions = McpServerFactory.CreateOptions(invoker);
                await using var transport = new StdioServerTransport(McpServerFactory.ServerName);
                await using var server = McpServer.Create(transport, serverOptions);
                await server.RunAsync(cts.Token).ConfigureAwait(false);
                L.Info("mcp.stop", null, ("calls", invoker.Calls), ("uptimeSec", Math.Round(started.Elapsed.TotalSeconds, 1)));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                reason = "ctrl-c";
            }
            catch (Exception ex)
            {
                L.Error("mcp.fatal", "MCP server failed", ex);
                Console.Error.WriteLine("fatal: " + ex);
                exitCode = 1;
                reason = "fatal:" + ex.GetType().Name;
            }
            finally
            {
                L.Info("process.exit", null, ("exitCode", exitCode), ("reason", reason), ("uptimeSec", Math.Round(started.Elapsed.TotalSeconds, 1)));
                Log.Shutdown();
            }
            return exitCode;
        }
    }
}
