using System;
using System.IO;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Engine.Index.Storage;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    /// <summary>v0.4.5: deprecation markers are a flag bit + a 12-byte entry per deprecated symbol (format 3).</summary>
    public class DeprecationIndexTests
    {
        private static DeclInput Dep(string name, SymbolKind kind, string container, string version, string message, int offset = 0)
        {
            var d = T.D(name, kind, container, offset: offset);
            d.Deprecation = new DeclDeprecation(version, message);
            return d;
        }

        private static FileSymbols[] Files() => new[]
        {
            T.F("/ue/Engine/Actor.h", ItemOrigin.Engine, new[]
            {
                T.D("AActor", SymbolKind.Class),
                Dep("GetActorLabelOld", SymbolKind.Method, "AActor", "5.1", "Use GetActorLabel instead.", 10),
                T.D("GetActorLabel", SymbolKind.Method, "AActor", offset: 20),
                Dep("bOldFlag_DEPRECATED", SymbolKind.Field, "AActor", "5.2", "No longer used.", 30),
            }, null, module: "Engine"),
            T.F("/proj/Game/Hero.cpp", ItemOrigin.Project, new[] { T.D("Run", SymbolKind.Function) },
                new[] { T.O("GetActorLabelOld", 100), T.O("GetActorLabelOld", 200, 4), T.O("GetActorLabel", 300) }),
            T.F("/proj/Plugins/P/Src/Plug.cpp", ItemOrigin.ProjectPlugin, new[] { T.D("Plug", SymbolKind.Function) }, new[] { T.O("GetActorLabelOld", 50) }),
            T.F("/ue/Engine/Other.cpp", ItemOrigin.Engine, new[] { T.D("Other", SymbolKind.Function) }, new[] { T.O("GetActorLabelOld", 70) }, module: "Engine"),
        };

        private static void AssertDeprecations(SymbolIndex idx)
        {
            var list = idx.ListDeprecated().OrderBy(d => d.Symbol.Name, StringComparer.Ordinal).ToList();
            Assert.Equal(2, list.Count);
            Assert.Equal("GetActorLabelOld", list[0].Symbol.Name);
            Assert.Equal("AActor", list[0].Symbol.Container);
            Assert.Equal("5.1", list[0].Version);
            Assert.Equal("Use GetActorLabel instead.", list[0].Message);
            Assert.Equal("bOldFlag_DEPRECATED", list[1].Symbol.Name);
            Assert.Equal("5.2", list[1].Version);

            var project = idx.FindProjectOccurrences("GetActorLabelOld", includePlugins: false);
            Assert.Equal(new[] { 100, 200 }, project.Select(h => h.Offset).ToArray());
            Assert.All(project, h => Assert.EndsWith("Hero.cpp", h.Path));
            var withPlugins = idx.FindProjectOccurrences("GetActorLabelOld", includePlugins: true);
            Assert.Equal(3, withPlugins.Count);
            Assert.DoesNotContain(withPlugins, h => h.Path.EndsWith("Other.cpp", StringComparison.Ordinal));
        }

        [Fact]
        public void Segment_RoundTripsDeprecations()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            idx.ReplaceBase(SegmentKind.Engine, Files().Where(f => f.Origin == ItemOrigin.Engine));
            idx.ReplaceBase(SegmentKind.Project, Files().Where(f => f.Origin != ItemOrigin.Engine));
            AssertDeprecations(idx);
        }

        [Fact]
        public void Delta_ServesDeprecations()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            foreach (var f in Files()) idx.Update(f);
            AssertDeprecations(idx);
        }

        [Fact]
        public void Compaction_AndReopen_KeepDeprecations()
        {
            using var dir = new TempDir();
            using (var idx = T.Open(dir.Path))
            {
                foreach (var f in Files()) idx.Update(f);
                Assert.True(idx.Compact());
                AssertDeprecations(idx);
                // re-export from the segment (SegmentExport) in a second compaction keeps the markers
                idx.Update(T.F("/proj/Game/Extra.h", ItemOrigin.Project, T.D("FExtra", SymbolKind.Struct)));
                Assert.True(idx.Compact());
                AssertDeprecations(idx);
            }
            using (var idx = T.Open(dir.Path)) AssertDeprecations(idx);
        }

        [Fact]
        public void UpdatedFile_DropsOldMarkers()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            idx.ReplaceBase(SegmentKind.Engine, Files().Where(f => f.Origin == ItemOrigin.Engine));
            idx.Update(T.F("/ue/Engine/Actor.h", ItemOrigin.Engine, new[] { T.D("AActor", SymbolKind.Class) }, null, module: "Engine"));
            Assert.Empty(idx.ListDeprecated());
        }

        [Fact]
        public void Format2Segment_IsRejectedForRebuild()
        {
            using var dir = new TempDir();
            using (var idx = T.Open(dir.Path)) idx.ReplaceBase(SegmentKind.Project, Files());
            var seg = Directory.GetFiles(dir.Path, "*.seg").Single();
            var bytes = File.ReadAllBytes(seg);
            BitConverter.TryWriteBytes(bytes.AsSpan(SegmentFormat.OffVersion), 2u);
            File.WriteAllBytes(seg, bytes);
            using var reopened = SymbolIndex.Open(T.Options(dir.Path), out var result);
            Assert.Equal(SegmentLoadStatus.FormatVersionMismatch, Assert.Single(result.Segments).Status);
            Assert.True(result.NeedsProjectRebuild);
        }

        [Fact]
        public void NoDeprecations_CostOnlyAnEmptySection()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            idx.ReplaceBase(SegmentKind.Project, new[] { T.F("/proj/A.h", ItemOrigin.Project, T.D("FA", SymbolKind.Struct)) });
            Assert.Empty(idx.ListDeprecated());
        }
    }
}
