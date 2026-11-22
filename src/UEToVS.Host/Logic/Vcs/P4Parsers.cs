using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UEToVS.Host.Logic.Vcs
{
    /// <summary>
    /// <c>p4 -ztag</c> 输出：每行 "... key value"，记录之间空一行；多行的值（变更说明）续在下一行，没有 "... " 前缀。
    /// 说明里本身的空行不当作记录分隔：只有下一条 "... " 行的键在当前记录里已经出现过，才算新记录。
    /// </summary>
    public static class ZTagParser
    {
        public static List<Dictionary<string, string>> Parse(string output)
        {
            var records = new List<Dictionary<string, string>>();
            if (string.IsNullOrEmpty(output)) return records;
            var lines = output.Replace("\r\n", "\n").Split('\n');
            Dictionary<string, string> cur = null;
            string lastKey = null;
            int pendingBlank = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("... ", StringComparison.Ordinal))
                {
                    string rest = line.Substring(4);
                    int sp = rest.IndexOf(' ');
                    string key = sp < 0 ? rest : rest.Substring(0, sp);
                    string value = sp < 0 ? string.Empty : rest.Substring(sp + 1);
                    if (cur == null || cur.ContainsKey(key))
                    {
                        cur = new Dictionary<string, string>(StringComparer.Ordinal);
                        records.Add(cur);
                    }
                    cur[key] = value;
                    lastKey = key;
                    pendingBlank = 0;
                    continue;
                }
                if (line.Length == 0)
                {
                    pendingBlank++;
                    continue;
                }
                // 续行：前面的空行也是值的一部分
                if (cur != null && lastKey != null)
                {
                    cur[lastKey] = cur[lastKey] + new string('\n', pendingBlank + 1) + line;
                }
                pendingBlank = 0;
            }
            return records;
        }

        public static string Get(Dictionary<string, string> r, string key) => r != null && r.TryGetValue(key, out var v) ? v : null;

        public static int GetInt(Dictionary<string, string> r, string key) =>
            int.TryParse(Get(r, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

        public static DateTimeOffset GetTime(Dictionary<string, string> r, string key) =>
            long.TryParse(Get(r, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) ? DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime() : default;
    }

    public static class P4Parsers
    {
        /// <summary><c>p4 -ztag describe -s 变更号…</c>：每个变更一条记录（文件 depotFile0.. / action0.. / rev0..，最多取 <paramref name="maxFiles"/> 个）。</summary>
        public static List<CommitDetails> ParseDescribe(string output, int maxFiles = 200)
        {
            var list = new List<CommitDetails>();
            foreach (var r in ZTagParser.Parse(output))
            {
                string change = ZTagParser.Get(r, "change");
                if (string.IsNullOrEmpty(change)) continue;
                string desc = (ZTagParser.Get(r, "desc") ?? string.Empty).TrimEnd('\n', ' ');
                var d = new CommitDetails
                {
                    Commit = new VcsCommit
                    {
                        Id = change,
                        Author = ZTagParser.Get(r, "user"),
                        AuthorMail = ZTagParser.Get(r, "client"),
                        Time = ZTagParser.GetTime(r, "time"),
                        Message = desc,
                        Summary = GitLogParser.FirstLine(desc),
                    },
                };
                for (int i = 0; ; i++)
                {
                    string file = ZTagParser.Get(r, "depotFile" + i.ToString(CultureInfo.InvariantCulture));
                    if (file == null) break;
                    if (d.Files.Count >= maxFiles) { d.FilesTruncated = true; break; }
                    d.Files.Add(new CommitFile
                    {
                        Path = file,
                        Status = ZTagParser.Get(r, "action" + i.ToString(CultureInfo.InvariantCulture)),
                        Revision = ZTagParser.GetInt(r, "rev" + i.ToString(CultureInfo.InvariantCulture)),
                    });
                }
                list.Add(d);
            }
            return list;
        }

        /// <summary><c>p4 -ztag filelog -l -m N 文件</c>：rev0.. / change0.. / user0.. / time0.. / desc0..，最新的在前。</summary>
        public static List<HistoryEntry> ParseFilelog(string output)
        {
            var list = new List<HistoryEntry>();
            foreach (var r in ZTagParser.Parse(output))
            {
                string depot = ZTagParser.Get(r, "depotFile");
                for (int i = 0; ; i++)
                {
                    string n = i.ToString(CultureInfo.InvariantCulture);
                    string change = ZTagParser.Get(r, "change" + n);
                    if (change == null) break;
                    string desc = (ZTagParser.Get(r, "desc" + n) ?? string.Empty).TrimEnd('\n', ' ');
                    int rev = ZTagParser.GetInt(r, "rev" + n);
                    string action = ZTagParser.Get(r, "action" + n) ?? string.Empty;
                    list.Add(new HistoryEntry
                    {
                        Commit = new VcsCommit
                        {
                            Id = change,
                            Author = ZTagParser.Get(r, "user" + n),
                            AuthorMail = ZTagParser.Get(r, "client" + n),
                            Time = ZTagParser.GetTime(r, "time" + n),
                            Message = desc,
                            Summary = GitLogParser.FirstLine(desc),
                        },
                        Path = depot,
                        Revision = "#" + rev.ToString(CultureInfo.InvariantCulture),
                        PreviousPath = depot,
                        PreviousRevision = rev > 1 && !action.EndsWith("add", StringComparison.Ordinal) && action != "branch"
                            ? "#" + (rev - 1).ToString(CultureInfo.InvariantCulture) : null,
                    });
                }
            }
            return list;
        }

        // "12345: jdoe 2024/01/31 text"（-c 变更号、-u 作者和日期、-q 不要文件头）
        private static readonly Regex AnnotateLine = new Regex(@"^(\d+): (\S+) (\d{4})/(\d{2})/(\d{2}) ?", RegexOptions.CultureInvariant);

        /// <summary><c>p4 annotate -u -c -q 文件</c>。说明不在这里（只有变更号、作者、日期），悬停时再 describe。</summary>
        public static BlameResult ParseAnnotate(string output)
        {
            var result = new BlameResult();
            if (string.IsNullOrEmpty(output)) return result;
            var lines = new List<BlameLine>();
            var text = output.Replace("\r\n", "\n");
            if (text.EndsWith("\n", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1);
            foreach (var line in text.Split('\n'))
            {
                var m = AnnotateLine.Match(line);
                if (!m.Success)
                {
                    // 行内容里有换行符的极少见情况：算到上一行，不打乱行号
                    if (lines.Count == 0) continue;
                    continue;
                }
                string change = m.Groups[1].Value;
                if (!result.Commits.ContainsKey(change))
                {
                    DateTimeOffset time = default;
                    try
                    {
                        var date = new DateTime(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                                                int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture), 12, 0, 0, DateTimeKind.Local);
                        time = new DateTimeOffset(date);
                    }
                    catch (ArgumentException)
                    {
                        // 日期不合法：不显示日期
                    }
                    result.Commits[change] = new VcsCommit { Id = change, Author = m.Groups[2].Value, Time = time, DateOnly = true };
                }
                lines.Add(new BlameLine { CommitId = change, OriginalLine = lines.Count + 1 });
            }
            result.Lines = lines.ToArray();
            return result;
        }

        /// <summary><c>p4 -ztag info</c> 的 clientRoot（"*unknown*" / "null" 时是 null）。</summary>
        public static string ClientRoot(string ztagInfo)
        {
            foreach (var r in ZTagParser.Parse(ztagInfo))
            {
                string root = ZTagParser.Get(r, "clientRoot");
                if (string.IsNullOrWhiteSpace(root) || root.StartsWith("*", StringComparison.Ordinal) || string.Equals(root, "null", StringComparison.OrdinalIgnoreCase)) continue;
                return root.Trim();
            }
            return null;
        }

        /// <summary><c>p4 -ztag fstat -T haveRev,headRev,action 文件</c> 的 have 版本号；没同步过 / 不在仓库里是 0。</summary>
        public static int HaveRevision(string ztagFstat)
        {
            foreach (var r in ZTagParser.Parse(ztagFstat))
            {
                int have = ZTagParser.GetInt(r, "haveRev");
                if (have > 0) return have;
            }
            return 0;
        }

        /// <summary>p4 的报错是不是票据过期 / 没登录（这时提示用户自己登录，不存密码）。</summary>
        public static bool IsLoginError(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return false;
            return stderr.IndexOf("session has expired", StringComparison.OrdinalIgnoreCase) >= 0
                || stderr.IndexOf("P4PASSWD", StringComparison.OrdinalIgnoreCase) >= 0
                || stderr.IndexOf("please login", StringComparison.OrdinalIgnoreCase) >= 0
                || stderr.IndexOf("ticket", StringComparison.OrdinalIgnoreCase) >= 0 && stderr.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>文件不在仓库里（没加入、在 client 视图外）。</summary>
        public static bool IsNotInDepot(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return false;
            return stderr.IndexOf("no such file", StringComparison.OrdinalIgnoreCase) >= 0
                || stderr.IndexOf("not on client", StringComparison.OrdinalIgnoreCase) >= 0
                || stderr.IndexOf("not under client", StringComparison.OrdinalIgnoreCase) >= 0
                || stderr.IndexOf("not in client view", StringComparison.OrdinalIgnoreCase) >= 0
                || stderr.IndexOf("file(s) not in", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>p4 命令行参数（只读命令；不加 -P，用 p4 自己的票据和 P4CONFIG / P4PORT 环境）。</summary>
    public static class P4Args
    {
        public static string Info => "-ztag info";
        public static string Fstat(string path) => "-ztag fstat -T haveRev,headRev,action " + CommandLine.Quote(path);
        public static string Print(string path, string revision) => "print -q " + CommandLine.Quote(path + (revision ?? "#have"));
        public static string Annotate(string path, string revision) => "annotate -u -c -q " + CommandLine.Quote(path + (revision ?? "#have"));

        public static string Describe(IEnumerable<string> changes)
        {
            var sb = new System.Text.StringBuilder("-ztag describe -s");
            foreach (var c in changes) sb.Append(' ').Append(c);
            return sb.ToString();
        }

        public static string Filelog(string path, int max) => "-ztag filelog -l -m " + Math.Max(1, max).ToString(CultureInfo.InvariantCulture) + " " + CommandLine.Quote(path);
    }
}
