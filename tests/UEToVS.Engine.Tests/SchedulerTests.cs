using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Scheduling;
using UEToVS.Engine.Unreal;
using UEToVS.Engine.Vfs;
using Xunit;

namespace UEToVS.Engine.Tests
{
    public class SchedulerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task RunsInteractiveBeforeVisibleBeforeBackground()
        {
            using var s = new RequestScheduler(1);
            var gate = new ManualResetEventSlim();
            var order = new ConcurrentQueue<string>();
            var blocker = s.RunAsync(WorkPriority.Interactive, "blocker", _ => gate.Wait(Timeout));
            await WaitUntil(() => s.RunningItems().Count == 1);

            var tasks = new List<Task>
            {
                s.RunAsync(WorkPriority.Background, "bg1", _ => order.Enqueue("bg1")),
                s.RunAsync(WorkPriority.Visible, "vis1", _ => order.Enqueue("vis1")),
                s.RunAsync(WorkPriority.Background, "bg2", _ => order.Enqueue("bg2")),
                s.RunAsync(WorkPriority.Interactive, "int1", _ => order.Enqueue("int1")),
                s.RunAsync(WorkPriority.Visible, "vis2", _ => order.Enqueue("vis2")),
                s.RunAsync(WorkPriority.Interactive, "int2", _ => order.Enqueue("int2")),
            };
            Assert.Equal(2, s.QueueLength(WorkPriority.Background));
            Assert.True(s.ShouldYield);
            gate.Set();
            await Task.WhenAll(tasks.Append(blocker)).WaitAsync(Timeout);
            Assert.Equal(new[] { "int1", "int2", "vis1", "vis2", "bg1", "bg2" }, order.ToArray());
        }

        [Fact]
        public async Task CancelledWhileQueuedNeverRuns()
        {
            using var s = new RequestScheduler(1);
            var gate = new ManualResetEventSlim();
            var blocker = s.RunAsync(WorkPriority.Interactive, "blocker", _ => gate.Wait(Timeout));
            await WaitUntil(() => s.RunningItems().Count == 1);
            using var cts = new CancellationTokenSource();
            bool ran = false;
            var t = s.RunAsync(WorkPriority.Interactive, "victim", _ => ran = true, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
            gate.Set();
            await blocker.WaitAsync(Timeout);
            await s.RunAsync(WorkPriority.Interactive, "after", _ => { }).WaitAsync(Timeout);
            Assert.False(ran);

            var pre = new CancellationTokenSource();
            pre.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RunAsync(WorkPriority.Background, "pre", _ => { }, pre.Token));
        }

