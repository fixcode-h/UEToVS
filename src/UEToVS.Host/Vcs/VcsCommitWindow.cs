using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Vcs;
using UEToVS.Host.Shell;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Vcs
{
    /// <summary>
    /// 提交面板（v0.10.2，照用户给的 Rider“在更改列表中提交的修订”截图）：在注解里点一行打开。停靠窗口，标题是提交号 / 变更号；
    /// 上面按目录树列出这次提交改的所有文件（<see cref="ChangeTree"/>，目录后面是文件数，可展开折叠），下面是提交说明（带 vcs.issueLinks 链接）。
    /// 双击 / 回车一个文件看它在这次提交里的 diff。工具栏：看 diff、全部展开 / 折叠、复制提交号、追溯上一版。只有一个实例，换提交时内容替换。
    /// </summary>
    [Guid(GuidString)]
    public sealed class VcsCommitWindow : ToolWindowPane
    {
        public const string GuidString = "8a2f6c1e-4b7d-4e59-9c3a-6d1f0b2e7a41";

        private readonly ToolWindowShell _shell = new ToolWindowShell();
        private readonly TreeList<ChangeTreeNode> _tree;
        private readonly UiText _who = new UiText((string)null, TextVariant.Small) { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 2) };
        private readonly TextBlock _message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 8) };
        private readonly UiText _status = new UiText((string)null, TextVariant.Small) { Margin = new Thickness(10, 6, 10, 6) };
        private readonly UiButton _blamePrev;
        private CancellationTokenSource _cts;

        // 当前显示的提交
        private IVcsProvider _provider;
        private VcsCommit _commit;
        private CommitDetails _details;
        private VcsDocument _doc;
        private int _baseLine;

        public VcsCommitWindow() : base(null)
        {
            Caption = VcsStrings.CommitWindowCaption(null, false);
            _tree = new TreeList<ChangeTreeNode>(new DelegatePresenter<ChangeTreeNode>(Present));
            _tree.ItemInvoked += (s, e) => Safe.Execute(VcsService.L, "vcs.commitWindow.invoke", () => OpenDiff(e.Item));

            var diff = new IconButton(KnownMonikers.CompareFiles, VcsStrings.ShowFileDiff, "Enter");
            diff.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.commitWindow.diff", () => OpenDiff(_tree.SelectedNode?.Item));
            var expand = new IconButton(Ui.FrozenGeometry("M 0,0 L 4,4 L 8,0 M 0,5 L 4,9 L 8,5"), () => UEToVS.UI.Strings.TreeExpandAll, null, 9);
            expand.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.commitWindow.expand", () => _tree.ExpandAll(true));
            var collapse = new IconButton(Ui.FrozenGeometry("M 0,4 L 4,0 L 8,4 M 0,9 L 4,5 L 8,9"), () => UEToVS.UI.Strings.TreeCollapseAll, null, 9);
            collapse.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.commitWindow.collapse", () => _tree.ExpandAll(false));
            var copy = new IconButton(KnownMonikers.Copy, VcsStrings.CopyId);
            copy.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.commitWindow.copy", () =>
            {
                if (_commit == null) return;
                Clipboard.SetText(_commit.Id);
                VsStatusBar.SetText(VcsStrings.Copied + "：" + _commit.Id);
            });
            _blamePrev = new UiButton(VcsStrings.BlamePrevious, ButtonKind.Ghost) { ToolTip = VcsStrings.BlamePreviousTip, IsEnabled = false };
            _blamePrev.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.commitWindow.blamePrevious", BlamePrevious);
            _shell.AddToolbarItem(diff);
            _shell.AddToolbarItem(expand);
            _shell.AddToolbarItem(collapse);
            _shell.AddToolbarItem(copy);
            _shell.AddToolbarItem(_blamePrev, right: true);
            _shell.ShowFooter = false;

            _message.Bind(TextBlock.ForegroundProperty, Tokens.TextPrimary).Bind(TextBlock.FontSizeProperty, Tokens.FontSizeBody);
            var bottom = new StackPanel();
            bottom.Children.Add(_who);
            bottom.Children.Add(_message);
            var bottomScroll = new ScrollViewer { Content = bottom, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

            var top = new Grid();
            top.Children.Add(_tree);
            top.Children.Add(_status);
            var split = new Grid();
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 60 });
            split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 40 });
            var splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Focusable = false };
            splitter.Bind(Control.BackgroundProperty, Tokens.BorderSubtle);
            Grid.SetRow(top, 0);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(bottomScroll, 2);
            split.Children.Add(top);
            split.Children.Add(splitter);
            split.Children.Add(bottomScroll);
            _shell.Body = split;
            Content = _shell;
        }

        private static void Present(ChangeTreeNode n, RowData d)
        {
            d.Primary = n.Name;
            if (n.IsDirectory)
            {
                d.Moniker = KnownMonikers.FolderClosed;
                d.Right = VcsStrings.FileCount(n.FileCount);
                d.ToolTip = n.FullPath;
                return;
            }
            d.Moniker = KnownMonikers.Document;
            d.ToolTip = n.File.OldPath != null && n.File.OldPath != n.File.Path ? n.File.OldPath + " → " + n.File.Path : n.File.Path;
            var kind = ChangeTree.KindOf(n.File.Status);
            if (kind == FileChangeKind.Added) d.AddBadge(VcsStrings.FileAdded, BadgeKind.Neutral);
            else if (kind == FileChangeKind.Deleted)
            {
                d.AddBadge(VcsStrings.FileDeleted, BadgeKind.Neutral);
                d.IsDimmed = true;
            }
            else if (kind == FileChangeKind.Moved) d.AddBadge(VcsStrings.FileMoved, BadgeKind.Neutral);
        }

        /// <summary>注解里点了一行：打开面板显示这次提交（UI 线程）。</summary>
        internal static void Show(VcsDocument doc, VcsCommit commit, int baseLine)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var package = HostPackage.Instance;
            if (package == null || doc?.Provider == null || commit == null) return;
            if (commit.IsUncommitted)
            {
                VsStatusBar.SetText(VcsStrings.Uncommitted);
                return;
            }
            _ = Safe.RunAsync(VcsService.L, "vcs.commitWindow.show", async () =>
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                var window = await package.ShowToolWindowAsync(typeof(VcsCommitWindow), 0, true, package.DisposalToken) as VcsCommitWindow;
                if (window == null)
                {
                    VcsService.L.Error("vcs.commitWindow.create.failed", "Cannot create the commit tool window", null);
                    CommitCard.Show(doc, commit, baseLine, UEToVS.UI.Shells.PopupAnchor.Mouse()); // 退回小卡片
                    return;
                }
                window.Load(doc, commit, baseLine);
            });
        }

        private void Load(VcsDocument doc, VcsCommit commit, int baseLine)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _doc = doc;
            _provider = doc.Provider;
            _commit = commit;
            _baseLine = baseLine;
            _details = null;
            bool p4 = _provider.Kind == VcsKind.Perforce;
            string id = p4 ? commit.Id : _provider.ShortId(commit.Id);
            Caption = VcsStrings.CommitWindowCaption(id, p4);
            _shell.TitleText = () => commit.Summary ?? string.Empty;
            _who.Text = Who(commit);
            CommitCard.FillMessage(_message, commit.Message ?? commit.Summary);
            _tree.SetRoots(Array.Empty<TreeNode<ChangeTreeNode>>());
            _status.Text = VcsStrings.ReadingDetails;
            _status.Visibility = Visibility.Visible;
            _blamePrev.IsEnabled = _provider.PreviousOf(doc.Blame, commit.Id, doc.Revision?.Path ?? doc.FilePath) != null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = Safe.RunAsync(VcsService.L, "vcs.commitWindow.details", async () =>
            {
                CommitDetails d = null;
                string error = null;
                try
                {
                    d = await System.Threading.Tasks.Task.Run(() => VcsService.Instance.GetCommitAsync(_provider, commit.Id, ct), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (VcsException ex) { error = ex.Message; }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ct.IsCancellationRequested) return;
                _details = d;
                if (d == null)
                {
                    _status.Text = error ?? VcsStrings.NoDetails;
                    return;
                }
                if (d.Commit != null)
                {
                    _who.Text = Who(d.Commit);
                    CommitCard.FillMessage(_message, d.Commit.Message ?? d.Commit.Summary);
                }
                var nodes = ChangeTree.Build(d.Files);
                _tree.SetRoots(ToTree(nodes));
                _status.Visibility = d.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (d.Files.Count == 0) _status.Text = VcsStrings.NoFiles;
                if (d.FilesTruncated) _shell.TitleText = () => (commit.Summary ?? string.Empty) + "  ·  " + VcsStrings.FilesHeader(d.Files.Count, true);
                SelectCurrentFile();
                VcsService.L.Info("vcs.commitWindow.loaded", null, ("kind", _provider.Kind.ToString()), ("id", id), ("files", d.Files.Count),
                                  ("truncated", d.FilesTruncated), ("ms", sw.ElapsedMilliseconds));
            });
        }

        private static string Who(VcsCommit c)
        {
            string when = RelativeTime.Absolute(c.Time, c.DateOnly);
            string rel = RelativeTime.Format(c.Time, DateTimeOffset.Now, Loc.IsEnglish, c.DateOnly);
            return (c.Author ?? "?") + "  ·  " + when + (rel.Length > 0 ? " (" + rel + ")" : string.Empty);
        }

        private static List<TreeNode<ChangeTreeNode>> ToTree(List<ChangeTreeNode> nodes)
        {
            var list = new List<TreeNode<ChangeTreeNode>>(nodes.Count);
            foreach (var n in nodes)
                list.Add(n.IsDirectory ? new TreeNode<ChangeTreeNode>(n, ToTree(n.Children)) : new TreeNode<ChangeTreeNode>(n));
            return list;
        }

        /// <summary>选中打开注解的那个文件，方便直接回车看它的 diff。</summary>
        private void SelectCurrentFile()
        {
            if (_details == null || _doc == null) return;
            string current = (_doc.Revision?.Path ?? _doc.FilePath ?? string.Empty).Replace('\\', '/');
            string name = Path.GetFileName(current);
            CommitFile best = null;
            int bestLen = 0;
            foreach (var f in _details.Files)
            {
                string p = (f.Path ?? string.Empty).Replace('\\', '/');
                int n = CommonSuffix(p, current);
                if (n > bestLen && n >= name.Length) { best = f; bestLen = n; }
            }
            if (best != null) _tree.SelectItem(n => ReferenceEquals(n.File, best));
        }

        private static int CommonSuffix(string a, string b)
        {
            int n = 0;
            while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[a.Length - 1 - n]) == char.ToLowerInvariant(b[b.Length - 1 - n])) n++;
            return n;
        }

        private void OpenDiff(ChangeTreeNode node)
        {
            if (node == null || node.IsDirectory || _provider == null || _commit == null) return;
            var file = node.File;
            var p = _provider;
            var (before, after) = p.RevisionsOf(_details?.Commit ?? _commit, file);
            string afterPath = file.Path;
            string beforePath = file.OldPath ?? afterPath;
            string name = Path.GetFileName(afterPath.Replace('/', '\\'));
            VcsService.L.Info("vcs.commitWindow.fileDiff", null, ("file", name), ("status", file.Status));
            VcsDiffWindow.Run("vcs.commitWindow.diff", c => VcsDiffWindow.ShowRevisionsAsync(p, beforePath, before, afterPath, after,
                                                                                             VcsStrings.CommitCaption(name, p.ShortId(_commit.Id)), c));
        }

        private void BlamePrevious()
        {
            if (_doc == null || _commit == null || _provider == null) return;
            var prev = _provider.PreviousOf(_doc.Blame, _commit.Id, _doc.Revision?.Path ?? _doc.FilePath);
            if (prev == null) { VsStatusBar.SetText(VcsStrings.NoPrevious); return; }
            int line = _baseLine >= 0 && _doc.Blame != null && _baseLine < _doc.Blame.Lines.Length ? _doc.Blame.Lines[_baseLine].OriginalLine - 1 : _baseLine;
            var (rev, path) = prev.Value;
            var p = _provider;
            VcsDiffWindow.Run("vcs.blamePrevious", c => VcsDiffWindow.OpenRevisionAsync(p, path, rev, line, c));
        }
    }
}
