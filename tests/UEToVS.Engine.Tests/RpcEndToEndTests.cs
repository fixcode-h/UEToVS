using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Resolvers;
using StreamJsonRpc;
using UEToVS.Contracts;
using UEToVS.Engine.Rpc;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>Host-side client exactly as the VS extension must configure it (see RpcProtocol docs).</summary>
    internal sealed class TestClient : IDisposable, IEngineCallbacks
    {
        private readonly NamedPipeClientStream _pipe;

        private TestClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            var options = MessagePackSerializerOptions.Standard
                .WithResolver(ContractlessStandardResolver.Instance)
                .WithSecurity(MessagePackSecurity.UntrustedData);
            var formatter = new MessagePackFormatter();
            formatter.SetMessagePackSerializerOptions(options);
            Rpc = new JsonRpc(new LengthHeaderMessageHandler(pipe, pipe, formatter));
            Rpc.ActivityTracingStrategy = new ActivityTracingStrategy();
            Rpc.AddLocalRpcTarget<IEngineCallbacks>(this, null);
            Engine = Rpc.Attach<IEngineService>();
            Rpc.StartListening();
        }

        public JsonRpc Rpc { get; }
        public IEngineService Engine { get; }
        public ConcurrentQueue<EngineStatus> Notifications { get; } = new ConcurrentQueue<EngineStatus>();

        public static async Task<TestClient> ConnectAsync(string pipeName)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10000);
            return new TestClient(pipe);
        }

        public Task OnStatusChangedAsync(EngineStatus status)
        {
            Notifications.Enqueue(status);
            return Task.CompletedTask;
        }

        public static HandshakeRequest Hello(string solutionDir, int version = ProtocolInfo.Version) => new HandshakeRequest
        {
            ProtocolVersion = version,
            HostVersion = "test-host",
            VsVersion = "17.12",
            SolutionDir = solutionDir,
        };

        public void Dispose()
        {
            Rpc.Dispose();
            _pipe.Dispose();
        }
    }

    public sealed class RpcEndToEndTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private readonly FakeUnrealTree _t = new FakeUnrealTree();
        private readonly EngineHost _host;
        private readonly string _pipeName = "uetovs-test-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        private readonly Task _server;
        private readonly PipeServer _pipeServer;

        public RpcEndToEndTests()
        {
            _host = new EngineHost(new EngineOptions { PipeName = _pipeName, SolutionDir = _t.ProjectRoot, MaxParallelism = 2, WatchEngine = false });
            _host.StartIndexing();
            _pipeServer = new PipeServer(_pipeName, _host);
            _server = _pipeServer.RunAsync(_host.ShutdownToken);
        }

        public void Dispose()
        {
            _host.RequestShutdown("test-end");
            try { _server.Wait(5000); } catch { }
            _host.StopAsync(TimeSpan.FromSeconds(5)).Wait(10000);
            _t.Dispose();
        }

        private async Task<TestClient> ConnectReadyAsync()
        {
            await _pipeServer.Listening.WaitAsync(Timeout);
            var c = await TestClient.ConnectAsync(_pipeName);
            await c.Engine.HandshakeAsync(TestClient.Hello(_t.ProjectRoot), CancellationToken.None);
            await _host.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(Timeout);
            return c;
        }

        [Fact]
        public async Task HandshakeStatusSearchAndSymbols()
        {
            using var c = await ConnectReadyAsync();
            var hs = await c.Engine.HandshakeAsync(TestClient.Hello(_t.ProjectRoot), CancellationToken.None);
            Assert.Equal(ProtocolInfo.Version, hs.ProtocolVersion);
            Assert.Equal(Environment.ProcessId, hs.ProcessId);
            Assert.Equal(Path.Combine(_t.ProjectRoot, ".vs", "UEToVS", "Logs"), hs.LogDirectory);
            Assert.Equal(Path.Combine(_t.ProjectRoot, ".vs", "UEToVS", "Index"), hs.IndexDirectory);

            var status = await c.Engine.GetStatusAsync(CancellationToken.None);
            Assert.Equal(IndexPhase.Ready, status.Phase);
            Assert.Equal(_t.ProjectRoot, status.ProjectRoot);
            Assert.Equal(_t.EngineRoot, status.EngineRoot);
            Assert.Equal("5.4.2", status.UnrealVersion);
            Assert.True(status.FileCount >= 15, "files: " + status.FileCount);
            Assert.True(status.SymbolCount >= 5, "symbols: " + status.SymbolCount);
            Assert.Equal(5, status.AssetCount); // /Game 2 + /MyTools 1 + /Engine 1 + /Niagara 1 (__ExternalActors__ skipped)
            Assert.NotNull(status.Resources);
            Assert.True(status.Resources.PrivateBytes > 0);

            var resp = await c.Engine.SearchAsync(new SearchRequest { Pattern = "MyChar", IncludeAssets = true }, CancellationToken.None);
            Assert.True(resp.IndexReady);
            var top = resp.Hits.First();
            Assert.Equal(ResultCategory.Type, top.Category);
            Assert.Equal("AMyCharacter", top.Symbol.Name);
            Assert.True(top.Symbol.Ue.HasFlag(UeFlags.UClass));
            Assert.Equal("MyGame", top.Symbol.Module);
            Assert.Equal(ItemOrigin.Project, top.Symbol.Origin);
            Assert.Equal(new[] { 1, 6 }, top.Highlights);
            Assert.Contains(resp.Hits, h => h.Category == ResultCategory.File && h.File.Name == "MyCharacter.h" && h.File.Module == "MyGame");

            // Non-project results are counted but not returned unless asked for.
            var fstr = await c.Engine.SearchAsync(new SearchRequest { Pattern = "FString" }, CancellationToken.None);
            Assert.Empty(fstr.Hits);
            Assert.True(fstr.NonProjectHitCount >= 1);
            fstr = await c.Engine.SearchAsync(new SearchRequest { Pattern = "FString", IncludeNonProject = true }, CancellationToken.None);
            Assert.Equal(ItemOrigin.Engine, fstr.Hits.First(h => h.Category == ResultCategory.Type).Symbol.Origin);

            var assets = await c.Engine.SearchAsync(new SearchRequest { Pattern = "LMain", IncludeAssets = true, IncludeTypes = false, IncludeSymbols = false, IncludeFiles = false }, CancellationToken.None);
            Assert.Equal("/Game/Maps/L_Main.L_Main", Assert.Single(assets.Hits).Asset.ObjectPath);

            var found = await c.Engine.FindSymbolsAsync("Fire", "AMyCharacter", CancellationToken.None);
            // The real parser finds both the declaration (.h) and the out-of-line definition (.cpp).
            Assert.All(found, f => Assert.Equal(SymbolKind.Method, f.Kind));
            Assert.Contains(found, f => f.IsDefinition);
            Assert.Contains(found, f => !f.IsDefinition);
            Assert.Empty(await c.Engine.FindSymbolsAsync("Fire", "Other", CancellationToken.None));

            var fileSyms = await c.Engine.GetFileSymbolsAsync(_t.P("Game/Source/MyGame/Public/MyCharacter.h"), CancellationToken.None);
            Assert.Equal(new[] { "AMyCharacter", "Fire", "Health" }, fileSyms.Select(s => s.Name).ToArray());
            Assert.Empty(await c.Engine.GetFileSymbolsAsync(_t.P("Game/nope.h"), CancellationToken.None));
        }

        [Fact]
        public async Task ProtocolMismatchIsAnErrorResponse()
        {
            await _pipeServer.Listening.WaitAsync(Timeout);
            using var c = await TestClient.ConnectAsync(_pipeName);
            var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => c.Engine.HandshakeAsync(TestClient.Hello(_t.ProjectRoot, 999), CancellationToken.None));
            Assert.Equal(RpcErrorCodes.ProtocolMismatch, ex.ErrorCode);
            Assert.Contains("v999", ex.Message);
            // The connection stays usable (the host can show a proper message).
            Assert.NotNull(await c.Engine.GetStatusAsync(CancellationToken.None));
        }

        [Fact]
        public async Task SequentialConnectionsAndNotifications()
        {
            using (var first = await ConnectReadyAsync())
            {
                Assert.NotNull(await first.Engine.GetStatusAsync(CancellationToken.None));
            }
            await SchedulerTests.WaitUntil(() => _pipeServer.ConnectionCount == 0);

            using var second = await ConnectReadyAsync();
            await SchedulerTests.WaitUntil(() => _pipeServer.ConnectionCount == 1);

            // A change from the host → re-index → status notification + new symbol.
            string newFile = _t.Write("Game/Source/MyGame/Public/Weapon.h", "UCLASS()\nclass MYGAME_API AWeapon : public AActor\n{\n};\n");
            await second.Engine.NotifyFilesChangedAsync(new List<FileChange> { new FileChange { FilePath = newFile, Kind = FileChangeKind.Created } }, CancellationToken.None);
            await SchedulerTests.WaitUntil(() => _host.Index.Backend.FindSymbols("AWeapon", null).Count == 1);
            var w = Assert.Single(await second.Engine.FindSymbolsAsync("AWeapon", null, CancellationToken.None));
            Assert.Equal("MyGame", w.Module);
            await SchedulerTests.WaitUntil(() => !second.Notifications.IsEmpty);
            Assert.Equal(IndexPhase.Ready, second.Notifications.Last().Phase);

            File.Delete(newFile);
            await second.Engine.NotifyFilesChangedAsync(new List<FileChange> { new FileChange { FilePath = newFile, Kind = FileChangeKind.Deleted } }, CancellationToken.None);
            await SchedulerTests.WaitUntil(() => _host.Index.Backend.FindSymbols("AWeapon", null).Count == 0);
            var files = await second.Engine.SearchAsync(new SearchRequest { Pattern = "Weapon.h" }, CancellationToken.None);
            Assert.DoesNotContain(files.Hits, h => h.File?.Name == "Weapon.h");
        }

        [Fact]
        public async Task FileSystemWatcherPicksUpEditsWithoutTheHost()
        {
            using var c = await ConnectReadyAsync();
            await SchedulerTests.WaitUntil(() => _host.Index.Workspace != null);
            await Task.Delay(200); // watchers are attached right after discovery
            string path = _t.Write("Game/Source/MyGame/Private/Spawner.h", "USTRUCT()\nstruct FSpawnInfo\n{\n};\n");
            await SchedulerTests.WaitUntil(() => _host.Index.Backend.FindSymbols("FSpawnInfo", null).Count == 1, 15000);
            File.WriteAllText(path, "USTRUCT()\nstruct FSpawnInfo2\n{\n};\n");
            await SchedulerTests.WaitUntil(() => _host.Index.Backend.FindSymbols("FSpawnInfo2", null).Count == 1, 15000);
            Assert.Empty(_host.Index.Backend.FindSymbols("FSpawnInfo", null));
            // Build output is ignored.
            _t.Write("Game/Intermediate/Build/Ignored.h", "class FIgnored {};\n");
            await Task.Delay(800);
            Assert.Empty(_host.Index.Backend.FindSymbols("FIgnored", null));
        }

        [Fact]
        public async Task CancellationAndLogLevel()
        {
            using var c = await ConnectReadyAsync();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Engine.SearchAsync(new SearchRequest { Pattern = "A" }, cts.Token));

            var old = UEToVS.Core.Diagnostics.Log.MinLevel;
            try
            {
                await c.Engine.SetLogLevelAsync("Debug", CancellationToken.None);
                Assert.Equal(UEToVS.Core.Diagnostics.LogLevel.Debug, UEToVS.Core.Diagnostics.Log.MinLevel);
                var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => c.Engine.SetLogLevelAsync("Loud", CancellationToken.None));
                Assert.Equal(RpcErrorCodes.InvalidArgument, ex.ErrorCode);
            }
            finally
            {
                UEToVS.Core.Diagnostics.Log.MinLevel = old;
            }
        }

        [Fact]
        public async Task PerformanceSnapshotOverRpc()
        {
            using var c = await ConnectReadyAsync();
            await c.Engine.SearchAsync(new SearchRequest { Pattern = "MyChar" }, CancellationToken.None);
            _host.Performance.Sample();
            _host.Scheduler.SetBuildInProgress(true);
            try
            {
                var p = await c.Engine.GetPerformanceSnapshotAsync(CancellationToken.None);
                Assert.Equal(Environment.ProcessId, p.ProcessId);
                Assert.True(p.UptimeSeconds > 0);
                Assert.InRange(p.CpuPercent, 0, 100);
                Assert.True(p.Resources.PrivateBytes > 0);
                Assert.NotEmpty(p.History);
                Assert.True(p.History.Last().PrivateBytes > 0);
                Assert.Equal(IndexPhase.Ready, p.Status.Phase);
                Assert.True(p.BackgroundPaused);

                // Every phase of the run, with wall time, CPU and item counts.
                foreach (var phase in new[] { IndexPhase.DiscoveringProject, IndexPhase.ListingFiles, IndexPhase.LoadingCache, IndexPhase.Parsing, IndexPhase.Merging })
                    Assert.Contains(p.Phases, x => x.Phase == phase && !x.Running && x.DurationMs >= 0 && x.CpuMs >= 0);
                var parseProject = p.Phases.First(x => x.Phase == IndexPhase.Parsing && x.Detail.StartsWith("project"));
                Assert.True(parseProject.Items > 0);
                Assert.Contains("cacheHits=", parseProject.Detail);

                Assert.Contains(p.IndexParts, x => (x.Name == "segments" || x.Name == "delta") && (x.ManagedBytes > 0 || x.MappedBytes > 0));
                Assert.Contains(p.IndexParts, x => x.Name == "file-table");
                Assert.Contains(p.IndexParts, x => x.Name == "index-folder" && x.DiskBytes > 0);

                var search = p.Operations.Single(o => o.Name == "Rpc/rpc.search");
                Assert.True(search.Count >= 1);
                Assert.Equal(100, search.BudgetMs);
                Assert.True(search.MaxMs >= search.P50Ms);
            }
            finally
            {
                _host.Scheduler.SetBuildInProgress(false);
            }
        }
    }
}
