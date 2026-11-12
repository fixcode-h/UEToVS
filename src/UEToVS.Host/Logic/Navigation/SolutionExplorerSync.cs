using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>
    /// Pure rules for "reveal the active C++ file in Solution Explorer" (Rider's Autoscroll from Source, scoped to C++):
    /// which documents qualify and whether a document switch should trigger a reveal.
    /// </summary>
    public static class SolutionExplorerSync
    {
        private static readonly HashSet<string> CppExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".h", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".ipp", ".tpp", ".inc",
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".ixx", ".cppm",
        };

        /// <summary>Delay after the last document switch before revealing (rapid Ctrl+Tab cycling reveals only the final file).</summary>
        public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

        /// <summary>True for C/C++ source and header paths (by extension; no I/O).</summary>
        public static bool IsCppFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext;
            try
            {
                ext = Path.GetExtension(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
            return !string.IsNullOrEmpty(ext) && CppExtensions.Contains(ext);
        }

        /// <summary>Whether switching the active document to <paramref name="path"/> should reveal it: the setting is on, a UE solution is open and the file is C++.</summary>
        public static bool ShouldReveal(bool enabled, bool unrealSolution, string path) => enabled && unrealSolution && IsCppFile(path);
    }
}
