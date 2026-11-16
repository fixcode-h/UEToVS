using System;
using System.IO;

namespace UEToVS.Core.Storage
{
    /// <summary>
    /// Where UEToVS keeps per-solution data: &lt;solution dir&gt;/.vs/UEToVS/{Index,Logs,Cache,Lua}.
    /// Deleting the .vs folder removes everything. When there is no solution directory (e.g. command-line use without
    /// one) a fallback under the user's local app data is used.
    /// </summary>
    public sealed class StoragePaths
    {
        public const string FolderName = "UEToVS";

        private StoragePaths(string root)
        {
            Root = root;
            Index = Path.Combine(root, "Index");
            Logs = Path.Combine(root, "Logs");
            Cache = Path.Combine(root, "Cache");
            Lua = Path.Combine(root, "Lua");
            Debug = Path.Combine(root, "Debug");
            LocalSettings = Path.Combine(root, "settings.local.json");
        }

        public string Root { get; }
        public string Index { get; }
        public string Logs { get; }
        public string Cache { get; }

        /// <summary>Lua script index (lua-index.json), written by the engine and read by the MCP server.</summary>
        public string Lua { get; }
        public string LocalSettings { get; }

        /// <summary>
        /// VS debug control (v0.8.0): endpoint.json written by the VS host while a UE solution is open, so the MCP server and
        /// the Agent panel can find the pipe that drives VS's debugger. Removed when the solution closes.
        /// </summary>
        public string Debug { get; }

        /// <summary>The debug control endpoint file (<see cref="Debug"/>\endpoint.json).</summary>
        public string DebugEndpoint => Path.Combine(Debug, "endpoint.json");

        /// <summary>Paths for a solution directory (the folder holding the .sln).</summary>
        public static StoragePaths ForSolution(string solutionDir)
        {
            if (string.IsNullOrEmpty(solutionDir)) return Fallback();
            return new StoragePaths(Path.Combine(Path.GetFullPath(solutionDir), ".vs", FolderName));
        }

        /// <summary>Per-user fallback used when no solution directory is known.</summary>
        public static StoragePaths Fallback()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
            return new StoragePaths(Path.Combine(baseDir, FolderName, "NoSolution"));
        }

        /// <summary>Creates all directories; returns this for chaining. Never throws (logging must keep working).</summary>
        public StoragePaths EnsureCreated()
        {
            foreach (var d in new[] { Root, Index, Logs, Cache })
            {
                try { Directory.CreateDirectory(d); }
                catch { /* reported by the caller when it tries to write */ }
            }
            return this;
        }
    }
}
