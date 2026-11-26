using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEToVS.Host.Logic.Build
{
    /// <summary>
    /// v0.11.0 background UnrealBuildTool runs (design 后续规划-代码诊断与提示 1.7 and stage 3 C), the pure parts: which target, the
    /// command lines, and whether the include-path export is out of date.
    /// </summary>
    public static class BackgroundUbtPlan
    {
        /// <summary>
        /// The editor target of the project (what VS builds while developing): "&lt;Project&gt;Editor.Target.cs", else any
        /// "*Editor.Target.cs", else "&lt;Project&gt;.Target.cs". Null when there is none.
        /// </summary>
        public static string PickTarget(IEnumerable<string> targetFiles, string projectName)
        {
            var names = (targetFiles ?? Enumerable.Empty<string>())
                .Select(f => Path.GetFileName((f ?? string.Empty).Replace('\\', '/')))
                .Where(f => f.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Substring(0, f.Length - ".Target.cs".Length))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0) return null;
            string editor = (projectName ?? string.Empty) + "Editor";
            return names.FirstOrDefault(n => string.Equals(n, editor, StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault(n => n.EndsWith("Editor", StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault(n => string.Equals(n, projectName, StringComparison.OrdinalIgnoreCase))
                ?? names[0];
        }

        /// <summary>UBT JsonExport: every module's rules as UBT evaluated them (no compile).</summary>
        public static string ExportArgs(string target, string uproject, string outputFile) =>
            $"-Mode=JsonExport {target} Win64 Development -Project=\"{uproject}\" -OutputFile=\"{outputFile}\"";

        /// <summary>Compiles one file the way the build does (PCH, defines, include paths); -WaitMutex queues behind another UBT.</summary>
        public static string SingleFileArgs(string target, string uproject, string file) =>
            $"{target} Win64 Development -Project=\"{uproject}\" -SingleFile=\"{file}\" -NoHotReloadFromIDE -WaitMutex";

        /// <summary>The export is missing or older than one of the Build.cs / Target.cs files.</summary>
        public static bool ExportOutOfDate(DateTime? exportUtc, IEnumerable<DateTime> rulesUtc)
        {
            if (exportUtc == null) return true;
            foreach (var t in rulesUtc ?? Enumerable.Empty<DateTime>())
                if (t > exportUtc.Value) return true;
            return false;
        }

        /// <summary>A file the save check compiles: a project .cpp / .c / .cc (headers have no compile of their own).</summary>
        public static bool IsCompilable(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext == ".cpp" || ext == ".cc" || ext == ".cxx" || ext == ".c";
        }

        /// <summary>UBT refused because the editor's Live Coding owns the build (nothing to show as errors).</summary>
        public static bool IsLiveCodingRefusal(string line) =>
            line != null && line.IndexOf("Live Coding", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (line.IndexOf("Unable to build", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("is active", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
