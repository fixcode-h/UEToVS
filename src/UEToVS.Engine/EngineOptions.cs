using System;
using System.Collections.Generic;
using System.IO;
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
  --max-parallelism <n>  worker threads (default: cores - 1)
  --query <text>         CLI: print search results
  --all                  CLI: include engine / non-project results
  --max <n>              CLI: results per category (default 20)
  --status               CLI: print EngineStatus + performance snapshot as JSON
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
        public string Query { get; set; }
        public bool QueryAll { get; set; }
        public int QueryMax { get; set; } = 20;
        public bool Status { get; set; }
        public bool Help { get; set; }

        public bool IsCli => Query != null || Status;

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
                        case "--query": o.Query = Next(); break;
                        case "--all": o.QueryAll = true; break;
                        case "--max":
                            o.QueryMax = int.TryParse(Next(), out int mx) && mx > 0 ? mx : throw new ArgumentException("--max expects a positive number");
                            break;
                        case "--status": o.Status = true; break;
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
                error = "Either --pipe <name> or --query/--status is required";
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