        [Fact]
        public async Task RunningWorkSeesCancellation()
        {
            using var s = new RequestScheduler(2);
            using var cts = new CancellationTokenSource();
            var started = new ManualResetEventSlim();
            var t = s.RunAsync(WorkPriority.Interactive, "loop", ct =>
            {
                started.Set();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(5);
                }
            }, cts.Token);
            Assert.True(started.Wait(Timeout));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.WaitAsync(Timeout));
        }

        [Fact]
        public async Task BuildInProgressPausesBackgroundOnly()
        {
            using var s = new RequestScheduler(2) { BuildPolicy = BuildPolicy.Pause };
            s.SetBuildInProgress(true);
            Assert.True(s.IsBuildInProgress);
            Assert.True(s.ShouldYield);
            var bg = s.RunAsync(WorkPriority.Background, "bg", _ => 1);
            var fg = s.RunAsync(WorkPriority.Interactive, "fg", _ => 2);
            Assert.Equal(2, await fg.WaitAsync(Timeout));
            await Task.Delay(100);
            Assert.False(bg.IsCompleted);
            s.SetBuildInProgress(false);
            Assert.Equal(1, await bg.WaitAsync(Timeout));
        }

        [Fact]
        public async Task BuildPolicySingleThreadRunsOneBackgroundItemAtATime()
        {
            using var s = new RequestScheduler(4); // background limit 3
            Assert.Equal(BuildPolicy.SingleThread, s.BuildPolicy);
            s.SetBuildInProgress(true);
            Assert.False(s.IsBackgroundPaused);
            int running = 0, maxRunning = 0;
            var tasks = Enumerable.Range(0, 6).Select(i => s.RunAsync(WorkPriority.Background, "bg" + i, _ =>
            {
                int r = Interlocked.Increment(ref running);
                int m;
                while (r > (m = Volatile.Read(ref maxRunning)) && Interlocked.CompareExchange(ref maxRunning, r, m) != m) { }
                Thread.Sleep(30);
                Interlocked.Decrement(ref running);
            })).ToArray();
            await Task.WhenAll(tasks).WaitAsync(Timeout);
            Assert.Equal(1, maxRunning);
        }

        [Fact]
        public async Task BuildPolicyFullSpeedKeepsBackgroundRunning()
        {
            using var s = new RequestScheduler(3) { BuildPolicy = BuildPolicy.FullSpeed };
            s.SetBuildInProgress(true);
            Assert.False(s.ShouldYield);
            Assert.False(s.IsBackgroundPaused);
            Assert.Equal(1, await s.RunAsync(WorkPriority.Background, "bg", _ => 1).WaitAsync(Timeout));
        }

        [Fact]
        public async Task KeepsAWorkerFreeForForegroundWhenBackgroundSaturates()
        {
            using var s = new RequestScheduler(2);
            Assert.Equal(1, s.BackgroundLimit);
            var gate = new ManualResetEventSlim();
            var bgs = Enumerable.Range(0, 3).Select(i => s.RunAsync(WorkPriority.Background, "bg" + i, _ => gate.Wait(Timeout))).ToList();
            await WaitUntil(() => s.RunningItems().Count == 1);
            var fg = s.RunAsync(WorkPriority.Interactive, "fg", _ => 42);
            Assert.Equal(42, await fg.WaitAsync(Timeout)); // not stuck behind the background chunk
            gate.Set();
            await Task.WhenAll(bgs).WaitAsync(Timeout);
        }

        [Fact]
        public async Task FlowsRequestIdAndPropagatesExceptions()
        {
            using var s = new RequestScheduler(1);
            Log.RequestId = "req-42";
            string seen = await s.RunAsync(WorkPriority.Visible, "ctx", _ => Log.RequestId);
            Assert.Equal("req-42", seen);
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.RunAsync<int>(WorkPriority.Interactive, "boom", _ => throw new InvalidOperationException()));
            Assert.Equal(7, await s.RunAsync(WorkPriority.Interactive, "still-alive", _ => 7));
        }

        internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > end) throw new TimeoutException("condition not met");
                await Task.Delay(10);
            }
        }
    }

    public class WatchdogTests
    {
        [Fact]
        public async Task ReportsLongRunningRequestsOnce()
        {
            using var w = new UEToVS.Engine.Health.Watchdog(TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1));
            using (w.Track("rpc.slow"))
            {
                Assert.Equal(1, w.InFlight);
                await Task.Delay(100);
                w.Check();
                w.Check();
                Assert.Equal(1, w.Reported);
            }
            Assert.Equal(0, w.InFlight);
        }
    }

    public class ChangeTrackerTests
    {
        [Fact]
        public async Task CoalescesAndDetectsBatches()
        {
            var batches = new List<ChangeBatch>();
            using var t = new FileChangeTracker(b => { lock (batches) batches.Add(b); return Task.CompletedTask; })
            {
                Debounce = TimeSpan.FromHours(1),
                MaxDelay = TimeSpan.FromHours(1),
                BatchThreshold = 50,
            };
            string a = System.IO.Path.GetFullPath("a.h");
            t.Enqueue(new[] { new FileChange { FilePath = a, Kind = FileChangeKind.Deleted } }, "host");
            t.Enqueue(new[] { new FileChange { FilePath = a, Kind = FileChangeKind.Created } }, "host");
            await t.FlushAsync();
            var single = Assert.Single(batches);
            Assert.False(single.IsBatch);
            Assert.Equal(FileChangeKind.Changed, Assert.Single(single.Changes).Kind); // delete + create = change

            t.Enqueue(Enumerable.Range(0, 120).Select(i => new FileChange { FilePath = System.IO.Path.GetFullPath($"f{i}.h"), Kind = FileChangeKind.Changed }), "watcher");
            await t.FlushAsync();
            Assert.Equal(2, batches.Count);
            Assert.True(batches[1].IsBatch);
            Assert.Equal(120, batches[1].Changes.Count);
            Assert.Equal("watcher", batches[1].Source);
        }

        [Fact]
        public async Task DebounceFlushesAutomatically()
        {
            var got = new TaskCompletionSource<ChangeBatch>();
            using var t = new FileChangeTracker(b => { got.TrySetResult(b); return Task.CompletedTask; }) { Debounce = TimeSpan.FromMilliseconds(30) };
            t.Enqueue(new[] { new FileChange { FilePath = "x.h", Kind = FileChangeKind.Changed } }, "host");
            var b = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(b.Changes);
        }

        [Fact]
        public void HashesContent()
        {
            Assert.Equal(ContentHasher.Hash(new byte[] { 1, 2, 3 }), ContentHasher.Hash(new byte[] { 1, 2, 3 }));
            Assert.NotEqual(ContentHasher.Hash(new byte[] { 1, 2, 3 }), ContentHasher.Hash(new byte[] { 1, 2, 4 }));
        }
    }

    public class PlaceholderParserTests
    {
        [Fact]
        public void FindsDeclarationsAndUeMarkers()
        {
            string code = "#define MY_MACRO 1\nnamespace Foo {\n// class Commented {};\nclass FForward;\nUCLASS(Blueprintable)\nclass MYGAME_API AMyActor : public AActor\n{\n" +
                          "\tGENERATED_BODY()\n\tUFUNCTION(BlueprintCallable, Category=\"X\")\n\tvoid Fire(float P);\n\tUPROPERTY(EditAnywhere)\n\tTArray<int32> Items;\n};\n" +
                          "UENUM()\nenum class EMode : uint8 { A, B };\nDECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnHit, int32, Damage);\n}";
            var p = new SimpleDeclarationParser();
            var ctx = new FileContext { Path = "/x/A.h", Module = "M", Origin = ItemOrigin.Project, Kind = SourceFileKind.Header };
            var syms = p.Parse(ctx, System.Text.Encoding.UTF8.GetBytes(code), CancellationToken.None).Symbols;
            var names = syms.Select(s => s.Name).ToList();
            Assert.Equal(new[] { "MY_MACRO", "Foo", "AMyActor", "Fire", "Items", "EMode", "FOnHit" }, names);
            var actor = syms.Single(s => s.Name == "AMyActor");
            Assert.Equal(SymbolKind.Class, actor.Kind);
            Assert.Equal(UeFlags.UClass, actor.Ue);
            Assert.Equal("Blueprintable", actor.Specifiers);
            Assert.Equal(5, actor.Location.Line);
            var fire = syms.Single(s => s.Name == "Fire");
            Assert.Equal(SymbolKind.Method, fire.Kind);
            Assert.Equal("AMyActor", fire.Container);
            Assert.Equal(UeFlags.UFunction, fire.Ue);
            Assert.Equal(SymbolKind.Field, syms.Single(s => s.Name == "Items").Kind);
            Assert.Equal(UeFlags.UEnum, syms.Single(s => s.Name == "EMode").Ue);
            Assert.False(p.CanParse(new FileContext { Kind = SourceFileKind.Source, Origin = ItemOrigin.ThirdParty }));
            Assert.True(p.CanParse(new FileContext { Kind = SourceFileKind.Header, Origin = ItemOrigin.ThirdParty }));
        }
    }
}
