using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>
    /// v0.7.5: parses <c>git status --porcelain=v1 -z</c> into the full paths that differ from Git (modified, added,
    /// renamed / copied targets, untracked; deleted files are left out because no tab can show them).
    /// 版本信息（2026-11）：改成 <c>--untracked-files=normal</c>（=all 在有大量未跟踪文件的 UE 项目里要列出 Intermediate / Saved 下几万个文件，
    /// 很慢）：未跟踪的整个文件夹只报一行 "dir/"，<see cref="IsChanged"/> 对文件夹下的文件也算改动，行为和以前一样；
    /// 并加 <c>--no-optional-locks</c>，不和用户自己的 git 操作抢 index.lock。
    /// </summary>
    public static class GitStatusParser
    {
        public const string Arguments = "--no-optional-locks status --porcelain=v1 -z --untracked-files=normal --ignore-submodules";

        /// <summary>Changed files; untracked folders ("dir/" entries) are in <paramref name="untrackedDirs"/> when given.</summary>
        public static HashSet<string> Parse(string output, string repoRoot, List<string> untrackedDirs = null)
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
                if (x == '!') continue; // ignored
                try
                {
                    bool dir = rel.EndsWith("/", StringComparison.Ordinal);
                    string full = Path.GetFullPath(Path.Combine(repoRoot, rel.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
                    if (dir)
                    {
                        if (x == '?') untrackedDirs?.Add(full);
                        continue;
                    }
                    result.Add(full);
                }
                catch (Exception)
                {
                    // a path git printed that this OS cannot represent: skip it
                }
            }
            return result;
        }

        /// <summary>文件改过，或者在一个未跟踪的文件夹里。</summary>
        public static bool IsChanged(string path, HashSet<string> files, IReadOnlyList<string> untrackedDirs)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (files != null && files.Contains(path)) return true;
            if (untrackedDirs == null) return false;
            foreach (var d in untrackedDirs)
                if (path.Length > d.Length + 1 && path.StartsWith(d, StringComparison.OrdinalIgnoreCase) &&
                    (path[d.Length] == Path.DirectorySeparatorChar || path[d.Length] == Path.AltDirectorySeparatorChar)) return true;
            return false;
        }
    }
}
