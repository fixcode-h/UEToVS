using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Host.Logic.Mcp
{
    /// <summary>
    /// Finds the version-control root above a folder, by marker files/folders: Git (.git folder or file — worktrees and
    /// submodules), Mercurial (.hg), Subversion (.svn), Plastic SCM / Unity Version Control (.plastic) and Perforce
    /// (the P4CONFIG file: the P4CONFIG environment variable, else .p4config / p4config.txt). A Perforce workspace without a
    /// P4CONFIG file has no marker; the host asks p4 itself for the client root in that case (McpRegistration).
    /// </summary>
    public static class VcsRoot
    {
        private static readonly string[] DefaultP4ConfigNames = { ".p4config", "p4config.txt", ".p4config.txt" };

        /// <summary>
        /// Walks up from <paramref name="startDir"/> (inclusive) and returns the nearest folder holding a VCS marker, or
        /// null. For Subversion the outermost of a run of folders with .svn wins (working copies older than 1.7 have .svn
        /// in every folder). <paramref name="p4ConfigName"/> is the P4CONFIG value (null: environment variable).
        /// </summary>
        public static string Find(string startDir, string p4ConfigName = null)
        {
            if (string.IsNullOrWhiteSpace(startDir)) return null;
            string dir;
            try { dir = Path.GetFullPath(startDir); }
            catch (Exception) { return null; }

            var p4Names = new List<string>();
            string configured = p4ConfigName ?? SafeEnv("P4CONFIG");
            if (!string.IsNullOrWhiteSpace(configured) && configured.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) p4Names.Add(configured.Trim());
            foreach (var n in DefaultP4ConfigNames) if (!p4Names.Contains(n)) p4Names.Add(n);

            for (var d = dir; !string.IsNullOrEmpty(d); d = Parent(d))
            {
                if (Exists(d, ".svn"))
                {
                    var top = d;
                    for (var p = Parent(d); !string.IsNullOrEmpty(p) && Exists(p, ".svn"); p = Parent(p)) top = p;
                    return Trim(top);
                }
                if (Exists(d, ".git") || Exists(d, ".hg") || Exists(d, ".plastic")) return Trim(d);
                foreach (var n in p4Names) if (File.Exists(Path.Combine(d, n))) return Trim(d);
            }
            return null;
        }

        /// <summary>True when <paramref name="dir"/> is <paramref name="ancestor"/> or lies below it (case-insensitive, Windows paths).</summary>
        public static bool IsSameOrUnder(string dir, string ancestor)
        {
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(ancestor)) return false;
            string a, d;
            try
            {
                a = Trim(Path.GetFullPath(ancestor));
                d = Trim(Path.GetFullPath(dir));
            }
            catch (Exception) { return false; }
            if (string.Equals(a, d, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.Length == 0) return false;
            char last = a[a.Length - 1];
            string prefix = last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar ? a : a + Path.DirectorySeparatorChar;
            return d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Exists(string dir, string name)
        {
            string p = Path.Combine(dir, name);
            return Directory.Exists(p) || File.Exists(p);
        }

        private static string Parent(string dir)
        {
            try { return Path.GetDirectoryName(dir); }
            catch (Exception) { return null; }
        }

        /// <summary>Drops a trailing separator, except on a drive/filesystem root ("G:\", "/").</summary>
        private static string Trim(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;
            string root = Path.GetPathRoot(dir);
            string t = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return t.Length < (root ?? string.Empty).Length || t.Length == 0 ? root : t;
        }

        private static string SafeEnv(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch (Exception) { return null; }
        }
    }
}
