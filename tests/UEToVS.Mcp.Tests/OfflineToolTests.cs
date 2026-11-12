using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>Every tool against the index on disk (no engine running): arguments, result format, fallbacks.</summary>
    public sealed class OfflineToolTests : IClassFixture<OfflineFixture>
    {
        private readonly OfflineFixture _f;

        public OfflineToolTests(OfflineFixture f) => _f = f;

        [Fact]
        public async Task IndexStatus_ReportsOnDiskIndex()
        {
            var r = await _f.Call("index_status");
            Assert.Equal("index", r.Str("source"));
            Assert.True(r.Bool("ready"));
            Assert.Equal("Ready", r.Str("phase"));
            Assert.True(r.Int("symbols") >= 10);
            Assert.Equal("5.4.2", r.Str("unrealVersion"));
            Assert.False(r.GetProperty("engine").Bool("running"));
            Assert.Contains("engine.json", r.GetProperty("engine").Str("reason"));
            Assert.True(r.GetProperty("onDisk").Int("segments") >= 1);
            Assert.Contains("not running", r.Str("note"));
        }

        [Fact]
        public async Task SearchSymbols_FuzzyCamelHumps_OneBasedPositions()
        {
            var r = await _f.Call("search_symbols", new { query = "AMC" });
            var top = r.Items().First();
            Assert.Equal("AMyCharacter", top.Str("name"));
            Assert.Equal("class", top.Str("kind"));
            Assert.Equal(6, top.Int("line"));   // "class MYGAME_API AMyCharacter" is the 6th line
            Assert.Equal(18, top.Int("col"));
            Assert.Contains("UCLASS", top.Str("ue"));
            Assert.Equal("Blueprintable", top.Str("spec"));
            Assert.True(top.Bool("def"));
            Assert.False(top.TryGetProperty("origin", out _)); // project = default, omitted
            Assert.Equal("index", r.Str("source"));
        }

        [Fact]
        public async Task SearchSymbols_ExactKindsUeAndScope()
        {
            var exact = await _f.Call("search_symbols", new { query = "Fire", match = "exact" });
            Assert.Equal(2, exact.Int("count"));
            Assert.All(exact.Items(), i => Assert.Equal("AMyCharacter", i.Str("container")));
            Assert.Contains(exact.Items(), i => i.Bool("def") && i.Str("file").EndsWith("MyCharacter.cpp"));

            var qualified = await _f.Call("search_symbols", new { query = "AMyCharacter::Fire", match = "exact" });
            Assert.Equal(2, qualified.Int("count"));

            var fields = await _f.Call("search_symbols", new { query = "Health", kinds = new[] { "field" }, ue = new[] { "UPROPERTY" } });
            Assert.Equal("Health", fields.Items().Single().Str("name"));
            var none = await _f.Call("search_symbols", new { query = "Health", kinds = new[] { "function" } });
            Assert.Equal(0, none.Int("count"));

            // engine types are hidden by default (counted), visible with scope=all
            var project = await _f.Call("search_symbols", new { query = "ACharacter", match = "exact" });
            Assert.Equal(0, project.Int("count"));
            Assert.Equal(1, project.Int("nonProjectHidden"));
            var all = await _f.Call("search_symbols", new { query = "ACharacter", match = "exact", scope = "all" });
            var c = all.Items().Single();
            Assert.Equal("engine", c.Str("origin"));
            Assert.Equal("Engine", c.Str("module"));
        }

        [Fact]
        public async Task SearchSymbols_Paging()
        {
            var page1 = await _f.Call("search_symbols", new { query = "A", scope = "all", limit = 2 });
            Assert.Equal(2, page1.Int("count"));
            Assert.True(page1.Bool("hasMore"));
            Assert.Equal(2, page1.Int("nextOffset"));
            var page2 = await _f.Call("search_symbols", new { query = "A", scope = "all", limit = 2, offset = 2 });
            var names1 = page1.Items().Select(i => i.Str("name") + i.Str("file") + i.Int("line")).ToList();
            var names2 = page2.Items().Select(i => i.Str("name") + i.Str("file") + i.Int("line")).ToList();
            Assert.Empty(names1.Intersect(names2));
        }

        [Theory]
        [InlineData("search_symbols", "{}", "'query' is required")]
        [InlineData("search_symbols", "{\"query\":\"x\",\"limit\":0}", "between 1 and 200")]
        [InlineData("search_symbols", "{\"query\":\"x\",\"limit\":\"lots\"}", "must be an integer")]
        [InlineData("search_symbols", "{\"query\":\"x\",\"kinds\":[\"widget\"]}", "unknown kind")]
        [InlineData("search_symbols", "{\"query\":\"x\",\"ue\":[\"UFOO\"]}", "unknown UE marker")]
        [InlineData("search_symbols", "{\"query\":\"x\",\"match\":\"regex\"}", "must be one of")]
        [InlineData("search_symbols", "{\"query\":\"x\",\"limt\":3}", "unknown argument(s): limt")]
        [InlineData("search_symbols", "{\"query\":42}", "must be a string")]
        [InlineData("go_to_definition", "{}", "either 'symbol' or 'path'")]
        [InlineData("go_to_definition", "{\"symbol\":\"A\",\"path\":\"x.h\",\"line\":1}", "either 'symbol' or 'path'")]
        [InlineData("go_to_definition", "{\"path\":\"x.h\"}", "'line' is required")]
        [InlineData("find_references", "{\"symbol\":\"A\",\"include_declarations\":\"maybe\"}", "true or false")]
        [InlineData("get_type_hierarchy", "{\"type_name\":\"A\",\"direction\":\"up\"}", "must be one of")]
        [InlineData("get_members", "{}", "'type_name' is required")]
        [InlineData("get_file_outline", "{\"path\":\"\"}", "must not be empty")]
        [InlineData("no_such_tool", "{}", "Unknown tool")]
        public async Task InvalidArguments_AreToolErrorsWithActionableMessages(string tool, string json, string expected)
        {
            using var doc = JsonDocument.Parse(json);
            var args = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var r = await _f.Invoker.InvokeAsync(tool, args, default);
            Assert.True(r.IsError, r.Text);
            using var err = JsonDocument.Parse(r.Text);
            Assert.Contains(expected, err.RootElement.GetProperty("message").GetString());
            Assert.NotNull(err.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task SearchFiles_FuzzyAndExtensionFilter()
        {
            var r = await _f.Call("search_files", new { query = "MyHero" });
            Assert.Equal(2, r.Int("count"));
            var h = await _f.Call("search_files", new { query = "MyHero", extensions = new[] { "h" } });
            var only = h.Items().Single();
            Assert.Equal("MyHero.h", only.Str("name"));
            Assert.Equal("MyGame", only.Str("module"));
            Assert.True(Path.IsPathRooted(only.Str("path")));
        }

        [Fact]
        public async Task SearchAssets_ClassFilterAndGuess()
        {
            var r = await _f.Call("search_assets", new { query = "Hero" });
            var bp = r.Items().Single();
            Assert.Equal("BP_Hero", bp.Str("name"));
            Assert.Equal("/Game/Characters/BP_Hero.BP_Hero", bp.Str("objectPath"));
            Assert.Equal("Blueprint", bp.Str("classGuess"));

            var maps = await _f.Call("search_assets", new { query = "Main", @class = "World" });
            Assert.Equal("L_Main", maps.Items().Single().Str("name"));
            Assert.Equal("World", maps.Items().Single().Str("class"));
            var noMat = await _f.Call("search_assets", new { query = "Hero", @class = "Material" });
            Assert.Equal(0, noMat.Int("count"));
            // __ExternalActors__ are never listed
            var ext = await _f.Call("search_assets", new { query = "KCUWB", scope = "all" });
            Assert.Equal(0, ext.Int("count"));
        }

        [Fact]
        public async Task FileOutline_RelativePath_SourceOrder()
        {
            var r = await _f.Call("get_file_outline", new { path = "Source/MyGame/Public/MyCharacter.h" });
            Assert.EndsWith("MyCharacter.h", r.Str("file"));
            var names = r.Items().Select(i => i.Str("name")).ToList();
            Assert.Equal(new[] { "AMyCharacter", "Fire", "Health" }, names);
            Assert.All(r.Items(), i => Assert.False(i.TryGetProperty("file", out _)));
            var lines = r.Items().Select(i => i.Int("line")).ToList();
            Assert.Equal(lines.OrderBy(x => x), lines);

            var methods = await _f.Call("get_file_outline", new { path = "Source/MyGame/Public/MyCharacter.h", kinds = new[] { "method" } });
            Assert.Equal("Fire", methods.Items().Single().Str("name"));

            var missing = await _f.Call("get_file_outline", new { path = "Source/Nope.h" });
            Assert.Equal(0, missing.Int("count"));
            Assert.Contains("does not exist", missing.Str("note"));
        }

        [Fact]
        public async Task GoToDefinition_BySymbol_DefinitionFirst_DeclarationOnRequest()
        {
            var r = await _f.Call("go_to_definition", new { symbol = "AMyCharacter::Fire" });
            var first = r.Items("targets").First();
            Assert.Equal("definition", first.Str("role"));
            Assert.EndsWith("MyCharacter.cpp", first.Str("file"));
            var decl = await _f.Call("go_to_definition", new { symbol = "AMyCharacter::Fire", kind = "declaration" });
            Assert.EndsWith("MyCharacter.h", decl.Items("targets").First().Str("file"));
            var missing = await _f.Call("go_to_definition", new { symbol = "NoSuchThing" });
            Assert.Equal(0, missing.Int("count"));
            Assert.Contains("search_symbols", missing.Str("note"));
        }

        [Fact]
        public async Task GoToDefinition_ByPosition_FallsBackToNameLookup()
        {
            // MyHero.cpp line 5 is "\tFire(10.f);" — column 3 is inside "Fire"
            var r = await _f.Call("go_to_definition", new { path = "Source/MyGame/Private/MyHero.cpp", line = 5, column = 3 });
            Assert.Equal("Fire", r.Str("word"));
            Assert.Equal("index-heuristic", r.Str("source"));
            Assert.Equal("definition", r.Items("targets").First().Str("role"));
            // qualifier is honoured: "AMyHero::Taunt" on line 3
            var q = await _f.Call("go_to_definition", new { path = "Source/MyGame/Private/MyHero.cpp", line = 3, column = 16, kind = "declaration" });
            Assert.Equal("Taunt", q.Str("word"));
            var t = q.Items("targets").First();
            Assert.Equal("AMyHero", t.Str("container"));
            Assert.EndsWith("MyHero.h", t.Str("file"));
        }

        [Fact]
        public async Task FindReferences_FromOccurrenceIndex_GroupedByFile()
        {
            var r = await _f.Call("find_references", new { symbol = "Fire" });
            Assert.Equal("index-heuristic", r.Str("source"));
            var file = r.Items("files").Single();
            Assert.EndsWith("MyHero.cpp", file.Str("file"));
            var hit = file.Items("hits").Single();
            Assert.Equal(5, hit.Int("line"));
            Assert.Equal(2, hit.Int("col"));
            Assert.Equal("call", hit.Str("kind"));
            Assert.Equal("Fire(10.f);", hit.Str("text"));

            var write = await _f.Call("find_references", new { path = "Source/MyGame/Private/MyHero.cpp", line = 7, column = 2 });
            Assert.Equal("write", write.Items("files").Single().Items("hits").Single().Str("kind"));

            // engine-only uses are filtered by scope=project
            var jump = await _f.Call("find_references", new { symbol = "Jump" });
            Assert.Single(jump.Items("files"));
        }

        [Fact]
        public async Task TypeHierarchy_BasesFromSignatures_DerivedFromBaseClauses()
        {
            var r = await _f.Call("get_type_hierarchy", new { type_name = "AMyCharacter", max_depth = 8 });
            Assert.Equal("AMyCharacter", r.GetProperty("root").Str("name"));
            var chain = new List<string>();
            var level = r.GetProperty("bases");
            while (level.GetArrayLength() > 0)
            {
                var n = level[0];
                chain.Add(n.Str("name"));
                if (!n.TryGetProperty("bases", out level)) break;
            }
            Assert.Equal(new[] { "ACharacter", "APawn", "AActor", "UObject" }, chain);
            var derived = r.GetProperty("derived").EnumerateArray().Select(n => n.Str("name")).ToList();
            Assert.Equal(new[] { "AMyHero" }, derived);

            var depth1 = await _f.Call("get_type_hierarchy", new { type_name = "AMyCharacter", direction = "bases", max_depth = 1 });
            Assert.False(depth1.TryGetProperty("derived", out _));
            Assert.True(depth1.GetProperty("bases")[0].Bool("truncated"));
        }

        [Fact]
        public async Task Members_OwnAndInherited_UeFilter()
        {
            var own = await _f.Call("get_members", new { type_name = "AMyCharacter" });
            Assert.Equal(new[] { "Fire", "Health" }, own.Items().Select(i => i.Str("name")).ToArray());
            Assert.Equal("AMyCharacter", own.GetProperty("type").Str("name"));

            var inherited = await _f.Call("get_members", new { type_name = "AMyHero", include_inherited = true });
            var names = inherited.Items().Select(i => i.Str("name")).ToList();
            Assert.Contains("Taunt", names);
            Assert.Contains("Fire", names);
            Assert.Contains("Jump", names);
            Assert.Contains("BeginPlay", names);

            var ufuncs = await _f.Call("get_members", new { type_name = "AMyCharacter", ue = new[] { "UFUNCTION" } });
            Assert.Equal("Fire", ufuncs.Items().Single().Str("name"));
        }
    }
}
