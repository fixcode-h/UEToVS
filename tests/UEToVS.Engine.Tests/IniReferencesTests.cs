using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Rpc;
using Xunit;

namespace UEToVS.Engine.Tests
{
    public class IniReferencesTests
    {
        [Theory]
        [InlineData("MaxPlayers=4", "MaxPlayers")]
        [InlineData("  +Maps=/Game/A", "Maps")]
        [InlineData("-Maps=/Game/B", "Maps")]
        [InlineData(".Maps=x", "Maps")]
        [InlineData("!Maps=ClearArray", "Maps")]
        [InlineData("Slots[2]=3", "Slots")]
        [InlineData("; MaxPlayers=4", null)]
        [InlineData("[/Script/MyGame.MySettings]", null)]
        [InlineData("NoValue", null)]
        public void KeyOfStripsOperatorsAndIndexes(string line, string key) => Assert.Equal(key, IniReferences.KeyOf(line));

        [Fact]
        public void ScriptClassAndConfigSpecifiers()
        {
            Assert.Equal("MySettings", IniReferences.ScriptClass("[/Script/MyGame.MySettings]"));
            Assert.Equal("MySettings", IniReferences.ScriptClass("/Script/MyGame.MySettings"));
            Assert.Null(IniReferences.ScriptClass("[Core.Log]"));
            Assert.True(IniReferences.IsConfigProperty("EditAnywhere, Config"));
            Assert.True(IniReferences.IsConfigProperty("GlobalConfig"));
            Assert.False(IniReferences.IsConfigProperty("EditAnywhere, Category=Config"));
        }

        [Fact]
        public void FindsKeysOnlyUnderTheClassSections()
        {
            string[] lines =
            {
                "[/Script/MyGame.MySettings]", "MaxPlayers=4", "Other=1", "",
                "[/Script/MyGame.Unrelated]", "MaxPlayers=9",
                "[/Script/MyGame.MyChildSettings]", "; MaxPlayers=0", "+MaxPlayers=8",
            };
            var file = new IniFileData { Path = "D:/G/Config/DefaultGame.ini" };
            foreach (var name in new[] { "/Script/MyGame.MySettings", "/Script/MyGame.Unrelated", "/Script/MyGame.MyChildSettings" })
            {
                var s = new IniSectionData(name);
                s.Keys["MaxPlayers"] = "x";
                file.Sections.Add(s);
            }
            var hits = IniReferences.Find(new[] { file }, new[] { "MySettings", "MyChildSettings" }, "MaxPlayers", 10, _ => lines);
            Assert.Equal(new[] { 1, 8 }, hits.Select(h => h.Location.Line));
            Assert.Equal(1, hits[1].Location.Column);
            Assert.Equal("/Script/MyGame.MyChildSettings", hits[1].Context);
            Assert.All(hits, h => Assert.Equal(ReferenceKind.Write, h.Kind));
            Assert.Single(IniReferences.Find(new[] { file }, new[] { "MySettings" }, "MaxPlayers", 1, _ => lines));
        }

        [Fact]
        public async Task FindReferencesOfAConfigPropertyListsIniLines()
        {
            using var tree = new FakeUnrealTree();
            File.WriteAllText(tree.P("Game/Source/MyGame/Public/MySettings.h"),
                "#pragma once\n#include \"MySettings.generated.h\"\n\n" +
                "UCLASS(config=Game)\nclass MYGAME_API UMySettings : public UObject\n{\n\tGENERATED_BODY()\npublic:\n" +
                "\tUPROPERTY(Config)\n\tint32 MaxPlayers = 2;\n};\n\n" +
                "UCLASS(config=Game)\nclass MYGAME_API UMyChildSettings : public UMySettings\n{\n\tGENERATED_BODY()\n};\n");
            File.WriteAllText(tree.P("Game/Config/DefaultGame.ini"),
                "[/Script/MyGame.MySettings]\nMaxPlayers=4\n\n[/Script/MyGame.Unrelated]\nMaxPlayers=9\n\n[/Script/MyGame.MyChildSettings]\n+MaxPlayers=8\n");

            var options = EngineOptions.Parse(new[] { "--status", "--solution-dir", tree.ProjectRoot, "--project", tree.ProjectFile, "--no-watch", "--skip-engine" }, out var error);
            Assert.Null(error);
            using var host = new EngineHost(options);
            _ = host.StartIndexing();
            Assert.True(await host.Index.WaitForReadyAsync(CancellationToken.None));

            var service = new EngineService(host, 1);
            string header = tree.P("Game/Source/MyGame/Public/MySettings.h");
            var r = await service.FindReferencesAsync(new ReferencesRequest { Caret = new CaretRequest { FilePath = header, Line = 9, Column = 8 } }, CancellationToken.None);
            Assert.Equal("MaxPlayers", r.Symbol?.Name);
            var ini = r.Hits.Where(h => h.Location.FilePath.EndsWith("DefaultGame.ini", StringComparison.OrdinalIgnoreCase)).OrderBy(h => h.Location.Line).ToList();
            Assert.Equal(new[] { 1, 7 }, ini.Select(h => h.Location.Line));
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
    }
}
