using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// End-to-end indexing benchmark on a synthetic UE tree. Small by default (runs in CI as a smoke test); set
    /// UETOVS_BENCH_FILES=20000 (and optionally UETOVS_BENCH_ROOT=/some/dir to keep the generated tree between runs)
    /// for real numbers. Prints cold index, warm restart and per-phase timings.
    /// </summary>
    public class IndexingBenchmarkTests
    {
        private readonly ITestOutputHelper _out;

        public IndexingBenchmarkTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void ColdAndWarmIndexOfSyntheticTree()
        {
            int files = int.TryParse(Environment.GetEnvironmentVariable("UETOVS_BENCH_FILES"), out int n) && n > 0 ? n : 600;
            string keepRoot = Environment.GetEnvironmentVariable("UETOVS_BENCH_ROOT");
            int projectFiles = Math.Max(20, files / 20);
            SyntheticUeTree tree = null;
            var gen = Stopwatch.StartNew();
            if (keepRoot != null && File.Exists(Path.Combine(keepRoot, "Game", "BenchGame.uproject")))
            {
                // reuse; only the index is dropped
            }
            else
            {
                tree = new SyntheticUeTree(files, projectFiles, root: keepRoot);
            }
            string root = keepRoot ?? tree.Root;
            string projectRoot = Path.Combine(root, "Game");
            string vs = Path.Combine(projectRoot, ".vs");
            if (Directory.Exists(vs)) Directory.Delete(vs, true);
            _out.WriteLine($"tree: {files} engine + {projectFiles} project files, generated in {gen.Elapsed.TotalSeconds:F1}s");

            try
            {
                double cold = Run(projectRoot, "cold", out var coldStatus);
                double warm = Run(projectRoot, "warm", out var warmStatus);
                Assert.Equal(IndexPhase.Ready, coldStatus.Phase);
                Assert.Equal(IndexPhase.Ready, warmStatus.Phase);
                Assert.True(coldStatus.SymbolCount > 0);
                Assert.Equal(coldStatus.SymbolCount, warmStatus.SymbolCount);
            }
            finally
            {
                if (keepRoot == null) tree?.Dispose();
                else if (Directory.Exists(vs)) Directory.Delete(vs, true);
            }
        }

        private double Run(string projectRoot, string label, out EngineStatus status)
        {
            var options = EngineOptions.Parse(new[] { "--status", "--solution-dir", projectRoot, "--project", Path.Combine(projectRoot, "BenchGame.uproject"), "--no-watch" }, out _);
            using var host = new EngineHost(options);
            var sw = Stopwatch.StartNew();
            host.StartIndexing();
            Assert.True(host.Index.WaitForReadyAsync(CancellationToken.None).Wait(TimeSpan.FromMinutes(30)));
            double ms = sw.Elapsed.TotalMilliseconds;
            status = host.GetStatus();
            _out.WriteLine($"[{label}] {ms / 1000:F2}s  files={status.FileCount} parsed={status.FilesParsed}/{status.FilesToParse} symbols={status.SymbolCount}");
            var snap = host.GetPerformanceSnapshot();
            foreach (var p in snap.Phases.AsEnumerable().Reverse())
                _out.WriteLine($"   {p.Phase,-18} {p.DurationMs,9:F0} ms  cpu {p.CpuMs,9:F0} ms  items {p.Items,7}  {p.Detail}");
            foreach (var t in snap.Throughput.AsEnumerable().Reverse())
            {
                _out.WriteLine($"   {t.Stage}: {t.FilesDone}/{t.FilesTotal} files, {t.FilesPerSecond:F0} files/s, {t.MegabytesPerSecond:F1} MB/s, parse p50 {t.ParseP50Ms:F2} p95 {t.ParseP95Ms:F2} max {t.ParseMaxMs:F1} ms, " +
                    $"read {t.ReadMs:F0} parse {t.ParseMs:F0} apply {t.ApplyMs:F0} ms, io {t.IoShare:P0}, threads {t.Threads}/{t.ConfiguredThreads}, util {t.ThreadUtilization:P0}, throttled {t.ThrottledMs:F0} ms, segments {t.SegmentsWritten} ({t.SegmentWriteMs:F0} ms)");
                if (t.SlowestFiles.Count > 0) _out.WriteLine($"     slowest: {t.SlowestFiles[0].Ms:F1} ms {t.SlowestFiles[0].Bytes} B");
            }
            return ms;
        }
    }
}
