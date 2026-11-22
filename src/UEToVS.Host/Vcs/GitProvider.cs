using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Vcs;

namespace UEToVS.Host.Vcs
{
    /// <summary>找 git：先 PATH，再 VS 自带的那份（和标签页 Git 标记共用，找到一次就记住）。</summary>
    internal static class GitLocator
    {
        private static string _found;

        public static string Found
        {
            get => _found;
            set => _found = value;
        }

        public static IEnumerable<string> Candidates()
        {
            if (_found != null) yield return _found;
            yield return "git";
            string ide = null;
            try { ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName); }
            catch (Exception) { } // 拿不到进程路径：只用 PATH 上的 git
            if (ide != null)
            {
                string bundled = Path.Combine(ide, "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer", "Git", "cmd", "git.exe");
                if (File.Exists(bundled)) yield return bundled;
            }
        }

        /// <summary>按候选顺序跑一次，第一个能启动的 git 记下来以后直接用。</summary>
        public static async Task<ProcessResult> RunAsync(string args, string workDir, TimeSpan timeout, CancellationToken ct, string what, Action<byte[], int> onChunk = null)
        {
            foreach (var git in Candidates().Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                try
                {
                    var r = await ProcessRunner.RunAsync(git, args, workDir, timeout, ct, what, onChunk).ConfigureAwait(false);
                    _found = git;
                    return r;
                }
                catch (VcsException ex) when (ex.Failure == VcsFailure.NotFound)
                {
                    // 下一个候选
                }
            }
            throw new VcsException(VcsStrings.GitNotFound, VcsFailure.NotFound);
        }
    }

    /// <summary>
    /// 常驻的 <c>git cat-file --batch</c>：取 HEAD（或任意版本）的文件内容不用每次起进程。一个仓库一个进程，请求排队；
    /// 某次读超时就结束进程，下次再起。不用时（关解决方案、仓库缓存被清掉）Dispose 结束进程。
    /// </summary>
    internal sealed class GitCatFile : IDisposable
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
        private readonly string _root;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private Process _process;
        private Stream _out;
        private Stream _in;
        private bool _disposed;

        public GitCatFile(string root) => _root = root;

        /// <summary>(找不到, 对象哈希, 内容)。<paramref name="spec"/> 是 "HEAD:路径"、"&lt;sha&gt;:路径" 或 "HEAD"。</summary>
        public async Task<(bool Missing, string Sha, byte[] Data)> ReadAsync(string spec, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(spec) || spec.IndexOf('\n') >= 0 || spec.IndexOf('\r') >= 0) return (true, null, null);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed) throw new VcsException(VcsStrings.CommandFailed("git cat-file", "closed"));
                await EnsureStartedAsync(ct).ConfigureAwait(false);
                using (var timeout = new CancellationTokenSource(ReadTimeout))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
                using (linked.Token.Register(Stop))
                {
                    try
                    {
                        var req = Encoding.UTF8.GetBytes(spec + "\n");
                        await _in.WriteAsync(req, 0, req.Length, linked.Token).ConfigureAwait(false);
                        await _in.FlushAsync(linked.Token).ConfigureAwait(false);
                        string header = await ReadLineAsync(linked.Token).ConfigureAwait(false);
                        if (!CatFileHeader.TryParse(header, out string sha, out _, out long size, out bool missing)) throw new IOException("bad cat-file header: " + header);
                        if (missing) return (true, null, null);
                        if (size > ProcessRunner.MaxOutputBytes) throw new IOException("object too large");
                        var data = new byte[size];
                        int got = 0;
                        while (got < size)
                        {
                            int n = await _out.ReadAsync(data, got, (int)Math.Min(size - got, 1 << 20), linked.Token).ConfigureAwait(false);
                            if (n <= 0) throw new IOException("cat-file closed");
                            got += n;
                        }
                        var lf = new byte[1];
                        await _out.ReadAsync(lf, 0, 1, linked.Token).ConfigureAwait(false); // 内容后面的换行
                        return (false, sha, data);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException) || !ct.IsCancellationRequested)
                    {
                        Stop();
                        if (timeout.IsCancellationRequested) throw new VcsException(VcsStrings.TimedOut("git cat-file"), VcsFailure.TimedOut);
                        throw new VcsException(VcsStrings.CommandFailed("git cat-file", ex.Message));
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureStartedAsync(CancellationToken ct)
        {
            if (_process != null && !HasExited(_process)) return;
            Stop();
            foreach (var git in GitLocator.Candidates().Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var psi = new ProcessStartInfo(git, GitArgs.CatFileBatch)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = false,
                        WorkingDirectory = _root,
                    };
                    psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                    var p = Process.Start(psi);
                    if (p == null) continue;
                    GitLocator.Found = git;
                    _process = p;
                    _in = p.StandardInput.BaseStream;
                    _out = new BufferedStream(p.StandardOutput.BaseStream, 64 * 1024);
                    VcsService.L.Debug("vcs.catFile.started", null, ("root", _root), ("git", git));
                    await Task.CompletedTask.ConfigureAwait(false);
                    return;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // 下一个候选
                }
            }
            throw new VcsException(VcsStrings.GitNotFound, VcsFailure.NotFound);
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var sb = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                int n = await _out.ReadAsync(one, 0, 1, ct).ConfigureAwait(false);
                if (n <= 0) throw new IOException("cat-file closed");
                if (one[0] == (byte)'\n') break;
                sb.WriteByte(one[0]);
                if (sb.Length > 4096) throw new IOException("cat-file header too long");
            }
            return Encoding.UTF8.GetString(sb.ToArray());
        }

        private static bool HasExited(Process p)
        {
            try { return p.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        private void Stop()
        {
            var p = _process;
            _process = null;
            _in = null;
            _out = null;
            if (p == null) return;
            try { p.StandardInput.Close(); } catch (Exception) { }
            ProcessRunner.Kill(p);
            p.Dispose();
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }
    }

    /// <summary>
    /// Git 的只读版本信息：基准 = HEAD（常驻 cat-file 取），追溯 = <c>git blame --porcelain HEAD</c>（编辑过的行用本地 diff 映射成“未提交”，
    /// 不用 --contents 每次重跑），提交详情 = <c>git show</c>，选中部分历史 = <c>git log -L</c>。全部带 --no-optional-locks。
    /// </summary>
    internal sealed class GitProvider : IVcsProvider
    {
        private static readonly TimeSpan BlameTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan ShowTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan LogTimeout = TimeSpan.FromMinutes(10);
        private readonly GitCatFile _catFile;

        public GitProvider(string root)
        {
            Root = root;
            _catFile = new GitCatFile(root);
        }

        public VcsKind Kind => VcsKind.Git;
        public string Root { get; }

        public string RepoPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (!Path.IsPathRooted(path)) return path.Replace('\\', '/');
            string full = Path.GetFullPath(path);
            string root = Root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return full.Substring(root.Length).Replace('\\', '/');
        }

        public async Task<VcsBase> GetBaseAsync(string path, CancellationToken ct)
        {
            string rel = RepoPath(path);
            if (rel == null) return new VcsBase { Key = "outside", RevisionLabel = "HEAD" };
            var (missing, sha, data) = await _catFile.ReadAsync("HEAD:" + rel, ct).ConfigureAwait(false);
            if (missing || data == null || TextDecoding.LooksBinary(data)) return new VcsBase { Key = "untracked", RevisionLabel = "HEAD" };
            return new VcsBase { Key = sha, Text = TextDecoding.Decode(data, Encoding.Default), RevisionLabel = "HEAD" };
        }

        public async Task<BlameResult> BlameAsync(string path, string revision, CancellationToken ct)
        {
            string rel = RepoPath(path);
            if (rel == null) throw new VcsException(VcsStrings.NotInVcs, VcsFailure.NotTracked);
            var r = await GitLocator.RunAsync(GitArgs.Blame(revision, rel), Root, BlameTimeout, ct, "git blame").ConfigureAwait(false);
            if (r.ExitCode != 0) throw new VcsException(VcsStrings.CommandFailed("git blame", FirstLine(r.Stderr)), IsNoSuchPath(r.Stderr) ? VcsFailure.NotTracked : VcsFailure.Failed);
            var blame = GitBlameParser.Parse(r.Text);
            foreach (var c in blame.Commits.Values) if (c.Time != default) c.Time = c.Time.ToLocalTime();
            return blame;
        }

        public async Task<CommitDetails> GetCommitAsync(string commitId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(commitId) || commitId == GitBlameParser.UncommittedId) return null;
            var show = GitLocator.RunAsync(GitArgs.Show(commitId), Root, ShowTimeout, ct, "git show");
            var files = GitLocator.RunAsync(GitArgs.ShowFiles(commitId), Root, ShowTimeout, ct, "git show");
            var r = await show.ConfigureAwait(false);
            if (r.ExitCode != 0) throw new VcsException(VcsStrings.CommandFailed("git show", FirstLine(r.Stderr)));
            var rec = GitLogParser.Parse(r.Text).FirstOrDefault();
            if (rec == null) throw new VcsException(VcsStrings.CommandFailed("git show", "empty"));
            var d = new CommitDetails { Commit = rec.Commit };
            var f = await files.ConfigureAwait(false);
            if (f.ExitCode == 0)
            {
                var list = GitLogParser.ParseNameStatus(f.Text);
                const int max = 200;
                d.Files.AddRange(list.Take(max));
                d.FilesTruncated = list.Count > max;
            }
            return d;
        }

        public async Task<string> GetFileTextAsync(string path, string revision, CancellationToken ct)
        {
            string rel = RepoPath(path);
            if (rel == null || string.IsNullOrEmpty(revision)) return null;
            var (missing, _, data) = await _catFile.ReadAsync(revision + ":" + rel, ct).ConfigureAwait(false);
            return missing || data == null ? null : TextDecoding.Decode(data, Encoding.Default);
        }

        public async Task<IReadOnlyList<HistoryEntry>> GetLineHistoryAsync(string path, int startLine, int endLine, int maxCommits, IProgress<string> progress, CancellationToken ct)
        {
            string rel = RepoPath(path);
            if (rel == null) throw new VcsException(VcsStrings.NotInVcs, VcsFailure.NotTracked);
            int seen = 0;
            progress?.Report(VcsStrings.HistoryProgress(0));
            var r = await GitLocator.RunAsync(GitArgs.LineLog(startLine, endLine, rel, maxCommits), Root, LogTimeout, ct, "git log -L", (buf, n) =>
            {
                int add = 0;
                for (int i = 0; i < n; i++) if (buf[i] == 0x1e) add++;
                if (add == 0) return;
                seen += add;
                progress?.Report(VcsStrings.HistoryProgress(seen));
            }).ConfigureAwait(false);
            if (r.ExitCode != 0) throw new VcsException(VcsStrings.CommandFailed("git log -L", FirstLine(r.Stderr)));
            var list = new List<HistoryEntry>();
            foreach (var rec in GitLogParser.Parse(r.Text))
            {
                var (before, after) = GitLogParser.PatchPaths(rec.Tail);
                list.Add(new HistoryEntry
                {
                    Commit = rec.Commit,
                    Revision = rec.Commit.Id,
                    Path = after ?? before ?? rel,
                    PreviousRevision = before != null && rec.Commit.Parents.Length > 0 ? rec.Commit.Parents[0] : null,
                    PreviousPath = before,
                });
            }
            return list;
        }

        public (string Revision, string Path)? PreviousOf(BlameResult blame, string commitId, string path)
        {
            if (blame != null && commitId != null && blame.Previous.TryGetValue(commitId, out var prev)) return prev;
            return null;
        }

        public (string Before, string After) RevisionsOf(VcsCommit commit, CommitFile file)
        {
            if (commit == null) return (null, null);
            string before = file != null && file.Status == "A" || commit.Parents.Length == 0 ? null : commit.Parents[0];
            string after = file != null && file.Status == "D" ? null : commit.Id;
            return (before, after);
        }

        public string ShortId(string commitId) => string.IsNullOrEmpty(commitId) ? string.Empty : commitId.Length > 8 ? commitId.Substring(0, 8) : commitId;

        public async Task<string> GetStateKeyAsync(CancellationToken ct)
        {
            var (missing, sha, _) = await _catFile.ReadAsync("HEAD", ct).ConfigureAwait(false);
            return missing ? string.Empty : sha;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "?";
            s = s.Trim();
            int i = s.IndexOf('\n');
            return (i < 0 ? s : s.Substring(0, i)).Trim();
        }

        private static bool IsNoSuchPath(string stderr) =>
            stderr != null && (stderr.IndexOf("no such path", StringComparison.OrdinalIgnoreCase) >= 0 || stderr.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0);

        public void Dispose() => _catFile.Dispose();
    }
}
