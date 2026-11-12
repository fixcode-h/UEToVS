using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Host.Logic.Editing
{
    public enum StepFilterAction
    {
        None,
        Write,
        Delete,
    }

    /// <summary>
    /// Where and whether the bundled <c>UEToVS.natstepfilter</c> is deployed (VS reads *.natstepfilter from the per-user
    /// <c>Documents\Visual Studio &lt;version&gt;\Visualizers</c> folder; a VSIX asset cannot carry one). Pure decisions; the host
    /// does the file IO.
    /// </summary>
    public static class StepFilterPlan
    {
        public const string FileName = "UEToVS.natstepfilter";

        /// <summary>Marker in the bundled file: only a file carrying it is ever overwritten or deleted.</summary>
        public const string Marker = "UEToVS debug step filter";

        /// <summary>Documents sub-folder names VS of major version <paramref name="vsMajor"/> may use (first = the one to create).</summary>
        public static IReadOnlyList<string> FolderNames(int vsMajor)
        {
            switch (vsMajor)
            {
                case 16: return new[] { "Visual Studio 2019" };
                case 17: return new[] { "Visual Studio 2022" };
                case 18: return new[] { "Visual Studio 18", "Visual Studio 2026" };
                default: return vsMajor > 18 ? new[] { "Visual Studio " + vsMajor } : Array.Empty<string>();
            }
        }

        /// <summary>
        /// Target files: <c>Visualizers\UEToVS.natstepfilter</c> under every existing candidate folder, else under the first one
        /// (created). Empty when the documents folder or the VS version is unknown.
        /// </summary>
        public static List<string> Targets(string documents, int vsMajor, Func<string, bool> directoryExists)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(documents)) return result;
            var names = FolderNames(vsMajor);
            foreach (var n in names)
            {
                string dir = Path.Combine(documents, n);
                if (directoryExists != null && directoryExists(dir)) result.Add(Path.Combine(dir, "Visualizers", FileName));
            }
            if (result.Count == 0 && names.Count > 0) result.Add(Path.Combine(documents, names[0], "Visualizers", FileName));
            return result;
        }

        /// <summary>What to do with one target given the setting, its current content (null = absent) and the bundled content.</summary>
        public static StepFilterAction Decide(bool enabled, string existing, string bundled)
        {
            if (enabled)
            {
                if (string.IsNullOrEmpty(bundled)) return StepFilterAction.None;
                if (existing == null) return StepFilterAction.Write;
                if (string.Equals(Normalize(existing), Normalize(bundled), StringComparison.Ordinal)) return StepFilterAction.None;
                return IsOurs(existing) ? StepFilterAction.Write : StepFilterAction.None; // a user's own file of the same name stays
            }
            return existing != null && IsOurs(existing) ? StepFilterAction.Delete : StepFilterAction.None;
        }

        public static bool IsOurs(string content) => content != null && content.IndexOf(Marker, StringComparison.Ordinal) >= 0;

        private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
    }
}
