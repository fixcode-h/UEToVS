using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Unreal;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>Counts what reaches the parser (and can block it) around the real syntax parser.</summary>
    internal sealed class CountingParser : IFileParser
    {
        private readonly SyntaxFileParser _inner = new SyntaxFileParser();
        public readonly ConcurrentBag<string> Parsed = new ConcurrentBag<string>();
        public int Concurrent, MaxConcurrent;
        public int DelayMs;
        public ManualResetEventSlim Gate;

        public string Name => _inner.Name;
        public string Version => _inner.Version;
        public bool CanParse(FileContext file) => _inner.CanParse(file);

        public ParseResult Parse(FileContext file, ReadOnlyMemory<byte> utf8, CancellationToken ct)
        {
            int c = Interlocked.Increment(ref Concurrent);
            int m;
            while (c > (m = Volatile.Read(ref MaxConcurrent)) && Interlocked.CompareExchange(ref MaxConcurrent, c, m) != m) { }
            try
            {
                Gate?.Wait(ct);
                if (DelayMs > 0) Thread.Sleep(DelayMs);
                Parsed.Add(file.Path);
                return _inner.Parse(file, utf8, ct);
            }
            finally
            {
                Interlocked.Decrement(ref Concurrent);
            }
        }

        public void Clear()
        {
            while (Parsed.TryTake(out _)) { }
        }
    }

    public class IndexingSettingsTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        private static EngineHost Host(string solutionDir, IFileParser parser, Action<EngineOptions> configure = null)
        {
            var o = new EngineOptions { SolutionDir = solutionDir, MaxParallelism = 2, WatchEngine = false, NoWatch = true };
            configure?.Invoke(o);
            return new EngineHost(o, parser: parser);
        }

        private static async Task Ready(EngineHost h)
        {
            Assert.True(await h.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(Timeout));
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            var end = DateTime.UtcNow + Timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > end) throw new TimeoutException("condition not reached");
                await Task.Delay(20);
            }
        }

        // ------------------------------------------------------------------ defaults / config

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(4, 1)]
        [InlineData(8, 2)]
        [InlineData(16, 4)]
        [InlineData(32, 8)]
        public void DefaultThreadsAreAQuarterOfTheCoresAtLeastOne(int cores, int expected)
        {
            Assert.Equal(expected, IndexingConfig.DefaultThreads(cores));
            var c = IndexingConfig.From(new IndexingSettings(), cores);
            Assert.Equal(expected, c.CppThreads);
            Assert.Equal(expected, c.AssetThreads);
        }

        [Fact]
        public void SettingsResolve()
        {
            var c = IndexingConfig.From(new IndexingSettings { CppThreads = 6, AssetThreads = 500, DuringBuild = 0, ExcludedFolders = new List<string> { " Intermediate/", "", "Generated" } }, 16);
            Assert.Equal(6, c.CppThreads);
            Assert.Equal(IndexingConfig.MaxThreads, c.AssetThreads);
            Assert.Equal(BuildPolicy.Pause, c.DuringBuild);
            Assert.Equal(new[] { "Intermediate", "Generated" }, c.ExcludedFolders);
            Assert.False(c.SharedEngineCache); // default: engine index stays in the solution
            Assert.Equal(BuildPolicy.SingleThread, IndexingConfig.From(new IndexingSettings()).DuringBuild);
            Assert.Equal(BuildPolicy.FullSpeed, IndexingConfig.From(new IndexingSettings { DuringBuild = 2 }).DuringBuild);
            var back = c.ToSettings();
            Assert.Equal(6, back.CppThreads);
            Assert.Equal(0, back.DuringBuild);
        }

        [Fact]
        public void CommandLineGivesInitialSettings()
        {
            var o = EngineOptions.Parse(new[] { "--status", "--cpp-threads", "3", "--asset-threads", "2", "--during-build", "full", "--shared-engine-cache",
                "--engine-cache-dir", "/tmp/x", "--exclude-dir", "Gen", "--exclude-dir", "Out" }, out string error);
            Assert.Null(error);
            var s = o.InitialIndexingSettings();
            Assert.Equal(3, s.CppThreads);
            Assert.Equal(2, s.AssetThreads);
            Assert.Equal(2, s.DuringBuild);
            Assert.True(s.SharedEngineCache);
            Assert.Equal("/tmp/x", s.EngineCacheDirectory);
            Assert.Equal(new[] { "Gen", "Out" }, s.ExcludedFolders);
            Assert.Null(EngineOptions.Parse(new[] { "--status", "--during-build", "sometimes" }, out error));
            Assert.Contains("--during-build", error);
        }

        // ------------------------------------------------------------------ throttle / build policy

        [Fact]
        public void ThrottleFollowsBuildPolicyAndPause()
        {
            var t = new WorkerThrottle("t", 4);
            Assert.Equal(4, t.Effective);
            Assert.Equal(ThreadPriority.BelowNormal, t.Priority);
            t.SetBuilding(true);
            Assert.Equal(1, t.Effective); // default policy: single thread
            Assert.Equal(ThreadPriority.Lowest, t.Priority);
            t.SetPolicy(BuildPolicy.Pause);
            Assert.Equal(0, t.Effective);
            t.SetPolicy(BuildPolicy.FullSpeed);
            Assert.Equal(4, t.Effective);
            t.SetBuilding(false);
            t.SetUserPaused(true);
            Assert.Equal(0, t.Effective);
            t.SetUserPaused(false);
            t.SetConfigured(2);
            Assert.Equal(2, t.Effective);
        }

        [Fact]
        public async Task WaitForTurnBlocksUntilAllowed()
        {
            var t = new WorkerThrottle("t", 1);
            t.SetUserPaused(true);
            var waiter = Task.Run(() => t.WaitForTurn(0, CancellationToken.None));
            await Task.Delay(100);
            Assert.False(waiter.IsCompleted);
            t.SetUserPaused(false);
            Assert.True(await waiter.WaitAsync(Timeout) > 0);
            using var cts = new CancellationTokenSource();
            var second = Task.Run(() => t.WaitForTurn(3, cts.Token));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Timeout));
        }

        [Fact]
        public async Task PipelineUsesConfiguredThreadsAndAdaptsAtRuntime()
        {
            var throttle = new WorkerThrottle("t", 2);
            int running = 0, max = 0, done = 0;
            var phase2 = new ManualResetEventSlim();
            var items = Enumerable.Range(0, 200).ToList();
            var run = ParsePipeline.RunAsync(items, throttle, null, (i, w, ct) =>
            {
                int r = Interlocked.Increment(ref running);
                int m;
                while (r > (m = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, r, m) != m) { }
                Thread.Sleep(5);
                Interlocked.Decrement(ref running);
                if (Interlocked.Increment(ref done) == 40) phase2.Set();
            }, "test", CancellationToken.None);
            Assert.True(phase2.Wait(Timeout));
            Assert.Equal(2, Volatile.Read(ref max));
            Volatile.Write(ref max, 0);
            throttle.SetConfigured(4); // more threads while running
            await WaitUntil(() => Volatile.Read(ref max) >= 3 || run.IsCompleted);
            await run.WaitAsync(Timeout);
            Assert.Equal(200, done);
            Assert.True(max <= 4);
        }

        [Fact]
        public async Task PipelinePausesDuringBuildWithPausePolicyAndResumes()
        {
            var throttle = new WorkerThrottle("t", 2);
            throttle.SetPolicy(BuildPolicy.Pause);
            throttle.SetBuilding(true);
            int done = 0;
            var run = ParsePipeline.RunAsync(Enumerable.Range(0, 20).ToList(), throttle, null, (i, w, ct) => Interlocked.Increment(ref done), "test", CancellationToken.None);
            await Task.Delay(150);
            Assert.Equal(0, done);
            throttle.SetBuilding(false);
            await run.WaitAsync(Timeout);
            Assert.Equal(20, done);
        }

        // ------------------------------------------------------------------ ordering / filtering

        [Fact]
        public void PrioritizesProjectThenEngineApiHeadersThenOthers()
        {
            ListedFile F(string path, ItemOrigin o, SourceFileKind k) => new ListedFile { Path = path, Origin = o, Kind = k };
            var files = new[]
            {
                F("/UE/Engine/Source/ThirdParty/z/z.h", ItemOrigin.ThirdParty, SourceFileKind.Header),
                F("/UE/Engine/Source/Runtime/Core/Private/A.cpp", ItemOrigin.Engine, SourceFileKind.Source),
                F("/UE/Engine/Source/Runtime/Core/Private/A.h", ItemOrigin.Engine, SourceFileKind.Header),
                F("/UE/Engine/Source/Runtime/Core/Classes/C.h", ItemOrigin.Engine, SourceFileKind.Header),
                F("/UE/Engine/Source/Runtime/Core/Public/P.h", ItemOrigin.Engine, SourceFileKind.Header),
                F("/Game/Source/G/Private/G.cpp", ItemOrigin.Project, SourceFileKind.Source),
                F("/Game/Plugins/X/Source/X/Public/X.h", ItemOrigin.ProjectPlugin, SourceFileKind.Header),
            };
            var order = IndexOrchestrator.Prioritize(files).Select(f => Path.GetFileName(f.Path)).ToArray();
            Assert.Equal(new[] { "X.h", "G.cpp", "C.h", "P.h", "A.h", "A.cpp", "z.h" }, order);
        }

        [Fact]
        public async Task OnlyCppFilesReachTheParser()
        {
            using var t = new FakeUnrealTree();
            var parser = new CountingParser();
            using var h = Host(t.ProjectRoot, parser);
            h.StartIndexing();
            await Ready(h);
            Assert.NotEmpty(parser.Parsed);
            Assert.DoesNotContain(parser.Parsed, p => !(p.EndsWith(".h") || p.EndsWith(".cpp") || p.EndsWith(".c")));
            Assert.True(h.Index.Files.TryGet(t.P("Game/Config/DefaultEngine.ini"), out _)); // listed for file search
            Assert.DoesNotContain(parser.Parsed, p => p.Contains("Intermediate"));

            // A changed .ini goes to the file table only.
            parser.Clear();
            File.AppendAllText(t.P("Game/Config/DefaultEngine.ini"), "\n[X]\n");
            var batch = new Vfs.ChangeBatch { Source = "test" };
            batch.Changes.Add(new FileChange { FilePath = t.P("Game/Config/DefaultEngine.ini"), Kind = FileChangeKind.Changed });
            await h.Index.ApplyChangesAsync(batch, CancellationToken.None);
            Assert.Empty(parser.Parsed);
        }

        // ------------------------------------------------------------------ runtime settings

        [Fact]
        public async Task SettingsApplyAtRuntime()
        {
            using var t = new FakeUnrealTree();
            using var h = Host(t.ProjectRoot, new CountingParser());
            h.StartIndexing();
            await Ready(h);
            h.ApplyIndexingSettings(new IndexingSettings { CppThreads = 3, AssetThreads = 2, DuringBuild = 0 });
            Assert.Equal(3, h.Index.CppThrottle.Configured);
            Assert.Equal(2, h.Index.AssetThrottle.Configured);
            Assert.Equal(BuildPolicy.Pause, h.Scheduler.BuildPolicy);
            h.Scheduler.SetBuildInProgress(true);
            Assert.Equal(0, h.Index.CppThrottle.Effective);
            Assert.True(h.Scheduler.IsBackgroundPaused);
            h.Scheduler.SetBuildInProgress(false);
            Assert.Equal(3, h.Index.CppThrottle.Effective);
            h.Scheduler.SetUserPaused(true);
            Assert.Equal(0, h.Index.CppThrottle.Effective);
            h.Scheduler.SetUserPaused(false);
            Assert.Equal(IndexPhase.Ready, h.Index.Phase); // thread counts never restart the run

            // Turning the engine off restarts without engine files.
            h.ApplyIndexingSettings(new IndexingSettings { IncludeEngine = false });
            await WaitUntil(() => h.Index.Phase == IndexPhase.Ready && !h.Index.Files.TryGet(t.P("UE/Engine/Source/Runtime/Core/Public/Containers/UnrealString.h"), out _));
            Assert.False(h.Index.Options.IncludeEngine);
            var snap = h.GetPerformanceSnapshot();
            Assert.False(snap.IndexingSettings.IncludeEngine);
        }

        [Fact]
        public async Task SingleThreadPolicyKeepsIndexingDuringABuild()
        {
            using var t = new FakeUnrealTree();
            var parser = new CountingParser { DelayMs = 20 };
            using var logs = new CapturingLogSink();
            using var h = Host(t.ProjectRoot, parser, o => o.CppThreads = 4);
            h.Scheduler.SetBuildInProgress(true);
            h.StartIndexing();
            await Ready(h); // finished although a build is running
            // throughput is logged per stage, with the thread count actually allowed
            Assert.Contains(logs.Events, e => e.Event == "index.done" && e.Data.Any(kv => kv.Key == "stage" && (string)kv.Value == "parse.engine"));
            Assert.Contains(logs.Events, e => e.Event == "index.done" && e.Data.Any(kv => kv.Key == "threads" && (int)kv.Value == 1));
            Assert.Equal(1, parser.MaxConcurrent);
            h.Scheduler.SetBuildInProgress(false);
        }

        // ------------------------------------------------------------------ rebuild

        [Fact]
        public async Task RebuildScopesReparseTheRightParts()
        {
            using var t = new FakeUnrealTree();
            var parser = new CountingParser();
            using var h = Host(t.ProjectRoot, parser);
            h.StartIndexing();
            await Ready(h);
            string projectHeader = t.P("Game/Source/MyGame/Public/MyCharacter.h");
            string engineHeader = t.P("UE/Engine/Source/Runtime/Core/Public/Containers/UnrealString.h");
            Assert.Contains(projectHeader, parser.Parsed);
            Assert.Contains(engineHeader, parser.Parsed);

            async Task<string[]> Rebuild(RebuildScope scope)
            {
                parser.Clear();
                h.RebuildIndex(scope);
                await Task.Delay(50);
                await Ready(h);
                await h.Index.RunTask;
                return parser.Parsed.ToArray();
            }

            var p = await Rebuild(RebuildScope.Project);
            Assert.Contains(projectHeader, p);
            Assert.DoesNotContain(engineHeader, p);
            var e = await Rebuild(RebuildScope.Engine);
            Assert.Contains(engineHeader, e);
            Assert.DoesNotContain(projectHeader, e);
            var both = await Rebuild(RebuildScope.Cpp);
            Assert.Contains(engineHeader, both);
            Assert.Contains(projectHeader, both);

            // Search still finds both sides after rebuilds.
            var hits = h.Index.Backend.FindSymbols("FString", null);
            Assert.NotEmpty(hits);
            Assert.NotEmpty(h.Index.Backend.FindSymbols("AMyCharacter", null));

            // A plain restart reuses everything.
            parser.Clear();
            h.StartIndexing();
            await Ready(h);
            await h.Index.RunTask;
            Assert.Empty(parser.Parsed);
        }

        [Fact]
        public async Task ConcurrentRebuildRequestsMerge()
        {
            using var t = new FakeUnrealTree();
            var parser = new CountingParser();
            using var h = Host(t.ProjectRoot, parser);
            h.StartIndexing();
            await Ready(h);
            parser.Clear();
            h.RebuildIndex(RebuildScope.Project);
            h.RebuildIndex(RebuildScope.Engine); // cancels the first; its part is carried over
            await Task.Delay(50);
            await Ready(h);
            await h.Index.RunTask;
            Assert.Contains(t.P("Game/Source/MyGame/Public/MyCharacter.h"), parser.Parsed);
            Assert.Contains(t.P("UE/Engine/Source/Runtime/Core/Public/Containers/UnrealString.h"), parser.Parsed);
        }

        [Fact]
        public async Task AssetRebuildRescansContent()
        {
            using var t = new FakeUnrealTree();
            using var h = Host(t.ProjectRoot, new CountingParser());
            h.StartIndexing();
            await Ready(h);
            int before = h.Index.Assets.Count;
            File.Delete(t.P("Game/Content/Characters/BP_Hero.uasset"));
            t.Write("Game/Content/Characters/BP_Villain.uasset", "");
            t.Write("Game/Content/Characters/BP_Boss.uasset", "");
            h.RebuildIndex(RebuildScope.Assets);
            await h.Index.AssetTask.WaitAsync(Timeout);
            Assert.Equal(before + 1, h.Index.Assets.Count);
            Assert.Contains(h.Index.Assets.Snapshot(), a => a.Name == "BP_Villain");
            Assert.DoesNotContain(h.Index.Assets.Snapshot(), a => a.Name == "BP_Hero");
            Assert.Contains(h.Index.Throughput(), x => x.Stage == "assets.rebuild" && x.FilesDone == h.Index.Assets.Count);
        }

        // ------------------------------------------------------------------ storage layout / shared engine cache

        [Fact]
        public async Task EngineAndProjectPartsAreStoredSeparately()
        {
            using var t = new FakeUnrealTree();
            using var h = Host(t.ProjectRoot, new CountingParser());
            h.StartIndexing();
            await Ready(h);
            string index = h.Paths.Index;
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(index, "engine"), "*.seg"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(index, "project"), "project-*.seg"));
            Assert.Empty(Directory.GetFiles(Path.Combine(index, "project"), "engine-*.seg"));

            // A project change only goes to the project side (delta → project segment); engine files are untouched.
            var engineSegs = Directory.GetFiles(Path.Combine(index, "engine"), "*.seg").Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();
            string header = t.P("Game/Source/MyGame/Public/MyCharacter.h");
            File.AppendAllText(header, "\nclass FAddedLater {};\n");
            var batch = new Vfs.ChangeBatch { Source = "test" };
            batch.Changes.Add(new FileChange { FilePath = header, Kind = FileChangeKind.Changed });
            await h.Index.ApplyChangesAsync(batch, CancellationToken.None);
            h.Index.SaveState(); // compacts the delta
            Assert.NotEmpty(h.Index.Backend.FindSymbols("FAddedLater", null));
            Assert.All(engineSegs, s => Assert.True(File.Exists(s.f) && File.GetLastWriteTimeUtc(s.f) == s.Item2));
        }

        [Fact]
        public async Task SharedEngineCacheIsReusedByASecondProject()
        {
            using var t = new FakeUnrealTree();
            string cacheRoot = Path.Combine(t.Root, "shared-cache");
            // second project on the same engine
            t.Write("Game2/Other.uproject", "{ \"FileVersion\": 3, \"EngineAssociation\": \"../UE\", \"Modules\": [ { \"Name\": \"Other\", \"Type\": \"Runtime\" } ] }");
            t.Write("Game2/Source/Other/Other.Build.cs", "public class Other : ModuleRules { public Other(ReadOnlyTargetRules T) : base(T) { } }");
            t.Write("Game2/Source/Other/Public/OtherThing.h", "class OTHER_API FOtherThing {};\n");
            string engineHeader = t.P("UE/Engine/Source/Runtime/Core/Public/Containers/UnrealString.h");

            void Shared(EngineOptions o)
            {
                o.SharedEngineCache = true;
                o.EngineCacheDirectory = cacheRoot;
            }

            var p1 = new CountingParser();
            using (var h1 = Host(t.ProjectRoot, p1, Shared))
            {
                h1.StartIndexing();
                await Ready(h1);
                Assert.Contains(engineHeader, p1.Parsed);
                h1.Index.SaveState();
                // The project manifest points at the shared copies. (The local engine copies are deleted once unmapped;
                // on Windows a still-mapped file is only removed on the next open, so don't assert on the folder here.)
                string manifest = File.ReadAllText(Path.Combine(h1.Paths.Index, "project", "manifest.json")).Replace("\\\\", "/").Replace('\\', '/');
                Assert.Contains("shared-cache", manifest);
            }
            Assert.NotEmpty(Directory.GetFiles(cacheRoot, "*.seg", SearchOption.AllDirectories));

            var p2 = new CountingParser();
            using (var h2 = Host(t.P("Game2"), p2, Shared))
            {
                h2.StartIndexing();
                await Ready(h2);
                Assert.DoesNotContain(p2.Parsed, p => p.StartsWith(t.EngineRoot)); // hit: no engine file parsed
                Assert.Contains(t.P("Game2/Source/Other/Public/OtherThing.h"), p2.Parsed);
                Assert.NotEmpty(h2.Index.Backend.FindSymbols("FString", null));
                Assert.Contains(h2.Index.Phases.Snapshot(), x => x.Phase == IndexPhase.LoadingCache && x.Detail.Contains("sharedEngine=True"));
            }

            // Invalidation: a changed engine file is re-parsed (alone); the shared files are never modified in place.
            var sharedSegs = Directory.GetFiles(cacheRoot, "*.seg", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();
            File.AppendAllText(engineHeader, "\nclass FNewEngineThing {};\n");
            File.SetLastWriteTimeUtc(engineHeader, DateTime.UtcNow.AddMinutes(1));
            var p3 = new CountingParser();
            using (var h3 = Host(t.P("Game2"), p3, Shared))
            {
                h3.StartIndexing();
                await Ready(h3);
                Assert.Equal(new[] { engineHeader }, p3.Parsed.Where(p => p.StartsWith(t.EngineRoot)).ToArray());
                Assert.NotEmpty(h3.Index.Backend.FindSymbols("FNewEngineThing", null));
            }
            foreach (var (f, time) in sharedSegs)
                if (File.Exists(f)) Assert.Equal(time, File.GetLastWriteTimeUtc(f));

            // A different key (parser / format version) never matches.
            var cache = new SharedEngineCache(cacheRoot);
            Assert.Null(cache.FindLatestVersion("5.4.2-cl123-x", "some-other-key"));
        }

        [Fact]
        public async Task TurningSharingOffCopiesTheEnginePartBack()
        {
            using var t = new FakeUnrealTree();
            string cacheRoot = Path.Combine(t.Root, "shared-cache");
            using (var h1 = Host(t.ProjectRoot, new CountingParser(), o => { o.SharedEngineCache = true; o.EngineCacheDirectory = cacheRoot; }))
            {
                h1.StartIndexing();
                await Ready(h1);
                h1.Index.SaveState();
            }
            var p2 = new CountingParser();
            using (var h2 = Host(t.ProjectRoot, p2))
            {
                h2.StartIndexing();
                await Ready(h2);
                Assert.Empty(p2.Parsed); // nothing re-parsed
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(h2.Paths.Index, "engine"), "*.seg"));
                Assert.NotEmpty(h2.Index.Backend.FindSymbols("FString", null));
            }
        }
    }
}
