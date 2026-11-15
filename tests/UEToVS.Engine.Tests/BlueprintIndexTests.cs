using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Unreal;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>Offline .uasset reading (summary, name table, asset registry tags) and the Blueprint index built on it.</summary>
    public sealed class BlueprintIndexTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "uetovs-bp-" + Guid.NewGuid().ToString("N"));

        public BlueprintIndexTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); }
            catch (IOException) { } // temp folder: leftovers are harmless
        }

        /// <summary>
        /// Writes a package header like UE writes it: legacy version -7 (UE 4.26/4.27), -8 with UE5 version 1009 (5.1–5.3)
        /// or 1016 (5.4+, saved hash before the custom versions); then the name table and an asset registry block.
        /// </summary>
        public static byte[] Package(int legacy, int ue5, IList<string> names, IDictionary<string, string> tags, bool unicodeName = false)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            void FStr(string s, bool unicode = false)
            {
                if (unicode)
                {
                    w.Write(-(s.Length + 1));
                    w.Write(Encoding.Unicode.GetBytes(s + "\0"));
                }
                else
                {
                    w.Write(s.Length + 1);
                    w.Write(Encoding.ASCII.GetBytes(s));
                    w.Write((byte)0);
                }
            }
            w.Write(UassetReader.PackageTag);
            w.Write(legacy);
            w.Write(864);      // LegacyUE3Version
            w.Write(522);      // FileVersionUE4
            if (legacy <= -8) w.Write(ue5);
            w.Write(0);        // licensee
            long totalHeaderPos;
            if (ue5 >= 1016)
            {
                w.Write(new byte[20]);
                totalHeaderPos = ms.Position;
                w.Write(0);
            }
            else totalHeaderPos = -1;
            w.Write(2);        // custom versions
            w.Write(new byte[40]);
            if (ue5 < 1016)
            {
                totalHeaderPos = ms.Position;
                w.Write(0);
            }
            FStr("/Game/Test");
            w.Write(0u);       // package flags
            w.Write(names.Count);
            long nameOffsetPos = ms.Position;
            w.Write(0);
            w.Write(new byte[64]); // the rest of the summary (not read)

            long nameOffset = ms.Position;
            foreach (var n in names)
            {
                FStr(n, unicodeName && n.Any(c => c > 127));
                w.Write(0);    // hashes
            }
            // asset registry data: object path, class, tag count, key / value pairs
            w.Write(0L);
            w.Write(1);
            FStr("/Game/Test.Test");
            FStr("Blueprint");
            w.Write(tags.Count);
            foreach (var kv in tags)
            {
                FStr(kv.Key);
                FStr(kv.Value);
            }
            long total = ms.Position;
            w.Write(new byte[128]); // export data after the header
            ms.Position = nameOffsetPos;
            w.Write((int)nameOffset);
            ms.Position = totalHeaderPos;
            w.Write((int)total);
            return ms.ToArray();
        }

        private static readonly Dictionary<string, string> HeroTags = new Dictionary<string, string>
        {
            ["ParentClass"] = "/Script/CoreUObject.Class'/Script/LyraGame.LyraCharacter'",
            ["NativeParentClass"] = "/Script/CoreUObject.Class'/Script/LyraGame.LyraCharacter'",
            ["GeneratedClass"] = "/Script/Engine.BlueprintGeneratedClass'/Game/B_Hero.B_Hero_C'",
        };

        [Theory]
        [InlineData(-7, 0)]
        [InlineData(-8, 1009)]
        [InlineData(-8, 1016)]
        public void ReadsNamesAndTagsAcrossVersions(int legacy, int ue5)
        {
            var bytes = Package(legacy, ue5, new[] { "ParentClass", "LyraCharacter", "K2_Jump", "None", "/Script/LyraGame", "Health" }, HeroTags);
            var info = UassetReader.Parse(bytes);
            Assert.Null(info.NamesError);
            Assert.Equal(6, info.Names.Length);
            Assert.Contains("K2_Jump", info.Names);
            Assert.Equal(522, info.FileVersionUE4);
            Assert.Equal(ue5, info.FileVersionUE5);
            // "ParentClass" in the name table must not be mistaken for the tag
            Assert.Equal("/Script/LyraGame.LyraCharacter", info.ParentClass);
            Assert.Equal("/Script/LyraGame.LyraCharacter", info.NativeParentClass);
            Assert.Equal("/Game/B_Hero.B_Hero_C", info.GeneratedClass);
            Assert.True(info.IsBlueprint);
        }

        [Fact]
        public void ReadsUtf16NamesAndSurvivesGarbage()
        {
            var info = UassetReader.Parse(Package(-8, 1009, new[] { "Größe", "Health" }, new Dictionary<string, string>(), unicodeName: true));
            Assert.Equal(new[] { "Größe", "Health" }, info.Names);
            Assert.False(info.IsBlueprint);

            Assert.Equal("not a package", UassetReader.Parse(new byte[] { 1, 2, 3, 4, 5 }).NamesError);
            var truncated = Package(-8, 1009, new[] { "A", "B" }, HeroTags).Take(60).ToArray();
            var t = UassetReader.Parse(truncated);
            Assert.NotNull(t.NamesError);
            Assert.Empty(t.Names);
        }

        [Theory]
        [InlineData("/Script/CoreUObject.Class'/Script/LyraGame.LyraCharacter'", "/Script/LyraGame.LyraCharacter", "LyraCharacter", "LyraGame")]
        [InlineData("Class'/Script/Engine.Actor'", "/Script/Engine.Actor", "Actor", "Engine")]
        [InlineData("/Game/B_Base.B_Base_C", "/Game/B_Base.B_Base_C", "B_Base", null)]
        [InlineData("None", null, null, null)]
        public void ObjectPathsAndClassNames(string tag, string path, string cls, string module)
        {
            Assert.Equal(path, UassetReader.ObjectPathOf(tag));
            Assert.Equal(cls, UassetReader.ClassName(path));
            Assert.Equal(module, UassetReader.NativeModule(path));
        }

        [Theory]
        [InlineData("ALyraCharacter", "LyraCharacter")]
        [InlineData("UHealthComponent", "HealthComponent")]
        [InlineData("FVector", "Vector")]
        [InlineData("Apple", "Apple")]
        [InlineData("ns::AFoo", "Foo")]
        public void UeClassNames(string cpp, string ue) => Assert.Equal(ue, BlueprintIndex.UeClassName(cpp));

        private string Write(string name, byte[] bytes)
        {
            string p = Path.Combine(_dir, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        private BlueprintIndex BuildProject(out string hero, out string child, out string map, out string texture)
        {
            hero = Write("B_Hero.uasset", Package(-8, 1009, new[] { "LyraCharacter", "K2_Jump", "Health" }, HeroTags));
            child = Write("B_HeroChild.uasset", Package(-8, 1009, new[] { "B_Hero_C", "Health" }, new Dictionary<string, string>
            {
                ["ParentClass"] = "/Script/Engine.BlueprintGeneratedClass'/Game/B_Hero.B_Hero_C'",
                ["NativeParentClass"] = "/Script/CoreUObject.Class'/Script/LyraGame.LyraCharacter'",
                ["GeneratedClass"] = "/Script/Engine.BlueprintGeneratedClass'/Game/B_HeroChild.B_HeroChild_C'",
            }));
            map = Write("L_Test.umap", Package(-8, 1009, new[] { "LyraCharacter", "K2_Jump" }, new Dictionary<string, string>()));
            texture = Write("T_Noise.uasset", Package(-8, 1009, new[] { "K2_Jump", "LyraCharacter" }, new Dictionary<string, string>()));
            var index = new BlueprintIndex();
            int read = index.Refresh(new[] { (hero, "/Game/B_Hero.B_Hero"), (child, "/Game/B_HeroChild.B_HeroChild"), (map, (string)null), (texture, (string)null) }, CancellationToken.None);
            Assert.Equal(4, read);
            return index;
        }

        [Fact]
        public void DerivedBlueprintsFormATree()
        {
            var index = BuildProject(out var hero, out var child, out _, out _);
            Assert.Equal(3, index.SearchableCount); // texture is remembered but not searchable
            var tree = index.Derived(new[] { "LyraCharacter" });
            var root = Assert.Single(tree);
            Assert.Equal(hero, root.Entry.FilePath);
            Assert.Equal(child, Assert.Single(root.Children).Entry.FilePath);
            Assert.Equal(2, BlueprintIndex.CountTree(tree));
            Assert.Empty(index.Derived(new[] { "Pawn" }));
        }

        [Fact]
        public void UsagesNeedTheMemberAndItsClass()
        {
            var index = BuildProject(out var hero, out var child, out var map, out _);
            var jump = index.Usages("K2_Jump", new[] { "LyraCharacter" }).Select(e => e.FilePath).ToList();
            Assert.Equal(new[] { hero, map }.OrderBy(x => x), jump.OrderBy(x => x));
            // the child has "Health" but not the class name in its table: found through its native parent
            var health = index.Usages("Health", new[] { "LyraCharacter" }).Select(e => e.FilePath).ToList();
            Assert.Contains(child, health);
            Assert.Empty(index.Usages("K2_Jump", new[] { "OtherClass" }));
            Assert.Equal(2, index.Usages("K2_Jump", null).Count);
        }

        [Fact]
        public void SavesLoadsAndRereadsOnlyChangedFiles()
        {
            var index = BuildProject(out var hero, out var child, out var map, out var texture);
            string file = BlueprintIndex.DefaultPath(_dir);
            index.Save(file);
            var loaded = new BlueprintIndex();
            Assert.True(loaded.Load(file));
            Assert.Equal(4, loaded.Count);
            Assert.Equal(3, loaded.SearchableCount);
            Assert.Single(loaded.Derived(new[] { "LyraCharacter" }));

            var assets = new[] { (hero, "/Game/B_Hero.B_Hero"), (child, "/Game/B_HeroChild.B_HeroChild"), (map, (string)null), (texture, (string)null) };
            Assert.Equal(0, loaded.Refresh(assets, CancellationToken.None));
            File.WriteAllBytes(map, Package(-8, 1009, new[] { "Something" }, new Dictionary<string, string>()));
            File.SetLastWriteTimeUtc(map, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(1, loaded.Refresh(assets, CancellationToken.None));
            Assert.DoesNotContain(loaded.Usages("K2_Jump", null), e => e.FilePath == map);
            Assert.Equal(0, loaded.Refresh(assets.Take(2), CancellationToken.None)); // removed files drop out
            Assert.Equal(2, loaded.Count);

            Assert.False(new BlueprintIndex().Load(Path.Combine(_dir, "missing.gz")));
        }
    }
}

namespace UEToVS.Engine.Tests
{
    using System.Threading.Tasks;
    using UEToVS.Contracts;
    using UEToVS.Engine.Rpc;

    /// <summary>The Blueprint index behind the RPC: Find References and the type hierarchy list Blueprints.</summary>
    public sealed class BlueprintRpcTests
    {
        [Fact]
        public async Task FindReferencesAndHierarchyIncludeBlueprints()
        {
            using var tree = new FakeUnrealTree();
            File.WriteAllBytes(tree.P("Game/Content/Characters/BP_Hero.uasset"), BlueprintIndexTests.Package(-8, 1009,
                new[] { "MyCharacter", "Fire", "K2Node_CallFunction" }, new Dictionary<string, string>
                {
                    ["ParentClass"] = "/Script/CoreUObject.Class'/Script/MyGame.MyCharacter'",
                    ["NativeParentClass"] = "/Script/CoreUObject.Class'/Script/MyGame.MyCharacter'",
                    ["GeneratedClass"] = "/Script/Engine.BlueprintGeneratedClass'/Game/Characters/BP_Hero.BP_Hero_C'",
                }));
            File.WriteAllBytes(tree.P("Game/Content/Maps/L_Main.umap"), BlueprintIndexTests.Package(-8, 1009,
                new[] { "MyCharacter", "Health" }, new Dictionary<string, string>()));

            var options = EngineOptions.Parse(new[] { "--status", "--solution-dir", tree.ProjectRoot, "--project", tree.ProjectFile, "--no-watch", "--skip-engine" }, out var error);
            Assert.Null(error);
            using var host = new EngineHost(options);
            _ = host.StartIndexing();
            Assert.True(await host.Index.WaitForReadyAsync(CancellationToken.None));
            await host.Blueprints.Schedule(TimeSpan.Zero);
            Assert.Equal(2, host.Blueprints.Index.SearchableCount);
            Assert.True(File.Exists(BlueprintIndex.DefaultPath(host.Paths.Root)));

            var service = new EngineService(host, 1);
            string header = tree.P("Game/Source/MyGame/Public/MyCharacter.h");
            var fire = await service.FindReferencesAsync(new ReferencesRequest { Caret = new CaretRequest { FilePath = header, Line = 10, Column = 7 } }, CancellationToken.None);
            Assert.Equal("Fire", fire.Symbol?.Name);
            var bp = Assert.Single(fire.Hits, h => (h.Kind & ReferenceKind.Blueprint) != 0);
            Assert.EndsWith("BP_Hero.uasset", bp.Location.FilePath);
            Assert.Contains("BP_Hero", bp.LineText);
            Assert.True((bp.Kind & ReferenceKind.Call) != 0);

            var health = await service.FindReferencesAsync(new ReferencesRequest { Caret = new CaretRequest { FilePath = header, Line = 13, Column = 8 } }, CancellationToken.None);
            Assert.Equal("Health", health.Symbol?.Name);
            Assert.EndsWith("L_Main.umap", Assert.Single(health.Hits, h => (h.Kind & ReferenceKind.Blueprint) != 0).Location.FilePath);

            var hierarchy = await service.GetTypeHierarchyAsync(new TypeHierarchyRequest { TypeName = "AMyCharacter", Direction = HierarchyDirection.Derived }, CancellationToken.None);
            var node = Assert.Single(hierarchy.Derived, n => n.Asset != null);
            Assert.Equal("BP_Hero", node.DisplayName);
            Assert.Equal("/Game/Characters/BP_Hero.BP_Hero", node.Asset.ObjectPath);

            // Code Vision: counts at the end of the reflected declaration lines
            var hints = await service.GetInlayHintsAsync(new InlayHintsRequest
            {
                Document = new CaretRequest { FilePath = header }, StartLine = 0, EndLine = 20, ParameterNames = false, CodeVision = true,
            }, CancellationToken.None);
            var usages = hints.Items.Where(h => h.Kind == InlayHintKind.Usages).ToDictionary(h => h.Line);
            // drawn on a row above the reflection macro (UCLASS line 4, UFUNCTION line 9, UPROPERTY line 12), at its indentation
            Assert.Equal(1, usages[4].Blueprints);                 // class AMyCharacter: BP_Hero derives from it
            Assert.Equal("AMyCharacter", usages[4].SymbolName);
            Assert.Equal(0, usages[4].Column);
            Assert.Equal(1, usages[9].Blueprints);                 // Fire: used in BP_Hero
            Assert.Equal("AMyCharacter::Fire", usages[9].SymbolName);
            Assert.Equal(1, usages[9].Column);
            Assert.Equal(1, usages[12].Blueprints);                // Health: used in L_Main
            var off = await service.GetInlayHintsAsync(new InlayHintsRequest { Document = new CaretRequest { FilePath = header }, StartLine = 0, EndLine = 20 }, CancellationToken.None);
            Assert.DoesNotContain(off.Items, h => h.Kind == InlayHintKind.Usages);
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
    }

    public sealed class CodeVisionAnchorTests
    {
        [Theory]
        [InlineData(new[] { "UCLASS()", "class A {" }, 1, 0)]
        [InlineData(new[] { "UCLASS(Blueprintable,", "  meta=(X))", "class A {" }, 2, 0)]
        [InlineData(new[] { "\tUFUNCTION(BlueprintCallable)", "\t// comment", "\tvoid F();" }, 2, 2)]
        [InlineData(new[] { "\tUPROPERTY()", "\tint32 A;", "\tint32 B;" }, 2, 2)]
        [InlineData(new[] { "class A {" }, 0, 0)]
        [InlineData(new[] { "UCLASSX()", "class A {" }, 1, 1)]
        public void AnchorsOnTheReflectionMacro(string[] lines, int decl, int expected) =>
            Assert.Equal(expected, UEToVS.Engine.Rpc.EngineService.CodeVisionAnchor(lines, decl));
    }
}
