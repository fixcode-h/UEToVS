using System;
using System.IO;

namespace UEToVS.Core.Storage
{
    /// <summary>
    /// Where UEToVS keeps per-solution data: &lt;solution dir&gt;/.vs/UEToVS/{Index,Logs,Cache}.
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
            LocalSettings = Path.Combine(root, "settings.local.json");
        }

        public string Root { get; }
        public string Index { get; }
        public string Logs { get; }
        public string Cache { get; }
        public string LocalSettings { get; }

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
