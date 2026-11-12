using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>Runs the real UEToVS.Engine executable as a child process.</summary>
    public sealed class ProcessTests : IDisposable
    {
        private readonly FakeUnrealTree _t = new FakeUnrealTree();

        public void Dispose() => _t.Dispose();

        private static string EngineDll => Path.Combine(AppContext.BaseDirectory, "UEToVS.Engine.dll");

        private static Process Start(params string[] args)
        {
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(EngineDll);
            foreach (var a in args) psi.ArgumentList.Add(a);
            return Process.Start(psi);
        }

        private List<JsonElement> ReadLogLines()
        {
            string logs = Path.Combine(_t.ProjectRoot, ".vs", "UEToVS", "Logs");
            var files = Directory.GetFiles(logs, "engine-*.log");
            Assert.NotEmpty(files);
            var lines = new List<JsonElement>();
            foreach (var f in files.OrderBy(f => f))
                foreach (var line in File.ReadAllLines(f).Where(l => l.Length > 0))
                    lines.Add(JsonDocument.Parse(line).RootElement.Clone()); // throws if any line is not valid JSON
            return lines;
        }

        private static string Evt(JsonElement e) => e.TryGetProperty("evt", out var v) ? v.GetString() : null;

        [Fact]
        public async Task ServesOverPipeLogsAndExitsOnShutdown()
        {
            string pipe = "uetovs-proc-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            using var p = Start("--pipe", pipe, "--solution-dir", _t.ProjectRoot, "--parent", Environment.ProcessId.ToString(), "--log-level", "debug");
            try
            {
                using var c = await TestClient.ConnectAsync(pipe);
                var hs = await c.Engine.HandshakeAsync(TestClient.Hello(_t.ProjectRoot), CancellationToken.None);
                Assert.Equal(p.Id, hs.ProcessId);

                EngineStatus status = null;
                var end = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < end)
                {
                    status = await c.Engine.GetStatusAsync(CancellationToken.None);
                    if (status.Phase == IndexPhase.Ready) break;
                    await Task.Delay(50);
                }
                Assert.Equal(IndexPhase.Ready, status.Phase);
                var resp = await c.Engine.SearchAsync(new SearchRequest { Pattern = "MyChar" }, CancellationToken.None);
                Assert.Equal("AMyCharacter", resp.Hits.First().Symbol.Name);
                var perf = await c.Engine.GetPerformanceSnapshotAsync(CancellationToken.None);
                Assert.Equal(p.Id, perf.ProcessId);

                await c.Engine.ShutdownAsync(CancellationToken.None);
                Assert.True(p.WaitForExit(15000), "engine did not exit after Shutdown");
                Assert.Equal(0, p.ExitCode);
            }
            finally
            {
                if (!p.HasExited) p.Kill(true);
            }

            var lines = ReadLogLines();
            Assert.Equal("session.start", Evt(lines[0]));
            var data = lines[0].GetProperty("data");
            Assert.Equal(_t.ProjectFile, data.GetProperty("projectFile").GetString());
            Assert.Equal(_t.EngineRoot, data.GetProperty("engineRoot").GetString());
            Assert.Equal("5.4.2", data.GetProperty("ueVersion").GetString());
            Assert.True(data.GetProperty("cpus").GetInt32() > 0);
            Assert.True(data.TryGetProperty("totalMemoryMB", out _));
            Assert.True(data.TryGetProperty("config", out _));
            Assert.All(lines, l => Assert.Equal("engine", l.GetProperty("proc").GetString()));

            var events = lines.Select(Evt).ToList();
            Assert.Contains("rpc.handshake.ok", events);
            Assert.Contains("rpc.search", events);         // Debug timing line (log level debug)
            Assert.Contains("index.phase.done", events);
            Assert.Contains("index.ready", events);
            Assert.Contains("process.parent.watch", events);
            var search = lines.First(l => Evt(l) == "rpc.search");
            Assert.True(search.TryGetProperty("req", out _));
            Assert.True(search.TryGetProperty("ms", out _));
            var exit = lines.Last(l => Evt(l) == "process.exit");
            Assert.Equal(0, exit.GetProperty("data").GetProperty("exitCode").GetInt32());
            Assert.Equal("rpc-shutdown", exit.GetProperty("data").GetProperty("reason").GetString());
        }

        [Fact]
        public void ExitsWhenParentIsGone()
        {
            // A pid that does not exist: the engine must not linger.
            using var p = Start("--pipe", "uetovs-orphan-" + Guid.NewGuid().ToString("N").Substring(0, 8), "--solution-dir", _t.ProjectRoot, "--parent", "999999", "--skip-engine");
            try
            {
                Assert.True(p.WaitForExit(15000), "engine kept running without its parent");
                Assert.Equal(0, p.ExitCode);
            }
            finally
            {
                if (!p.HasExited) p.Kill(true);
            }
            var exit = ReadLogLines().Last(l => Evt(l) == "process.exit");
            Assert.Equal("parent-missing", exit.GetProperty("data").GetProperty("reason").GetString());
        }

        [Fact]
        public void CliQueryAndUsage()
        {
            using var p = Start("--solution-dir", _t.ProjectRoot, "--query", "MyChar");
            string stdout = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(30000));
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("AMyCharacter", stdout);

            using var s = Start("--solution-dir", _t.ProjectRoot, "--status", "--skip-engine");
            string status = s.StandardOutput.ReadToEnd();
            Assert.True(s.WaitForExit(30000));
            Assert.Equal(0, s.ExitCode);
            Assert.Contains("\"Phase\": \"Ready\"", status);

            using var bad = Start("--bogus");
            bad.StandardError.ReadToEnd();
            Assert.True(bad.WaitForExit(10000));
            Assert.Equal(2, bad.ExitCode);
        }
    }
}
