using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Vcs;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>版本信息命令的 action id（CommandCatalog 0x01B0–0x01BF 分派到这里）。</summary>
    public static class VcsIds
    {
        public const string ToggleInlineBlame = "uetovs.vcs.toggleInlineBlame";
        public const string ToggleBlameGutter = "uetovs.vcs.toggleBlameGutter";
        public const string NextChange = "uetovs.vcs.nextChange";
        public const string PreviousChange = "uetovs.vcs.previousChange";
        public const string ShowChange = "uetovs.vcs.showChange";
        public const string CompareWithBase = "uetovs.vcs.compareWithBase";
        public const string SelectionHistory = "uetovs.vcs.selectionHistory";
        public const string FunctionHistory = "uetovs.vcs.functionHistory";
    }

    /// <summary>版本信息（Git / Perforce 只读）的界面文字。</summary>
    public static class VcsStrings
    {
        // ---- 命令
        public static string CmdToggleInlineBlame => Loc.T("显示 / 隐藏行尾追溯", "Show / Hide Inline Blame");
        public static string CmdToggleBlameGutter => Loc.T("注解", "Annotate");
        public static string CmdNextChange => Loc.T("下一处改动", "Next Change");
        public static string CmdPreviousChange => Loc.T("上一处改动", "Previous Change");
        public static string CmdShowChange => Loc.T("查看光标处的改动…", "Show Change at Caret...");
        public static string CmdCompareWithBase => Loc.T("和 HEAD / #have 比较", "Compare with HEAD / #have");
        public static string CmdSelectionHistory => Loc.T("查看选中部分的历史…", "Show History for Selection...");
        public static string CmdFunctionHistory => Loc.T("查看此函数的历史…", "Show History for Function...");

        public static readonly string[] KwBlame = { "git", "blame", "annotate", "追溯", "作者", "提交", "perforce", "p4", "gitlens" };
        public static readonly string[] KwChanges = { "git", "change", "diff", "hunk", "改动", "下一处", "上一处", "回滚", "rollback", "revert" };
        public static readonly string[] KwHistory = { "git", "history", "log", "历史", "提交", "函数", "selection", "选中", "p4", "filelog" };

        // ---- 状态栏
        public static string NotInVcs => Loc.T("当前文件不在 Git 仓库或 Perforce 工作区里", "The current file is not in a Git repository or Perforce workspace");
        public static string NotTracked => Loc.T("当前文件还没加入版本库，没有可比较的版本", "The current file is not in version control yet: nothing to compare with");
        public static string NoChanges(VcsKind kind) => kind == VcsKind.Perforce
            ? Loc.T("当前文件和 #have 版本相同", "The current file matches its #have revision")
            : Loc.T("当前文件和 HEAD 相同", "The current file matches HEAD");
        public static string Wrapped => Loc.T("已回到第一处改动", "Wrapped to the first change");
        public static string WrappedBack => Loc.T("已回到最后一处改动", "Wrapped to the last change");
        public static string NoChangeAtCaret => Loc.T("光标处没有改动（点行号旁的色条也能打开）", "No change at the caret (clicking the bar next to the line numbers works too)");
        public static string Loading => Loc.T("正在读取版本信息…", "Reading version information...");
        public static string Blaming => Loc.T("正在追溯…", "Blaming...");
        public static string InlineOn => Loc.T("已显示行尾追溯", "Inline blame shown");
        public static string InlineOff => Loc.T("已隐藏行尾追溯（本次 VS 会话）", "Inline blame hidden (for this VS session)");
        public static string InlineOffInSettings => Loc.T("行尾追溯已在设置里关闭（设置 > 编辑 > 版本信息）", "Inline blame is off in Settings > Editing > Version control");
        public static string InlineEngineOff => Loc.T("引擎文件默认不做行尾追溯，可在设置 > 编辑 > 版本信息里打开；注解栏仍可用", "Engine files are not blamed inline by default (Settings > Editing > Version control); annotations still work");
        public static string Copied => Loc.T("已复制", "Copied");
        public static string RolledBack => Loc.T("已回滚这一块（Ctrl+Z 撤销）", "Block rolled back (Ctrl+Z undoes it)");
        public static string ReadOnly => Loc.T("文件是只读的，不能回滚", "The file is read-only: nothing was rolled back");
        public static string Stale => Loc.T("这一块已经变了，请重新打开", "The block has changed meanwhile: open it again");
        public static string Canceled => Loc.T("已取消", "Canceled");
        public static string NotInFunction => Loc.T("光标不在函数里（或代码索引还没准备好）", "The caret is not in a function (or the code index is not ready)");
        public static string AllUncommitted => Loc.T("选中的行都还没提交，没有历史", "The selected lines are not committed yet: no history");
        public static string HistoryEmpty => Loc.T("没有找到改过这几行的提交", "No commit changed these lines");
        public static string NoPrevious => Loc.T("这已经是这个文件最早的版本", "This is the earliest revision of the file");
        public static string Uncommitted => Loc.T("这一行还没提交", "This line is not committed yet");
        public static string Failed(string reason) => Loc.F("版本信息：{0}", "Version control: {0}", reason);

        // ---- 失败原因（VcsException.Message）
        public static string GitNotFound => Loc.T("找不到 git，请安装 Git 或把 git 加到 PATH", "git was not found: install Git or add it to PATH");
        public static string P4NotFound => Loc.T("找不到 p4 命令行（安装 P4V 时勾选 Command-Line Client）", "p4 was not found (install the Command-Line Client with P4V)");
        public static string P4Login => Loc.T("Perforce 登录已过期：请在 P4V 或命令行（p4 login）登录后再试", "Your Perforce login has expired: log in with P4V or p4 login and try again");
        public static string TimedOut(string what) => Loc.F("{0} 超时", "{0} timed out", what);
        public static string CommandFailed(string what, string detail) => Loc.F("{0} 失败：{1}", "{0} failed: {1}", what, detail);

        // ---- 悬停卡片
        public static string CopyHash => Loc.T("复制哈希", "Copy Hash");
        public static string CopyChange => Loc.T("复制变更号", "Copy Change Number");
        public static string ShowDiff => Loc.T("查看这次提交的 diff", "Show This Commit's Diff");
        public static string ShowDiffTip => Loc.T("在 VS 的比较窗口里看这次提交对当前文件的改动", "See what this commit changed in the current file, in VS's diff window");
        public static string BlamePrevious => Loc.T("追溯上一版", "Blame Previous Revision");
        public static string BlamePreviousTip => Loc.T("打开这次提交之前的版本并显示注解栏，看改这行之前是谁写的", "Open the revision before this commit with annotations, to see who wrote the line before");
        public static string FilesHeader(int n, bool truncated) => truncated
            ? Loc.F("改动的文件（前 {0} 个）", "Changed files (first {0})", n)
            : Loc.F("改动的文件（{0}）", "Changed files ({0})", n);
        public static string FileTip => Loc.T("点击看这个文件在这次提交里的 diff", "Click to see this file's diff in the commit");
        public static string ReadingDetails => Loc.T("正在读取提交详情…", "Reading the commit...");
        public static string ChangeLabel(string id) => Loc.F("变更 {0}", "Change {0}", id);

        // ---- 提交面板（注解里点一行）
        public static string CommitWindowCaption(string id, bool perforce) => string.IsNullOrEmpty(id)
            ? Loc.T("提交的文件", "Committed Files")
            : perforce ? Loc.F("在变更 {0} 中提交的文件", "Files Submitted in Change {0}", id)
                       : Loc.F("提交 {0} 改动的文件", "Files Changed in Commit {0}", id);
        public static string ShowFileDiff => Loc.T("查看选中文件在这次提交里的 diff（也可以双击文件）", "Show the selected file's diff in this commit (or double-click the file)");
        public static string CopyId => Loc.T("复制提交号 / 变更号", "Copy the commit hash / change number");
        public static string FileCount(int n) => Loc.F("{0} 个文件", n == 1 ? "{0} file" : "{0} files", n);
        public static string FileAdded => Loc.T("新增", "Added");
        public static string FileDeleted => Loc.T("删除", "Deleted");
        public static string FileMoved => Loc.T("移动", "Moved");
        public static string NoDetails => Loc.T("读不到这次提交的详情", "Cannot read this commit");
        public static string NoFiles => Loc.T("这次提交没有文件改动", "This commit changes no files");

        // ---- 改动块
        public static string HunkTitle(ChangeKind kind, int first, int last)
        {
            string k = kind == ChangeKind.Added ? Loc.T("新增", "Added") : kind == ChangeKind.Deleted ? Loc.T("删除", "Deleted") : Loc.T("修改", "Modified");
            return first == last ? Loc.F("{0} · 第 {1} 行", "{0} · line {1}", k, first) : Loc.F("{0} · 第 {1}–{2} 行", "{0} · lines {1}–{2}", k, first, last);
        }
        public static string RollBack => Loc.T("回滚这一块", "Roll Back");
        public static string RollBackTip => Loc.T("把这几行改回基准版本（只改编辑器里的内容，可以撤销）", "Put these lines back to the base version (editor only, undoable)");
        public static string CopyOld => Loc.T("复制旧内容", "Copy Old Text");
        public static string PrevHunkTip => Loc.T("上一处改动", "Previous change");
        public static string NextHunkTip => Loc.T("下一处改动", "Next change");
        public static string MoreLines(int n) => Loc.F("… 还有 {0} 行", "… {0} more lines", n);

        // ---- 历史
        public static string HistoryProgress(int n) => n <= 0 ? Loc.T("正在查看历史…", "Reading the history...") : Loc.F("正在查看历史…已找到 {0} 次提交", "Reading the history... {0} commits so far", n);
        public static string HistoryTitle(string file, int start, int end, int count) =>
            Loc.F("{0} 第 {1}–{2} 行的历史（{3} 次提交，选一个看 diff）", "History of {0} lines {1}–{2} ({3} commits; pick one for its diff)", file, start, end, count);
        public static string HistoryWait => Loc.T("UEToVS 版本信息", "UEToVS version control");
        public static string HistoryWaitText(string file) => Loc.F("正在查看 {0} 选中部分的历史…", "Reading the history of the selection in {0}...", file);

        // ---- 比较窗口 / 旧版本文档
        public static string WorkingCopy => Loc.T("工作区", "Working copy");
        public static string CompareCaption(string file, string baseLabel) => Loc.F("{0}：{1} ↔ 工作区", "{0}: {1} ↔ working copy", file, baseLabel);
        public static string CommitCaption(string file, string id) => Loc.F("{0}：{1} 的改动", "{0}: changes in {1}", file, id);
        public static string Before => Loc.T("之前", "Before");
        public static string NewFile => Loc.T("（新文件）", "(new file)");
        public static string GutterLoading => Loc.T("追溯中…", "Blaming…");
    }
}
