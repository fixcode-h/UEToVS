using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Host.Logic.Vcs
{
    /// <summary>文件所在的版本管理（最近一层）。只有 Git 和 Perforce 有编辑器内的版本信息，其它（svn、hg、plastic）当作没有。</summary>
    public enum VcsKind
    {
        None = 0,
        Git = 1,
        Perforce = 2,
    }

    /// <summary>一次提交（Git 提交 / P4 变更列表）。P4 的 <see cref="Id"/> 是变更列表号。</summary>
    public sealed class VcsCommit
    {
        public string Id { get; set; }
        public string Author { get; set; }
        public string AuthorMail { get; set; }
        public DateTimeOffset Time { get; set; }

        /// <summary>提交说明首行。</summary>
        public string Summary { get; set; }

        /// <summary>完整提交说明（blame 只给首行，悬停卡片再补）。</summary>
        public string Message { get; set; }

        /// <summary>本地还没提交的行（Git 的 0000… 提交，或编辑器里改过的行）。</summary>
        public bool IsUncommitted { get; set; }

        public string[] Parents { get; set; } = Array.Empty<string>();

        /// <summary>只有日期没有时间（p4 annotate 只给日期）。</summary>
        public bool DateOnly { get; set; }
    }

    /// <summary>追溯结果里的一行。</summary>
    public sealed class BlameLine
    {
        public string CommitId { get; set; }

        /// <summary>这一行在那次提交里的行号（从 1 开始），“追溯上一版”时滚到这里。</summary>
        public int OriginalLine { get; set; }
    }

    /// <summary>整个文件的追溯：每行属于哪次提交（Lines[i] 是第 i 行，从 0 开始）。</summary>
    public sealed class BlameResult
    {
        public BlameLine[] Lines { get; set; } = Array.Empty<BlameLine>();
        public Dictionary<string, VcsCommit> Commits { get; } = new Dictionary<string, VcsCommit>(StringComparer.Ordinal);

        /// <summary>Git：提交 → 它的上一版（父提交与那时的文件路径），来自 porcelain 的 previous 行。</summary>
        public Dictionary<string, (string Revision, string Path)> Previous { get; } = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        public VcsCommit CommitAt(int line)
        {
            if (line < 0 || line >= Lines.Length || Lines[line] == null) return null;
            return Commits.TryGetValue(Lines[line].CommitId ?? string.Empty, out var c) ? c : null;
        }
    }

    /// <summary>提交里改动的一个文件。</summary>
    public sealed class CommitFile
    {
        /// <summary>M / A / D / R / C（Git name-status）或 P4 的 action（edit / add / delete / move/add …）。</summary>
        public string Status { get; set; }
        public string Path { get; set; }
        public string OldPath { get; set; }

        /// <summary>P4：这个文件在这次变更里的版本号（#rev）。</summary>
        public int Revision { get; set; }
    }

    /// <summary>悬停卡片要的完整信息。</summary>
    public sealed class CommitDetails
    {
        public VcsCommit Commit { get; set; }
        public List<CommitFile> Files { get; } = new List<CommitFile>();

        /// <summary>文件太多被截断（P4 describe 的文件列表只取前面的）。</summary>
        public bool FilesTruncated { get; set; }
    }

    /// <summary>选中部分 / 函数历史里的一项：哪次提交，以及看 diff 用的前后两个版本。</summary>
    public sealed class HistoryEntry
    {
        public VcsCommit Commit { get; set; }

        /// <summary>这次提交里文件的版本（Git 提交哈希 / P4 "@=变更号"）。</summary>
        public string Revision { get; set; }
        public string Path { get; set; }

        /// <summary>上一版（Git 父提交 / P4 "@变更号-1"）；null 表示这次是新加的文件。</summary>
        public string PreviousRevision { get; set; }
        public string PreviousPath { get; set; }
    }

    /// <summary>改动色条的基准：Git 的 HEAD 版本 / P4 的 #have 版本。</summary>
    public sealed class VcsBase
    {
        /// <summary>缓存键：Git 的 blob 哈希、P4 的 have 版本号；内容没变就不重算追溯。</summary>
        public string Key { get; set; }

        /// <summary>基准版本的文本；null = 这个文件不在版本库里（新建、未跟踪）。</summary>
        public string Text { get; set; }

        /// <summary>基准的版本说明（"HEAD" / "#12"），用在 diff 标题。</summary>
        public string RevisionLabel { get; set; }

        public bool Tracked => Text != null;
    }

    /// <summary>
    /// 一种版本管理的只读操作（Git、Perforce 各一个实现，在 Host/Vcs）。所有方法都在后台线程调用，进程都带超时并响应取消；
    /// 失败时抛 <see cref="VcsException"/>（带给用户看的说明），不返回半截数据。路径一律是本机绝对路径，实现自己换成仓库内路径。
    /// </summary>
    public interface IVcsProvider : IDisposable
    {
        VcsKind Kind { get; }

        /// <summary>仓库 / 工作区根目录。</summary>
        string Root { get; }

        /// <summary>改动色条的基准版本。</summary>
        Task<VcsBase> GetBaseAsync(string path, CancellationToken ct);

        /// <summary>追溯。<paramref name="revision"/> 为 null 时是基准版本；否则是 <see cref="PreviousOf"/> 给的版本，<paramref name="path"/> 也可以是那时的路径。</summary>
        Task<BlameResult> BlameAsync(string path, string revision, CancellationToken ct);

        /// <summary>提交的完整说明和改动的文件（结果可以全局缓存：提交不会变）。</summary>
        Task<CommitDetails> GetCommitAsync(string commitId, CancellationToken ct);

        /// <summary>某个版本的文件内容；文件在那个版本不存在时返回 null。</summary>
        Task<string> GetFileTextAsync(string path, string revision, CancellationToken ct);

        /// <summary>
        /// 基准版本第 <paramref name="startLine"/>–<paramref name="endLine"/> 行（从 1 开始，含两端）的历史，最近的在前，最多 <paramref name="maxCommits"/> 次提交。
        /// </summary>
        Task<IReadOnlyList<HistoryEntry>> GetLineHistoryAsync(string path, int startLine, int endLine, int maxCommits, IProgress<string> progress, CancellationToken ct);

        /// <summary>“追溯上一版”：改这一行的提交之前的版本和那时的路径；没有上一版（第一次提交）时 null。</summary>
        (string Revision, string Path)? PreviousOf(BlameResult blame, string commitId, string path);

        /// <summary>提交在这个提交里文件的版本（看这次提交的 diff 用）：(之前, 之后)。</summary>
        (string Before, string After) RevisionsOf(VcsCommit commit, CommitFile file);

        /// <summary>界面上显示的短编号（Git 前 8 位，P4 变更号）。</summary>
        string ShortId(string commitId);

        /// <summary>
        /// 便宜地看仓库状态变没变（Git：HEAD 指向的提交；P4：null = 不查，靠文件重新载入）。变了就丢掉这个仓库的缓存。
        /// </summary>
        Task<string> GetStateKeyAsync(CancellationToken ct);
    }

    /// <summary>版本管理命令失败（找不到 git / p4、超时、未登录…），<see cref="Exception.Message"/> 是给用户看的说明。</summary>
    public sealed class VcsException : Exception
    {
        public VcsException(string message, VcsFailure failure = VcsFailure.Failed) : base(message)
        {
            Failure = failure;
        }

        public VcsFailure Failure { get; }
    }

    public enum VcsFailure
    {
        Failed = 0,
        NotFound = 1,
        TimedOut = 2,
        /// <summary>P4 票据过期 / 没登录：提示在 P4V 或命令行登录，不弹自己的登录框。</summary>
        LoginRequired = 3,
        NotTracked = 4,
    }
}
