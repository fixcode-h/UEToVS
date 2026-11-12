using System;
using System.IO;
using System.Linq;

namespace UEToVS.Engine.IntelliSense
{
    public sealed partial class CompletionEngine
    {
        /// <summary>
        /// Whether a /Game/ package path ("/Game/A/B") exists, for the UE0008 squiggle: true when the asset table has the package or
        /// the folder, false only when the table knows the /Game mount, has neither, and the file / folder is not on disk either
        /// (the table may lag behind the editor); null when that cannot be told. Reuses the completion's folder tree (same cache).
        /// </summary>
        internal bool? GamePackageExists(string packagePath)
        {
            var table = Strings?.Assets;
            if (table == null || string.IsNullOrEmpty(packagePath)) return null;
            var snap = table.Snapshot();
            if (snap.Length == 0) return null;
            var folders = Folders(snap);
            if (!folders.Mounts.Contains("Game")) return null;
            if (folders.Leaf.ContainsKey(packagePath) || folders.Children.ContainsKey(packagePath)) return true; // a folder
            int slash = packagePath.LastIndexOf('/');
            if (slash <= 0) return null;
            string dir = packagePath.Substring(0, slash);
            string name = packagePath.Substring(slash + 1);
            if (AssetsIn(snap, folders, dir).Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return true;
            if (folders.Children.TryGetValue(dir, out var subs) && subs.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) return true;

            // confirm on disk next to any /Game asset: <Content>/<rel>.uasset / .umap or a folder
            var any = snap.FirstOrDefault(a => a.PackageDir != null && (a.PackageDir == "/Game" || a.PackageDir.StartsWith("/Game/", StringComparison.Ordinal)));
            if (any == null) return null;
            string rel = any.PackageDir.Length > 5 ? any.PackageDir.Substring(6) : string.Empty;
            string file = any.FilePath.Replace('\\', '/');
            string tail = (rel.Length > 0 ? rel + "/" : string.Empty) + Path.GetFileName(file);
            if (!file.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase)) return null;
            string content = file.Substring(0, file.Length - tail.Length - 1);
            string target = content + packagePath.Substring(5);
            try
            {
                if (File.Exists(target + ".uasset") || File.Exists(target + ".umap") || Directory.Exists(target)) return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
            return false;
        }
    }
}
