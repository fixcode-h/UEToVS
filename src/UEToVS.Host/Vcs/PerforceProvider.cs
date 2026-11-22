using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Vcs;

namespace UEToVS.Host.Vcs
{
    /// <summary>
    /// Perforce 的只读版本信息，和 Git 同一套界面，数据来自本机 p4 命令行：基准 <c>p4 print -q 文件#have</c>，追溯
    /// <c>p4 annotate -u -c -q 文件#have</c>，说明 <c>p4 -ztag filelog -l</c>（一次拿到这个文件每个变更的完整说明和版本号）/
    /// <c>p4 -ztag describe -s</c>（悬停卡片的文件列表）。每条命令都要访问服务器，所以只在打开文件、光标停下、用户点按钮时才问，
    /// 不定时访问；结果按“文件 + have 版本号”缓存（VcsService），变更说明全局缓存。只用 p4 自己的票据和 P4CONFIG / P4PORT 环境，不存密码。
    /// </summary>
    internal sealed class PerforceProvider : IVcsProvider
    {
        private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan AnnotateTimeout = TimeSpan.FromSeconds(120);
        private const int FilelogMax = 2000;

        // 文件 → filelog（变更号 → 版本号、说明）；同一个 have 版本内不重复问
        private readonly LruCache<string, Dictionary<string, HistoryEntry>> _filelogs = new LruCache<string, Dictionary<string, HistoryEntry>>(32, StringComparer.OrdinalIgnoreCase);

        public PerforceProvider(string root) => Root = root;

        public VcsKind Kind => VcsKind.Perforce;
        public string Root { get; }

        private async Task<ProcessResult> RunAsync(string args, string path, TimeSpan timeout, CancellationToken ct, string what)
        {
            // 在文件所在目录跑，P4CONFIG 才能生效；p4 优先看 PWD
            string dir = DirOf(path) ?? Root;
            var env = new Dictionary<string, string> { ["PWD"] = dir };
            ProcessResult r;
            try
            {
                r = await ProcessRunner.RunAsync("p4", args, dir, timeout, ct, what, null, env).ConfigureAwait(false);
            }
            catch (VcsException ex) when (ex.Failure == VcsFailure.NotFound)
            {
                throw new VcsException(VcsStrings.P4NotFound, VcsFailure.NotFound);
            }
            if (P4Parsers.IsLoginError(r.Stderr)) throw new VcsException(VcsStrings.P4Login, VcsFailure.LoginRequired);
            return r;
        }

        private static string DirOf(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("//", StringComparison.Ordinal)) return null;
            try { return Path.GetDirectoryName(path); }
            catch (ArgumentException) { return null; }
        }

        public async Task<VcsBase> GetBaseAsync(string path, CancellationToken ct)
        {
            var f = await RunAsync(P4Args.Fstat(path), path, QuickTimeout, ct, "p4 fstat").ConfigureAwait(false);
            int have = P4Parsers.HaveRevision(f.Text);
            if (have <= 0) return new VcsBase { Key = "untracked", RevisionLabel = "#have" };
            string rev = "#" + have.ToString(CultureInfo.InvariantCulture);
            var p = await RunAsync(P4Args.Print(path, rev), path, QuickTimeout, ct, "p4 print").ConfigureAwait(false);
            if (p.ExitCode != 0 || P4Parsers.IsNotInDepot(p.Stderr)) return new VcsBase { Key = "untracked", RevisionLabel = rev };
            if (TextDecoding.LooksBinary(p.Stdout)) return new VcsBase { Key = "binary", RevisionLabel = rev };
            return new VcsBase { Key = rev, Text = TextDecoding.Decode(p.Stdout, Encoding.Default), RevisionLabel = rev };
        }

        public async Task<BlameResult> BlameAsync(string path, string revision, CancellationToken ct)
        {
            var annotate = RunAsync(P4Args.Annotate(path, revision), path, AnnotateTimeout, ct, "p4 annotate");
            var filelog = FilelogAsync(path, ct);
            var r = await annotate.ConfigureAwait(false);
            if (r.ExitCode != 0 || P4Parsers.IsNotInDepot(r.Stderr) && r.Stdout.Length == 0)
                throw new VcsException(VcsStrings.CommandFailed("p4 annotate", First(r.Stderr)), P4Parsers.IsNotInDepot(r.Stderr) ? VcsFailure.NotTracked : VcsFailure.Failed);
            var blame = P4Parsers.ParseAnnotate(r.Text);
            Dictionary<string, HistoryEntry> log = null;
            try { log = await filelog.ConfigureAwait(false); }
            catch (VcsException) { } // 没有说明也能显示作者和日期
            if (log != null)
                foreach (var c in blame.Commits.Values)
                {
                    if (!log.TryGetValue(c.Id, out var e)) continue;
                    c.Summary = e.Commit.Summary;
                    c.Message = e.Commit.Message;
                    if (e.Commit.Time != default) { c.Time = e.Commit.Time; c.DateOnly = false; }
                    if (e.PreviousRevision != null) blame.Previous[c.Id] = (e.PreviousRevision, path);
                }
            return blame;
        }

