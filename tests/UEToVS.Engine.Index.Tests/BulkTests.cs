using System.IO;
using System.Linq;
using UEToVS.Contracts;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    /// <summary>Bulk operations used by the indexing pipeline: append, merge, attach (shared), reset, separate engine folder.</summary>
    public class BulkTests
    {
        private static IndexOptions Split(string root) => new IndexOptions
        {
            Directory = Path.Combine(root, "project"),
            EngineDirectory = Path.Combine(root, "engine"),
            KnowledgeBaseVersion = "kb1",
            EngineBuildId = "ue",
        };

        [Fact]
        public void AppendSegment_WritesEngineSegmentsInTheEngineFolder_AndReopens()
        {
            using var dir = new TempDir();
            using (var idx = SymbolIndex.Open(Split(dir.Path), out _))
            {
                idx.AppendSegment(SegmentKind.Engine, new[] { T.F("/ue/Core/A.h", ItemOrigin.Engine, T.D("FA")) });
                idx.AppendSegment(SegmentKind.Engine, new[] { T.F("/ue/Core/B.h", ItemOrigin.Engine, T.D("FB")) });
                idx.AppendSegment(SegmentKind.Project, new[] { T.F("/p/Src/G.h", ItemOrigin.Project, T.D("AG")) });
                Assert.Equal(2, idx.BaseSegmentCount(SegmentKind.Engine));
            }
            Assert.Equal(2, Directory.GetFiles(Path.Combine(dir.Path, "engine"), "*.seg").Length);
            Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "project"), "*.seg"));
            Assert.Contains("../engine/", File.ReadAllText(Path.Combine(dir.Path, "project", "manifest.json")).Replace('\\', '/'));
            using var again = SymbolIndex.Open(Split(dir.Path), out var r);
            Assert.False(r.NeedsEngineRebuild);
            Assert.Single(again.FindByName("FA"));
            Assert.Single(again.FindByName("FB"));
            Assert.Single(again.FindByName("AG"));
            var states = again.BaseFileStates(SegmentKind.Engine, includeDead: false);
            Assert.Equal(2, states.Count);
            Assert.All(states, s => Assert.True(s.MtimeTicks > 0 && s.Length > 0));
        }

        [Fact]
        public void AppendSegment_SupersedesOlderCopies_ButNotDeltaUpdates()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            idx.AppendSegment(SegmentKind.Engine, new[] { T.F("/ue/A.h", ItemOrigin.Engine, T.D("FOld")), T.F("/ue/B.h", ItemOrigin.Engine, T.D("FB")) });
            idx.Update(T.F("/ue/B.h", ItemOrigin.Engine, T.D("FBDelta")));
            idx.AppendSegment(SegmentKind.Engine, new[] { T.F("/ue/A.h", ItemOrigin.Engine, T.D("FNew")), T.F("/ue/B.h", ItemOrigin.Engine, T.D("FBStale")) });
            Assert.Empty(idx.FindByName("FOld"));
            Assert.Single(idx.FindByName("FNew"));
            Assert.Single(idx.FindByName("FBDelta")); // the delta is at least as new as a bulk batch
            Assert.Empty(idx.FindByName("FBStale"));
            Assert.Equal(2, idx.Stats.FileCount);
        }

        [Fact]
        public void PrepareSegment_InParallel_PublishInOrder_LaterWins_AndDiscardDeletesTheFile()
        {
            using var dir = new TempDir();
            using (var idx = SymbolIndex.Open(Split(dir.Path), out _))
            {
                var prepared = new PreparedSegment[4];
                System.Threading.Tasks.Parallel.For(0, 4, i =>
                    prepared[i] = idx.PrepareSegment(SegmentKind.Engine, new[]
                    {
                        T.F($"/ue/P{i}.h", ItemOrigin.Engine, T.D("P" + i)),
                        T.F("/ue/Shared.h", ItemOrigin.Engine, T.D("Shared" + i)),
                    }));
                Assert.Equal(0, idx.BaseSegmentCount(SegmentKind.Engine)); // nothing visible before publishing
                for (int i = 0; i < 3; i++) idx.PublishSegment(prepared[i]);
                string discarded = Path.Combine(dir.Path, "engine", prepared[3].FileName);
                Assert.True(File.Exists(discarded));
                prepared[3].Dispose();
                Assert.False(File.Exists(discarded));
                Assert.Throws<System.InvalidOperationException>(() => idx.PublishSegment(prepared[0])); // published once only
                Assert.Single(idx.FindByName("P2"));
                Assert.Empty(idx.FindByName("P3"));
                Assert.Single(idx.FindByName("Shared2")); // the last published copy wins
                Assert.Empty(idx.FindByName("Shared0"));
                Assert.Empty(idx.FindByName("Shared1"));
            }
            using var again = SymbolIndex.Open(Split(dir.Path), out var r);
            Assert.False(r.NeedsEngineRebuild);
            Assert.Single(again.FindByName("P0"));
            Assert.Single(again.FindByName("Shared2"));
            Assert.Empty(again.FindByName("Shared0"));
        }

        [Fact]
        public void MergeBases_KeepsLiveFilesOnly()
        {
            using var dir = new TempDir();
            using var idx = SymbolIndex.Open(Split(dir.Path), out _);
            for (int i = 0; i < 5; i++)
                idx.AppendSegment(SegmentKind.Engine, new[] { T.F($"/ue/F{i}.h", ItemOrigin.Engine, T.D("F" + i)) });
            idx.AppendSegment(SegmentKind.Engine, new[] { T.F("/ue/F0.h", ItemOrigin.Engine, T.D("F0b")) });
            Assert.True(idx.MergeBases(SegmentKind.Engine));
            Assert.Equal(1, idx.BaseSegmentCount(SegmentKind.Engine));
            Assert.Empty(idx.FindByName("F0"));
            Assert.Single(idx.FindByName("F0b"));
            Assert.Equal(5, idx.Stats.FileCount);
            Assert.False(idx.MergeBases(SegmentKind.Engine));
        }

        [Fact]
        public void AttachExternal_UsesFilesInPlace_AndNeverDeletesThem()
        {
            using var dir = new TempDir();
            string shared = Path.Combine(dir.Path, "shared");
            Directory.CreateDirectory(shared);
            string builder = Path.Combine(dir.Path, "builder");
            using (var b = SymbolIndex.Open(Split(builder), out _))
                b.AppendSegment(SegmentKind.Engine, new[] { T.F("/ue/A.h", ItemOrigin.Engine, T.D("FA")) });
            string src = Directory.GetFiles(Path.Combine(builder, "engine"), "*.seg").Single();
            string external = Path.Combine(shared, "engine-0000.seg");
            File.Copy(src, external);

            string projectRoot = Path.Combine(dir.Path, "p");
            using (var idx = SymbolIndex.Open(Split(projectRoot), out _))
            {
                idx.AppendSegment(SegmentKind.Engine, new[] { T.F("/ue/Local.h", ItemOrigin.Engine, T.D("FLocal")) });
                Assert.True(idx.AttachExternalSegments(SegmentKind.Engine, new[] { external }, out _));
                Assert.True(idx.AllBasesExternal(SegmentKind.Engine));
                Assert.Single(idx.FindByName("FA"));
                Assert.Empty(idx.FindByName("FLocal")); // replaced
            }
            Assert.Empty(Directory.GetFiles(Path.Combine(projectRoot, "engine"), "*.seg")); // own copy deleted once unused
            using (var idx = SymbolIndex.Open(Split(projectRoot), out var r))
            {
                Assert.False(r.NeedsEngineRebuild); // manifest references the shared file by absolute path
                Assert.Single(idx.FindByName("FA"));
                idx.Reset(SegmentKind.Engine);
                Assert.Empty(idx.FindByName("FA"));
            }
            Assert.True(File.Exists(external)); // shared files are never deleted by a project
        }

        [Fact]
        public void Reset_ProjectKeepsEngine_EngineKeepsDelta()
        {
            using var dir = new TempDir();
            using var idx = SymbolIndex.Open(Split(dir.Path), out _);
            idx.AppendSegment(SegmentKind.Engine, new[] { T.F("/ue/A.h", ItemOrigin.Engine, T.D("FA")) });
            idx.AppendSegment(SegmentKind.Project, new[] { T.F("/p/G.h", ItemOrigin.Project, T.D("AG")) });
            idx.Update(T.F("/p/H.h", ItemOrigin.Project, T.D("AH")));
            idx.Reset(SegmentKind.Engine);
            Assert.Empty(idx.FindByName("FA"));
            Assert.Single(idx.FindByName("AG"));
            Assert.Single(idx.FindByName("AH"));
            idx.Reset(SegmentKind.Project);
            Assert.Empty(idx.FindByName("AG"));
            Assert.Empty(idx.FindByName("AH"));
        }
    }
}
