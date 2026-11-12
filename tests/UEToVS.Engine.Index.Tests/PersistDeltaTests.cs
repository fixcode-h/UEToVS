using System.IO;
using System.Linq;
using UEToVS.Contracts;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    /// <summary>Shutdown flush: the delta goes to disk as an extra project segment without rewriting the project segment.</summary>
    public class PersistDeltaTests
    {
        private static SymbolIndex Build(string dir) => IndexTests.BuildIndex(dir);

        [Fact]
        public void PersistDelta_WritesOnlyTheDelta_AndReadOnlyReadersSeeIt()
        {
            using var dir = new TempDir();
            string before;
            using (var idx = Build(dir.Path))
            {
                var project = Directory.GetFiles(dir.Path, "project-*.seg").Single();
                idx.Update(T.F("/proj/Source/Game/MyActor.h", ItemOrigin.Project, T.D("AMyActorEdited")));
                idx.Update(T.F("/proj/Source/Game/NewThing.h", ItemOrigin.Project, T.D("ANewThing")));
                before = IndexTests.Fingerprint(idx);

                Assert.Equal(2, idx.PersistDelta());
                Assert.Equal(0, idx.Stats.DeltaFileCount);
                Assert.Equal(before, IndexTests.Fingerprint(idx));
                Assert.True(File.Exists(project)); // the big segment was not rewritten
                Assert.Equal(2, Directory.GetFiles(dir.Path, "project-*.seg").Length);
                Assert.Equal(0, idx.PersistDelta()); // nothing left

                // what UEToVS.Mcp opens while the engine still runs
                using var ro = ReadOnlyIndex.TryOpen(dir.Path, out _);
                Assert.Single(ro.FindByName("AMyActorEdited"));
                Assert.Single(ro.FindByName("ANewThing"));
                Assert.Empty(ro.FindByName("AMyActor"));
            }
            using (var idx = T.Open(dir.Path))
            {
                Assert.Equal(before, IndexTests.Fingerprint(idx));
                Assert.True(idx.Compact()); // merges the extra segment back
                Assert.Single(Directory.GetFiles(dir.Path, "project-*.seg"));
                Assert.Single(idx.FindByName("AMyActorEdited"));
                Assert.Single(idx.FindByName("ANewThing"));
                Assert.Empty(idx.FindByName("AMyActor"));
                Assert.Equal(before, IndexTests.Fingerprint(idx));
            }
        }

        [Fact]
        public void PersistDelta_Cancelled_KeepsTheDelta()
        {
            using var dir = new TempDir();
            using var idx = Build(dir.Path);
            idx.Update(T.F("/proj/Source/Game/Late.h", ItemOrigin.Project, T.D("ALate")));
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<System.OperationCanceledException>(() => idx.PersistDelta(cts.Token));
            Assert.Equal(1, idx.Stats.DeltaFileCount);
            Assert.Single(idx.FindByName("ALate"));
            Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        }
    }
}
