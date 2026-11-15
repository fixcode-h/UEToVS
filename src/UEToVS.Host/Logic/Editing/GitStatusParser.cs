using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>
    /// v0.7.5: parses <c>git status --porcelain=v1 -z --untracked-files=all</c> into the full paths that differ from Git
    /// (modified, added, renamed / copied targets, untracked; deleted files are left out because no tab can show them).
    /// </summary>
    public static class GitStatusParser
    {
        public const string Arguments = "status --porcelain=v1 -z --untracked-files=all --ignore-submodules";

        public static HashSet<string> Parse(string output, string repoRoot)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(repoRoot)) return result;
            var parts = output.Split('\0');
            for (int i = 0; i < parts.Length; i++)
            {
                string entry = parts[i];
                if (entry.Length < 4) continue;
                char x = entry[0], y = entry[1];
                string rel = entry.Substring(3);
                // renames / copies: "R  new\0old" — the old path is the next field
                if (x == 'R' || x == 'C') i++;
                if (x == 'D' && y == ' ' || y == 'D' && x == ' ' || x == 'D' && y == 'D') continue;
                if (x == '!' ) continue; // ignored
                try
                {
                    result.Add(Path.GetFullPath(Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar))));
                }
                catch (Exception)
                {
                    // a path git printed that this OS cannot represent: skip it
                }
            }
            return result;
        }
    }
}
