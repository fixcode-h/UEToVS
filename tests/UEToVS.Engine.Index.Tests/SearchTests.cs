using System;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    public class SearchTests
    {
        private static SymbolIndex Build(TempDir dir, bool asDelta = false)
        {
            var engine = new[]
            {
                T.F("/ue/Engine/Actor.h", ItemOrigin.Engine,
                    T.D("AActor", SymbolKind.Class, ue: UeFlags.UClass),
                    T.D("Tick", SymbolKind.Method, "AActor"),
                    T.D("ActorComponentHelper", SymbolKind.Function),
                    T.D("UActorComponent", SymbolKind.Class, ue: UeFlags.UClass)),
                T.F("/ue/Engine/ActorComponent.h", ItemOrigin.Engine,
                    T.D("Tick", SymbolKind.Method, "UActorComponent"),
                    T.D("Tick", SymbolKind.Method, "UE::Detail::AActor")),
                T.F("/ue/Engine/Pawn.h", ItemOrigin.Engine,
                    T.D("APawn", SymbolKind.Class, ue: UeFlags.UClass),
                    T.D("Tick", SymbolKind.Method, "APawn")),
                T.F("/ue/Engine/PlayerCamera.h", ItemOrigin.Engine,
                    T.D("PlayerCameraUpdate", SymbolKind.Function),
                    T.D("PlayerCountUpdater", SymbolKind.Function),
                    T.D("UEngineThing", SymbolKind.Class)),
            };
            var project = new[]
            {
                T.F("/proj/Source/Ranking.h", ItemOrigin.Project,
                    T.D("Xxplacamxx", SymbolKind.Class),
                    T.D("PlayerCamera", SymbolKind.Class),
                    T.D("PlaCamera", SymbolKind.Class),
                    T.D("PlaCam", SymbolKind.Class),
                    T.D("UProjectThing", SymbolKind.Class),
                    T.D("ProjectHelper", SymbolKind.Function, ue: UeFlags.UFunction),
                    T.D("ProjectHelperNoUe", SymbolKind.Function)),
                T.F("/proj/Source/RecentA.h", ItemOrigin.Project, T.D("RecentThing", SymbolKind.Function)),
                T.F("/proj/Source/RecentB.h", ItemOrigin.Project, T.D("RecentThing", SymbolKind.Function, container: "Other")),
            };
            var idx = T.Open(dir.Path);
            idx.ReplaceBase(SegmentKind.Engine, engine);
            if (asDelta)
            {
                idx.ReplaceBase(SegmentKind.Project, Array.Empty<FileSymbols>());
                foreach (var f in project) idx.Update(f);
            }
            else
            {
                idx.ReplaceBase(SegmentKind.Project, project);
            }
            return idx;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Ranking_Exact_Prefix_Camel_Substring(bool asDelta)
        {
            using var dir = new TempDir();
            using var idx = Build(dir, asDelta);
            var names = T.Names(idx.Search(T.Req("placam")), ResultCategory.Type);
            Assert.Equal(new[] { "PlaCam", "PlaCamera", "PlayerCamera", "Xxplacamxx" }, names);
        }

        [Fact]
        public void CamelHumps_PCU()
        {
            using var dir = new TempDir();
            using var idx = Build(dir);
            var r = idx.Search(T.Req("PCU"));
            Assert.Equal("PlayerCameraUpdate", T.Names(r, ResultCategory.Symbol).First());
            var hit = r.Hits.First(h => h.Symbol?.Name == "PlayerCameraUpdate");
            Assert.Equal(new[] { 0, 1, 6, 1, 12, 1 }, hit.Highlights);
            Assert.Equal(new[] { "PlayerCameraUpdate", "PlayerCountUpdater" }, T.Names(idx.Search(T.Req("PlCUp")), ResultCategory.Symbol).ToArray().Take(2));
        }

        [Fact]
        public void UePrefix_ActorFindsAActor()
        {
            using var dir = new TempDir();
            using var idx = Build(dir);
            var types = T.Names(idx.Search(T.Req("Actor")), ResultCategory.Type);
            Assert.Equal("AActor", types.First());
            Assert.Contains("UActorComponent", types);
        }

        [Fact]
        public void ShortPatterns_FindSubstringsWhenFewStrongMatches()
        {
            using var dir = new TempDir();
            using var idx = Build(dir);
            // "wn" occurs only mid-word (APawn): found by the fallback scan
            Assert.Contains("APawn", T.Names(idx.Search(T.Req("wn")), ResultCategory.Type));
            Assert.Contains("APawn", T.Names(idx.Search(T.Req("P")), ResultCategory.Type));
        }

        [Fact]
        public void ContainerFilter()
        {
            using var dir = new TempDir();
            using var idx = Build(dir);
            var r = idx.Search(T.Req("AActor::Tick"));
            var syms = r.Hits.Where(h => h.Category == ResultCategory.Symbol).Select(h => h.Symbol).ToList();
            Assert.Equal(2, syms.Count);
            Assert.All(syms, s => Assert.Equal("Tick", s.Name));
            Assert.Equal(new[] { "AActor", "UE::Detail::AActor" }, syms.Select(s => s.Container).OrderBy(x => x));

            var r2 = idx.Search(new SearchRequest { Pattern = "Tick", Container = "APawn", IncludeNonProject = true });
            Assert.Equal(new[] { "APawn" }, r2.Hits.Where(h => h.Category == ResultCategory.Symbol).Select(h => h.Symbol.Container));
        }

        [Fact]
        public void NonProjectFiltering()
        {
            using var dir = new TempDir();
            using var idx = Build(dir);
            var projOnly = idx.Search(T.Req("Thing", nonProject: false));
            var names = T.Names(projOnly, ResultCategory.Type);
            Assert.Contains("UProjectThing", names);
            Assert.DoesNotContain("UEngineThing", names);
            Assert.True(projOnly.NonProjectHitCount >= 1);

            var all = idx.Search(T.Req("Thing", nonProject: true));
            Assert.Contains("UEngineThing", T.Names(all, ResultCategory.Type));
            Assert.Equal(0, all.NonProjectHitCount);
            // project items rank above engine items of the same match quality
            var types = T.Names(all, ResultCategory.Type);
            Assert.True(types.IndexOf("UProjectThing") < types.IndexOf("UEngineThing"));
        }

        [Fact]
        public void RequireUe()
        {
            using var dir = new TempDir();
            using var idx = Build(dir);
            var r = idx.Search(new SearchRequest { Pattern = "ProjectHelper", RequireUe = UeFlags.UFunction, IncludeNonProject = true });
            Assert.Equal(new[] { "ProjectHelper" }, T.Names(r, ResultCategory.Symbol));
        }

        [Fact]
        public void RecentPaths_Boost()
        {
            using var dir = new TempDir();
            using var idx = Build(dir);
            var a = idx.Search(new SearchRequest { Pattern = "RecentThing", IncludeNonProject = true, RecentPaths = new[] { "/proj/Source/RecentB.h" } });
            Assert.Equal("/proj/Source/RecentB.h", a.Hits.First(h => h.Category == ResultCategory.Symbol).Symbol.Location.FilePath);
            var b = idx.Search(new SearchRequest { Pattern = "RecentThing", IncludeNonProject = true, RecentPaths = new[] { @"\PROJ\Source\RecentA.h" } });
            Assert.Equal("/proj/Source/RecentA.h", b.Hits.First(h => h.Category == ResultCategory.Symbol).Symbol.Location.FilePath);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Files(bool asDelta)
        {
            using var dir = new TempDir();
            using var idx = Build(dir, asDelta);
            var r = idx.Search(new SearchRequest { Pattern = "rank", IncludeNonProject = true, IncludeTypes = false, IncludeSymbols = false });
            Assert.Equal(new[] { "Ranking.h" }, T.Names(r, ResultCategory.File));
            Assert.Equal("/proj/Source/Ranking.h", r.Hits[0].File.Path);
            var r2 = idx.Search(new SearchRequest { Pattern = "ActCom", IncludeNonProject = false, IncludeTypes = false, IncludeSymbols = false });
            Assert.Empty(r2.Hits);
            Assert.Equal(1, r2.NonProjectHitCount);
        }

        [Fact]
        public void Wildcards()
        {
            using var dir = new TempDir();
            using var idx = Build(dir);
            var names = T.Names(idx.Search(T.Req("Player*Update")), ResultCategory.Symbol);
            Assert.Contains("PlayerCameraUpdate", names);
            Assert.Contains("PlayerCountUpdater", names);
        }

        [Fact]
        public void MaxResultsPerCategory_AndDedup()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            var files = Enumerable.Range(0, 300).Select(i => T.F($"/p/F{i}.h", ItemOrigin.Project,
                T.D("Widget" + i, SymbolKind.Class),
                T.D("Draw", SymbolKind.Method, "SWidget"))).ToArray();
            idx.ReplaceBase(SegmentKind.Project, files);
            var r = idx.Search(T.Req("Widget", max: 20));
            Assert.Equal(20, r.Hits.Count(h => h.Category == ResultCategory.Type));
            Assert.Equal("Widget0", r.Hits.First(h => h.Category == ResultCategory.Type).Symbol.Name);
            // 300 declarations of SWidget::Draw collapse into one result
            Assert.Single(idx.Search(T.Req("Draw")).Hits, h => h.Category == ResultCategory.Symbol);
        }

        [Fact]
        public void Cancellation()
        {
            using var dir = new TempDir();
            using var idx = Build(dir, asDelta: true);
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => idx.Search(T.Req("Actor"), cts.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => idx.FindByName("Tick", null, cts.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => idx.FindOccurrences("Tick", cts.Token).ToList());
            Assert.ThrowsAny<OperationCanceledException>(() => idx.ListFiles(cts.Token).ToList());
            Assert.ThrowsAny<OperationCanceledException>(() => idx.Compact(cts.Token));
            Assert.Equal(3, idx.Stats.DeltaFileCount); // compaction aborted, delta intact
        }

        [Fact]
        public void EmptyPattern_ReturnsNothing()
        {
            using var dir = new TempDir();
            using var idx = Build(dir);
            var r = idx.Search(T.Req(""));
            Assert.Empty(r.Hits);
            Assert.True(r.IndexReady);
        }
    }
}
