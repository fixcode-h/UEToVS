using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Unreal;

namespace UEToVS.Host.Logic.Unreal
{
    /// <summary>An #include of a source file (0-based line).</summary>
    public sealed class IncludeRef
    {
        public IncludeRef(string path, int line)
        {
            Path = path;
            Line = line;
        }

        public string Path { get; }
        public int Line { get; }
    }

    /// <summary>An include whose module the including module cannot see (missing Build.cs dependency).</summary>
    public sealed class MissingDependency
    {
        public string Include { get; set; }
        public int Line { get; set; }
        public string Module { get; set; }
        public string OwnerModule { get; set; }
        public string OwnerBuildCsPath { get; set; }

        public string Key => OwnerModule + "|" + Module;
    }

    /// <summary>What the checker needs from the outside (index lookups, file system). All lookups may be slow/async.</summary>
    public interface IIncludeEnvironment
    {
        /// <summary>Absolute paths of indexed files named like the include's last segment (e.g. "Actor.h"), any module.</summary>
        Task<IReadOnlyList<string>> FindFilesByNameAsync(string fileName, CancellationToken ct);

        /// <summary>The Build.cs that owns <paramref name="filePath"/>, or null.</summary>
        string FindBuildCsForFile(string filePath);

        /// <summary>The Build.cs of module <paramref name="module"/>, or null when unknown.</summary>
        Task<string> FindBuildCsForModuleAsync(string module, CancellationToken ct);

        /// <summary>Text of a file (open editor buffer first), or null.</summary>
        string ReadText(string path);

        bool FileExists(string path);
    }

    /// <summary>
    /// Detects #includes whose header lives in a module the including module does not depend on (the classic
    /// "cannot open include file" / LNK2019 after adding an include). Port of the prototype indexer's CheckIncludes,
    /// running in the host over index lookups. Pure apart from <see cref="IIncludeEnvironment"/>.
    /// </summary>
    public static class IncludeDependencyChecker
    {
        private static readonly Logger L = Log.For("BuildCs");

        public static bool IsCppFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".cpp" || ext == ".inl" || ext == ".cc" || ext == ".cxx";
        }

        public static List<IncludeRef> ParseIncludes(string text)
        {
            var list = new List<IncludeRef>();
            if (string.IsNullOrEmpty(text)) return list;
            using (var r = new StringReader(text))
            {
                string line;
                int i = 0;
                while ((line = r.ReadLine()) != null)
                {
                    var inc = IncludeDirective.Parse(line);
                    if (inc != null) list.Add(new IncludeRef(inc, i));
                    i++;
                }
            }
            return list;
        }

        public static async Task<List<MissingDependency>> CheckAsync(string filePath, IReadOnlyList<IncludeRef> includes, IIncludeEnvironment env, CancellationToken ct)
        {
            var result = new List<MissingDependency>();
            if (string.IsNullOrEmpty(filePath) || includes == null || includes.Count == 0) return result;
            string ownerCs = env.FindBuildCsForFile(filePath);
            if (ownerCs == null)
            {
                L.Debug("buildcs.check.noOwner", null, ("file", filePath));
                return result;
            }
            string owner = BuildCsFile.ModuleNameFromPath(ownerCs);

            // Build.cs of every module reachable through public dependencies (loaded lazily, cached).
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [owner] = ownerCs };
            var files = new Dictionary<string, BuildCsFile>(StringComparer.OrdinalIgnoreCase);
            async Task Preload(string module)
            {
                var queue = new Queue<string>();
                queue.Enqueue(module);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (queue.Count > 0 && seen.Count < 256)
                {
                    string m = queue.Dequeue();
                    if (!seen.Add(m)) continue;
                    if (!paths.TryGetValue(m, out var p))
                    {
                        p = await env.FindBuildCsForModuleAsync(m, ct).ConfigureAwait(false);
                        paths[m] = p;
                    }
                    if (p == null) { files[m] = null; continue; }
                    if (!files.TryGetValue(m, out var f))
                    {
                        string text = env.ReadText(p);
                        f = text == null ? null : new BuildCsFile(m, text);
                        files[m] = f;
                    }
                    if (f == null) continue;
                    // The owner's direct dependencies and, transitively, the public dependencies of those.
                    var next = m.Equals(module, StringComparison.OrdinalIgnoreCase)
                        ? f.DirectlyVisibleModules()
                        : f.PublicDependencies.Concat(f.PublicIncludePathModules);
                    foreach (var d in next) queue.Enqueue(d);
                }
            }
            await Preload(owner).ConfigureAwait(false);
            var visible = BuildCsFile.VisibleModules(owner, m => files.TryGetValue(m, out var f) ? f : null);
            string dir = System.IO.Path.GetDirectoryName(filePath);

            foreach (var inc in includes)
            {
                ct.ThrowIfCancellationRequested();
                if (inc?.Path == null || inc.Path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)) continue;
                // A header next to the including file (relative include) is always fine.
                if (dir != null && env.FileExists(System.IO.Path.Combine(dir, inc.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)))) continue;
                string name = System.IO.Path.GetFileName(inc.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                var candidates = (await env.FindFilesByNameAsync(name, ct).ConfigureAwait(false) ?? Array.Empty<string>())
                    .Where(p => IncludeDirective.Matches(p, inc.Path))
                    .OrderByDescending(p => IsPublicHeaderPath(p))
                    .ToList();
                if (candidates.Count == 0) continue;
                string module = null;
                foreach (var c in candidates)
                {
                    var cs = env.FindBuildCsForFile(c);
                    if (cs == null) continue;
                    string m = BuildCsFile.ModuleNameFromPath(cs);
                    paths[m] = cs;
                    if (visible.Contains(m) || string.Equals(m, owner, StringComparison.OrdinalIgnoreCase)) { module = null; break; }
                    if (module == null) module = m;
                }
                if (module == null) continue;
                result.Add(new MissingDependency
                {
                    Include = inc.Path,
                    Line = inc.Line,
                    Module = module,
                    OwnerModule = owner,
                    OwnerBuildCsPath = ownerCs,
                });
            }
            L.Info("buildcs.check", null, ("file", filePath), ("owner", owner), ("includes", includes.Count), ("visible", visible.Count),
                   ("missing", result.Select(m => m.Module).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            return result;
        }

        private static bool IsPublicHeaderPath(string p)
        {
            string s = p.Replace('\\', '/');
            return s.IndexOf("/Public/", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("/Classes/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Adds the missing modules to the owner Build.cs text. Returns the new text and the modules actually added
        /// (empty when everything was already there).
        /// </summary>
        public static (string Text, List<string> Added) ApplyToText(string buildCsPath, string text, IEnumerable<string> modules, bool isPublic)
        {
            var file = new BuildCsFile(BuildCsFile.ModuleNameFromPath(buildCsPath), text ?? string.Empty);
            var added = new List<string>();
            foreach (var m in modules.Distinct(StringComparer.OrdinalIgnoreCase))
                if (file.AddDependency(m, isPublic)) added.Add(m);
            return (file.Text, added);
        }
    }
}
