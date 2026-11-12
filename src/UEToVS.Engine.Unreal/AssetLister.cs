using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;

namespace UEToVS.Engine.Unreal
{
    /// <summary>Lists .uasset / .umap files under content roots and maps them to UE object paths.</summary>
    public static class AssetLister
    {
        /// <summary>
        /// One-file-per-actor folders of World Partition levels: thousands of hashed names, never useful in search.
        /// </summary>
        private static readonly HashSet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "__ExternalActors__", "__ExternalObjects__",
        };

        public static bool IsAsset(string path) =>
            path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

        public static List<AssetItem> List(IEnumerable<IndexRoot> contentRoots, int maxParallelism = 0, CancellationToken ct = default)
        {
            var bag = new ConcurrentBag<AssetItem>();
            var seeds = contentRoots.Where(r => r.Kind == RootKind.Content && Directory.Exists(r.Path)).Select(r => (Dir: r.Path, Root: r)).ToList();
            ParallelDirectoryWalk.Run(seeds, item =>
            {
                FileSystemInfo[] entries;
                try { entries = new DirectoryInfo(item.Dir).GetFileSystemInfos(); }
                catch (Exception) { return Array.Empty<(string, IndexRoot)>(); }
                List<(string, IndexRoot)> subs = null;
                foreach (var e in entries)
                {
                    if (e is DirectoryInfo d)
                    {
                        if (SkippedDirectories.Contains(d.Name) || d.Name.StartsWith(".", StringComparison.Ordinal) || (d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        (subs ??= new List<(string, IndexRoot)>()).Add((d.FullName, item.Root));
                    }
                    else if (IsAsset(e.Name))
                    {
                        bag.Add(Create(e.FullName, item.Root));
                    }
                }
                return (IEnumerable<(string, IndexRoot)>)subs ?? Array.Empty<(string, IndexRoot)>();
            }, maxParallelism, ct);
            return bag.OrderBy(a => a.ObjectPath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static AssetItem Create(string file, IndexRoot root)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            return new AssetItem
            {
                Name = name,
                FilePath = file,
                ObjectPath = ObjectPath(root.ContentMount, root.Path, file),
                ClassName = file.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ? "World" : null,
                Origin = root.Origin,
            };
        }

        /// <summary>"/Game/Characters/BP_Hero.BP_Hero" for Content/Characters/BP_Hero.uasset mounted at /Game.</summary>
        public static string ObjectPath(string mount, string contentRoot, string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string rel = Path.GetRelativePath(contentRoot, Path.GetDirectoryName(file)).Replace('\\', '/');
            string package = (mount ?? "/Game").TrimEnd('/') + (rel == "." ? "" : "/" + rel) + "/" + name;
            return package + "." + name;
        }
    }
}
