using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using UEToVS.Core.Protocol;
using UEToVS.Indexer.Index;
using UEToVS.Indexer.Rpc;
using UEToVS.Indexer.Text;
using UEToVS.Indexer.Unreal;
using Xunit;

namespace UEToVS.Indexer.Tests
{
    public class RpcTests : IDisposable
    {
        private readonly string _root;

        public RpcTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uetovs-test-" + Guid.NewGuid().ToString("N"));
            var src = Path.Combine(_root, "Source", "MyGame");
            Directory.CreateDirectory(Path.Combine(src, "Public"));
            Directory.CreateDirectory(Path.Combine(src, "Private"));
            Directory.CreateDirectory(Path.Combine(_root, "Content", "Maps"));
            File.WriteAllText(Path.Combine(_root, "MyGame.uproject"), "{\"EngineAssociation\":\"5.4\"}");
            File.WriteAllText(Path.Combine(src, "MyGame.Build.cs"), "public class MyGame : ModuleRules { public MyGame(ReadOnlyTargetRules Target) : base(Target) { } }");
            var samples = Path.Combine(AppContext.BaseDirectory, "Samples");
            File.Copy(Path.Combine(samples, "MyCharacter.h"), Path.Combine(src, "Public", "MyCharacter.h"));
            File.Copy(Path.Combine(samples, "MyCharacter.cpp"), Path.Combine(src, "Private", "MyCharacter.cpp"));
            File.WriteAllText(Path.Combine(_root, "Content", "Maps", "L_Main.umap"), "");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public async Task EndToEndOverNamedPipe()
        {
            var layout = ProjectLayout.Discover(Path.Combine(_root, "MyGame.uproject"), null);
            using var store = new IndexStore(layout, Path.Combine(_root, ".cache"));
            await store.BuildAsync(CancellationToken.None);
            var cts = new CancellationTokenSource();
            var service = new IndexService(store, new TextSearcher(store), cts);

            string pipe = "uetovs-test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var serverTask = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                var rpc = JsonRpc.Attach(server, service);
                await rpc.Completion;
            });

            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            var proxy = JsonRpc.Attach<IIndexService>(client);

            var status = await proxy.GetStatusAsync(CancellationToken.None);
            Assert.True(status.SymbolsReady);
            Assert.True(status.SymbolCount > 10);

            var resp = await proxy.SearchAsync(new SearchRequest { Pattern = "MyChar", IncludeAssets = true }, CancellationToken.None);
            var top = resp.Hits.First();
            Assert.Equal(ResultCategory.Type, top.Category);
            Assert.Equal("AMyCharacter", top.Symbol.Name);
            Assert.Equal("MyGame", top.Symbol.Module);
            Assert.Contains(resp.Hits, h => h.Category == ResultCategory.File && h.File.Name == "MyCharacter.h");

            var assets = await proxy.SearchAsync(new SearchRequest { Pattern = "LMain", IncludeAssets = true, IncludeTypes = false, IncludeSymbols = false, IncludeFiles = false }, CancellationToken.None);
            Assert.Equal("/Game/Maps/L_Main.L_Main", Assert.Single(assets.Hits).Asset.ObjectPath);

            var related = await proxy.FindSymbolsAsync(new[] { "Server_Fire_Implementation", "Server_Fire_Validate" }, "AMyCharacter", CancellationToken.None);
            Assert.Equal(2, related.Count);

            var fileSyms = await proxy.GetFileSymbolsAsync(Path.Combine(_root, "Source", "MyGame", "Public", "MyCharacter.h"), CancellationToken.None);
            Assert.Contains(fileSyms, s => s.Name == "Tick");

            var module = await proxy.GetModuleForFileAsync(Path.Combine(_root, "Source", "MyGame", "Private", "MyCharacter.cpp"), CancellationToken.None);
            Assert.Equal("MyGame", module.Name);
            Assert.EndsWith("MyGame.Build.cs", module.BuildCsPath);

            var inc = await proxy.FindModuleForIncludeAsync("MyCharacter.h", CancellationToken.None);
            Assert.Equal("MyGame", inc.Name);

            var text = await proxy.SearchTextAsync(new TextSearchRequest { Query = "Server_Fire_Validate" }, CancellationToken.None);
            Assert.Contains(text, t => t.FilePath.EndsWith("MyCharacter.cpp") && t.Line == 28);

            // incremental update
            var newFile = Path.Combine(_root, "Source", "MyGame", "Public", "Added.h");
            File.WriteAllText(newFile, "class UAddedThing { };");
            Assert.True(store.Refresh(newFile));
            store.Rebuild();
            var added = await proxy.SearchAsync(new SearchRequest { Pattern = "AddedThing" }, CancellationToken.None);
            Assert.Contains(added.Hits, h => h.Symbol?.Name == "UAddedThing");

            client.Dispose();
        }
    }
}
