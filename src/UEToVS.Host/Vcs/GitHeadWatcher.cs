using System;
using System.IO;
using System.Threading;
using UEToVS.Host.Diagnostics;

namespace UEToVS.Host.Vcs
{
    /// <summary>
    /// 盯着一个 Git 仓库的 HEAD 有没有动：在 VS 自己的“Git 更改”窗口里提交、切分支、拉取时 VS 不会失去再得到焦点，
    /// 光靠“VS 被激活时检查”色条会一直拿旧的 HEAD 比，已经提交的行还显示成改动。
    /// 只监听 gitdir 下的 HEAD（切分支）和 logs\HEAD（每次 HEAD 移动都会追加一行：提交、amend、reset、pull、checkout），
    /// 两个非递归、按文件名过滤的 FileSystemWatcher，不碰 objects 目录；事件合并 500 ms 后交给 <see cref="VcsService"/> 跑一次 rev-parse。
    /// worktree / 子模块的 .git 是文件（gitdir: …）时跟到真正的 gitdir。
    /// </summary>
    internal sealed class GitHeadWatcher : IDisposable
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

        private readonly FileSystemWatcher _head;
        private readonly FileSystemWatcher _log;
        private readonly Timer _timer;
        private readonly Action _changed;
        private int _disposed;

        private GitHeadWatcher(string gitDir, Action changed)
        {
            _changed = changed;
            _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _head = Watch(gitDir);
            string logs = Path.Combine(gitDir, "logs");
            if (Directory.Exists(logs)) _log = Watch(logs);
        }

        /// <summary>仓库根目录 <paramref name="root"/> 的 HEAD 监听；找不到 gitdir 或建不了监听时 null（退回只在 VS 激活时检查）。</summary>
        public static GitHeadWatcher TryCreate(string root, Action changed)
        {
            try
            {
                string gitDir = ResolveGitDir(root);
                if (gitDir == null || !File.Exists(Path.Combine(gitDir, "HEAD"))) return null;
                return new GitHeadWatcher(gitDir, changed);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is PlatformNotSupportedException)
            {
                VcsService.L.Info("vcs.headWatcher.failed", ex.Message, ("root", root));
                return null;
            }
        }

        internal static string ResolveGitDir(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            string dotGit = Path.Combine(root, ".git");
            if (Directory.Exists(dotGit)) return dotGit;
            if (!File.Exists(dotGit)) return null;
            foreach (var raw in File.ReadAllLines(dotGit))
            {
                string line = raw.Trim();
                if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) continue;
                string dir = line.Substring("gitdir:".Length).Trim().Replace('/', Path.DirectorySeparatorChar);
                if (dir.Length == 0) return null;
                if (!Path.IsPathRooted(dir)) dir = Path.GetFullPath(Path.Combine(root, dir));
                return Directory.Exists(dir) ? dir : null;
            }
            return null;
        }

        private FileSystemWatcher Watch(string dir)
        {
            var w = new FileSystemWatcher(dir, "HEAD")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            w.Changed += OnEvent;
            w.Created += OnEvent;
            w.Renamed += OnEvent; // git 写 HEAD 是先写 HEAD.lock 再改名
            w.EnableRaisingEvents = true;
            return w;
        }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _timer.Change(Debounce, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        private void Fire()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Safe.Execute(VcsService.L, "vcs.headWatcher.fire", _changed);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var w in new[] { _head, _log })
            {
                if (w == null) continue;
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            _timer.Dispose();
        }
    }
}
