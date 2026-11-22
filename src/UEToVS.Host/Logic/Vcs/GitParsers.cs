using System;
using System.Collections.Generic;
using System.Globalization;

namespace UEToVS.Host.Logic.Vcs
{
    /// <summary>
    /// 解析 <c>git blame --porcelain</c>：每组行先是 "&lt;sha&gt; &lt;原行号&gt; &lt;现行号&gt; [&lt;行数&gt;]"，提交第一次出现时跟着
    /// author / author-mail / author-time / author-tz / summary / previous / filename 等，最后是以 Tab 开头的行内容。
    /// 没提交的行是 40 个 0 的哈希（作者 "Not Committed Yet"）。
    /// </summary>
    public static class GitBlameParser
    {
        public const string UncommittedId = "0000000000000000000000000000000000000000";

        public static BlameResult Parse(string output, int expectedLines = 0)
        {
            var result = new BlameResult();
            if (string.IsNullOrEmpty(output)) return result;
            var lines = new List<BlameLine>(Math.Max(expectedLines, 16));
            VcsCommit current = null;
            string currentId = null;
            int finalLine = 0, originalLine = 0;
            int pos = 0, len = output.Length;
            while (pos < len)
            {
                int end = output.IndexOf('\n', pos);
                if (end < 0) end = len;
                string line = output.Substring(pos, end - pos);
                pos = end + 1;
                if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);
                if (line.Length == 0) continue;
                if (line[0] == '\t')
                {
                    // 行内容：记到 finalLine（从 1 开始）
                    if (currentId != null && finalLine > 0)
                    {
                        while (lines.Count < finalLine) lines.Add(null);
                        lines[finalLine - 1] = new BlameLine { CommitId = currentId, OriginalLine = originalLine };
                    }
                    continue;
                }
                if (IsHeader(line, out string sha, out int orig, out int fin))
                {
                    currentId = sha;
                    originalLine = orig;
                    finalLine = fin;
                    if (!result.Commits.TryGetValue(sha, out current))
                    {
                        current = new VcsCommit { Id = sha, IsUncommitted = sha == UncommittedId };
                        result.Commits[sha] = current;
                    }
                    continue;
                }
                if (current == null) continue;
                int sp = line.IndexOf(' ');
                string key = sp < 0 ? line : line.Substring(0, sp);
                string value = sp < 0 ? string.Empty : line.Substring(sp + 1);
                switch (key)
                {
                    case "author": current.Author = value; break;
                    case "author-mail": current.AuthorMail = value.Trim('<', '>'); break;
                    case "author-time":
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t))
                            current.Time = DateTimeOffset.FromUnixTimeSeconds(t);
                        break;
                    case "author-tz":
                        if (TryParseOffset(value, out var off)) current.Time = current.Time.ToOffset(off);
                        break;
                    case "summary": current.Summary = value; break;
                    case "previous":
                        int sp2 = value.IndexOf(' ');
                        if (sp2 > 0) result.Previous[currentId] = (value.Substring(0, sp2), value.Substring(sp2 + 1));
                        break;
                }
            }
            for (int i = 0; i < lines.Count; i++)
                if (lines[i] == null) lines[i] = new BlameLine { CommitId = UncommittedId, OriginalLine = i + 1 };
            result.Lines = lines.ToArray();
            return result;
        }

        private static bool IsHeader(string line, out string sha, out int orig, out int fin)
        {
            sha = null;
            orig = fin = 0;
            if (line.Length < 44 || line[40] != ' ') return false;
            for (int i = 0; i < 40; i++)
            {
                char c = line[i];
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            }
            var parts = line.Substring(41).Split(' ');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out orig)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out fin)) return false;
            sha = line.Substring(0, 40);
            return true;
        }

        /// <summary>"+0800" / "-0530" → 时区偏移。</summary>
        public static bool TryParseOffset(string tz, out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            if (string.IsNullOrEmpty(tz) || tz.Length != 5 || tz[0] != '+' && tz[0] != '-') return false;
            if (!int.TryParse(tz.Substring(1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) return false;
            if (!int.TryParse(tz.Substring(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)) return false;
            if (h > 14 || m > 59) return false;
            offset = new TimeSpan(h, m, 0);
            if (tz[0] == '-') offset = -offset;
            return true;
        }
    }

    /// <summary>
    /// <c>git show</c> / <c>git log</c> 用 <see cref="Format"/> 输出的记录：每条以 \x1e 开头，字段用 \x1f 分开
    /// （哈希、父提交、作者、邮箱、时间、完整说明），最后一个 \x1f 之后是 name-status 或补丁。
    /// </summary>
    public static class GitLogParser
    {
        public const string Format = "%x1e%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%B%x1f";

        public sealed class Record
        {
            public VcsCommit Commit { get; set; }

            /// <summary>说明之后的部分：name-status 行或补丁。</summary>
            public string Tail { get; set; }
        }

        public static List<Record> Parse(string output)
        {
            var list = new List<Record>();
            if (string.IsNullOrEmpty(output)) return list;
            foreach (var chunk in output.Split('\x1e'))
            {
                if (chunk.Length == 0) continue;
                var f = chunk.Split(new[] { '\x1f' }, 7);
                if (f.Length < 6 || f[0].Trim().Length < 7) continue;
                string message = f[5].Replace("\r\n", "\n").TrimEnd('\n', ' ');
                var c = new VcsCommit
                {
                    Id = f[0].Trim(),
                    Parents = f[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                    Author = f[2],
                    AuthorMail = f[3],
                    Message = message,
                    Summary = FirstLine(message),
                };
                if (long.TryParse(f[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)) c.Time = DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime();
                list.Add(new Record { Commit = c, Tail = f.Length > 6 ? f[6] : string.Empty });
            }
            return list;
        }

        /// <summary>name-status 部分："M\tpath"、"R100\told\tnew"。</summary>
        public static List<CommitFile> ParseNameStatus(string tail)
        {
            var files = new List<CommitFile>();
            if (string.IsNullOrEmpty(tail)) return files;
            foreach (var raw in tail.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length < 3) continue;
                var parts = line.Split('\t');
                if (parts.Length < 2 || parts[0].Length == 0 || !char.IsLetter(parts[0][0])) continue;
                string status = parts[0].Substring(0, 1);
                if ((status == "R" || status == "C") && parts.Length >= 3)
                    files.Add(new CommitFile { Status = status, OldPath = Unquote(parts[1]), Path = Unquote(parts[2]) });
                else
                    files.Add(new CommitFile { Status = status, Path = Unquote(parts[1]) });
            }
            return files;
        }

        /// <summary>
        /// <c>git log -L</c> 的补丁里这次提交前后的路径（"--- a/x"、"+++ b/y"；新文件是 /dev/null）。
        /// </summary>
        public static (string Before, string After) PatchPaths(string tail)
        {
            string before = null, after = null;
            if (string.IsNullOrEmpty(tail)) return (null, null);
            foreach (var raw in tail.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (before == null && line.StartsWith("--- ", StringComparison.Ordinal)) before = DiffPath(line.Substring(4));
                else if (after == null && line.StartsWith("+++ ", StringComparison.Ordinal)) after = DiffPath(line.Substring(4));
                if (before != null && after != null) break;
            }
            return (before == "/dev/null" ? null : before, after == "/dev/null" ? null : after);
        }

        private static string DiffPath(string p)
        {
            p = Unquote(p.Trim());
            if (p.StartsWith("a/", StringComparison.Ordinal) || p.StartsWith("b/", StringComparison.Ordinal)) return p.Substring(2);
            return p;
        }

        /// <summary>git 对特殊字符的路径加引号并转义（core.quotePath）；这里只去掉引号和常见转义，中文路径请求时已关掉 quotePath。</summary>
        public static string Unquote(string p)
        {
            if (p == null || p.Length < 2 || p[0] != '"' || p[p.Length - 1] != '"') return p;
            var s = p.Substring(1, p.Length - 2);
            return s.Replace("\\\"", "\"").Replace("\\t", "\t").Replace("\\\\", "\\");
        }

        public static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            string m = message.TrimStart('\n', '\r', ' ');
            int i = m.IndexOf('\n');
            return (i < 0 ? m : m.Substring(0, i)).TrimEnd('\r', ' ');
        }
    }

    /// <summary>常驻的 <c>git cat-file --batch</c> 的回答头："&lt;sha&gt; &lt;type&gt; &lt;size&gt;" 或 "&lt;name&gt; missing"。</summary>
    public static class CatFileHeader
    {
        public static bool TryParse(string header, out string sha, out string type, out long size, out bool missing)
        {
            sha = type = null;
            size = 0;
            missing = false;
            if (string.IsNullOrEmpty(header)) return false;
            header = header.TrimEnd('\r', '\n');
            if (header.EndsWith(" missing", StringComparison.Ordinal) || header.EndsWith(" ambiguous", StringComparison.Ordinal))
            {
                missing = true;
                return true;
            }
            var parts = header.Split(' ');
            if (parts.Length != 3) return false;
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 0) return false;
            sha = parts[0];
            type = parts[1];
            return true;
        }
    }

    /// <summary>
    /// git 命令行参数。所有命令都带 <c>--no-optional-locks</c>（不和用户自己的 git 操作抢 index.lock），
    /// 并关掉 quotePath（中文路径原样输出）。路径用仓库内的 / 路径。
    /// </summary>
    public static class GitArgs
    {
        public const string Prefix = "--no-optional-locks -c core.quotePath=false";

        public static string CatFileBatch => Prefix + " cat-file --batch";

        public static string Blame(string revision, string repoPath) =>
            Prefix + " blame --porcelain " + (string.IsNullOrEmpty(revision) ? "HEAD" : CommandLine.Quote(revision)) + " -- " + CommandLine.Quote(repoPath);

        public static string Show(string commit) =>
            Prefix + " show --no-color --no-patch --format=" + GitLogParser.Format + " " + CommandLine.Quote(commit);

        /// <summary>name-status 单独要（合在一个 show 里 merge 提交不列文件）。</summary>
        public static string ShowFiles(string commit) =>
            Prefix + " show --no-color -M --name-status --first-parent --format= " + CommandLine.Quote(commit);

        public static string LineLog(int startLine, int endLine, string repoPath, int maxCommits) =>
            Prefix + " log --no-color -n " + Math.Max(1, maxCommits).ToString(CultureInfo.InvariantCulture) + " --format=" + GitLogParser.Format +
            " " + CommandLine.Quote("-L" + startLine.ToString(CultureInfo.InvariantCulture) + "," + endLine.ToString(CultureInfo.InvariantCulture) + ":" + repoPath);

        public static string RevParseHead => Prefix + " rev-parse HEAD";
    }

    /// <summary>Windows 命令行参数加引号（CommandLineToArgvW 规则：反斜杠只在引号前才转义）。</summary>
    public static class CommandLine
    {
        public static string Quote(string arg)
        {
            if (arg == null) return "\"\"";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) return arg;
            var sb = new System.Text.StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
                else sb.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
