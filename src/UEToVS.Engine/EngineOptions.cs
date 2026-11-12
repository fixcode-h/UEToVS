using System;
using System.Collections.Generic;
using System.IO;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine
{
    /// <summary>Command line of UEToVS.Engine.</summary>
    public sealed class EngineOptions
    {
        public const string Usage =
@"UEToVS.Engine — out-of-process code engine for the UEToVS Visual Studio extension

Server mode (started by the extension):
  UEToVS.Engine --pipe <name> --solution-dir <dir> [--project <x.uproject>] [--engine <root>]
                [--parent <pid>] [--log-level info]

CLI mode (debugging / CI): index, answer, exit
  UEToVS.Engine --solution-dir <dir> --query <text> [--all] [--max <n>]
  UEToVS.Engine --solution-dir <dir> --status

Benchmark (see docs/基准测试.md): index (or reuse the index), time completion / go to definition / find references at
N random code positions, append the result to <solution>/.vs/UEToVS/bench/bench.jsonl and compare with the previous run
  UEToVS.Engine bench --project <x.uproject> [--samples 1000] [--seed 1] [--ops completion,definition,references]
                [--reindex] [--all] [--details] [--out <dir>] [--no-compare]

Options:
  --pipe <name>          serve IEngineService (MessagePack JSON-RPC) on this named pipe
  --solution-dir <dir>   folder holding the .sln; logs/index go to <dir>/.vs/UEToVS (default: project dir or cwd)
  --project <file>       .uproject (default: first *.uproject in the solution dir or its parents)
  --engine <root>        engine root override (folder containing Engine/)
  --parent <pid>         exit when this process exits
  --log-level <lvl>      Error | Warn | Info (default) | Debug | Trace
  --skip-engine          index the project only
  --watch-engine         watch engine folders for changes (default: on for Windows)
  --no-watch             no file system watchers (changes only via the host)
  --max-parallelism <n>  request worker threads (default: cores - 1)
  --cpp-threads <n>      C++ indexing threads (default: max(1, cores / 4)); the settings window overrides it
  --asset-threads <n>    Blueprint/asset scan threads (default: max(1, cores / 4))
  --during-build <mode>  indexing during a build: pause | single (default) | full
  --shared-engine-cache  keep the engine index in the shared per-engine folder (default: in the solution)
  --engine-cache-dir <d> root of the shared engine index (default: %LocalAppData%\UEToVS\EngineIndex)
  --exclude-dir <name>   folder name never indexed (repeatable; replaces Intermediate/Binaries/Saved/DerivedDataCache)
  --lua-dir <dir>        Lua script folder to index (repeatable; relative = to the project folder); the settings window overrides it
  --query <text>         CLI: print search results
  --all                  CLI: include engine / non-project results
  --max <n>              CLI: results per category (default 20)
  --status               CLI: print EngineStatus + performance snapshot as JSON
  bench                  benchmark mode (first argument); options below
  --samples <n>          bench: code positions to sample (default 1000)
  --seed <n>             bench: random seed (default 1; same seed + same index = same positions)
  --ops <list>           bench: comma-separated subset of completion,definition,references
  --reindex              bench: drop the C++ index and rebuild it (default: reuse the index on disk)
  --details              bench: also write every sample's timing to bench-<time>-samples.jsonl
  --out <dir>            bench: output folder (default <solution>/.vs/UEToVS/bench)
  --no-compare           bench: do not compare with the previous run
  --help                 this text";

        public string PipeName { get; set; }
        public string SolutionDir { get; set; }
        public string ProjectFile { get; set; }
        public string EngineRoot { get; set; }
        public int? ParentPid { get; set; }
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
        public bool SkipEngine { get; set; }
        public bool WatchEngine { get; set; } = OperatingSystem.IsWindows();
        public bool NoWatch { get; set; }
        public int MaxParallelism { get; set; }
        public int CppThreads { get; set; }
        public int AssetThreads { get; set; }
        public int DuringBuild { get; set; } = 1;
        public bool SharedEngineCache { get; set; }
        public string EngineCacheDirectory { get; set; }
        public List<string> ExcludedFolders { get; set; }
        public List<string> LuaScriptPaths { get; set; }

        /// <summary>Initial indexing settings from the command line (the host's settings replace them at runtime).</summary>
        public IndexingSettings InitialIndexingSettings() => new IndexingSettings
        {
            CppThreads = CppThreads,
            AssetThreads = AssetThreads,
            DuringBuild = DuringBuild,
            IncludeEngine = !SkipEngine,
            SharedEngineCache = SharedEngineCache,
            EngineCacheDirectory = EngineCacheDirectory,
            ExcludedFolders = ExcludedFolders,
            LuaScriptPaths = LuaScriptPaths,
        };
        public string Query { get; set; }
        public bool QueryAll { get; set; }
        public int QueryMax { get; set; } = 20;
        public bool Status { get; set; }
        public bool Help { get; set; }

        // ---- v0.4.5 bench (Cli/BenchRunner.cs)
        public bool Bench { get; set; }
        public int BenchSamples { get; set; } = 1000;
        public int BenchSeed { get; set; } = 1;
        public List<string> BenchOps { get; set; }
        public bool BenchReindex { get; set; }
        public bool BenchDetails { get; set; }
        public string BenchOut { get; set; }
        public bool BenchCompare { get; set; } = true;

        public bool IsCli => Query != null || Status || Bench;

        /// <summary>Parses arguments; returns null and an error message on invalid input.</summary>
        public static EngineOptions Parse(IReadOnlyList<string> args, out string error)
        {
            error = null;
            var o = new EngineOptions();
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                string Next()
                {
                    if (i + 1 >= args.Count) throw new ArgumentException("Missing value for " + a);
                    return args[++i];
                }
                try
                {
                    switch (a)
                    {
                        case "--pipe": o.PipeName = Next(); break;
                        case "--solution-dir": o.SolutionDir = Next(); break;
                        case "--project": o.ProjectFile = Next(); break;
                        case "--engine": o.EngineRoot = Next(); break;
                        case "--parent":
                            o.ParentPid = int.TryParse(Next(), out int pid) ? pid : throw new ArgumentException("--parent expects a process id");
                            break;
                        case "--log-level":
                            o.LogLevel = Log.TryParseLevel(Next(), out var lvl) ? lvl : throw new ArgumentException("Unknown log level: " + args[i]);
                            break;
                        case "--skip-engine": o.SkipEngine = true; break;
                        case "--watch-engine": o.WatchEngine = true; break;
                        case "--no-watch": o.NoWatch = true; break;
                        case "--max-parallelism":
                            o.MaxParallelism = int.TryParse(Next(), out int mp) && mp > 0 ? mp : throw new ArgumentException("--max-parallelism expects a positive number");
                            break;
                        case "--cpp-threads":
                            o.CppThreads = int.TryParse(Next(), out int ct) && ct > 0 ? ct : throw new ArgumentException("--cpp-threads expects a positive number");
                            break;
                        case "--asset-threads":
                            o.AssetThreads = int.TryParse(Next(), out int at) && at > 0 ? at : throw new ArgumentException("--asset-threads expects a positive number");
                            break;
                        case "--during-build":
                            o.DuringBuild = Next().ToLowerInvariant() switch
                            {
                                "pause" or "0" => 0,
                                "single" or "1" => 1,
                                "full" or "2" => 2,
                                _ => throw new ArgumentException("--during-build expects pause, single or full"),
                            };
                            break;
                        case "--shared-engine-cache": o.SharedEngineCache = true; break;
                        case "--engine-cache-dir": o.EngineCacheDirectory = Next(); break;
                        case "--exclude-dir": (o.ExcludedFolders ??= new List<string>()).Add(Next()); break;
                        case "--lua-dir": (o.LuaScriptPaths ??= new List<string>()).Add(Next()); break;
                        case "--query": o.Query = Next(); break;
                        case "--all": o.QueryAll = true; break;
                        case "--max":
                            o.QueryMax = int.TryParse(Next(), out int mx) && mx > 0 ? mx : throw new ArgumentException("--max expects a positive number");
                            break;
                        case "--status": o.Status = true; break;
                        case "bench" when i == 0: o.Bench = true; break;
                        case "--samples":
                            o.BenchSamples = int.TryParse(Next(), out int bs) && bs > 0 ? bs : throw new ArgumentException("--samples expects a positive number");
                            break;
                        case "--seed":
                            o.BenchSeed = int.TryParse(Next(), out int seed) ? seed : throw new ArgumentException("--seed expects a number");
                            break;
                        case "--ops":
                            o.BenchOps = new List<string>();
                            foreach (var op in Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                o.BenchOps.Add(Array.IndexOf(Cli.BenchRunner.AllOps, op.ToLowerInvariant()) >= 0
                                    ? op.ToLowerInvariant()
                                    : throw new ArgumentException("--ops: unknown operation " + op + " (completion, definition, references)"));
                            break;
                        case "--reindex": o.BenchReindex = true; break;
                        case "--details": o.BenchDetails = true; break;
                        case "--out": o.BenchOut = Next(); break;
                        case "--no-compare": o.BenchCompare = false; break;
                        case "--help":
                        case "-h":
                        case "/?": o.Help = true; break;
                        default: throw new ArgumentException("Unknown argument: " + a);
                    }
                }
                catch (ArgumentException ex)
                {
                    error = ex.Message;
                    return null;
                }
            }
            if (o.Help) return o;
            if (o.PipeName == null && !o.IsCli)
            {
                error = "Either --pipe <name>, --query/--status or bench is required";
                return null;
            }
            if (string.IsNullOrEmpty(o.SolutionDir))
            {
                if (!string.IsNullOrEmpty(o.ProjectFile))
                    o.SolutionDir = Directory.Exists(o.ProjectFile) ? o.ProjectFile : Path.GetDirectoryName(Path.GetFullPath(o.ProjectFile));
                else o.SolutionDir = Directory.GetCurrentDirectory();
            }
            o.SolutionDir = Path.GetFullPath(o.SolutionDir);
            return o;
        }
    }
}
