using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Engine.Index;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>Engine stop writes the in-memory delta so the offline index (UEToVS.Mcp) sees the latest edits.</summary>
    public class ShutdownFlushTests
    {
        [Fact]
        public async Task Stop_WritesTheDelta_WithoutRewritingTheProjectSegment()
        {
            using var t = new FakeUnrealTree();
            var h = new EngineHost(new EngineOptions { SolutionDir = t.ProjectRoot, MaxParallelism = 2, WatchEngine = false, NoWatch = true });
            var run = h.StartIndexing();
            Assert.True(await h.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)));
            string projectDir = Path.Combine(h.Paths.Index, "project");
            var segsBefore = Directory.GetFiles(projectDir, "project-*.seg");

            string header = t.P("Game/Source/MyGame/Public/MyCharacter.h");
            File.AppendAllText(header, "\nclass FEditedBeforeClose {};\n");
            var batch = new Vfs.ChangeBatch { Source = "test" };
            batch.Changes.Add(new FileChange { FilePath = header, Kind = FileChangeKind.Changed });
            await h.Index.ApplyChangesAsync(batch, CancellationToken.None);

            await h.StopAsync(TimeSpan.FromSeconds(5));

            // the original project segment(s) are kept; the delta became one more small segment
            Assert.All(segsBefore, s => Assert.True(File.Exists(s)));
            Assert.Equal(segsBefore.Length + 1, Directory.GetFiles(projectDir, "project-*.seg").Length);
            using var ro = ReadOnlyIndex.TryOpen(projectDir, out _);
            Assert.NotNull(ro);
            Assert.Single(ro.FindByName("FEditedBeforeClose"));
        }
    }
}
