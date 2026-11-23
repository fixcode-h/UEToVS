using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Text;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Logic.Vcs;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Vcs
{
    /// <summary>
    /// 一个打开的文件的版本信息状态（挂在 ITextBuffer 上，色条、行尾追溯、注解栏共用一份）。基准版本只取一次（VcsService 缓存），
    /// 编辑停 250 ms 后在后台线程上和基准做行 diff，UI 线程只换结果；追溯按需取（光标停下、打开注解栏），编辑过的行用 diff 映射成“未提交”，
    /// 不重跑 blame。“追溯上一版”打开的旧版本文件没有色条，追溯的是那个版本。
    /// </summary>
    internal sealed class VcsDocument
    {
        public const int LargeFileLines = 5000;
        private static readonly TimeSpan DiffDelay = TimeSpan.FromMilliseconds(250);

        private readonly ITextBuffer _buffer;
        private readonly ITextDocument _document;
        private readonly DispatcherTimer _diffTimer;
        private CancellationTokenSource _loadCts = new CancellationTokenSource();
        private bool _blameRunning;
        private int _blameGeneration;
        private bool _loading;
        private int _users;
        private int _diffGeneration;

        private VcsDocument(ITextBuffer buffer)
        {
            _buffer = buffer;
            _document = EditorServices.Document(buffer);
            FilePath = _document?.FilePath;
            Revision = VcsService.Instance.RevisionDocumentOf(FilePath);
            _diffTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = DiffDelay };
            _diffTimer.Tick += (s, e) =>
            {
                _diffTimer.Stop();
                Safe.Execute(VcsService.L, "vcs.diff.schedule", StartDiff);
            };
            _buffer.Changed += OnBufferChanged;
            if (_document != null) _document.FileActionOccurred += OnFileAction;
            VcsService.Instance.BaseInvalidated += OnBaseInvalidated;
            VcsService.Instance.SettingsChanged += OnSettingsChanged;
        }

        public static VcsDocument For(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(typeof(VcsDocument), () => new VcsDocument(buffer));

        /// <summary>色条 / 行尾追溯 / 注解栏各自 Attach 一次；全都 Detach（视图关了）后释放。</summary>
        public void Attach()
        {
            _users++;
            if (_users == 1 && Provider == null && !_loading) Reload();
        }

        public void Detach()
        {
            if (--_users > 0) return;
            _loadCts.Cancel();
            _diffTimer.Stop();
            _buffer.Changed -= OnBufferChanged;
            if (_document != null) _document.FileActionOccurred -= OnFileAction;
            VcsService.Instance.BaseInvalidated -= OnBaseInvalidated;
            VcsService.Instance.SettingsChanged -= OnSettingsChanged;
            _buffer.Properties.RemoveProperty(typeof(VcsDocument));
        }

        public string FilePath { get; }

        /// <summary>“追溯上一版”打开的旧版本（不是 null 时这个文件是临时文件）。</summary>
        public RevisionDocument Revision { get; }

        public IVcsProvider Provider { get; private set; }
        public VcsBase Base { get; private set; }
        public string[] BaseLines { get; private set; }

        /// <summary>和基准的差异（行号对应 <see cref="DiffSnapshot"/>）。</summary>
        public IReadOnlyList<DiffHunk> Hunks { get; private set; } = Array.Empty<DiffHunk>();
        public ITextSnapshot DiffSnapshot { get; private set; }

        public BlameResult Blame { get; private set; }
        public string BlameError { get; private set; }
        public bool BlameRunning => _blameRunning;

        /// <summary>有版本信息可显示（在仓库里且读到了基准，或是旧版本文档）。</summary>
        public bool Ready => Provider != null && (Revision != null || Base != null);

        public bool IsLarge => _buffer.CurrentSnapshot.LineCount > LargeFileLines;

        /// <summary>差异或追溯变了（UI 线程）：色条、注解栏、行尾重画。</summary>
        public event EventHandler Changed;

        private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        // ---------------------------------------------------------------- 载入基准

        private void Reload()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(FilePath) || !VcsService.Active) return;
            _loadCts.Cancel();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;
            _loading = true;
            Blame = null;
            BlameError = null;
            _blameRunning = false;
            _blameGeneration++;
            string path = FilePath;
            var revision = Revision;
            _ = Safe.RunAsync(VcsService.L, "vcs.document.load", async () =>
            {
                IVcsProvider provider = null;
                VcsBase b = null;
                string error = null;
                await TaskScheduler.Default;
                try
                {
                    provider = await VcsService.Instance.ProviderForAsync(path, ct).ConfigureAwait(false);
                    if (provider != null && revision == null) b = await VcsService.Instance.GetBaseAsync(provider, path, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (VcsException ex)
                {
                    error = ex.Message;
                    VcsService.L.Info("vcs.base.failed", ex.Message, ("file", Path.GetFileName(path)), ("failure", ex.Failure.ToString()));
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ct.IsCancellationRequested) return;
                _loading = false;
                Provider = provider;
                Base = b;
                BaseLines = b?.Text != null ? LineDiff.SplitLines(b.Text) : null;
                BlameError = error;
                if (BaseLines != null) StartDiff();
                else
                {
                    Hunks = Array.Empty<DiffHunk>();
                    DiffSnapshot = _buffer.CurrentSnapshot;
                    Raise();
                }
            });
        }

        private void OnBaseInvalidated(object sender, string rootOrPath)
        {
            if (Provider == null || FilePath == null) return;
            if (Logic.Mcp.VcsRoot.IsSameOrUnder(Path.GetDirectoryName(FilePath) ?? FilePath, rootOrPath) ||
                string.Equals(FilePath, rootOrPath, StringComparison.OrdinalIgnoreCase))
                Safe.Execute(VcsService.L, "vcs.document.invalidated", Reload);
        }

        private void OnSettingsChanged(object sender, EventArgs e) => Safe.Execute(VcsService.L, "vcs.document.settings", () =>
        {
            if (!VcsService.Active)
            {
                Provider = null;
                Base = null;
                BaseLines = null;
                Hunks = Array.Empty<DiffHunk>();
                Raise();
            }
            else if (Provider == null && !_loading) Reload();
            else Raise();
        });

        private void OnFileAction(object sender, TextDocumentFileActionEventArgs e)
        {
            // git checkout / p4 sync 改了磁盘上的文件，VS 重新载入：重新取基准（P4 只在这时重新问 have 版本，不定时访问服务器）
            if ((e.FileActionType & FileActionTypes.ContentLoadedFromDisk) == 0) return;
            Safe.Execute(VcsService.L, "vcs.document.reloaded", () =>
            {
                VcsService.Instance.InvalidateFile(FilePath);
                Reload();
            });
        }

        // ---------------------------------------------------------------- diff

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (BaseLines == null) return;
            _diffTimer.Stop();
            _diffTimer.Start();
        }

        private void StartDiff()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var baseLines = BaseLines;
            if (baseLines == null) return;
            var snapshot = _buffer.CurrentSnapshot;
            int gen = ++_diffGeneration;
            _ = Safe.RunAsync(VcsService.L, "vcs.diff", async () =>
            {
                await TaskScheduler.Default;
                var sw = Stopwatch.StartNew();
                var current = new string[snapshot.LineCount];
                for (int i = 0; i < current.Length; i++) current[i] = snapshot.GetLineFromLineNumber(i).GetText();
                var hunks = LineDiff.Compute(baseLines, current);
                double ms = sw.Elapsed.TotalMilliseconds;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (gen != _diffGeneration || !ReferenceEquals(baseLines, BaseLines)) return;
                Hunks = hunks;
                DiffSnapshot = snapshot;
                if (ms > 50 || hunks.Count > 0) VcsService.L.Debug("vcs.diff", null, ("file", Path.GetFileName(FilePath)), ("lines", current.Length), ("hunks", hunks.Count), ("ms", Math.Round(ms, 1)));
                Raise();
            });
        }

        /// <summary>当前快照第 <paramref name="line"/> 行在 diff 快照里的行号（diff 还没跟上最新编辑时近似对齐）。</summary>
        public int ToDiffLine(ITextSnapshot snapshot, int line)
        {
            var ds = DiffSnapshot;
            if (ds == null || snapshot == null || ds == snapshot || line < 0 || line >= snapshot.LineCount) return line;
            var p = snapshot.GetLineFromLineNumber(line).Start.TranslateTo(ds, PointTrackingMode.Negative);
            return p.GetContainingLine().LineNumber;
        }

        // ---------------------------------------------------------------- 追溯

        /// <summary>要追溯（第一次调用时起后台任务，之后复用结果；结果经 <see cref="Changed"/> 通知）。失败时 <see cref="BlameError"/> 有原因。</summary>
        public void RequestBlame()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Blame != null || _blameRunning) return;
            var provider = Provider;
            if (provider == null) return;
            if (Revision == null && Base?.Tracked != true) return; // 没提交过的文件：全是“未提交”
            var ct = _loadCts.Token;
            string path = Revision?.Path ?? FilePath, revision = Revision?.Revision, key = Base?.Key;
            int gen = ++_blameGeneration;
            _blameRunning = true;
            _ = Safe.RunAsync(VcsService.L, "vcs.blame.apply", async () =>
            {
                BlameResult result = null;
                string error = null;
                try { result = await Task.Run(() => VcsService.Instance.BlameAsync(provider, path, revision, key, ct), ct); }
                catch (OperationCanceledException) { error = null; }
                catch (VcsException ex) { error = ex.Message; }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (gen != _blameGeneration) return;
                _blameRunning = false;
                Blame = result;
                BlameError = error;
                Raise();
            });
        }

        /// <summary>
        /// 当前快照第 <paramref name="line"/> 行属于哪次提交：编辑过 / 新加的行、没进版本库的文件是“未提交”；追溯还没好时 null。
        /// </summary>
        public VcsCommit CommitAt(ITextSnapshot snapshot, int line, out int baseLine)
        {
            baseLine = -1;
            if (!Ready) return null;
            if (Revision == null && Base?.Tracked != true) return UncommittedCommit;
            int diffLine = ToDiffLine(snapshot, line);
            baseLine = Revision != null ? diffLine : HunkMath.ToBaseLine(Hunks, diffLine);
            if (baseLine < 0) return UncommittedCommit;
            return Blame?.CommitAt(baseLine);
        }

        public static readonly VcsCommit UncommittedCommit = new VcsCommit { Id = GitBlameParser.UncommittedId, IsUncommitted = true };

        /// <summary>引擎文件（行尾追溯默认关）。</summary>
        private bool? _isEngine;

        public bool IsEngineFile => _isEngine ?? (bool)(_isEngine = ComputeIsEngine());

        private bool ComputeIsEngine()
        {
            {
                if (FilePath == null) return false;
                var p = HostPackage.Instance;
                string engineRoot = null;
                try { engineRoot = p == null ? null : Unreal.UnrealTools.EngineRoot(p); }
                catch (Exception) { } // 拿不到引擎目录时只按路径里的 Engine\ 判断
                return VcsDetector.IsEngineFile(Revision?.Path ?? FilePath, engineRoot);
            }
        }
    }
}
