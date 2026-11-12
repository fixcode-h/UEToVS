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

        /// <summary>
        /// New vertical offset that puts an item in the middle of the tree's viewport. <paramref name="itemTop"/> is the item's
        /// top relative to the viewport (pixels). Physical scrolling: offsets in pixels. Logical scrolling (virtualized tree,
        /// CanContentScroll): offsets in items, so the pixel distance is converted with the item height. Returns null when the
        /// item is already within the middle third (no jump for a small move) or the input is unusable.
        /// </summary>
        public static double? CenteredOffset(double currentOffset, double itemTop, double itemHeight, double viewportPixels, bool logicalScrolling,
                                             double maxOffset)
        {
            if (itemHeight <= 0 || viewportPixels <= 0 || double.IsNaN(itemTop) || double.IsInfinity(itemTop)) return null;
            double center = itemTop + itemHeight / 2;
            if (center >= viewportPixels / 3 && center <= viewportPixels * 2 / 3) return null;
            double deltaPixels = center - viewportPixels / 2;
            double delta = logicalScrolling ? Math.Round(deltaPixels / itemHeight) : deltaPixels;
            double target = Math.Max(0, Math.Min(maxOffset < 0 ? 0 : maxOffset, currentOffset + delta));
            return Math.Abs(target - currentOffset) < (logicalScrolling ? 1 : 1.0) ? (double?)null : target;
        }
    }
}