        private async Task<Dictionary<string, HistoryEntry>> FilelogAsync(string path, CancellationToken ct)
        {
            if (_filelogs.TryGet(path, out var cached)) return cached;
            var r = await RunAsync(P4Args.Filelog(path, FilelogMax), path, AnnotateTimeout, ct, "p4 filelog").ConfigureAwait(false);
            if (r.ExitCode != 0) throw new VcsException(VcsStrings.CommandFailed("p4 filelog", First(r.Stderr)));
            var map = new Dictionary<string, HistoryEntry>(StringComparer.Ordinal);
            foreach (var e in P4Parsers.ParseFilelog(r.Text))
                if (!map.ContainsKey(e.Commit.Id)) map[e.Commit.Id] = e;
            _filelogs.Set(path, map);
            return map;
        }

        /// <summary>文件同步了新版本（重新载入）：filelog 要重新问。</summary>
        public void Forget(string path) => _filelogs.Remove(path);

        public async Task<CommitDetails> GetCommitAsync(string commitId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(commitId) || !commitId.All(char.IsDigit)) return null;
            var r = await RunAsync(P4Args.Describe(new[] { commitId }), Root, QuickTimeout, ct, "p4 describe").ConfigureAwait(false);
            if (r.ExitCode != 0) throw new VcsException(VcsStrings.CommandFailed("p4 describe", First(r.Stderr)));
            return P4Parsers.ParseDescribe(r.Text).FirstOrDefault();
        }

        public async Task<string> GetFileTextAsync(string path, string revision, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(revision)) return null;
            var r = await RunAsync(P4Args.Print(path, revision), path, QuickTimeout, ct, "p4 print").ConfigureAwait(false);
            if (r.ExitCode != 0 || r.Stdout.Length == 0 && P4Parsers.IsNotInDepot(r.Stderr)) return null;
            return TextDecoding.Decode(r.Stdout, Encoding.Default);
        }

        /// <summary>
        /// P4 没有 git log -L：用基准版本的追溯拿到这几行涉及的变更，再从 filelog 取说明和版本号（不额外 describe）。
        /// </summary>
        public async Task<IReadOnlyList<HistoryEntry>> GetLineHistoryAsync(string path, int startLine, int endLine, int maxCommits, IProgress<string> progress, CancellationToken ct)
        {
            progress?.Report(VcsStrings.HistoryProgress(0));
            var blame = await BlameAsync(path, null, ct).ConfigureAwait(false);
            var log = await FilelogAsync(path, ct).ConfigureAwait(false);
            var changes = new HashSet<string>(StringComparer.Ordinal);
            for (int i = Math.Max(1, startLine); i <= endLine && i <= blame.Lines.Length; i++)
                if (blame.Lines[i - 1]?.CommitId != null) changes.Add(blame.Lines[i - 1].CommitId);
            var list = new List<HistoryEntry>();
            foreach (var id in changes.OrderByDescending(c => long.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0).Take(Math.Max(1, maxCommits)))
            {
                if (log.TryGetValue(id, out var e))
                    list.Add(new HistoryEntry { Commit = e.Commit, Revision = e.Revision, Path = path, PreviousRevision = e.PreviousRevision, PreviousPath = path });
                else if (blame.Commits.TryGetValue(id, out var c))
                    list.Add(new HistoryEntry { Commit = c, Revision = "@=" + id, Path = path, PreviousRevision = "@" + Prev(id), PreviousPath = path });
            }
            progress?.Report(VcsStrings.HistoryProgress(list.Count));
            return list;
        }

        private static string Prev(string change) =>
            long.TryParse(change, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? Math.Max(1, n - 1).ToString(CultureInfo.InvariantCulture) : change;

        public (string Revision, string Path)? PreviousOf(BlameResult blame, string commitId, string path)
        {
            if (blame != null && commitId != null && blame.Previous.TryGetValue(commitId, out var prev)) return prev;
            return null;
        }

        public (string Before, string After) RevisionsOf(VcsCommit commit, CommitFile file)
        {
            if (commit == null) return (null, null);
            if (file == null || file.Revision <= 0) return ("@" + Prev(commit.Id), "@=" + commit.Id);
            string status = file.Status ?? string.Empty;
            string before = file.Revision > 1 && !status.EndsWith("add", StringComparison.Ordinal) && status != "branch"
                ? "#" + (file.Revision - 1).ToString(CultureInfo.InvariantCulture) : null;
            string after = status.EndsWith("delete", StringComparison.Ordinal) ? null : "#" + file.Revision.ToString(CultureInfo.InvariantCulture);
            return (before, after);
        }

        public string ShortId(string commitId) => commitId ?? string.Empty;

        public Task<string> GetStateKeyAsync(CancellationToken ct) => Task.FromResult<string>(null);

        private static string First(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "?";
            s = s.Trim();
            int i = s.IndexOf('\n');
            return (i < 0 ? s : s.Substring(0, i)).Trim();
        }

        public void Dispose() => _filelogs.Clear();

        /// <summary>没有 P4CONFIG 文件时问 p4 当前目录在不在 client 里（3 秒上限）；在就返回 client 根。</summary>
        public static async Task<string> FindClientRootAsync(string dir, CancellationToken ct)
        {
            try
            {
                var env = new Dictionary<string, string> { ["PWD"] = dir };
                var r = await ProcessRunner.RunAsync("p4", P4Args.Info, dir, TimeSpan.FromSeconds(3), ct, "p4 info", null, env).ConfigureAwait(false);
                string root = P4Parsers.ClientRoot(r.Text);
                return root != null && Logic.Mcp.VcsRoot.IsSameOrUnder(dir, root) ? root : null;
            }
            catch (VcsException)
            {
                return null; // 没装 p4 / 超时：不是 P4 工作区
            }
        }
    }
}
