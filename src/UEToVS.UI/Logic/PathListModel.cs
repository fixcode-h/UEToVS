using System;
using System.Collections.Generic;
using System.Text;

namespace UEToVS.UI.Logic
{
    /// <summary>
    /// Pure logic behind the path list editor (Lua script folders and other multi-folder settings): parses the stored
    /// ';'-separated text (old format, also accepts new lines and '|'), emits it back, and turns a picked absolute folder
    /// into the form stored — relative to a base folder (the .uproject folder) when it lies inside it, absolute otherwise.
    /// WPF-free so it is unit-tested.
    /// </summary>
    public static class PathListModel
    {
        private static readonly char[] Separators = { ';', '\n', '\r', '|' };

        /// <summary>"a; b;;c" → [a, b, c]; trims quotes and spaces, drops duplicates (case-insensitive, separator-insensitive).</summary>
        public static List<string> Parse(string value)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(value)) return list;
            foreach (var part in value.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim().Trim('"').Trim();
                if (p.Length > 0 && IndexOf(list, p) < 0) list.Add(p);
            }
            return list;
        }

        /// <summary>[a, b] → "a;b" (empty entries skipped, duplicates dropped).</summary>
        public static string Join(IEnumerable<string> paths)
        {
            var kept = new List<string>();
            if (paths != null)
                foreach (var raw in paths)
                {
                    string p = (raw ?? string.Empty).Trim().Trim('"').Trim();
                    if (p.Length > 0 && IndexOf(kept, p) < 0) kept.Add(p);
                }
            return string.Join(";", kept);
        }

        /// <summary>Index of <paramref name="path"/> in <paramref name="list"/>, comparing case- and separator-insensitively; -1 if absent.</summary>
        public static int IndexOf(IReadOnlyList<string> list, string path)
        {
            string key = Key(path);
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(Key(list[i]), key, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public static bool IsRooted(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0])) return true; // C:\ or C:
            return path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
                   path[0] == '/' || path[0] == '\\';
        }

        /// <summary>
        /// Stored form of a picked folder: relative to <paramref name="baseDir"/> with '/' separators when inside it
        /// ("Content/Script"; "." for the base itself), the absolute path otherwise or when there is no base.
        /// </summary>
        public static string ToStored(string absolute, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(absolute)) return string.Empty;
            string abs = TrimEnd(Normalize(absolute.Trim()));
            if (string.IsNullOrWhiteSpace(baseDir) || !IsRooted(abs)) return abs;
            string b = TrimEnd(Normalize(baseDir.Trim()));
            if (string.Equals(abs, b, StringComparison.OrdinalIgnoreCase)) return ".";
            if (abs.Length > b.Length + 1 && abs.StartsWith(b, StringComparison.OrdinalIgnoreCase) && abs[b.Length] == '\\')
                return abs.Substring(b.Length + 1).Replace('\\', '/');
            return abs;
        }

        /// <summary>Absolute form of a stored path (relative paths are resolved against <paramref name="baseDir"/>); null when it cannot be resolved.</summary>
        public static string ToAbsolute(string stored, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            string s = stored.Trim().Trim('"');
            if (IsRooted(s)) return TrimEnd(Normalize(s));
            if (string.IsNullOrWhiteSpace(baseDir)) return null;
            if (s == ".") return TrimEnd(Normalize(baseDir));
            return Collapse(TrimEnd(Normalize(baseDir)) + "\\" + Normalize(s));
        }

        /// <summary>True when the stored path is relative (resolved against the .uproject folder).</summary>
        public static bool IsRelative(string stored) => !string.IsNullOrWhiteSpace(stored) && !IsRooted(stored.Trim().Trim('"'));

        /// <summary>Folder to open the picker at: the row's own folder when it resolves, else the base folder.</summary>
        public static string BrowseStart(string stored, string baseDir, Func<string, bool> exists)
        {
            string abs = ToAbsolute(stored, baseDir);
            if (abs != null && (exists == null || exists(abs))) return abs;
            return string.IsNullOrWhiteSpace(baseDir) ? null : baseDir;
        }

        private static string Key(string path) => TrimEnd(Normalize((path ?? string.Empty).Trim().Trim('"')));

        private static string Normalize(string p) => p.Replace('/', '\\');

        private static string TrimEnd(string p)
        {
            // Keep "C:\" and "\\" roots intact.
            while (p.Length > 3 && p[p.Length - 1] == '\\') p = p.Substring(0, p.Length - 1);
            return p;
        }

        /// <summary>Resolves "." and ".." segments ("C:\P\..\Q" → "C:\Q") without touching the disk.</summary>
        private static string Collapse(string path)
        {
            bool unc = path.StartsWith("\\\\", StringComparison.Ordinal);
            var parts = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var stack = new List<string>();
            foreach (var part in parts)
            {
                if (part == ".") continue;
                if (part == ".." && stack.Count > 1) { stack.RemoveAt(stack.Count - 1); continue; }
                stack.Add(part);
            }
            var sb = new StringBuilder();
            if (unc) sb.Append("\\\\");
            else if (path.StartsWith("\\", StringComparison.Ordinal)) sb.Append('\\');
            sb.Append(string.Join("\\", stack));
            if (stack.Count == 1 && stack[0].EndsWith(":", StringComparison.Ordinal)) sb.Append('\\');
            return sb.ToString();
        }
    }
}
