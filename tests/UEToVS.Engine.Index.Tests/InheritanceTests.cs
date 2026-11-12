using System;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Index.Storage;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    /// <summary>Base classes are stored per symbol and inverted into a base-name → derived table (format 2).</summary>
    public class InheritanceTests
    {
        private static DeclInput C(string name, string container = null, params string[] bases)
        {
            var d = T.D(name, SymbolKind.Class, container, def: true);
            d.Bases = bases.Length == 0 ? null : bases;
            return d;
        }

        private static FileSymbols[] Files() => new[]
        {
            T.F("/ue/Engine/Actor.h", ItemOrigin.Engine, new[] { C("AActor", null, "UObject"), T.D("Tick", SymbolKind.Method, "AActor") }, null, module: "Engine"),
            T.F("/ue/Engine/Pawn.h", ItemOrigin.Engine, new[] { C("APawn", null, "AActor", "INavAgentInterface") }, null, module: "Engine"),
            T.F("/proj/Game/MyPawn.h", ItemOrigin.Project, new[]
            {
                C("AMyPawn", null, "APawn", "TSharedFromThis<AMyPawn>"),
                C("FInner", "Game::Detail", "UE::Math::TBase<int, TArray<float>>"),
            }, null),
        };

        [Fact]
        public void BaseNames_Simple_StripsTemplatesAndNamespaces()
        {
            Assert.Equal("ACharacter", BaseNames.Simple("ACharacter"));
            Assert.Equal("TSharedFromThis", BaseNames.Simple("TSharedFromThis<FFoo>"));
            Assert.Equal("TBase", BaseNames.Simple("UE::Math::TBase<int, TArray<float>>"));
            Assert.Equal("UE::Math::TBase", BaseNames.StripTemplateArgs(" ::UE::Math::TBase < A<B> > "));
            Assert.Null(BaseNames.Simple("  "));
        }

        [Fact]
        public void Segment_RoundTripsBasesAndDerivedTable()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            idx.ReplaceBase(SegmentKind.Engine, Files().Take(2));
            idx.ReplaceBase(SegmentKind.Project, Files().Skip(2));
            AssertTables(idx);
        }

        [Fact]
        public void Delta_ServesBasesAndDerived()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            foreach (var f in Files()) idx.Update(f);
            Assert.Equal(3, idx.Stats.DeltaFileCount);
            AssertTables(idx);
        }

        [Fact]
        public void Compaction_AndReopen_KeepBases()
        {
            using var dir = new TempDir();
            using (var idx = T.Open(dir.Path))
            {
                idx.ReplaceBase(SegmentKind.Engine, Files().Take(2));
                foreach (var f in Files().Skip(2)) idx.Update(f);
                Assert.True(idx.Compact());
                Assert.Equal(0, idx.Stats.DeltaFileCount);
                AssertTables(idx);

                // a second compaction re-exports the bases from the segment (SegmentExport) and must keep them
                idx.Update(T.F("/proj/Game/Other.h", ItemOrigin.Project, new[] { C("AOther", null, "AMyPawn") }, null));
                Assert.True(idx.Compact());
                AssertTables(idx);
                Assert.Equal("AOther", Assert.Single(idx.FindDerived("AMyPawn")).Symbol.Name);
            }
            using (var idx = T.Open(dir.Path))
            {
                AssertTables(idx);
                Assert.Equal("AOther", Assert.Single(idx.FindDerived("AMyPawn")).Symbol.Name);
            }
        }

        [Fact]
        public void UpdatedFile_ReplacesItsBases()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            idx.ReplaceBase(SegmentKind.Project, Files());
            // AMyPawn now derives from ACharacter: the segment copy is tombstoned, the delta copy wins
            idx.Update(T.F("/proj/Game/MyPawn.h", ItemOrigin.Project, new[] { C("AMyPawn", null, "ACharacter") }, null));
            Assert.DoesNotContain(idx.FindDerived("APawn"), t => t.Symbol.Name == "AMyPawn");
            Assert.Equal("AMyPawn", Assert.Single(idx.FindDerived("ACharacter")).Symbol.Name);
            Assert.Equal(new[] { "ACharacter" }, Assert.Single(idx.FindTypes("AMyPawn")).Bases);
            idx.Remove("/proj/Game/MyPawn.h");
            Assert.Empty(idx.FindDerived("ACharacter"));
        }

        [Fact]
        public void OldFormatSegment_IsRejectedForRebuild()
        {
            using var dir = new TempDir();
            using (var idx = T.Open(dir.Path)) idx.ReplaceBase(SegmentKind.Project, Files());
            var seg = Directory.GetFiles(dir.Path, "*.seg").Single();
            var bytes = File.ReadAllBytes(seg);
            BitConverter.TryWriteBytes(bytes.AsSpan(SegmentFormat.OffVersion), 1u); // a format-1 segment (no inheritance sections)
            File.WriteAllBytes(seg, bytes);
            using var reopened = SymbolIndex.Open(T.Options(dir.Path), out var result);
            var r = Assert.Single(result.Segments);
            Assert.Equal(SegmentLoadStatus.FormatVersionMismatch, r.Status);
            Assert.True(result.NeedsProjectRebuild);
            Assert.Empty(reopened.FindTypes("AMyPawn"));
        }

        private static void AssertTables(SymbolIndex idx)
        {
            var pawn = Assert.Single(idx.FindTypes("AMyPawn"));
            Assert.Equal(new[] { "APawn", "TSharedFromThis<AMyPawn>" }, pawn.Bases);
            Assert.Equal(new[] { "AActor", "INavAgentInterface" }, Assert.Single(idx.FindTypes("APawn")).Bases);
            Assert.Equal(new[] { "UObject" }, Assert.Single(idx.FindTypes("AActor")).Bases);
            Assert.Empty(idx.FindTypes("Tick")); // methods are not types

            var inner = Assert.Single(idx.FindTypes("FInner", "Detail"));
            Assert.Equal("Game::Detail", inner.Symbol.Container);
            Assert.Equal(new[] { "UE::Math::TBase<int, TArray<float>>" }, inner.Bases);

            Assert.Equal("APawn", Assert.Single(idx.FindDerived("AActor")).Symbol.Name);
            Assert.Equal("AMyPawn", Assert.Single(idx.FindDerived("APawn")).Symbol.Name);
            Assert.Equal("AMyPawn", Assert.Single(idx.FindDerived("TSharedFromThis")).Symbol.Name);
            Assert.Equal("FInner", Assert.Single(idx.FindDerived("TBase")).Symbol.Name);
            Assert.Equal("APawn", Assert.Single(idx.FindDerived("INavAgentInterface")).Symbol.Name);
            Assert.Empty(idx.FindDerived("AMyPawnX"));
            Assert.Empty(idx.FindDerived(null));
        }
    }
}
