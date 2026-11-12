using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Storage;
using UEToVS.Mcp.Data;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>Tools answered by a running engine found through .vs/UEToVS/engine.json.</summary>
    public sealed class LiveEngineTests : IClassFixture<LiveFixture>
    {
        private readonly LiveFixture _f;

        public LiveEngineTests(LiveFixture f) => _f = f;

        [Fact]
        public void DiscoveryFile_IsReadable()
        {
            Assert.Equal(Path.Combine(_f.T.ProjectRoot, ".vs", "UEToVS", "engine.json"), _f.DiscoveryPath);
            var d = EngineDiscovery.TryRead(_f.DiscoveryPath, out string error);
            Assert.Null(error);
            Assert.Equal(_f.Host.Options.PipeName, d.Pipe);
            Assert.Equal(Environment.ProcessId, d.Pid);
            Assert.Equal(UEToVS.Contracts.ProtocolInfo.Version, d.ProtocolVersion);
            Assert.Equal(_f.T.ProjectRoot, d.SolutionDir);
            Assert.True(d.StartedUtc > DateTime.UtcNow.AddMinutes(-10));
        }

        [Fact]
        public async Task StatusAndSearch_ComeFromTheEngine()
        {
            var st = await _f.Call("index_status");
            Assert.Equal("engine", st.Str("source"));
            Assert.True(st.GetProperty("engine").Bool("running"));
            Assert.Equal(Environment.ProcessId, st.GetProperty("engine").Int("pid"));
            Assert.Equal(5, st.Int("assets"));

            var sym = await _f.Call("search_symbols", new { query = "AMyHero" });
            Assert.Equal("engine", sym.Str("source"));
            Assert.Equal("AMyHero", sym.Items().First().Str("name"));

            var files = await _f.Call("search_files", new { query = "MyCharacter", extensions = new[] { ".h" } });
            Assert.Equal("MyCharacter.h", files.Items().Single().Str("name"));

            var assets = await _f.Call("search_assets", new { query = "Hero", @class = "Blueprint" });
            Assert.Equal("engine", assets.Str("source"));
            Assert.Equal("BP_Hero", assets.Items().Single().Str("name"));

            var outline = await _f.Call("get_file_outline", new { path = Path.Combine(_f.T.ProjectRoot, "Source/MyGame/Public/MyHero.h") });
            Assert.Equal("engine", outline.Str("source"));
            Assert.Contains(outline.Items(), i => i.Str("name") == "Taunt");
        }

        [Fact]
        public async Task NavigationTools_AnswerWithEngineOrHeuristics()
        {
            // M2 navigation answers from the engine; when it has nothing the tools fall back to index heuristics.
            var def = await _f.Call("go_to_definition", new { path = "Source/MyGame/Private/MyHero.cpp", line = 6, column = 2 });
            Assert.Equal("Jump", def.Str("word"));
            Assert.Contains("Character.h", def.Items("targets").First().Str("file"));

            var refs = await _f.Call("find_references", new { symbol = "Health" });
            Assert.Contains(refs.Str("source"), new[] { "engine", "index-heuristic" });
            Assert.Contains(refs.Items("files").SelectMany(f => f.Items("hits")), h => h.Str("kind").Contains("write"));

            var th = await _f.Call("get_type_hierarchy", new { type_name = "AActor", direction = "derived", scope = "all", max_depth = 6 });
            Assert.Equal("APawn", th.GetProperty("derived")[0].Str("name"));

            var members = await _f.Call("get_members", new { type_name = "APawn" });
            Assert.Equal("PossessedBy", members.Items().Single().Str("name"));
        }

        [Fact]
        public async Task EngineOnlyMode_FailsClearlyWithoutEngine()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-mcp-noengine-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using var index = new CodeIndex(dir, DataSourceMode.EngineOnly);
                var invoker = new Tools.ToolInvoker(new Tools.ToolCatalog(index));
                var r = await invoker.InvokeAsync("search_symbols", Tree.Args(new { query = "A" }), CancellationToken.None);
                Assert.True(r.IsError);
                Assert.Contains("engine_unavailable", r.Text);

                using var auto = new CodeIndex(dir, DataSourceMode.Auto);
                var r2 = await new Tools.ToolInvoker(new Tools.ToolCatalog(auto)).InvokeAsync("search_symbols", Tree.Args(new { query = "A" }), CancellationToken.None);
                Assert.True(r2.IsError);
                Assert.Contains("no_index", r2.Text);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void StaleDiscoveryFile_IsIgnored()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-mcp-stale-" + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = StoragePaths.ForSolution(dir);
                new EngineDiscoveryFile { Pipe = "uetovs-dead", Pid = 999999, ProtocolVersion = 1, SolutionDir = dir, StartedUtc = DateTime.UtcNow }.Write(paths);
                using var conn = new EngineConnection(dir);
                Assert.Null(conn.GetAsync(CancellationToken.None).GetAwaiter().GetResult());
                Assert.Contains("stale", conn.LastError);

                EngineDiscoveryFile.DeleteIfOwned(paths, 123); // not ours: kept
                Assert.True(File.Exists(EngineDiscoveryFile.PathFor(paths)));
                EngineDiscoveryFile.DeleteIfOwned(paths, 999999);
                Assert.False(File.Exists(EngineDiscoveryFile.PathFor(paths)));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
