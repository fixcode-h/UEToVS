using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UEToVS.Host.Logic.Engine
{
    /// <summary>Where the engine executable is and how it is started. Pure logic.</summary>
    public static class EngineLaunch
    {
        public const string ExeName = "UEToVS.Engine.exe";
        public const string FolderName = "Engine";

        /// <summary>Exit codes of the .NET host (apphost) meaning "runtime not found / framework missing".</summary>
        public static readonly int[] MissingRuntimeExitCodes =
        {
            unchecked((int)0x80008083), // FrameworkMissingFailure (older hosts)
            unchecked((int)0x80008096), // FrameworkMissingFailure
        };

        /// <summary>
        /// Resolves the engine path: the override (a file, or a folder containing <see cref="ExeName"/>) when set, else
        /// &lt;extension dir&gt;\Engine\UEToVS.Engine.exe. <paramref name="source"/> is "override" or "bundled".
        /// </summary>
        public static string ResolveExecutable(string extensionDir, string overridePath, out string source)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                source = "override";
                string p = Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
                return Directory.Exists(p) ? Path.Combine(p, ExeName) : p;
            }
            source = "bundled";
            return Path.Combine(extensionDir ?? string.Empty, FolderName, ExeName);
        }

        /// <summary>
        /// Command line (contract with UEToVS.Engine):
        /// --pipe X --solution-dir "D" [--project "P.uproject"] [--engine "UE root"] --parent PID --log-level level.
        /// </summary>
        public static string BuildArguments(string pipeName, string solutionDir, int parentPid, string logLevel,
                                            string projectFile = null, string engineRoot = null)
        {
            var args = new List<string> { "--pipe", pipeName, "--solution-dir", solutionDir };
            if (!string.IsNullOrWhiteSpace(projectFile)) { args.Add("--project"); args.Add(projectFile); }
            if (!string.IsNullOrWhiteSpace(engineRoot)) { args.Add("--engine"); args.Add(engineRoot.Trim()); }
            args.Add("--parent");
            args.Add(parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--log-level");
            args.Add((logLevel ?? "info").ToLowerInvariant());
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a ?? string.Empty));
            }
            return sb.ToString();
        }

        /// <summary>Quotes one argument per the Windows CommandLineToArgvW rules.</summary>
        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static bool IsMissingRuntimeExitCode(int code) => Array.IndexOf(MissingRuntimeExitCodes, code) >= 0;
    }
}
