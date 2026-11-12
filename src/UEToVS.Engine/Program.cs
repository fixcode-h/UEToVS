using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using UEToVS.Engine.Cli;
using UEToVS.Engine.Health;
using UEToVS.Engine.Rpc;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine
{
    /// <summary>Exit codes (logged as process.exit).</summary>
    public static class ExitCodes
    {
        public const int Ok = 0;
        public const int Fatal = 1;
        public const int Usage = 2;
        public const int PipeFailed = 3;
        public const int CliNoResult = 4;
    }

    public static class Program
    {
        private static readonly Logger L = Log.For("Process");

        public static async Task<int> Main(string[] args)
        {
            var options = EngineOptions.Parse(args, out string error);
            if (options == null || options.Help)
            {
                if (error != null) Console.Error.WriteLine("error: " + error);
                Console.Error.WriteLine(EngineOptions.Usage);
                return options?.Help == true ? ExitCodes.Ok : ExitCodes.Usage;
            }
            return await RunAsync(options).ConfigureAwait(false);
        }

        public static async Task<int> RunAsync(EngineOptions options)
        {
            var started = Stopwatch.StartNew();
            var paths = StoragePaths.ForSolution(options.SolutionDir);
            JsonLinesFileSink sink = null;
            try
            {
                sink = LogSetup.Initialize(paths, "engine", options.LogLevel);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("warning: file logging unavailable: " + ex.Message);
            }

            int exitCode = ExitCodes.Fatal;
            string reason = "unknown";
            EngineHost host = null;
            DiagnosticsReporter reporter = null;
            PosixSignalRegistration sigterm = null;
            try
            {
                WriteSessionHeader(options, paths, sink);
                reporter = new DiagnosticsReporter(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5)) { PrivateBytesBudget = EngineHost.PrivateBytesBudget };

                host = new EngineHost(options);
                var h = host;
                reporter.ResourceExtras = () => h.ResourceExtras();
                AppDomain.CurrentDomain.ProcessExit += (s, e) => h.RequestShutdown("process-exit");
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    h.RequestShutdown("ctrl-c");
                };
                try { sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; h.RequestShutdown("sigterm"); }); }
                catch (Exception ex) { L.Debug("process.signal.unsupported", ex.Message); }

                if (options.ParentPid is int pid) ParentWatcher.Watch(pid, r => h.RequestShutdown(r), host.ShutdownToken);

                if (options.IsCli)
                {
                    exitCode = await CliRunner.RunAsync(host, options).ConfigureAwait(false);
                    reason = "cli-done";
                }
                else
                {
                    _ = host.StartIndexing();
                    var server = new PipeServer(options.PipeName, host);
                    var serving = server.RunAsync(host.ShutdownToken);
                    _ = DiscoveryPublisher.PublishWhenListeningAsync(server, host); // .vs/UEToVS/engine.json for MCP / local tools
                    await serving.ConfigureAwait(false);
                    exitCode = host.ExitCode;
                    reason = host.ShutdownReason ?? "pipe-closed";
                }
            }
            catch (Exception ex)
            {
                L.Error("process.fatal", "Engine failed", ex);
                Console.Error.WriteLine("fatal: " + ex);
                exitCode = ExitCodes.Fatal;
                reason = "fatal:" + ex.GetType().Name;
            }
            finally
            {
                if (host != null && !options.IsCli) DiscoveryPublisher.Remove(host);
                if (host != null)
                {
                    try { await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (Exception ex) { L.Error("process.stop.failed", "Graceful stop failed", ex); }
                }
                sigterm?.Dispose();
                reporter?.Dispose(); // writes a final perf.summary
                L.Info("process.exit", "Engine exiting", ("exitCode", exitCode), ("reason", reason), ("uptimeSec", Math.Round(started.Elapsed.TotalSeconds, 1)));
                Log.Shutdown();
            }
            return exitCode;
        }

        private static void WriteSessionHeader(EngineOptions options, StoragePaths paths, JsonLinesFileSink sink)
        {
            Workspace ws = null;
            string locateError = null;
            try
            {
                ws = WorkspaceDiscovery.Locate(new WorkspaceOptions { SolutionDir = options.SolutionDir, ProjectFile = options.ProjectFile, EngineRootOverride = options.EngineRoot });
            }
            catch (Exception ex)
            {
                locateError = ex.GetType().Name + ": " + ex.Message;
            }
            long gcAvailable = 0;
            try { gcAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { }
            var extra = new List<(string, object)>
            {
                ("protocolVersion", ProtocolInfo.Version),
                ("mode", options.IsCli ? "cli" : "server"),
                ("pipe", options.PipeName),
                ("parentPid", options.ParentPid),
                ("solutionDir", options.SolutionDir),
                ("projectFile", ws?.ProjectFile),
                ("engineRoot", ws?.EngineRoot),
                ("engineRootMethod", ws?.EngineRootMethod),
                ("engineAssociation", ws?.EngineAssociation),
                ("ueVersion", ws?.EngineVersion?.ToString()),
                ("ueChangelist", ws?.EngineVersion?.Changelist),
                ("ueBranch", ws?.EngineVersion?.BranchName),
                ("engineBuildId", ws?.EngineBuildId),
                ("totalMemoryMB", MachineMemory.TotalBytes() / 1048576),
                ("gcAvailableMB", gcAvailable / 1048576),
                ("logFile", sink?.CurrentFile),
                ("indexDir", paths.Index),
                ("config", new Dictionary<string, object>
                {
                    ["maxParallelism"] = options.MaxParallelism > 0 ? options.MaxParallelism : Math.Max(1, Environment.ProcessorCount - 1),
                    ["skipEngine"] = options.SkipEngine,
                    ["watchEngine"] = options.WatchEngine,
                    ["noWatch"] = options.NoWatch,
                    ["serverGc"] = GCSettings.IsServerGC,
                    ["gcLatency"] = GCSettings.LatencyMode.ToString(),
                    ["heapHardLimit"] = AppContext.GetData("System.GC.HeapHardLimit")?.ToString(),
                    ["privateBytesBudgetMB"] = EngineHost.PrivateBytesBudget / 1048576,
                }),
            };
            if (locateError != null) extra.Add(("locateError", locateError));
            DiagnosticsReporter.WriteSessionHeader("UEToVS.Engine", EngineHost.Version, extra);
            if (locateError != null) L.Error("workspace.locate.failed", "Could not locate project/engine", null, ("error", locateError));
            if (ws != null) foreach (var w in ws.Warnings) L.Warn("workspace.warning", w);
        }
    }
}

namespace UEToVS.Engine
{
    /// <summary>Physical memory of the machine (for the session header).</summary>
    internal static class MachineMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        public static long TotalBytes()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                    return GlobalMemoryStatusEx(ref s) ? (long)s.TotalPhys : 0;
                }
                if (System.IO.File.Exists("/proc/meminfo"))
                {
                    foreach (var line in System.IO.File.ReadLines("/proc/meminfo"))
                    {
                        if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        return long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
                    }
                }
            }
            catch (Exception) { }
            return 0;
        }
    }
}
