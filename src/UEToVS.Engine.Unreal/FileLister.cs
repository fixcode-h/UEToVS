using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;

namespace UEToVS.Engine.Unreal
{
    public enum SourceFileKind : byte
    {
        Header = 0,
        Source = 1,
        CSharp = 2,
        Shader = 3,
        Config = 4,
        Descriptor = 5,
    }

    /// <summary>One file found by <see cref="FileLister"/>.</summary>
    public sealed class ListedFile
    {
        public string Path { get; set; }
        public string Name { get; set; }

        /// <summary>Relative to the project root (project items) or the engine root (engine items).</summary>
        public string RelativePath { get; set; }

        public string Module { get; set; }
        public string Plugin { get; set; }
        public ItemOrigin Origin { get; set; }
        public SourceFileKind Kind { get; set; }
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }

        /// <summary>C/C++ declaration source the parser should look at (third-party: headers only).</summary>
        public bool IsCpp => Kind == SourceFileKind.Header || Kind == SourceFileKind.Source;
    }

    /// <summary>A module rules file found while listing.</summary>
    public sealed class FoundBuildCs
    {
        public string Path { get; set; }
        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }
        public string Plugin { get; set; }
    }

    public sealed class ListingResult
    {
        public List<ListedFile> Files { get; } = new List<ListedFile>();
        public List<FoundBuildCs> BuildCsFiles { get; } = new List<FoundBuildCs>();
        public int Directories { get; set; }
        public double ElapsedMs { get; set; }
    }

    /// <summary>
    /// Fast parallel listing of source/config files under index roots. Tracks the owning module (nearest *.Build.cs
    /// above a file within the root) and switches engine subtrees named "ThirdParty" to <see cref="ItemOrigin.ThirdParty"/>.
    /// </summary>
    public static class FileLister
    {
        private static readonly Dictionary<string, SourceFileKind> Extensions = new Dictionary<string, SourceFileKind>(StringComparer.OrdinalIgnoreCase)
        {
            [".h"] = SourceFileKind.Header,
            [".hpp"] = SourceFileKind.Header,
            [".inl"] = SourceFileKind.Header,
            [".cpp"] = SourceFileKind.Source,
            [".c"] = SourceFileKind.Source,
            [".cc"] = SourceFileKind.Source,
            [".cs"] = SourceFileKind.CSharp,
            [".ush"] = SourceFileKind.Shader,
            [".usf"] = SourceFileKind.Shader,
            [".hlsl"] = SourceFileKind.Shader,
            [".hlsli"] = SourceFileKind.Shader,
            [".ini"] = SourceFileKind.Config,
            [".uproject"] = SourceFileKind.Descriptor,
            [".uplugin"] = SourceFileKind.Descriptor,
        };

        public static readonly IReadOnlyCollection<string> DefaultSkippedDirectories = new[] { "Intermediate", "Binaries", "Saved", "DerivedDataCache" };

        private static volatile HashSet<string> _skippedDirectories = new HashSet<string>(DefaultSkippedDirectories, StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyCollection<string> ListedExtensions => Extensions.Keys;

        /// <summary>Folder names skipped by listing and change handling (settings window). Null or empty = defaults.</summary>
        public static IReadOnlyCollection<string> SkippedDirectories
        {
            get => _skippedDirectories;
            set => _skippedDirectories = new HashSet<string>(value == null || value.Count == 0 ? DefaultSkippedDirectories : value, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Configured folder names (default Intermediate, Binaries, Saved, DerivedDataCache) and any dot-folder (.git, .vs…).</summary>
        public static bool IsSkippedDirectory(string name) =>
            string.IsNullOrEmpty(name) || name[0] == '.' || _skippedDirectories.Contains(name);

        public static bool TryGetKind(string path, out SourceFileKind kind)
        {
            kind = default;
            string ext = System.IO.Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && Extensions.TryGetValue(ext, out kind);
        }

        /// <summary>
        /// True when a directory segment of <paramref name="path"/> below <paramref name="root"/> is skipped (e.g. a change
        /// under Intermediate). Segments above the root are ignored, so a project living in a dot-folder still works.
        /// </summary>
        public static bool IsInSkippedDirectory(string root, string path)
        {
            string rel = path;
            if (!string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) rel = path.Substring(root.Length);
            var parts = rel.Split('/', '\\');
            for (int i = 0; i < parts.Length - 1; i++)
                if (parts[i].Length > 0 && parts[i] != "." && parts[i] != ".." && IsSkippedDirectory(parts[i])) return true;
            return false;
        }

        private readonly struct DirItem
        {
            public DirItem(string dir, string module, ItemOrigin origin, IndexRoot root)
            {
                Dir = dir;
                Module = module;
                Origin = origin;
                Root = root;
            }

            public string Dir { get; }
            public string Module { get; }
            public ItemOrigin Origin { get; }
            public IndexRoot Root { get; }
        }

        /// <param name="extraFiles">Single files to list too (descriptor files at plugin/project roots that are not walked).</param>
        public static ListingResult List(Workspace ws, IEnumerable<IndexRoot> roots, IEnumerable<(string Path, ItemOrigin Origin)> extraFiles = null,
            int maxParallelism = 0, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new ListingResult();
            var files = new ConcurrentBag<ListedFile>();
            var buildCs = new ConcurrentBag<FoundBuildCs>();
            int dirs = 0;
            var rootList = roots.ToList();

            var seeds = rootList.Where(r => Directory.Exists(r.Path)).Select(r => new DirItem(r.Path, null, r.Origin, r)).ToList();
            ParallelDirectoryWalk.Run(seeds, item =>
            {
                Interlocked.Increment(ref dirs);
                FileSystemInfo[] entries;
                try
                {
                    entries = new DirectoryInfo(item.Dir).GetFileSystemInfos();
                }
                catch (Exception)
                {
                    return Array.Empty<DirItem>();
                }

                string module = item.Module;
                foreach (var e in entries)
                {
                    if (e is FileInfo && e.Name.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        module = e.Name.Substring(0, e.Name.Length - ".Build.cs".Length);
                        buildCs.Add(new FoundBuildCs { Path = e.FullName, Module = module, Origin = item.Origin, Plugin = item.Root.Plugin });
                        break;
                    }
                }

                List<DirItem> subdirs = null;
                foreach (var e in entries)
                {
                    if (e is DirectoryInfo d)
                    {
                        if (IsSkippedDirectory(d.Name) || (d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var origin = item.Origin;
                        if ((origin == ItemOrigin.Engine || origin == ItemOrigin.EnginePlugin) && string.Equals(d.Name, "ThirdParty", StringComparison.OrdinalIgnoreCase))
                            origin = ItemOrigin.ThirdParty;
                        (subdirs ??= new List<DirItem>()).Add(new DirItem(d.FullName, module, origin, item.Root));
                    }
                    else if (e is FileInfo f && TryGetKind(f.Name, out var kind))
                    {
                        files.Add(new ListedFile
                        {
                            Path = f.FullName,
                            Name = f.Name,
                            RelativePath = Relative(ws, item.Origin, f.FullName),
                            Module = module,
                            Plugin = item.Root.Plugin,
                            Origin = item.Origin,
                            Kind = kind,
                            Size = f.Length,
                            LastWriteUtcTicks = f.LastWriteTimeUtc.Ticks,
                        });
                    }
                }
                return (IEnumerable<DirItem>)subdirs ?? Array.Empty<DirItem>();
            }, maxParallelism, ct);

            foreach (var (path, origin) in extraFiles ?? Array.Empty<(string, ItemOrigin)>())
            {
                if (!TryGetKind(path, out var extraKind)) continue;
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) continue;
                    files.Add(new ListedFile
                    {
                        Path = fi.FullName,
                        Name = fi.Name,
                        RelativePath = Relative(ws, origin, fi.FullName),
                        Origin = origin,
                        Kind = extraKind,
                        Size = fi.Length,
                        LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                    });
                }
                catch (Exception) { }
            }

            // Deterministic order (parallel walk is not): by path.
            result.Files.AddRange(files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase));
            result.BuildCsFiles.AddRange(buildCs.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase));
            result.Directories = dirs;
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        public static string Relative(Workspace ws, ItemOrigin origin, string path)
        {
            string baseDir = origin == ItemOrigin.Project || origin == ItemOrigin.ProjectPlugin ? ws.ProjectRoot : ws.EngineRoot;
            if (string.IsNullOrEmpty(baseDir)) return System.IO.Path.GetFileName(path);
            try
            {
                return System.IO.Path.GetRelativePath(baseDir, path);
            }
            catch (Exception)
            {
                return System.IO.Path.GetFileName(path);
            }
        }
    }

    /// <summary>Level-synchronous parallel breadth-first walk: each level's directories are visited in parallel.</summary>
    public static class ParallelDirectoryWalk
    {
        public static void Run<T>(IEnumerable<T> seeds, Func<T, IEnumerable<T>> visit, int maxParallelism = 0, CancellationToken ct = default)
        {
            if (maxParallelism <= 0) maxParallelism = Math.Max(1, Environment.ProcessorCount - 1);
            var current = seeds.ToList();
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct };
            while (current.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var next = new ConcurrentBag<T>();
                if (current.Count == 1)
                {
                    foreach (var c in visit(current[0])) next.Add(c);
                }
                else
                {
                    Parallel.ForEach(current, options, item =>
                    {
                        foreach (var c in visit(item)) next.Add(c);
                    });
                }
                current = next.ToList();
            }
        }
    }
}
