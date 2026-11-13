using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UEToVS.Contracts;

namespace UEToVS.Engine.Navigation
{
    /// <summary>
    /// Where UHT expanded a <c>GENERATED_BODY()</c> of one class: the per-line macro
    /// (<c>#define FID_…_h_12_GENERATED_BODY</c>) in the class's <c>X.generated.h</c> under the module's
    /// <c>Intermediate/Build/&lt;Platform&gt;/&lt;Target&gt;/Inc/&lt;Module&gt;[/UHT]</c>. Found on demand with a handful of
    /// directory probes (no scan of Intermediate); null when the project has not been built or UHT output is elsewhere.
    /// </summary>
    public static class GeneratedBodyLocator
    {
        private static readonly HashSet<string> BodyMacros = new HashSet<string>(StringComparer.Ordinal)
        {
            "GENERATED_BODY", "GENERATED_UCLASS_BODY", "GENERATED_USTRUCT_BODY", "GENERATED_UINTERFACE_BODY", "GENERATED_IINTERFACE_BODY",
        };

        private static readonly Regex GeneratedInclude = new Regex(@"#\s*include\s*[""<]([^"">]+?)\.generated\.h["">]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsBodyMacro(string word) => word != null && BodyMacros.Contains(word);

        /// <summary>"Foo.generated.h" from the header's <c>#include "Foo.generated.h"</c>, or null.</summary>
        public static string GeneratedHeaderName(string headerText)
        {
            if (headerText == null) return null;
            var m = GeneratedInclude.Match(headerText);
            return m.Success ? Path.GetFileName(m.Groups[1].Value) + ".generated.h" : null;
        }

        /// <summary>Suffix of UHT's per-line macro for a body macro: _GENERATED_BODY or _GENERATED_BODY_LEGACY.</summary>
        public static string MacroSuffix(string word) =>
            word == "GENERATED_BODY" || word == "GENERATED_USTRUCT_BODY" ? "_GENERATED_BODY" : "_GENERATED_BODY_LEGACY";

        /// <summary>The expansion of the body macro used on 0-based <paramref name="line"/> of <paramref name="headerPath"/>, or null.</summary>
        public static SymbolInfo Find(string headerPath, string headerText, string word, int line)
        {
            if (!IsBodyMacro(word) || string.IsNullOrEmpty(headerPath) || headerText == null) return null;
            string genName = GeneratedHeaderName(headerText);
            if (genName == null) return null;
            string file = FindGeneratedHeader(headerPath, genName);
            if (file == null) return null;
            int defLine = FindDefineLine(File.ReadLines(file), line + 1, MacroSuffix(word));
            if (defLine < 0) return null;
            return new SymbolInfo
            {
                Name = word,
                Kind = SymbolKind.Macro,
                Container = genName,
                Signature = "UHT: " + genName,
                IsDefinition = true,
                Origin = ItemOrigin.Project,
                Location = new SourceLocation { FilePath = file, Line = defLine, Column = 0 },
            };
        }

        /// <summary>0-based line of <c>#define …_&lt;line1&gt;&lt;suffix&gt;</c>, or -1.</summary>
        public static int FindDefineLine(IEnumerable<string> lines, int line1, string suffix)
        {
            string want = "_" + line1.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
            int i = 0;
            foreach (var raw in lines)
            {
                var t = raw.TrimStart();
                if (t.StartsWith("#define", StringComparison.Ordinal))
                {
                    var name = t.Substring(7).TrimStart();
                    int end = 0;
                    while (end < name.Length && (char.IsLetterOrDigit(name[end]) || name[end] == '_')) end++;
                    if (name.Substring(0, end).EndsWith(want, StringComparison.Ordinal)) return i;
                }
                i++;
            }
            return -1;
        }

        /// <summary>
        /// <c>#include "X.generated.h"</c> → 所属模块最近一次 UHT 输出的那份 X.generated.h；不是 .generated.h 或还没编译过时 null。
        /// </summary>
        public static string ResolveGeneratedInclude(string includePath, string fromFile)
        {
            if (string.IsNullOrEmpty(includePath) || string.IsNullOrEmpty(fromFile) ||
                !includePath.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                return FindGeneratedHeader(fromFile, Path.GetFileName(includePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Newest <paramref name="genName"/> under the owning module's Intermediate/Build/*/*/Inc/&lt;Module&gt;[/UHT].</summary>
        public static string FindGeneratedHeader(string headerPath, string genName)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(headerPath));
            string module = null, root = null;
            for (var d = dir; d != null; d = Path.GetDirectoryName(d))
            {
                if (module == null)
                {
                    var build = SafeFiles(d, "*.Build.cs").FirstOrDefault();
                    if (build != null) module = Path.GetFileName(build).Replace(".Build.cs", string.Empty);
                }
                if (module != null && Directory.Exists(Path.Combine(d, "Intermediate")) &&
                    (SafeFiles(d, "*.uproject").Any() || SafeFiles(d, "*.uplugin").Any() || string.Equals(Path.GetFileName(d), "Engine", StringComparison.OrdinalIgnoreCase)))
                {
                    root = d;
                    break;
                }
            }
            if (module == null || root == null) return null;
            string buildDir = Path.Combine(root, "Intermediate", "Build");
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var platform in SafeDirs(buildDir))
                foreach (var target in SafeDirs(platform))
                    foreach (var candidate in new[] { Path.Combine(target, "Inc", module, "UHT", genName), Path.Combine(target, "Inc", module, genName) })
                    {
                        try
                        {
                            if (!File.Exists(candidate)) continue;
                            var t = File.GetLastWriteTimeUtc(candidate);
                            if (t > bestTime) { best = candidate; bestTime = t; }
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }
            return best;
        }

        private static IEnumerable<string> SafeFiles(string dir, string pattern)
        {
            try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : (IEnumerable<string>)Array.Empty<string>(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
    }
}
