using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Cli
{
    /// <summary>One sampled code position (an identifier token of a project file).</summary>
    public sealed class BenchSample
    {
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Word { get; set; }
    }

    /// <summary>
    /// <c>UEToVS.Engine bench</c>: index a real project (reusing the index on disk unless --reindex), pick N random
    /// identifier positions (seeded), and time completion, go to definition and find references at each of them with the
    /// same code paths as the RPC handlers. Writes one JSON line to &lt;solution&gt;\.vs\UEToVS\bench\bench.jsonl and prints
    /// the table with the change against the previous run of the same project. See docs/基准测试.md.
    /// </summary>
    public static class BenchRunner
    {
        private static readonly Logger L = Log.For("Bench");

        public const string OpCompletion = "completion";
        public const string OpDefinition = "definition";
        public const string OpReferences = "references";
        public static readonly string[] AllOps = { OpCompletion, OpDefinition, OpReferences };

        public static async Task<int> RunAsync(EngineHost host, EngineOptions options, TextWriter output = null)
        {
            output ??= Console.Out;
            var total = Stopwatch.StartNew();
            using var memory = new MemorySampler();

            bool hadIndex = !options.BenchReindex && HasIndexOnDisk(host.Paths.Index);
            var indexing = Stopwatch.StartNew();
            _ = host.StartIndexing();
            if (options.BenchReindex) host.RebuildIndex(RebuildScope.Cpp);
            bool ready;
            try { ready = await host.Index.WaitForReadyAsync(host.ShutdownToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                output.WriteLine("cancelled: " + host.ShutdownReason);
                return ExitCodes.Fatal;
            }
            indexing.Stop();
            if (!ready)
            {
                output.WriteLine("index failed: " + host.Index.LastError);
                return ExitCodes.Fatal;
            }

            var ops = options.BenchOps?.Count > 0 ? options.BenchOps : AllOps.ToList();
            var samples = PickSamples(host.Index.Files, host.Navigation.Documents, options.BenchSamples, options.BenchSeed, options.QueryAll, host.ShutdownToken);
            if (samples.Count == 0)
            {
                output.WriteLine("no project source files with identifiers to sample (use --all to include engine files)");
                return ExitCodes.CliNoResult;
            }
            L.Info("bench.start", null, ("project", host.Index.Workspace?.ProjectFile), ("samples", samples.Count), ("seed", options.BenchSeed),
                ("ops", ops), ("indexMs", Math.Round(indexing.Elapsed.TotalMilliseconds)), ("reindex", options.BenchReindex));

            var report = new BenchReport
            {
                EngineVersion = EngineHost.Version,
                Project = host.Index.Workspace?.ProjectFile ?? options.ProjectFile ?? options.SolutionDir,
                Seed = options.BenchSeed,
                Samples = samples.Count,
                Reindexed = options.BenchReindex,
                IndexReused = hadIndex,
                IndexMs = indexing.Elapsed.TotalMilliseconds,
                FilesParsed = host.Index.FilesParsed,
                Files = host.Index.Files.Count,
                Symbols = host.Index.Backend.GetStats().Symbols,
            };
            var details = options.BenchDetails ? new List<string>() : null;
            foreach (var op in ops)
            {
                var times = new List<double>(samples.Count);
                int empty = 0, errors = 0;
                foreach (var s in samples)
                {
                    host.ShutdownToken.ThrowIfCancellationRequested();
                    var sw = Stopwatch.StartNew();
                    int results;
                    try { results = Run(host, op, s, host.ShutdownToken); }
                    catch (OperationCanceledException) when (host.ShutdownToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        results = -1;
                        errors++;
                        L.Warn("bench.op.failed", ex.Message, ("op", op), ("file", s.File), ("line", s.Line), ("col", s.Column));
                    }
                    double ms = sw.Elapsed.TotalMilliseconds;
                    times.Add(ms);
                    if (results == 0) empty++;
                    details?.Add(new JsonObject
                    {
                        ["op"] = op, ["file"] = s.File, ["line"] = s.Line + 1, ["col"] = s.Column + 1, ["word"] = s.Word,
                        ["ms"] = Math.Round(ms, 3), ["results"] = results,
                    }.ToJsonString());
                }
                var stats = BenchOpStats.From(op, times, empty, errors);
                report.Ops.Add(stats);
                L.Info("bench.op", null, ("op", op), ("count", stats.Count), ("p50", stats.P50), ("p95", stats.P95), ("p99", stats.P99),
                    ("emptyRatio", stats.EmptyRatio), ("errors", errors));
            }

            memory.Sample();
            report.PeakWorkingSetMb = memory.PeakWorkingSet / 1048576.0;
            report.PeakPrivateMb = memory.PeakPrivate / 1048576.0;
            report.ManagedHeapMb = GC.GetTotalMemory(false) / 1048576.0;
            report.TotalMs = total.Elapsed.TotalMilliseconds;

            string dir = BenchDirectory(host, options);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, BenchReport.FileName);
            var previous = options.BenchCompare ? BenchReport.LoadPrevious(file, report.Project) : null;
            File.AppendAllText(file, report.ToJsonLine() + Environment.NewLine);
            if (details != null)
            {
                string detailFile = Path.Combine(dir, "bench-" + report.TimestampUtc.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-samples.jsonl");
                File.WriteAllLines(detailFile, details);
                output.WriteLine("samples:  " + detailFile);
            }
            output.WriteLine(report.Format(previous));
            output.WriteLine("written:  " + file);
            L.Info("bench.result", null, ("file", file), ("line", report.ToJsonLine()), ("previous", previous?.TimestampUtc.ToString("o")));
            return ExitCodes.Ok;
        }

        private static bool HasIndexOnDisk(string dir)
        {
            try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        public static string BenchDirectory(EngineHost host, EngineOptions options) =>
            string.IsNullOrEmpty(options.BenchOut) ? Path.Combine(host.Paths.Root, "bench") : Path.GetFullPath(options.BenchOut);

        /// <summary>Number of results of one operation at a sample (0 = empty).</summary>
        public static int Run(EngineHost host, string op, BenchSample s, CancellationToken ct)
        {
            switch (op)
            {
                case OpCompletion:
                {
                    // the user has typed the first half of the identifier
                    int typed = Math.Max(1, s.Word.Length / 2);
                    var caret = new CaretRequest { FilePath = s.File, Line = s.Line, Column = s.Column + typed };
                    return host.IntelliSense.GetCompletions(new CompletionRequest { Caret = caret }, ct).Items.Count;
                }
                case OpDefinition:
                {
                    var caret = new CaretRequest { FilePath = s.File, Line = s.Line, Column = s.Column + 1 };
                    return host.Navigation.Navigate(new NavigationRequest { Caret = caret, Kind = NavigationKind.Definition }, ct).Targets.Count;
                }
                case OpReferences:
                {
                    var caret = new CaretRequest { FilePath = s.File, Line = s.Line, Column = s.Column + 1 };
                    return host.Navigation.FindReferences(new ReferencesRequest { Caret = caret }, ct).Hits.Count;
                }
                default:
                    throw new ArgumentException("unknown op " + op);
            }
        }

        /// <summary>
        /// <paramref name="count"/> identifier positions: a random file (project .h/.cpp, sorted, seeded), then a random
        /// identifier token in it. Deterministic for a given index and seed.
        /// </summary>
        public static List<BenchSample> PickSamples(FileTable files, DocumentCache docs, int count, int seed, bool includeNonProject, CancellationToken ct)
        {
            var paths = files.Snapshot()
                .Where(e => e.File.Kind == SourceFileKind.Header || e.File.Kind == SourceFileKind.Source)
                .Where(e => includeNonProject || TypeText.IsProject(e.File.Origin))
                .Select(e => e.File.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var result = new List<BenchSample>(count);
            if (paths.Count == 0 || count <= 0) return result;
            var rnd = new Random(seed);
            var identifiers = new Dictionary<string, (DocumentModel Doc, int[] Tokens)>(StringComparer.Ordinal);
            int attempts = 0;
            while (result.Count < count && attempts++ < count * 20)
            {
                ct.ThrowIfCancellationRequested();
                string path = paths[rnd.Next(paths.Count)];
                if (!identifiers.TryGetValue(path, out var entry))
                {
                    var doc = docs.GetFile(path);
                    int[] ids = doc == null ? Array.Empty<int>() : Enumerable.Range(0, doc.Tokens.Length).Where(i => doc.Tokens[i].Kind == TokenKind.Identifier).ToArray();
                    entry = (doc, ids);
                    identifiers[path] = entry;
                }
                if (entry.Tokens.Length == 0) continue;
                int ti = entry.Tokens[rnd.Next(entry.Tokens.Length)];
                var (line, col) = entry.Doc.PositionOf(entry.Doc.Tokens[ti].Start);
                result.Add(new BenchSample { File = path, Line = line, Column = col, Word = entry.Doc.TokenText(ti) });
            }
            return result;
        }

        /// <summary>Samples the process working set / private bytes every 200 ms (peak values for the report).</summary>
        private sealed class MemorySampler : IDisposable
        {
            private readonly Timer _timer;
            private readonly Process _process = Process.GetCurrentProcess();
            private long _peakWs, _peakPrivate;

            public MemorySampler()
            {
                Sample();
                _timer = new Timer(_ => Sample(), null, 200, 200);
            }

            public long PeakWorkingSet
            {
                get
                {
                    long os = 0;
                    try { _process.Refresh(); os = _process.PeakWorkingSet64; } catch (Exception) { }
                    return Math.Max(Interlocked.Read(ref _peakWs), os);
                }
            }

            public long PeakPrivate => Interlocked.Read(ref _peakPrivate);

            public void Sample()
            {
                try
                {
                    lock (_process)
                    {
                        _process.Refresh();
                        Max(ref _peakWs, _process.WorkingSet64);
                        Max(ref _peakPrivate, _process.PrivateMemorySize64);
                    }
                }
                catch (Exception) { /* process info unavailable: peaks stay at what was seen */ }
            }

            private static void Max(ref long field, long value)
            {
                long cur;
                while (value > (cur = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, cur) != cur) { }
            }

            public void Dispose()
            {
                _timer.Dispose();
                _process.Dispose();
            }
        }
    }
}
