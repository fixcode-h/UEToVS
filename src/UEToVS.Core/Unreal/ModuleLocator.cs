using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Core.Unreal
{
    /// <summary>Finds the module (directory containing Foo.Build.cs) that owns a file.</summary>
    public sealed class ModuleLocator
    {
        private readonly Func<string, IEnumerable<string>> _listBuildCs;
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <param name="listBuildCs">Returns the *.Build.cs files directly inside a directory.</param>
        public ModuleLocator(Func<string, IEnumerable<string>> listBuildCs)
        {
            _listBuildCs = listBuildCs;
        }

        public static ModuleLocator ForFileSystem() =>
            new ModuleLocator(dir => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.Build.cs") : Array.Empty<string>());

        /// <summary>Returns the Build.cs path that owns <paramref name="filePath"/>, or null.</summary>
        public string FindBuildCs(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            var visited = new List<string>();
            while (!string.IsNullOrEmpty(dir))
            {
                if (_cache.TryGetValue(dir, out var cached))
                {
                    foreach (var v in visited) _cache[v] = cached;
                    return cached;
                }
                visited.Add(dir);
                foreach (var f in _listBuildCs(dir))
                {
                    foreach (var v in visited) _cache[v] = f;
                    return f;
                }
                string name = Path.GetFileName(dir);
                if (string.Equals(name, "Source", StringComparison.OrdinalIgnoreCase)) break;
                dir = Path.GetDirectoryName(dir);
            }
            foreach (var v in visited) _cache[v] = null;
            return null;
        }

        public string FindModuleName(string filePath)
        {
            var cs = FindBuildCs(filePath);
            return cs == null ? null : BuildCsFile.ModuleNameFromPath(cs);
        }

        public void Invalidate() => _cache.Clear();
    }
}
