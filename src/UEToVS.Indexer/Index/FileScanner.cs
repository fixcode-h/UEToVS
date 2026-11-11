using System;
using System.Collections.Generic;
using System.IO;
using UEToVS.Core.Protocol;
using UEToVS.Indexer.Unreal;

namespace UEToVS.Indexer.Index
{
    /// <summary>Walks index roots and classifies files. Tracks the owning module via *.Build.cs files.</summary>
    internal static class FileScanner
    {
        public static readonly HashSet<string> ParsedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".h", ".hpp", ".hh", ".hxx", ".inl", ".cpp", ".cc", ".cxx", ".c",
        };

        private static readonly HashSet<string> HeaderExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".h", ".hpp", ".hh", ".hxx", ".inl",
        };

        public static readonly HashSet<string> ListedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ini", ".usf", ".ush", ".hlsl", ".uplugin", ".uproject", ".json", ".py", ".lua", ".natvis", ".md", ".txt", ".xml", ".ispc",
        };

        private static readonly HashSet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Intermediate", "Binaries", "Saved", "DerivedDataCache", ".git", ".vs", ".idea", "node_modules", "__pycache__",
        };

        public static IEnumerable<FileRecord> Scan(IndexRoot root)
        {
            if (!Directory.Exists(root.Path)) yield break;
            var stack = new Stack<(string dir, string module)>();
            stack.Push((root.Path, null));
            bool thirdParty = root.Origin == ItemOrigin.ThirdParty;

            while (stack.Count > 0)
            {
                var (dir, inherited) = stack.Pop();
                string module = inherited;
                FileInfo[] files;
                DirectoryInfo[] subdirs;
                try
                {
                    var di = new DirectoryInfo(dir);
                    files = di.GetFiles();
                    subdirs = di.GetDirectories();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var f in files)
                {
                    if (f.Name.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        module = f.Name.Substring(0, f.Name.Length - ".Build.cs".Length);
                        break;
                    }
                }

                foreach (var f in files)
                {
                    string ext = f.Extension;
                    bool parse = ParsedExtensions.Contains(ext);
                    if (parse && thirdParty && !HeaderExtensions.Contains(ext)) parse = false; // only third-party headers
                    if (!parse && !ListedExtensions.Contains(ext) && !(thirdParty && ParsedExtensions.Contains(ext))) continue;
                    if (parse && f.Length > 4 * 1024 * 1024) parse = false; // generated monsters
                    yield return FileRecord.Create(f.FullName, root.Path, module, root.Origin, parse, f);
                }

                foreach (var s in subdirs)
                {
                    if (SkippedDirectories.Contains(s.Name) || (s.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push((s.FullName, module));
                }
            }
        }

        public static IEnumerable<string> ScanAssets(IndexRoot root)
        {
            if (!Directory.Exists(root.Path)) yield break;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root.Path, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
            }
            catch (Exception)
            {
                yield break;
            }
            foreach (var f in files)
            {
                if (f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }
        }
    }
}
