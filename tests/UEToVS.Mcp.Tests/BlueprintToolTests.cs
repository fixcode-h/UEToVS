using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Storage;
using UEToVS.Engine.Indexing;
using UEToVS.Mcp;
using UEToVS.Mcp.Data;
using UEToVS.Mcp.Tools;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>find_blueprint_usages over the engine's blueprints.jsonl.gz (no engine running).</summary>
    public sealed class BlueprintToolTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "uetovs-bp-tool-tests", Guid.NewGuid().ToString("N"));
        private readonly CodeIndex _index;
        private readonly ToolInvoker _invoker;

        public BlueprintToolTests()
        {
            Directory.CreateDirectory(_dir);
            _index = new CodeIndex(_dir, DataSourceMode.IndexOnly);
            _invoker = new ToolInvoker(new ToolCatalog(_index));
        }

        public void Dispose()
        {
            _index.Dispose();
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { } // temp folder
        }

        private static BlueprintEntry Bp(string name, string parent, string native, string[] names, string generated = null) => new BlueprintEntry
        {
            FilePath = "D:/G/Content/" + name + ".uasset", ObjectPath = "/Game/" + name + "." + name, ParentClass = parent, NativeParentClass = native,
            GeneratedClass = generated ?? "/Game/" + name + "." + name + "_C", Names = BlueprintIndex.SortedNames(names),
        };

        private void WriteIndex()
        {
            var index = new BlueprintIndex();
            index.Replace(new[]
            {
                Bp("B_Hero", "/Script/G.MyCharacter", "/Script/G.MyCharacter", new[] { "MyCharacter", "Fire" }),
                Bp("B_HeroChild", "/Game/B_Hero.B_Hero_C", "/Script/G.MyCharacter", new[] { "B_Hero_C" }),
                Bp("W_Hud", "/Script/UMG.UserWidget", "/Script/UMG.UserWidget", new[] { "UserWidget", "MyCharacter", "Health" }),
                new BlueprintEntry { FilePath = "D:/G/Content/L_Main.umap", ObjectPath = "/Game/L_Main.L_Main", Names = BlueprintIndex.SortedNames(new[] { "MyCharacter", "Fire" }) },
            });
            index.Save(BlueprintIndex.DefaultPath(StoragePaths.ForSolution(_dir).Root));
            _index.Blueprints.Invalidate();
        }

        private async Task<JsonElement> Call(object args)
        {
            var r = await _invoker.InvokeAsync("find_blueprint_usages", Tree.Args(args), CancellationToken.None);
            Assert.False(r.IsError, r.Text);
            using var doc = JsonDocument.Parse(r.Text);
            return doc.RootElement.Clone();
        }

        [Fact]
        public async Task NoIndexSaysHowToGetOne()
        {
            var r = await Call(new { @class = "AMyCharacter" });
            Assert.Contains("No Blueprint index", r.GetProperty("note").GetString());
        }

        [Fact]
        public async Task DerivedBlueprintsAndUsers()
        {
            WriteIndex();
            var r = await Call(new { @class = "AMyCharacter" });
            Assert.Equal("MyCharacter", r.GetProperty("class").GetString());
            var items = r.GetProperty("items").EnumerateArray().ToList();
            var hero = items.Single(i => i.GetProperty("name").GetString() == "B_Hero");
            Assert.Equal("derives", hero.GetProperty("relation").GetString());
            Assert.Equal("B_HeroChild", hero.GetProperty("children")[0].GetProperty("name").GetString());
            Assert.Contains(items, i => i.GetProperty("name").GetString() == "W_Hud" && i.GetProperty("relation").GetString() == "usesClass");
            Assert.Contains(items, i => i.GetProperty("name").GetString() == "L_Main" && i.GetProperty("kind").GetString() == "level");
            Assert.DoesNotContain(items, i => i.GetProperty("name").GetString() == "B_HeroChild"); // only under its parent

            var fire = await Call(new { @class = "AMyCharacter::Fire" });
            Assert.Equal("Fire", fire.GetProperty("member").GetString());
            Assert.Equal(new[] { "B_Hero", "L_Main" }, fire.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).OrderBy(x => x));

            var none = await Call(new { @class = "MyCharacter", member = "Jump" });
            Assert.Empty(none.GetProperty("items").EnumerateArray());
            Assert.Contains("No Blueprint or level", none.GetProperty("note").GetString());
        }
    }
}
