using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Logic.Vcs;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Vcs
{
    /// <summary>“追溯上一版”打开的旧版本临时文件：它对应哪个仓库、哪个路径、哪个版本。</summary>
    internal sealed class RevisionDocument
    {
        public IVcsProvider Provider { get; set; }
        public string Path { get; set; }
        public string Revision { get; set; }
        public string Label { get; set; }
    }

    /// <summary>
    /// 编辑器内版本信息的中心：按文件所在目录找最近一层 Git / Perforce（结果按目录缓存），每个仓库一个 provider（Git 带一个常驻
    /// cat-file 进程）；基准版本和追溯结果只缓存最近 32 个文件，提交详情全局缓存（提交不会变，最多 512 条）。所有 git / p4 调用都在
    /// provider 里、后台线程、带超时可取消。仓库状态变化（Git 的 HEAD 移动）在 VS 被激活时检查，最多 30 秒一次，变了就清掉那个仓库的缓存
    /// 并通知打开的文档重新取基准。日志 Log.For("Vcs")：vcs.base / vcs.blame / vcs.commit / vcs.history / vcs.diff，都记耗时。
    /// </summary>
    internal sealed class VcsService
    {
        public static readonly Logger L = Log.For("Vcs");
        public const int CacheFiles = 32;

        private static VcsService _instance;
        public static VcsService Instance => _instance ?? (_instance = new VcsService());

        private readonly ConcurrentDictionary<string, (VcsKind Kind, string Root)> _dirs = new ConcurrentDictionary<string, (VcsKind, string)>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IVcsProvider> _providers = new ConcurrentDictionary<string, IVcsProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _stateKeys = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly LruCache<string, VcsBase> _bases = new LruCache<string, VcsBase>(CacheFiles, StringComparer.OrdinalIgnoreCase);
        private readonly LruCache<string, BlameResult> _blames = new LruCache<string, BlameResult>(CacheFiles, StringComparer.OrdinalIgnoreCase);
        private readonly LruCache<string, CommitDetails> _commits = new LruCache<string, CommitDetails>(512, StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RevisionDocument> _revisionDocs = new ConcurrentDictionary<string, RevisionDocument>(StringComparer.OrdinalIgnoreCase);
        private readonly RefreshThrottle _activation = new RefreshThrottle(TimeSpan.FromSeconds(30));
        private string _p4ClientRoot;
        private bool _p4Asked;
        private string _solutionDir;

        /// <summary>某个仓库（Root）或某个文件（完整路径）的基准变了：打开的文档重新取。UI 线程上触发。</summary>
        public event EventHandler<string> BaseInvalidated;

        /// <summary>设置里版本信息相关的项变了（UI 线程）。</summary>
        public event EventHandler SettingsChanged;

        private bool _hooked;

        public void EnsureHooked()
        {
            if (_hooked) return;
            _hooked = true;
            NavigationContext.SettingsChanged += (s, e) =>
            {
                var o = e.Old;
                var n = e.New;
                if (o.VcsProvider != n.VcsProvider)
                {
                    var roots = new List<string>(_providers.Keys);
                    _dirs.Clear();
                    ClearProviders();
                    foreach (var root in roots) BaseInvalidated?.Invoke(this, root); // 打开的文档换 provider 重新载入
                }
                if (o.VcsAnnotate != n.VcsAnnotate || o.VcsInlineBlame != n.VcsInlineBlame || o.VcsInlineBlameScope != n.VcsInlineBlameScope || o.VcsInlineBlameEngine != n.VcsInlineBlameEngine ||
                    o.VcsChangeBars != n.VcsChangeBars || o.VcsProvider != n.VcsProvider || o.VcsIssueLinks != n.VcsIssueLinks)
                {
                    _issueRules = null;
                    L.Info("vcs.settings", null, ("annotate", n.VcsAnnotate), ("inlineBlame", n.VcsInlineBlame), ("scope", n.VcsInlineBlameScope.ToString()), ("engine", n.VcsInlineBlameEngine),
                           ("changeBars", n.VcsChangeBars), ("provider", n.VcsProvider.ToString()));
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            };
        }

        public static HostSettings Settings => NavigationContext.Settings;

        /// <summary>UEToVS 的功能只在 UE 解决方案里开（和标签页 Git 标记、内嵌提示一致）。</summary>
        public static bool Active => NavigationContext.IsUnrealSolution && Settings.VcsProvider != VcsProviderChoice.Off;

        // ---------------------------------------------------------------- 解决方案

        public void OnSolutionOpened(string solutionDir)
        {
            _solutionDir = solutionDir;
            _p4Asked = false;
            _p4ClientRoot = null;
            _dirs.Clear();
            EnsureHooked();
            SettingsChanged?.Invoke(this, EventArgs.Empty); // 解决方案打开前就开着的文档：现在才知道是 UE 解决方案，重新载入
        }

        public void OnSolutionClosing()
        {
            _solutionDir = null;
            _dirs.Clear();
            ClearProviders();
            _revisionDocs.Clear();
            VcsTempFiles.Cleanup(TimeSpan.Zero);
        }

        private void ClearProviders()
        {
            foreach (var p in _providers.Values)
            {
                try { p.Dispose(); } catch (Exception) { }
            }
            _providers.Clear();
            _bases.Clear();
            _blames.Clear();
            _stateKeys.Clear();
        }

        /// <summary>VS 被激活（可能在外面 commit / checkout / pull 了）：最多 30 秒一次，看 Git 的 HEAD 变没变。</summary>
        public void OnActivated()
        {
            if (!Active || _providers.IsEmpty || !_activation.TryEnter(DateTime.UtcNow)) return;
            _ = Safe.RunInBackgroundAsync(L, "vcs.stateCheck", CheckStateAsync);
        }

        private async Task CheckStateAsync()
        {
            var changed = new List<string>();
            foreach (var p in _providers.Values)
            {
                if (p.Kind != VcsKind.Git) continue; // P4 不定时问服务器：靠文件重新载入
                string key;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        key = await p.GetStateKeyAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is VcsException || ex is OperationCanceledException)
                {
                    continue;
                }
                if (key == null) continue;
                if (_stateKeys.TryGetValue(p.Root, out var old) && old != key)
                {
                    changed.Add(p.Root);
                    L.Info("vcs.head.changed", null, ("root", p.Root), ("head", key.Length > 8 ? key.Substring(0, 8) : key));
                }
                _stateKeys[p.Root] = key;
            }
            if (changed.Count == 0) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            foreach (var root in changed) InvalidateRoot(root);
        }

        public void InvalidateRoot(string root)
        {
            _bases.RemoveWhere(k => Logic.Mcp.VcsRoot.IsSameOrUnder(Path.GetDirectoryName(k) ?? k, root));
            _blames.RemoveWhere(k => Logic.Mcp.VcsRoot.IsSameOrUnder(Path.GetDirectoryName(KeyPath(k)) ?? k, root));
            BaseInvalidated?.Invoke(this, root);
        }

        /// <summary>文件从磁盘重新载入（git checkout、p4 sync 改了它）：这个文件的基准和追溯重新取。</summary>
        public void InvalidateFile(string path)
        {
            _bases.Remove(path);
            _blames.RemoveWhere(k => string.Equals(KeyPath(k), path, StringComparison.OrdinalIgnoreCase));
            if (_providers.TryGetValue(FindRootFast(path) ?? string.Empty, out var p) && p is PerforceProvider pp) pp.Forget(path);
        }

        private static string KeyPath(string blameKey)
        {
            int i = blameKey.IndexOf('|');
            return i < 0 ? blameKey : blameKey.Substring(0, i);
        }

        private string FindRootFast(string path)
        {
            string dir = SafeDir(path);
            return dir != null && _dirs.TryGetValue(dir, out var d) ? d.Root : null;
        }

        // ---------------------------------------------------------------- provider

        /// <summary>文件所在的仓库（后台线程调用：可能要跑一次 p4 info）。不在版本管理里返回 null。</summary>
        public async Task<IVcsProvider> ProviderForAsync(string path, CancellationToken ct)
        {
            EnsureHooked();
            if (string.IsNullOrEmpty(path)) return null;
            if (_revisionDocs.TryGetValue(path, out var rev)) return rev.Provider;
            var choice = Settings.VcsProvider;
            if (choice == VcsProviderChoice.Off) return null;
            string dir = SafeDir(path);
            if (dir == null) return null;
            if (!_dirs.TryGetValue(dir, out var found))
            {
                found = VcsDetector.Detect(dir, choice);
                if (found.Kind == VcsKind.None && choice != VcsProviderChoice.Git)
                {
                    // 没有 P4CONFIG 文件的 P4 工作区：问一次 p4 info（按解决方案只问一次）
                    string client = await P4ClientRootAsync(ct).ConfigureAwait(false);
                    if (client != null && Logic.Mcp.VcsRoot.IsSameOrUnder(dir, client)) found = (VcsKind.Perforce, client);
                }
                _dirs[dir] = found;
                L.Debug("vcs.detect", null, ("dir", dir), ("kind", found.Kind.ToString()), ("root", found.Root));
            }
            if (found.Kind == VcsKind.None || found.Root == null) return null;
            return _providers.GetOrAdd(found.Root, r => found.Kind == VcsKind.Git ? (IVcsProvider)new GitProvider(r) : new PerforceProvider(r));
        }

        private async Task<string> P4ClientRootAsync(CancellationToken ct)
        {
            if (_p4Asked) return _p4ClientRoot;
            _p4Asked = true;
            string dir = _solutionDir;
            if (string.IsNullOrEmpty(dir)) return null;
            _p4ClientRoot = await PerforceProvider.FindClientRootAsync(dir, ct).ConfigureAwait(false);
            L.Info("vcs.p4.clientRoot", null, ("solution", dir), ("root", _p4ClientRoot ?? "none"));
            return _p4ClientRoot;
        }

        private static string SafeDir(string path)
        {
            try { return Path.GetDirectoryName(Path.GetFullPath(path)); }
            catch (Exception) { return null; }
        }

        public RevisionDocument RevisionDocumentOf(string path) =>
            path != null && _revisionDocs.TryGetValue(path, out var r) ? r : null;

        public void RegisterRevisionDocument(string tempPath, RevisionDocument doc) => _revisionDocs[tempPath] = doc;

        // ---------------------------------------------------------------- 缓存的操作（后台线程）

        public async Task<VcsBase> GetBaseAsync(IVcsProvider p, string path, CancellationToken ct)
        {
            if (_bases.TryGet(path, out var cached)) return cached;
            var sw = Stopwatch.StartNew();
            var b = await p.GetBaseAsync(path, ct).ConfigureAwait(false);
            _bases.Set(path, b);
            if (p.Kind == VcsKind.Git && !_stateKeys.ContainsKey(p.Root))
            {
                try { _stateKeys[p.Root] = await p.GetStateKeyAsync(ct).ConfigureAwait(false) ?? string.Empty; }
                catch (VcsException) { }
            }
            L.Info("vcs.base", null, ("kind", p.Kind.ToString()), ("file", Path.GetFileName(path)), ("tracked", b.Tracked), ("chars", b.Text?.Length ?? 0),
                   ("rev", b.RevisionLabel), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            return b;
        }

        public async Task<BlameResult> BlameAsync(IVcsProvider p, string path, string revision, string baseKey, CancellationToken ct)
        {
            string key = path + "|" + (revision ?? "base") + "|" + (baseKey ?? string.Empty);
            if (_blames.TryGet(key, out var cached)) return cached;
            var sw = Stopwatch.StartNew();
            try
            {
                var b = await p.BlameAsync(path, revision, ct).ConfigureAwait(false);
                _blames.Set(key, b);
                L.Info("vcs.blame", null, ("kind", p.Kind.ToString()), ("file", Path.GetFileName(path)), ("rev", revision ?? "base"), ("lines", b.Lines.Length),
                       ("commits", b.Commits.Count), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                return b;
            }
            catch (VcsException ex)
            {
                L.Info("vcs.blame.failed", ex.Message, ("kind", p.Kind.ToString()), ("file", Path.GetFileName(path)), ("failure", ex.Failure.ToString()),
                       ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                throw;
            }
        }

        public async Task<CommitDetails> GetCommitAsync(IVcsProvider p, string id, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string key = p.Root + "|" + id;
            if (_commits.TryGet(key, out var cached)) return cached;
            var sw = Stopwatch.StartNew();
            var d = await p.GetCommitAsync(id, ct).ConfigureAwait(false);
            if (d != null) _commits.Set(key, d);
            L.Info("vcs.commit", null, ("kind", p.Kind.ToString()), ("id", p.ShortId(id)), ("files", d?.Files.Count ?? 0), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            return d;
        }

        // ---------------------------------------------------------------- 提交说明里的链接

        private List<IssueLinkRule> _issueRules;

        public IReadOnlyList<IssueLinkRule> IssueRules
        {
            get
            {
                var r = _issueRules;
                if (r != null) return r;
                var errors = new List<string>();
                r = IssueLinks.Parse(Settings.VcsIssueLinks, errors);
                if (errors.Count > 0) L.Info("vcs.issueLinks.invalid", null, ("entries", string.Join(" | ", errors)));
                _issueRules = r;
                return r;
            }
        }

        /// <summary>给用户看的失败原因。</summary>
        public static string Describe(Exception ex) =>
            ex is VcsException v ? v.Message : ex is OperationCanceledException ? VcsStrings.Canceled : VcsStrings.CommandFailed("vcs", ex?.Message ?? "?");
    }

    /// <summary>diff 窗口和“追溯上一版”用的临时文件：%TEMP%\UEToVS\Vcs，设成只读，关解决方案时清掉，启动后清掉一天前的。</summary>
    internal static class VcsTempFiles
    {
        private static bool _cleaned;

        public static string Folder => Path.Combine(Path.GetTempPath(), "UEToVS", "Vcs");

        public static string Write(string fileName, string text)
        {
            if (!_cleaned)
            {
                _cleaned = true;
                Cleanup(TimeSpan.FromDays(1));
            }
            string dir = Path.Combine(Folder, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string name = Sanitize(fileName);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, text ?? string.Empty, new System.Text.UTF8Encoding(true));
            try { File.SetAttributes(path, FileAttributes.ReadOnly); } catch (Exception) { }
            return path;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "file";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 120 ? name.Substring(name.Length - 120) : name;
        }

        public static void Cleanup(TimeSpan olderThan)
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (var d in Directory.GetDirectories(Folder))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) < olderThan) continue;
                        foreach (var f in Directory.GetFiles(d))
                        {
                            File.SetAttributes(f, FileAttributes.Normal);
                            File.Delete(f);
                        }
                        Directory.Delete(d, false);
                    }
                    catch (Exception)
                    {
                        // 还开着的文件：下次再清
                    }
                }
            }
            catch (Exception ex)
            {
                VcsService.L.Debug("vcs.temp.cleanupFailed", ex.Message);
            }
        }
    }
}
