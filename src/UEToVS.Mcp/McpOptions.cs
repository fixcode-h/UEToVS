using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;

namespace UEToVS.Mcp
{
    /// <summary>Command line of UEToVS.Mcp.</summary>
    public sealed class McpOptions
    {
        public const string Usage =
@"UEToVS.Mcp — MCP server (stdio) giving AI agents UEToVS's C++ / Unreal index

  UEToVS.Mcp [--solution-dir <dir>] [--log-level info] [--source auto|engine|index]

Options:
  --solution-dir <dir>  folder holding the .sln (index and logs live in <dir>/.vs/UEToVS). Default: $UETOVS_SOLUTION_DIR,
                        else the nearest folder at or above the working directory that has .vs/UEToVS, a .sln or a .uproject
  --log-level <lvl>     Error | Warn | Info (default) | Debug | Trace; logs go to <dir>/.vs/UEToVS/Logs/mcp-*.log
  --source <s>          auto (default): the running UEToVS.Engine, else the index on disk
                        engine: running engine only; index: index on disk only
  --help                this text

Stdout carries MCP JSON-RPC only; nothing else is ever written to it.";

        public string SolutionDir { get; set; }
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
        public DataSourceMode Source { get; set; } = DataSourceMode.Auto;
        public bool Help { get; set; }

        /// <summary>How the solution dir was chosen (logged): argument, environment, discovered, cwd.</summary>
        public string SolutionDirMethod { get; set; }

        public static McpOptions Parse(IReadOnlyList<string> args, out string error, string cwd = null, Func<string, string> env = null)
        {
            error = null;
            env = env ?? Environment.GetEnvironmentVariable;
            var o = new McpOptions();
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
                        case "--solution-dir": o.SolutionDir = Next(); o.SolutionDirMethod = "argument"; break;
                        case "--log-level":
                            o.LogLevel = Log.TryParseLevel(Next(), out var lvl) ? lvl : throw new ArgumentException("Unknown log level: " + args[i]);
                            break;
                        case "--source":
                            o.Source = Next().ToLowerInvariant() switch
                            {
                                "auto" => DataSourceMode.Auto,
                                "engine" => DataSourceMode.EngineOnly,
                                "index" => DataSourceMode.IndexOnly,
                                _ => throw new ArgumentException("--source expects auto, engine or index"),
                            };
                            break;
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
            if (string.IsNullOrWhiteSpace(o.SolutionDir))
            {
                string fromEnv = env("UETOVS_SOLUTION_DIR");
                if (!string.IsNullOrWhiteSpace(fromEnv))
                {
                    o.SolutionDir = fromEnv;
                    o.SolutionDirMethod = "environment";
                }
                else
                {
                    string start = cwd ?? Directory.GetCurrentDirectory();
                    o.SolutionDir = DiscoverSolutionDir(start, out bool found) ?? start;
                    o.SolutionDirMethod = found ? "discovered" : "cwd";
                }
            }
            o.SolutionDir = Path.GetFullPath(o.SolutionDir.Trim().Trim('"'));
            return o;
        }

        /// <summary>
        /// Walks up from <paramref name="start"/>: first a folder that already has .vs/UEToVS (indexed by the extension),
        /// otherwise the nearest folder with a .sln, otherwise one with a .uproject.
        /// </summary>
        public static string DiscoverSolutionDir(string start, out bool found)
        {
            found = false;
            if (string.IsNullOrEmpty(start)) return null;
            string sln = null, uproject = null;
            for (var d = new DirectoryInfo(Path.GetFullPath(start)); d != null; d = d.Parent)
            {
                try
                {
                    if (Directory.Exists(Path.Combine(d.FullName, ".vs", StoragePaths.FolderName)))
                    {
                        found = true;
                        return d.FullName;
                    }
                    if (sln == null && d.EnumerateFiles("*.sln").Any()) sln = d.FullName;
                    if (uproject == null && d.EnumerateFiles("*.uproject").Any()) uproject = d.FullName;
                }
                catch (Exception)
                {
                    // unreadable folder: keep walking
                }
            }
            string pick = sln ?? uproject;
            found = pick != null;
            return pick;
        }
    }

    public enum DataSourceMode
    {
        Auto = 0,
        EngineOnly = 1,
        IndexOnly = 2,
    }
}
