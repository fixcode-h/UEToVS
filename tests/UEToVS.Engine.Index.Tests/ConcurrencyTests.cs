using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    public class ConcurrencyTests
    {
        [Fact]
        public async Task ConcurrentReaders_DuringUpdatesAndCompaction()
        {
            using var dir = new TempDir();
            using var idx = T.Open(dir.Path);
            idx.ReplaceBase(SegmentKind.Engine, Enumerable.Range(0, 200).Select(i =>
                T.F($"/ue/E{i}.h", ItemOrigin.Engine, T.D("UStableEngine" + i), T.D("Tick", SymbolKind.Method, "UStableEngine" + i))));
            idx.ReplaceBase(SegmentKind.Project, Enumerable.Range(0, 200).Select(i =>
                T.F($"/p/P{i}.h", ItemOrigin.Project, new[] { T.D("AStableProject" + i) }, new[] { T.O("Tick", 10) })));

            var errors = new ConcurrentQueue<Exception>();
            int searches = 0;
            using var stop = new CancellationTokenSource();
            var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        // Each snapshot is internally consistent: stable symbols are always visible exactly once.
                        using var s = idx.AcquireSnapshot();
                        var hits = s.Search(new SearchRequest { Pattern = "AStableProject17", IncludeNonProject = true });
                        if (!hits.Hits.Any(h => h.Symbol?.Name == "AStableProject17")) throw new Exception("stable project symbol missing");
                        if (s.FindByName("UStableEngine5").Count != 1) throw new Exception("stable engine symbol count wrong");
                        if (s.FindByName("AStableProject3").Count != 1) throw new Exception("stable project symbol count wrong");
                        int occ = s.FindOccurrences("Tick").Count();
                        if (occ < 200) throw new Exception("occurrences missing: " + occ);
                        Interlocked.Increment(ref searches);
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                        return;
                    }
                }
            })).ToArray();

            // keep writing until the readers have overlapped with plenty of updates and compactions
            int rounds = 0;
            for (int round = 0; round < 6 || (Volatile.Read(ref searches) < 100 && round < 500 && errors.IsEmpty); round++)
            {
                rounds++;
                for (int i = 0; i < 40; i++)
                {
                    int k = (round % 6) * 40 + i;
                    idx.Update(T.F($"/p/P{k % 200}.h", ItemOrigin.Project,
                        new[] { T.D("AStableProject" + (k % 200)), T.D("AChurn" + k) }, new[] { T.O("Tick", 10) }));
                }
                idx.Compact();
            }
            stop.Cancel();
            await Task.WhenAll(readers);

            Assert.True(errors.IsEmpty, string.Join("\n", errors.Select(e => e.ToString())));
            Assert.True(searches >= 100, $"searches={searches} rounds={rounds}");
            // only the live segments remain on disk
            Assert.Equal(2, Directory.GetFiles(dir.Path, "*.seg").Length);
            Assert.Equal(200, idx.FindByName("Tick").Count);
            Assert.Equal(200, idx.FindOccurrences("Tick").Count());
        }
    }
}
