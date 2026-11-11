using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Core.Unreal
{
    /// <summary>Names and files related through UE's reflection and file layout conventions.</summary>
    public static class RelatedNames
    {
        public const string ImplementationSuffix = "_Implementation";
        public const string ValidateSuffix = "_Validate";

        /// <summary>
        /// For an RPC / BlueprintNativeEvent "Server_Fire" returns "Server_Fire_Implementation" and
        /// "Server_Fire_Validate"; for "Server_Fire_Implementation" returns "Server_Fire" and "Server_Fire_Validate".
        /// </summary>
        public static IReadOnlyList<string> RelatedFunctionNames(string functionName)
        {
            if (string.IsNullOrEmpty(functionName)) return Array.Empty<string>();
            string baseName = BaseFunctionName(functionName);
            var list = new List<string>();
            foreach (var candidate in new[] { baseName, baseName + ImplementationSuffix, baseName + ValidateSuffix })
                if (!string.Equals(candidate, functionName, StringComparison.Ordinal)) list.Add(candidate);
            return list;
        }

        public static string BaseFunctionName(string functionName)
        {
            if (functionName.EndsWith(ImplementationSuffix, StringComparison.Ordinal))
                return functionName.Substring(0, functionName.Length - ImplementationSuffix.Length);
            if (functionName.EndsWith(ValidateSuffix, StringComparison.Ordinal))
                return functionName.Substring(0, functionName.Length - ValidateSuffix.Length);
            return functionName;
        }

        private static readonly string[] HeaderExts = { ".h", ".hpp", ".hh", ".hxx", ".inl" };
        private static readonly string[] SourceExts = { ".cpp", ".cc", ".cxx", ".c" };

        public static bool IsHeader(string path) => Array.IndexOf(HeaderExts, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        public static bool IsSource(string path) => Array.IndexOf(SourceExts, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        /// <summary>
        /// File names (not paths) that pair with <paramref name="path"/>: Foo.h ↔ Foo.cpp, plus Foo.generated.h for headers.
        /// </summary>
        public static IReadOnlyList<string> CounterpartFileNames(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".generated", StringComparison.OrdinalIgnoreCase))
            {
                string stem = name.Substring(0, name.Length - ".generated".Length);
                return new[] { stem + ".h", stem + ".cpp", stem + ".gen.cpp" };
            }
            if (name.EndsWith(".gen", StringComparison.OrdinalIgnoreCase))
            {
                string stem = name.Substring(0, name.Length - ".gen".Length);
                return new[] { stem + ".h", stem + ".cpp" };
            }
            var list = new List<string>();
            if (IsHeader(path))
            {
                foreach (var e in SourceExts) list.Add(name + e);
                list.Add(name + ".generated.h");
            }
            else
            {
                foreach (var e in HeaderExts) list.Add(name + e);
            }
            return list;
        }

        /// <summary>
        /// Candidate full paths for the counterpart, using UE's Public/Private/Classes layout, most likely first.
        /// </summary>
        public static IReadOnlyList<string> CounterpartPathCandidates(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            var dirs = new List<string> { dir };
            var swaps = IsHeader(path)
                ? new[] { ("Public", "Private"), ("Classes", "Private"), ("Inc", "Src") }
                : new[] { ("Private", "Public"), ("Private", "Classes"), ("Src", "Inc") };
            foreach (var (from, to) in swaps)
            {
                string swapped = SwapSegment(dir, from, to);
                if (swapped != null && !dirs.Contains(swapped)) dirs.Add(swapped);
            }
            var result = new List<string>();
            foreach (var d in dirs)
                foreach (var f in CounterpartFileNames(path))
                    if (!f.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase))
                        result.Add(Path.Combine(d, f));
            return result;
        }

        private static string SwapSegment(string dir, string from, string to)
        {
            var parts = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (!string.Equals(parts[i], from, StringComparison.OrdinalIgnoreCase)) continue;
                parts[i] = to;
                return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
            }
            return null;
        }
    }
}
