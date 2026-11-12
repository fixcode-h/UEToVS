using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.IntelliSense;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// String context completion (design §7.6) on the IntelliSense corpus (Game/Private/TagUser.cpp, Config/*.ini, the
    /// fixture's assets): gameplay tags, asset paths, UFUNCTION names, ini sections / keys, and plain strings that must stay quiet.
    /// </summary>
    public sealed class StringCompletionTests : IClassFixture<IntelliSenseFixture>
    {
        private readonly IntelliSenseFixture _f;
        private readonly ITestOutputHelper _out;

        public StringCompletionTests(IntelliSenseFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        private CompletionResponse Complete(string id)
        {
            var r = _f.Service.GetCompletions(new CompletionRequest { Caret = _f.Caret(id) }, CancellationToken.None);
            _out.WriteLine($"{id}: [{r.Context}/{r.ResolvedType}] " + string.Join(", ", r.Items.Take(20).Select(i => i.Label + "=" + i.InsertText)));
            return r;
        }

        private static List<string> Labels(CompletionResponse r) => r.Items.Select(i => i.Label).ToList();

        // ================================================================== gameplay tags

        [Fact]
        public void GameplayTag_RootsFromIniAndCode()
        {
            var r = Complete("tag.root");
            Assert.Equal(CompletionContextKind.GameplayTag, r.Context);
            var labels = Labels(r);
            foreach (var root in new[] { "Ability", "Status", "Combat", "Local" }) Assert.Contains(root, labels);
            Assert.DoesNotContain("Fire", labels); // second level only after "Ability."
            Assert.Equal(SymbolKind.Namespace, r.Items.First(i => i.Label == "Ability").Kind);
        }

        [Fact]
        public void GameplayTag_NextLevelWithDevComment()
        {
            var r = Complete("tag.child");
            var labels = Labels(r);
            Assert.Equal(new[] { "Block", "Dash", "Fire", "Jump" }, labels.OrderBy(x => x).ToArray());
            var fire = r.Items.First(i => i.Label == "Fire");
            Assert.Equal("Primary fire", fire.Detail);
            Assert.Equal("Fire", fire.InsertText);
            Assert.Equal(SymbolKind.Namespace, fire.Kind); // Ability.Fire.Charged below it
            Assert.Equal("Short dash", r.Items.First(i => i.Label == "Dash").Detail);
            Assert.Equal("Combat.ini", r.Items.First(i => i.Label == "Dash").Container);
            // no comment: the full tag on the right
            Assert.Equal("Ability.Jump", r.Items.First(i => i.Label == "Jump").Detail);
            Assert.Equal(SymbolKind.EnumValue, r.Items.First(i => i.Label == "Jump").Kind);
            // the replace range is the segment after the last '.' (empty here)
            Assert.Equal(_f.Caret("tag.child").Column, r.ReplaceColumn);
        }

        [Fact]
        public void GameplayTag_PrefixReplacesSegment()
        {
            var r = Complete("tag.prefix");
            Assert.Equal("Fire", r.Items[0].Label);
            Assert.DoesNotContain("Jump", Labels(r));
            Assert.Equal(_f.Caret("tag.prefix").Column - 2, r.ReplaceColumn);
            Assert.Equal(2, r.ReplaceLength);
        }

        [Fact]
        public void GameplayTag_NativeTagsFromOtherFilesAndThisDocument()
        {
            var r = Complete("tag.native");
            var stunned = r.Items.First(i => i.Label == "Stunned");
            Assert.Equal("Cannot act", stunned.Detail);
            Assert.Equal("NativeTags.cpp", stunned.Container);
            Assert.Contains("Burning", Labels(r));
            Assert.Contains("OnlyHere", Labels(Complete2("Local.")));
            Assert.Contains("Local", Labels(Complete("tag.doc")));
        }

        /// <summary>Completion in an edited (unsaved) copy of TagUser.cpp with a new RequestGameplayTag literal.</summary>
        private CompletionResponse Complete2(string tagTyped)
        {
            string path = _f.PathOf("IS/Game/Private/TagUser.cpp");
            string text = System.IO.File.ReadAllText(path);
            string line = "\tFGameplayTag X = RequestGameplayTag(TEXT(\"" + tagTyped;
            text = text.Replace("\tFString S;", line + "\n\tFString S;");
            int lineNo = text.Substring(0, text.IndexOf(line, StringComparison.Ordinal)).Count(ch => ch == '\n');
            return _f.Service.GetCompletions(new CompletionRequest
            {
                Caret = new CaretRequest { FilePath = path, Text = text, TextVersion = 9000 + tagTyped.Length, Line = lineNo, Column = line.Length },
            }, CancellationToken.None);
        }

        [Fact]
        public void GameplayTag_UnterminatedLiteralWhileTyping()
        {
            var r = Complete2("Ability.Fire.");
            Assert.Equal(CompletionContextKind.GameplayTag, r.Context);
            Assert.Equal(new[] { "Charged" }, Labels(r).ToArray());
            Assert.Equal("Hold to charge", r.Items[0].Detail);
        }

        [Fact]
        public void GameplayTag_MetaCategories()
        {
            string path = _f.PathOf("IS/Game/Public/TagUser.h");
            string text = System.IO.File.ReadAllText(path);
            string line = "\tUPROPERTY(EditAnywhere, meta = (Categories = \"Combat.";
            text = text.Replace("\tvoid OnPlainHelper();", line + "\"))\n\tFGameplayTag Filter;\n\tvoid OnPlainHelper();");
            int lineNo = text.Substring(0, text.IndexOf(line, StringComparison.Ordinal)).Count(ch => ch == '\n');
            var r = _f.Service.GetCompletions(new CompletionRequest
            {
                Caret = new CaretRequest { FilePath = path, Text = text, TextVersion = 4242, Line = lineNo, Column = line.Length },
            }, CancellationToken.None);
            Assert.Equal(CompletionContextKind.GameplayTag, r.Context);
            Assert.Equal(new[] { "Hit" }, Labels(r).ToArray());
        }

        // ================================================================== asset paths

        [Fact]
        public void AssetPath_FinderFiltersByClassConvention()
        {
            var r = Complete("asset.mesh");
            Assert.Equal(CompletionContextKind.AssetPath, r.Context);
            var labels = Labels(r);
            // static meshes (SM_) first, then the rest of the folder; the subfolder is listed too
            Assert.Equal(new[] { "SM_Crate", "SM_Rock" }, labels.Take(2).OrderBy(x => x).ToArray());
            Assert.Contains("Decals", labels);
            Assert.Contains("BP_Door", labels);
            var rock = r.Items.First(i => i.Label == "SM_Rock");
            Assert.Equal("SM_Rock.SM_Rock", rock.InsertText);
            Assert.Equal("/Game/Props/SM_Rock.SM_Rock", rock.Detail);
            Assert.Equal("Decals", r.Items.First(i => i.Label == "Decals").InsertText);
            Assert.DoesNotContain("T_Dirt", labels); // not in this folder
        }

        [Fact]
        public void AssetPath_ClassFinderAppendsGeneratedClassSuffix()
        {
            var r = Complete("asset.class");
            var door = r.Items.First(i => i.Label == "BP_Door");
            Assert.Equal("BP_Door.BP_Door_C", door.InsertText);
            Assert.Equal("BP_Door", r.Items.First(i => i.Kind == SymbolKind.Unknown).Label); // blueprint naming ranks first
        }

        [Fact]
        public void AssetPath_MountsFoldersAndMaps()
        {
            var mounts = Labels(Complete("asset.mount"));
            Assert.Contains("Game", mounts);
            Assert.Contains("MyPlugin", mounts);
            Assert.Equal("Game", mounts[0]);

            var folders = Labels(Complete("asset.folders"));
            Assert.Equal(new[] { "Characters", "Maps", "Props" }, folders.OrderBy(x => x).ToArray());

            var world = Complete("asset.world");
            Assert.Equal(new[] { "L_Arena" }, Labels(world).ToArray()); // UWorld: maps only
            Assert.Equal("World", world.Items[0].Detail);
        }

        [Fact]
        public void AssetPath_AnyStringStartingWithAMount()
        {
            var r = Complete("asset.anywhere");
            Assert.Equal(CompletionContextKind.AssetPath, r.Context);
            Assert.Equal(new[] { "SM_Crate", "SM_Rock" }, Labels(r).Take(2).OrderBy(x => x).ToArray());
            Assert.Empty(Complete("none.unixpath").Items); // "/usr/…" is not a mount point
        }

        // ================================================================== UFUNCTION names

        [Fact]
        public void UFunction_ThisClassOnlyReflectedFunctions()
        {
            var r = Complete("ufn.this");
            Assert.Equal(CompletionContextKind.UFunctionName, r.Context);
            var labels = Labels(r);
            Assert.Contains("OnTagAdded", labels);
            Assert.Contains("OnTagRemoved", labels);
            Assert.DoesNotContain("OnPlainHelper", labels);
            Assert.Equal("ATagUser", r.ResolvedType);
            Assert.Equal("void OnTagAdded(int32 Count)", r.Items.First(i => i.Label == "OnTagAdded").Detail);
        }

        [Fact]
        public void UFunction_TargetArgumentAndReceiverTypes()
        {
            var other = Complete("ufn.other");
            Assert.Equal("ATagUser", other.ResolvedType);
            Assert.Contains("OnTagAdded", Labels(other));

            var listener = Complete("ufn.listener");
            Assert.Equal("ATagListener", listener.ResolvedType);
            Assert.Equal("HandleSignal", listener.Items[0].Label);
            Assert.DoesNotContain("OnTagAdded", Labels(listener));

            var find = Complete("ufn.find");
            Assert.Equal("ATagListener", find.ResolvedType);
            Assert.Contains("HandleSignal", Labels(find));
        }

        // ================================================================== ini

        [Fact]
        public void Ini_SectionsOfTheNamedConfigFile()
        {
            var r = Complete("ini.section");
            Assert.Equal(CompletionContextKind.IniKey, r.Context);
            var labels = Labels(r);
            Assert.Equal("/Script/MyGame.GadgetSettings", labels[0]);
            Assert.DoesNotContain("SystemSettings", labels); // DefaultEngine.ini, not GGameIni
            // the whole literal is replaced
            Assert.Equal(_f.Caret("ini.section").Column - "/Script/MyGame".Length, r.ReplaceColumn);
        }

        [Fact]
        public void Ini_KeysOfTheSection()
        {
            var r = Complete("ini.key");
            var labels = Labels(r);
            Assert.Equal(new[] { "AllowedModes", "bUseCharges", "MaxCharges" }, labels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
            Assert.Equal("= 5", r.Items.First(i => i.Label == "MaxCharges").Detail);
            Assert.Equal("DefaultGame.ini", r.Items[0].Container);

            var dotted = Complete("ini.dotkey");
            Assert.Equal(new[] { "r.Shadow.Quality" }, Labels(dotted).ToArray());
        }

        // ================================================================== plain strings stay quiet

        [Theory]
        [InlineData("none.log")]
        [InlineData("none.fname")]
        [InlineData("none.string")]
        public void PlainStrings_NoCompletion(string id)
        {
            var r = Complete(id);
            Assert.Empty(r.Items);
        }

        // ================================================================== catalogs

        [Fact]
        public void Catalog_IniParserAndTagTree()
        {
            var data = ConfigCatalog.Parse("/p/Config/DefaultGameplayTags.ini",
                "[S]\n+GameplayTagList=(Tag=\"A.B.C\",DevComment=\"deep\")\n; comment\n-Removed=1\n.Dot=2\n");
            Assert.Single(data.Tags);
            Assert.Equal("deep", data.Tags[0].Comment);
            Assert.True(data.Sections[0].Keys.ContainsKey("Removed"));
            Assert.True(data.Sections[0].Keys.ContainsKey("Dot"));

            var tree = GameplayTagCatalog.Build(data.Tags);
            Assert.Equal("A", tree[string.Empty].Single().Segment);
            Assert.True(tree[string.Empty][0].HasChildren);
            Assert.False(tree[string.Empty][0].Declared);
            Assert.Equal("C", tree["A.B"].Single().Segment);
            Assert.True(tree["a.b"].Single().Declared); // case-insensitive parents

            var code = GameplayTagCatalog.Extract("UE_DEFINE_GAMEPLAY_TAG_STATIC(TAG_X, TEXT(\"X.Y\"));\nUE_DEFINE_GAMEPLAY_TAG_COMMENT(T, \"P.Q\", \"why\");", "T.cpp");
            Assert.Equal(new[] { "X.Y", "P.Q" }, code.Select(t => t.Tag).ToArray());
            Assert.Equal("why", code[1].Comment);
            Assert.Null(code[0].Comment);
        }

        [Fact]
        public void Catalog_ReReadsChangedIniAndSkipsEngineFiles()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uetovs-ini-" + Guid.NewGuid().ToString("N"), "Config");
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string game = System.IO.Path.Combine(dir, "DefaultGame.ini");
                string engine = System.IO.Path.Combine(dir, "BaseEngine.ini");
                System.IO.File.WriteAllText(game, "[A]\nX=1\n");
                System.IO.File.WriteAllText(engine, "[E]\nY=1\n");
                var table = new UEToVS.Engine.Indexing.FileTable();
                table.Upsert(new UEToVS.Engine.Unreal.ListedFile { Path = game, Name = "DefaultGame.ini", Origin = ItemOrigin.Project, Kind = UEToVS.Engine.Unreal.SourceFileKind.Config });
                table.Upsert(new UEToVS.Engine.Unreal.ListedFile { Path = engine, Name = "BaseEngine.ini", Origin = ItemOrigin.Engine, Kind = UEToVS.Engine.Unreal.SourceFileKind.Config });
                var catalog = new ConfigCatalog { RecheckMs = 0 };
                var files = catalog.Files(table);
                Assert.Single(files);
                Assert.Equal("A", files[0].Sections[0].Name);
                int v = catalog.Version;
                Assert.Same(files, catalog.Files(table)); // unchanged: same parse
                System.IO.File.WriteAllText(game, "[A]\nX=1\n[B]\nZ=2\n");
                System.IO.File.SetLastWriteTimeUtc(game, DateTime.UtcNow.AddMinutes(1));
                files = catalog.Files(table);
                Assert.Equal(2, files[0].Sections.Count);
                Assert.True(catalog.Version > v);
            }
            finally
            {
                System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(dir), true);
            }
        }

        [Fact]
        public void AssetPath_LargeTableStaysWithinBudget()
        {
            // 300 000 assets in 3 000 folders: folder tree built once, each request scans the compact snapshot once
            var assets = new UEToVS.Engine.Indexing.AssetTable();
            for (int d = 0; d < 3000; d++)
                for (int a = 0; a < 100; a++)
                {
                    string dir = "/Game/Area" + (d / 100) + "/Set" + d;
                    string name = (a % 3 == 0 ? "SM_" : "T_") + "Item" + a;
                    assets.Upsert(new AssetItem { Name = name, ObjectPath = dir + "/" + name + "." + name, FilePath = "/p/Content" + dir + "/" + name + ".uasset", Origin = ItemOrigin.Project });
                }
            var service = new IntelliSenseService(_f.Backend, _f.Files, assets: assets);
            string path = _f.PathOf("IS/Game/Private/TagUser.cpp");
            string baseText = System.IO.File.ReadAllText(path);
            var times = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                string line = "\tUStaticMesh* M = LoadObject<UStaticMesh>(nullptr, TEXT(\"/Game/Area" + (i * 37 % 3000 / 100) + "/Set" + (i * 37 % 3000) + "/SM_";
                string text = baseText.Replace("\tFString S;", line + "\n\tFString S;");
                int lineNo = text.Substring(0, text.IndexOf(line, StringComparison.Ordinal)).Count(ch => ch == '\n');
                var r = service.GetCompletions(new CompletionRequest
                {
                    Caret = new CaretRequest { FilePath = path, Text = text, TextVersion = 70000 + i, Line = lineNo, Column = line.Length },
                }, CancellationToken.None);
                if (i > 0) times.Add(r.ElapsedMs);
                Assert.Equal(34, r.Items.Count); // SM_Item0, 3, 6 … 99
            }
            times.Sort();
            double p95 = times[(int)(times.Count * 0.95) - 1];
            _out.WriteLine($"asset completion p95 {p95} ms (max {times[^1]})");
            Assert.True(p95 < 50, "p95 " + p95);
        }

        [Fact]
        public void Logging_CarriesStringContextAndCandidates()
        {
            using var sink = new CapturingLogSink();
            Complete("tag.child");
            var e = sink.Events.LastOrDefault(x => x.Event == "is.completion" && x.Data.Any(kv => kv.Key == "context" && "gameplayTag".Equals(kv.Value)));
            Assert.NotNull(e);
            object Get(string key) => e.Data.First(kv => kv.Key == key).Value;
            Assert.Equal("gameplayTag", Get("context"));
            Assert.True(Convert.ToInt32(Get("candidates")) >= 4);
        }
    }
}
