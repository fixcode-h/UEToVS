using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Engine.Cli;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>The bench sub-command on a small synthetic UE tree: options, sampling, the JSON line and the comparison.</summary>
    public sealed class BenchTests
    {
        private readonly ITestOutputHelper _out;

        public BenchTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void ParsesTheBenchSubCommand()
        {
            var o = EngineOptions.Parse(new[] { "bench", "--project", "/tmp/x/Game.uproject", "--samples", "50", "--seed", "7", "--ops", "definition, completion", "--reindex", "--details", "--no-compare" }, out var error);
            Assert.Null(error);
            Assert.True(o.Bench);
            Assert.True(o.IsCli);
            Assert.Equal(50, o.BenchSamples);
            Assert.Equal(7, o.BenchSeed);
            Assert.Equal(new[] { "definition", "completion" }, o.BenchOps);
            Assert.True(o.BenchReindex);
            Assert.True(o.BenchDetails);
            Assert.False(o.BenchCompare);

            Assert.Null(EngineOptions.Parse(new[] { "bench", "--ops", "rename" }, out error));
            Assert.Contains("rename", error);
            Assert.Null(EngineOptions.Parse(new[] { "--samples", "10", "bench" }, out error)); // "bench" only as the first argument
            Assert.Equal(1000, EngineOptions.Parse(new[] { "bench" }, out _).BenchSamples);
        }

        [Fact]
        public void PercentilesAndEmptyRatio()
        {
            var ms = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
            var s = BenchOpStats.From("definition", ms, empty: 5, errors: 1);
            Assert.Equal(50, s.P50);
            Assert.Equal(95, s.P95);
            Assert.Equal(99, s.P99);
            Assert.Equal(100, s.Max);
            Assert.Equal(0.06, s.EmptyRatio);
            Assert.Equal(0, BenchOpStats.From("x", new List<double>(), 0, 0).P99);
        }

        [Fact]
        public void ReportRoundTripsAndComparesWithThePreviousRun()
        {
            var a = new BenchReport { Project = "/p/Game.uproject", EngineVersion = "0.4.5", Seed = 1, Samples = 10, IndexMs = 1000, PeakWorkingSetMb = 200 };
            a.Ops.Add(new BenchOpStats { Op = "definition", Count = 10, P50 = 1, P95 = 2, P99 = 3, Empty = 1 });
            var back = BenchReport.Parse(a.ToJsonLine());
            Assert.Equal("/p/Game.uproject", back.Project);
            Assert.Equal(2, back.Ops.Single().P95);
            Assert.Equal(0.1, back.Ops.Single().EmptyRatio);
            Assert.Null(BenchReport.Parse("{\"event\":\"other\"}"));
            Assert.Null(BenchReport.Parse("not json"));

            var b = new BenchReport { Project = a.Project, EngineVersion = "0.4.6", Seed = 1, Samples = 10, IndexMs = 800, PeakWorkingSetMb = 260 };
            b.Ops.Add(new BenchOpStats { Op = "definition", Count = 10, P50 = 1, P95 = 4, P99 = 3, Empty = 3 });
            string table = b.Format(back);
            _out.WriteLine(table);
            Assert.Contains("(-20%)", table);  // index faster
            Assert.Contains("(+30%!)", table); // memory up more than 10 %
            Assert.Contains("4.00 (+100%!)", table);
            Assert.Contains("(+20.0pt!)", table);
            Assert.Equal(" (+5%)", BenchReport.Delta(100, 105));
            Assert.Equal(string.Empty, BenchReport.Delta(null, 1));
        }

        [Fact]
        public void RunsOnTheSyntheticProjectAndComparesTheSecondRun()
        {
            using var tree = new SyntheticUeTree(engineFiles: 40, projectFiles: 30, seed: 99);
            string projectRoot = tree.ProjectRoot;
            string Run(params string[] extra)
            {
                var args = new List<string> { "bench", "--solution-dir", projectRoot, "--project", tree.ProjectFile, "--no-watch", "--samples", "40", "--seed", "3" };
                args.AddRange(extra);
                var options = EngineOptions.Parse(args, out var error);
                Assert.Null(error);
                using var host = new EngineHost(options);
                var output = new StringWriter();
                int code = BenchRunner.RunAsync(host, options, output).GetAwaiter().GetResult();
                host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _out.WriteLine(output.ToString());
                Assert.Equal(ExitCodes.Ok, code);
                return output.ToString();
            }

            string first = Run("--details");
            Assert.Contains("completion", first);
            Assert.Contains("references", first);
            string file = Path.Combine(projectRoot, ".vs", "UEToVS", "bench", BenchReport.FileName);
            Assert.True(File.Exists(file));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file), "bench-*-samples.jsonl"));
            var r1 = BenchReport.Parse(File.ReadAllLines(file).Single());
            Assert.Equal(40, r1.Samples);
            Assert.Equal(3, r1.Ops.Count);
            Assert.All(r1.Ops, o => Assert.Equal(40, o.Count));
            Assert.True(r1.Files > 0 && r1.Symbols > 0);
            Assert.True(r1.PeakWorkingSetMb > 0);
            var definition = r1.Ops.Single(o => o.Op == "definition");
            Assert.True(definition.EmptyRatio < 1, "go to definition found nothing at any sampled position");
            Assert.Equal(0, definition.Errors);

            // second run: index reused, same positions (same seed), compared with the first
            string second = Run("--ops", "definition");
            Assert.Contains("previous:", second);
            Assert.Contains("(reused)", second);
            var lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length);
            var r2 = BenchReport.Parse(lines[1]);
            Assert.True(r2.IndexReused);
            Assert.False(r1.IndexReused);
            Assert.Equal(definition.Empty, r2.Ops.Single().Empty); // same sampled positions → same empty results
        }
    

        [Fact]
        public void GoldenLinesRoundTripAndScore()
        {
            var e = GoldenSet.Parse("{\"file\":\"C:\\\\G\\\\A.cpp\",\"line\":3,\"col\":5,\"word\":\"Foo\",\"expected\":[{\"file\":\"c:/g/A.h\",\"line\":10},{\"file\":\"C:\\\\G\\\\A.h\",\"line\":10}]}");
            Assert.NotNull(e);
            Assert.Equal(2, e.Sample.Line);    // 0-based inside
            Assert.Equal(4, e.Sample.Column);
            Assert.Single(e.Expected);         // same path in another spelling is one answer
            var back = GoldenSet.Parse(GoldenSet.ToJsonLine(e));
            Assert.Equal(3, back.Sample.Line + 1);
            Assert.Equal(new GoldenLocation("C:/G/A.h", 10), back.Expected.Single());
            Assert.Null(GoldenSet.Parse("{\"file\":\"x\",\"line\":0,\"col\":1}"));
            Assert.Null(GoldenSet.Parse("not json"));

            var unanswered = GoldenSet.Parse("{\"file\":\"B.cpp\",\"line\":1,\"col\":1,\"expected\":[]}");
            var entries = new List<GoldenEntry> { e, e, e, unanswered };
            var a = new GoldenLocation("c:/g/a.h", 10);
            var other = new GoldenLocation("c:/g/b.h", 1);
            var score = GoldenSet.Score(entries, new List<IReadOnlyList<GoldenLocation>>
            {
                new[] { a }, new[] { other, a }, Array.Empty<GoldenLocation>(), new[] { a },
            });
            Assert.Equal(3, score.Scored);
            Assert.Equal(1, score.Top1);
            Assert.Equal(2, score.AnyHit);
            Assert.Equal(1, score.Empty);
            Assert.Equal(0, score.Wrong);
            Assert.Contains("top-1 33.3%", score.Format());
        }

        [Fact]
        public async System.Threading.Tasks.Task ScoresGoToDefinitionAgainstAGoldenFile()
        {
            using var tree = new SyntheticUeTree(engineFiles: 30, projectFiles: 20, seed: 7);
            string projectRoot = tree.ProjectRoot;
            string goldenFile = Path.Combine(projectRoot, "golden.jsonl");
            string Run(params string[] extra)
            {
                var args = new List<string> { "bench", "--solution-dir", projectRoot, "--project", tree.ProjectFile, "--no-watch", "--samples", "30", "--seed", "5", "--ops", "definition" };
                args.AddRange(extra);
                var options = EngineOptions.Parse(args, out var error);
                Assert.Null(error);
                using var host = new EngineHost(options);
                var output = new StringWriter();
                int code = BenchRunner.RunAsync(host, options, output).GetAwaiter().GetResult();
                host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _out.WriteLine(output.ToString());
                Assert.Equal(ExitCodes.Ok, code);
                return output.ToString();
            }

            Run("--emit-samples", goldenFile);
            var entries = GoldenSet.Load(goldenFile);
            Assert.Equal(30, entries.Count);
            Assert.All(entries, x => Assert.Empty(x.Expected));

            // stand-in for clangd: the engine's own answers, so the score must be 100 %
            {
                var options = EngineOptions.Parse(new[] { "--status", "--solution-dir", projectRoot, "--project", tree.ProjectFile, "--no-watch" }, out var error);
                Assert.Null(error);
                using var host = new EngineHost(options);
                _ = host.StartIndexing();
                Assert.True(await host.Index.WaitForReadyAsync(CancellationToken.None));
                foreach (var x in entries) x.Expected.AddRange(BenchRunner.DefinitionTargets(host, x.Sample, CancellationToken.None));
                await host.StopAsync(TimeSpan.FromSeconds(5));
            }
            entries[0].Expected.Clear();
            entries[0].Expected.Add(new GoldenLocation("/nowhere/Missing.h", 1)); // one deliberately wrong answer
            GoldenSet.Save(goldenFile, entries);
            int answered = entries.Count(x => x.Expected.Count > 0);
            Assert.True(answered > 1, "go to definition found nothing on the synthetic tree");

            string text = Run("--golden", goldenFile);
            Assert.Contains("accuracy: definition top-1", text);
            string file = Path.Combine(projectRoot, ".vs", "UEToVS", "bench", BenchReport.FileName);
            var r = BenchReport.Parse(File.ReadAllLines(file).Last());
            Assert.NotNull(r.Accuracy);
            Assert.Equal(answered, r.Accuracy.Scored);
            Assert.Equal(answered - 1, r.Accuracy.AnyHit);
            Assert.Equal(answered - 1, r.Accuracy.Top1);
            Assert.True(GoldenSet.SamePath(goldenFile, r.Golden));
            Assert.Equal(30, r.Samples);
        }

        [Fact]
        public void ParsesGoldenOptions()
        {
            var o = EngineOptions.Parse(new[] { "bench", "--golden", "g.jsonl", "--emit-samples", "s.jsonl" }, out var error);
            Assert.Null(error);
            Assert.Equal("g.jsonl", o.BenchGolden);
            Assert.Equal("s.jsonl", o.BenchEmitSamples);
        }
    }
}
