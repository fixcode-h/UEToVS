using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Core.Navigation;
using UEToVS.Core.Query;
using UEToVS.Host.Logic.Link;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Logic.Unreal;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class FeatureStringsTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        [Fact]
        public void EveryFeatureStringExistsInBothLanguages()
        {
            var props = typeof(FeatureStrings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)).ToArray();
            Loc.Language = UiLanguage.Chinese;
            var zh = props.ToDictionary(p => p.Name, p => (string)p.GetValue(null));
            Loc.Language = UiLanguage.English;
            var en = props.ToDictionary(p => p.Name, p => (string)p.GetValue(null));
            Assert.True(zh.Count > 60);
            foreach (var key in zh.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(zh[key]), key);
                Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
                Assert.NotEqual(zh[key], en[key]);
                Assert.DoesNotContain(en[key], c => c >= 0x4E00 && c <= 0x9FFF);
            }
        }
    }

    public class NavigationStateTests
    {
        private static readonly DateTime T = new DateTime(2026, 1, 1);

        [Fact]
        public void JumpThenBackAndForward()
        {
            var s = new NavigationState();
            var a = new CodeLocation("a.cpp", 10, 0);
            var b = new CodeLocation("b.h", 5, 0);
            s.OnCaret(a, T);
            s.RecordJump(null, b, JumpReasons.Search, T);
            using (s.Suppress()) s.OnCaret(b, T); // the jump's own caret move is not recorded twice
            Assert.True(s.History.CanGoBack);
            var back = s.Pick(NavigationStep.Back, b, _ => true);
            Assert.Equal(a, back);
            s.SetLast(back);
            var fwd = s.Pick(NavigationStep.Forward, back, _ => true);
            Assert.Equal(b, fwd);
            Assert.Equal("b.h", s.Recent.Items[0].Path);
        }

        [Fact]
        public void BigCaretMoveIsRecordedSmallIsNot()
        {
            var s = new NavigationState();
            s.Configure(100, 15);
            s.OnCaret(new CodeLocation("a.cpp", 10, 0), T);
            s.OnCaret(new CodeLocation("a.cpp", 12, 0), T);
            Assert.False(s.History.CanGoBack);
            s.OnCaret(new CodeLocation("a.cpp", 200, 0), T);
            Assert.True(s.History.CanGoBack);
        }

        [Fact]
        public void BackSkipsDeletedFiles()
        {
            var s = new NavigationState();
            s.OnCaret(new CodeLocation("gone.cpp", 1, 0), T);
            s.OnCaret(new CodeLocation("kept.cpp", 1, 0), T);
            s.OnCaret(new CodeLocation("cur.cpp", 1, 0), T);
            var target = s.Pick(NavigationStep.Back, new CodeLocation("cur.cpp", 1, 0), p => p != "kept.cpp" ? p != "gone.cpp" : true);
            Assert.Equal("kept.cpp", target.FilePath);
            Assert.Null(s.Pick(NavigationStep.Back, target, p => p != "gone.cpp"));
        }

        [Fact]
        public void EditsAndPersistenceRoundTrip()
        {
            using (var dir = new TempDir())
            {
                var s = new NavigationState();
                s.OnCaret(new CodeLocation("x.cpp", 1, 0), T);
                s.OnEdit(new CodeLocation("x.cpp", 40, 2), "  int a;", T);
                s.OnCaret(new CodeLocation("y.cpp", 1, 0), T);
                s.Save(dir.Path);
                var t = new NavigationState();
                t.Load(dir.Path);
                Assert.Single(t.Edits.Items);
                Assert.True(t.History.CanGoBack);
                Assert.Contains(t.Recent.Items, e => e.Path == "x.cpp" && e.Edited);
                var prev = t.Pick(NavigationStep.PreviousEdit, new CodeLocation("y.cpp", 1, 0), _ => true);
                Assert.Equal(40, prev.Line);
            }
        }
    }

    public class SearchEverywhereModelTests
    {
        private static SearchHit Sym(string name, ResultCategory cat, int score, UeFlags ue = UeFlags.None, ItemOrigin origin = ItemOrigin.Project,
                                     Contracts.SymbolKind kind = Contracts.SymbolKind.Class) =>
            new SearchHit
            {
                Category = cat,
                Score = score,
                Highlights = new[] { 0, 1 },
                Symbol = new SymbolInfo { Name = name, Kind = kind, Ue = ue, Origin = origin, Location = new SourceLocation { FilePath = "/p/" + name + ".h", Line = 9, Column = 6 } },
            };

        private static SearchHit File(string path, int score, ItemOrigin origin = ItemOrigin.Project) =>
            new SearchHit { Category = ResultCategory.File, Score = score, File = new FileItem { Path = path, Name = Path.GetFileName(path), RelativePath = path.TrimStart('/'), Origin = origin } };

        [Fact]
        public void TabsAreInTheRequestedOrder()
        {
            Assert.Equal(new[] { SearchTab.All, SearchTab.Types, SearchTab.Symbols, SearchTab.Files, SearchTab.Assets, SearchTab.Actions }, SearchEverywhereModel.Tabs);
            Assert.Equal(SearchTab.Files, SearchEverywhereModel.TabFromId(SearchEverywhereModel.TabId(SearchTab.Files)));
        }

        [Fact]
        public void PrefixOverridesTabAndTextMapsToAll()
        {
            Assert.Equal(SearchTab.Types, SearchEverywhereModel.EffectiveTab(SearchQuery.Parse("/types Actor"), SearchTab.All));
            Assert.Equal(SearchTab.All, SearchEverywhereModel.EffectiveTab(SearchQuery.Parse("/text foo"), SearchTab.Files));
            var prefixes = SearchEverywhereModel.PrefixRows("/t");
            Assert.Contains(prefixes.Rows, r => r.Title == "/types");
            Assert.DoesNotContain(prefixes.Rows, r => r.Title == "/text");
        }

        [Fact]
        public void RequestFollowsTabAndFileLikeQuery()
        {
            var o = new SearchOptions { RecentPaths = new[] { "/a.h" } };
            var r = SearchEverywhereModel.BuildRequest(SearchQuery.Parse("Foo.cpp"), SearchTab.All, o);
            Assert.True(r.IncludeFiles);
            Assert.False(r.IncludeSymbols);
            Assert.Equal("/a.h", r.RecentPaths[0]);
            var t = SearchEverywhereModel.BuildRequest(SearchQuery.Parse("Tick"), SearchTab.Symbols, new SearchOptions { OnlyReflected = true });
            Assert.False(t.IncludeTypes);
            Assert.Equal(UeFlags.UFunction | UeFlags.UProperty, t.RequireUe);
            Assert.True(SearchEverywhereModel.BuildRequest(SearchQuery.Parse("BP"), SearchTab.Assets, o).IncludeAssets);
        }

        [Fact]
        public void AllTabGroupsByBestScoreAndAddsMore()
        {
            var resp = new SearchResponse { IndexReady = true };
            for (int i = 0; i < 10; i++) resp.Hits.Add(File("/Source/F" + i + ".cpp", 100 - i));
            resp.Hits.Add(Sym("AActor", ResultCategory.Type, 500, UeFlags.UClass));
            var set = SearchEverywhereModel.BuildRows(SearchQuery.Parse("a"), SearchTab.All, resp, new SearchOptions(), null);
            Assert.Equal(SearchRowKind.Header, set.Rows[0].Kind);
            Assert.Equal(SearchTab.Types, set.Rows[0].TargetTab);
            Assert.Equal("UCLASS", set.Rows[1].Badges[0].Key);
            Assert.Equal(SearchRowKind.More, set.Rows.Last().Kind);
            Assert.Equal(SearchEverywhereModel.AllTabGroupSize, set.Rows.Count(r => r.Kind == SearchRowKind.File));
            Assert.Equal(10, set.TabCounts["files"]);
        }

        [Fact]
        public void LineSuffixJumpsToLine()
        {
            var resp = new SearchResponse { IndexReady = true };
            resp.Hits.Add(File("/Source/Foo.cpp", 10));
            var set = SearchEverywhereModel.BuildRows(SearchQuery.Parse("Foo.cpp:120:8"), SearchTab.Files, resp, new SearchOptions(), null);
            Assert.Equal(119, set.Rows[0].Line);
            Assert.Equal(7, set.Rows[0].Column);
        }

        [Fact]
        public void PathFragmentsFilterFiles()
        {
            Assert.True(SearchEverywhereModel.MatchesFragments("Engine/Source/Runtime/Renderer/Private/SceneRendering.cpp", new[] { "Eng", "Ren" }));
            Assert.False(SearchEverywhereModel.MatchesFragments("Engine/Source/Runtime/Core/Foo.cpp", new[] { "Ren" }));
        }

        [Fact]
        public void ReflectedFilterKeepsUeTypesOnly()
        {
            var resp = new SearchResponse { IndexReady = true };
            resp.Hits.Add(Sym("AActor", ResultCategory.Type, 10, UeFlags.UClass));
            resp.Hits.Add(Sym("FPlain", ResultCategory.Type, 10));
            var set = SearchEverywhereModel.BuildRows(SearchQuery.Parse("a"), SearchTab.Types, resp, new SearchOptions { OnlyReflected = true }, null);
            Assert.Single(set.Rows);
            Assert.Equal("AActor", set.Rows[0].Title);
        }

        [Fact]
        public void FallbackAndStatus()
        {
            var o = new SearchOptions();
            var empty = new SearchResponse { IndexReady = false, NonProjectHitCount = 3 };
            Assert.True(SearchEverywhereModel.ShouldFallBackToNonProject(o, empty));
            Assert.False(SearchEverywhereModel.ShouldFallBackToNonProject(new SearchOptions { IncludeNonProject = true }, empty));
            string status = SearchEverywhereModel.StatusFor(empty, o, true, new EngineStatus { Phase = IndexPhase.Parsing, FilesParsed = 1, FilesToParse = 4 });
            Assert.Contains("1/4", status);
        }

        [Fact]
        public void EmptyQueryShowsExistingRecentFiles()
        {
            var set = SearchEverywhereModel.EmptyRows(SearchTab.All, new SearchOptions { RecentPaths = new[] { "/x/a.h", "/x/gone.h" } }, p => p != "/x/gone.h");
            Assert.Equal(2, set.Rows.Count);
            Assert.Equal("a.h", set.Rows[1].Title);
            Assert.Empty(SearchEverywhereModel.EmptyRows(SearchTab.Actions, new SearchOptions { RecentPaths = new[] { "/x/a.h" } }, _ => true).Rows);
        }
    }

    public class IncludeDependencyCheckerTests
    {
        private sealed class FsEnv : IIncludeEnvironment
        {
            private readonly string _root;

            public FsEnv(string root) => _root = root;

            public Task<IReadOnlyList<string>> FindFilesByNameAsync(string fileName, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<string>>(Directory.GetFiles(_root, fileName, SearchOption.AllDirectories));

            public string FindBuildCsForFile(string filePath) => Core.Unreal.ModuleLocator.ForFileSystem().FindBuildCs(filePath);

            public Task<string> FindBuildCsForModuleAsync(string module, CancellationToken ct) =>
                Task.FromResult(Directory.GetFiles(_root, module + ".Build.cs", SearchOption.AllDirectories).FirstOrDefault());

            public string ReadText(string path) => System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;

            public bool FileExists(string path) => System.IO.File.Exists(path);
        }

        private static string BuildCs(string module, string deps) =>
            "using UnrealBuildTool;\npublic class " + module + " : ModuleRules\n{\n    public " + module + "(ReadOnlyTargetRules Target) : base(Target)\n    {\n" +
            "        PublicDependencyModuleNames.AddRange(new string[] { " + deps + " });\n    }\n}\n";

        [Fact]
        public async Task FindsMissingModuleAndFixesBuildCs()
        {
            using (var d = new TempDir())
            {
                string game = d.File("Source/Game/Game.Build.cs", BuildCs("Game", "\"Core\""));
                d.File("Source/Core/Core.Build.cs", BuildCs("Core", ""));
                d.File("Source/Core/Public/CoreMinimal.h");
                d.File("Source/GameplayTags/GameplayTags.Build.cs", BuildCs("GameplayTags", "\"Core\""));
                d.File("Source/GameplayTags/Classes/GameplayTagContainer.h");
                string cpp = d.File("Source/Game/Private/Foo.cpp", "#include \"CoreMinimal.h\"\n#include \"GameplayTagContainer.h\"\n#include \"Foo.generated.h\"\n");

                var includes = IncludeDependencyChecker.ParseIncludes(System.IO.File.ReadAllText(cpp));
                Assert.Equal(3, includes.Count);
                var missing = await IncludeDependencyChecker.CheckAsync(cpp, includes, new FsEnv(d.Path), CancellationToken.None);
                var m = Assert.Single(missing);
                Assert.Equal("GameplayTags", m.Module);
                Assert.Equal("Game", m.OwnerModule);
                Assert.Equal(1, m.Line);

                var (text, added) = IncludeDependencyChecker.ApplyToText(game, System.IO.File.ReadAllText(game), new[] { "GameplayTags" }, false);
                Assert.Equal(new[] { "GameplayTags" }, added);
                Assert.Contains("PrivateDependencyModuleNames", text);
                System.IO.File.WriteAllText(game, text);
                Assert.Empty(await IncludeDependencyChecker.CheckAsync(cpp, includes, new FsEnv(d.Path), CancellationToken.None));
            }
        }

        [Fact]
        public async Task PublicDependencyOfDependencyIsVisible()
        {
            using (var d = new TempDir())
            {
                d.File("Source/Game/Game.Build.cs", BuildCs("Game", "\"Engine\""));
                d.File("Source/Engine/Engine.Build.cs", BuildCs("Engine", "\"CoreUObject\""));
                d.File("Source/CoreUObject/CoreUObject.Build.cs", BuildCs("CoreUObject", ""));
                d.File("Source/CoreUObject/Public/UObject/Object.h");
                string cpp = d.File("Source/Game/Private/Bar.cpp", "#include \"UObject/Object.h\"\n");
                var missing = await IncludeDependencyChecker.CheckAsync(cpp, IncludeDependencyChecker.ParseIncludes(System.IO.File.ReadAllText(cpp)), new FsEnv(d.Path), CancellationToken.None);
                Assert.Empty(missing);
            }
        }
    }

    public class EditorLinkTests
    {
        [Fact]
        public async Task NoPortFileMeansUnreachable()
        {
            using (var d = new TempDir())
            {
                var c = new UeEditorLinkClient(() => d.Path, () => 0);
                Assert.Equal(0, c.ResolvePort());
                var r = await c.PingAsync();
                Assert.False(r.Reached);
            }
        }

        [Fact]
        public async Task TalksJsonLinesWithTheEditor()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string received = null;
            var server = Task.Run(async () =>
            {
                using (var client = await listener.AcceptTcpClientAsync())
                using (var s = client.GetStream())
                using (var r = new StreamReader(s, Encoding.UTF8))
                using (var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true })
                {
                    received = await r.ReadLineAsync();
                    await w.WriteLineAsync("{\"ok\":false,\"message\":\"Live Coding is disabled\"}");
                }
            });
            try
            {
                using (var d = new TempDir())
                {
                    d.File("Saved/UEToVSLink/port.txt", port + "\n");
                    var c = new UeEditorLinkClient(() => d.Path, () => 0);
                    Assert.Equal(port, c.ResolvePort());
                    var reply = await c.LiveCodingAsync();
                    await server;
                    Assert.True(reply.Reached);
                    Assert.False(reply.Ok);
                    Assert.Equal("Live Coding is disabled", reply.Message);
                    Assert.Contains("\"cmd\":\"liveCoding\"", received);
                }
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
