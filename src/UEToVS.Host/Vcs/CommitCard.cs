using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Vcs;
using UEToVS.Host.Shell;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Vcs
{
    /// <summary>
    /// 行尾追溯 / 注解栏的悬停卡片（UEToVS.UI 的 PopupShell，和其它弹窗同一套样式）：提交说明首行、作者和邮箱、时间、哈希，
    /// 后台补上完整说明（带 vcs.issueLinks 链接）和本次提交改动的文件；按钮：复制哈希、查看这次提交的 diff、追溯上一版。整个应用只有一个实例。
    /// </summary>
    internal static class CommitCard
    {
        private static PopupShell _shell;
        private static CancellationTokenSource _cts;

        public static bool IsOpen => _shell != null && _shell.IsVisible;

        public static void Close()
        {
            _cts?.Cancel();
            if (IsOpen) _shell.Dismiss();
        }

        /// <param name="baseLine">这一行在基准（或旧版本文档）里的行号，从 0 开始；“追溯上一版”滚到那附近。</param>
        public static void Show(VcsDocument doc, VcsCommit commit, int baseLine, PopupAnchor anchor)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (doc?.Provider == null || commit == null) return;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            if (_shell == null)
            {
                _shell = new PopupShell("uetovs.vcs.commit", new Size(540, 340)) { MinVisibleWidth = 320, MinVisibleHeight = 140 };
                _shell.Dismissed += (s, e) => _cts?.Cancel();
            }
            _shell.OwnerHandle = VsMainWindow.Handle;
            var p = doc.Provider;
            bool english = Loc.IsEnglish;

            var root = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            var summary = new UiText(commit.IsUncommitted ? VcsStrings.Uncommitted : commit.Summary ?? string.Empty, TextVariant.Strong)
            {
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 0, 0, 4),
            };
            root.Children.Add(summary);
            if (!commit.IsUncommitted)
            {
                string who = commit.Author ?? "?";
                if (!string.IsNullOrEmpty(commit.AuthorMail) && p.Kind == VcsKind.Git) who += " <" + commit.AuthorMail + ">";
                string when = RelativeTime.Absolute(commit.Time, commit.DateOnly);
                string rel = RelativeTime.Format(commit.Time, DateTimeOffset.Now, english, commit.DateOnly);
                root.Children.Add(new UiText(who + "  ·  " + when + (rel.Length > 0 ? " (" + rel + ")" : string.Empty), TextVariant.Small) { TextWrapping = TextWrapping.Wrap });
                root.Children.Add(new UiText(p.Kind == VcsKind.Perforce ? VcsStrings.ChangeLabel(commit.Id) : commit.Id, TextVariant.Mono)
                {
                    Margin = new Thickness(0, 2, 0, 6), Focusable = false,
                });
            }
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6), Visibility = Visibility.Collapsed };
            message.Bind(TextBlock.ForegroundProperty, Tokens.TextPrimary).Bind(TextBlock.FontSizeProperty, Tokens.FontSizeBody);
            root.Children.Add(message);
            var filesHeader = new UiText(VcsStrings.ReadingDetails, TextVariant.GroupLabel) { Margin = new Thickness(0, 4, 0, 2) };
            var files = new StackPanel();
            if (!commit.IsUncommitted)
            {
                root.Children.Add(filesHeader);
                root.Children.Add(new ScrollViewer { Content = files, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            }

            CommitDetails details = null;
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            if (!commit.IsUncommitted)
            {
                var copy = new UiButton(p.Kind == VcsKind.Perforce ? VcsStrings.CopyChange : VcsStrings.CopyHash, ButtonKind.Secondary) { Margin = new Thickness(0, 0, 6, 4) };
                copy.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.card.copy", () =>
                {
                    Clipboard.SetText(commit.Id);
                    VsStatusBar.SetText(VcsStrings.Copied + "：" + commit.Id);
                });
                buttons.Children.Add(copy);
                var diff = new UiButton(VcsStrings.ShowDiff, ButtonKind.Secondary) { Margin = new Thickness(0, 0, 6, 4), ToolTip = VcsStrings.ShowDiffTip };
                diff.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.card.diff", () =>
                {
                    _shell.Dismiss();
                    ShowFileDiff(doc, commit, details, null);
                });
                buttons.Children.Add(diff);
                var prev = p.PreviousOf(doc.Blame, commit.Id, doc.Revision?.Path ?? doc.FilePath);
                var blamePrev = new UiButton(VcsStrings.BlamePrevious, ButtonKind.Secondary)
                {
                    Margin = new Thickness(0, 0, 6, 4), ToolTip = VcsStrings.BlamePreviousTip, IsEnabled = prev != null,
                };
                int originalLine = baseLine >= 0 && doc.Blame != null && baseLine < doc.Blame.Lines.Length ? doc.Blame.Lines[baseLine].OriginalLine - 1 : baseLine;
                blamePrev.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.card.blamePrevious", () =>
                {
                    _shell.Dismiss();
                    if (prev == null) { VsStatusBar.SetText(VcsStrings.NoPrevious); return; }
                    var (rev, path) = prev.Value;
                    VcsDiffWindow.Run("vcs.blamePrevious", c => VcsDiffWindow.OpenRevisionAsync(p, path, rev, originalLine, c));
                });
                buttons.Children.Add(blamePrev);
            }
            root.Children.Add(buttons);
            _shell.Body = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _shell.Title = commit.Summary ?? string.Empty;
            _shell.ShowAt(anchor);
            VcsService.L.Debug("vcs.card.show", null, ("kind", p.Kind.ToString()), ("id", p.ShortId(commit.Id)), ("uncommitted", commit.IsUncommitted));
            if (commit.IsUncommitted) return;

            // 完整说明和文件：后台取（Git 本地很快；P4 一次 describe，结果全局缓存）
            _ = Safe.RunAsync(VcsService.L, "vcs.card.details", async () =>
            {
                CommitDetails d = null;
                string error = null;
                try
                {
                    d = await System.Threading.Tasks.Task.Run(() => VcsService.Instance.GetCommitAsync(p, commit.Id, ct), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (VcsException ex) { error = ex.Message; }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ct.IsCancellationRequested) return;
                details = d;
                if (d == null)
                {
                    filesHeader.Text = error ?? string.Empty;
                    return;
                }
                string full = d.Commit?.Message ?? commit.Message ?? string.Empty;
                string body = BodyAfterSummary(full);
                summary.Text = d.Commit?.Summary ?? summary.Text;
                FillMessage(message, body);
                filesHeader.Text = VcsStrings.FilesHeader(d.Files.Count, d.FilesTruncated);
                foreach (var f in d.Files.Take(200))
                {
                    var file = f;
                    var link = new LinkText((f.Status ?? "?").PadRight(2) + " " + DisplayPath(f)) { Margin = new Thickness(0, 1, 0, 1), ToolTip = VcsStrings.FileTip };
                    link.Bind(TextBlock.FontSizeProperty, Tokens.FontSizeSmall);
                    link.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.card.fileDiff", () =>
                    {
                        _shell.Dismiss();
                        ShowFileDiff(doc, commit, details, file);
                    });
                    files.Children.Add(link);
                }
            });
        }

        private static string DisplayPath(CommitFile f) => f.OldPath != null && f.OldPath != f.Path ? f.OldPath + " → " + f.Path : f.Path;

        private static string BodyAfterSummary(string full)
        {
            if (string.IsNullOrEmpty(full)) return string.Empty;
            string m = full.Replace("\r\n", "\n").TrimStart('\n', ' ');
            int i = m.IndexOf('\n');
            return i < 0 ? string.Empty : m.Substring(i + 1).Trim('\n', ' ');
        }

        /// <summary>说明正文，vcs.issueLinks 匹配到的部分做成链接（点了用默认浏览器打开）。</summary>
        internal static void FillMessage(TextBlock block, string text)
        {
            block.Inlines.Clear();
            if (string.IsNullOrEmpty(text))
            {
                block.Visibility = Visibility.Collapsed;
                return;
            }
            block.Visibility = Visibility.Visible;
            int pos = 0;
            foreach (var m in IssueLinks.Find(text, VcsService.Instance.IssueRules))
            {
                if (m.Start > pos) block.Inlines.Add(new Run(text.Substring(pos, m.Start - pos)));
                var link = new Hyperlink(new Run(text.Substring(m.Start, m.Length))) { ToolTip = m.Url };
                link.BindC(TextElement.ForegroundProperty, Tokens.TextLink);
                string url = m.Url;
                link.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.card.link", () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
                block.Inlines.Add(link);
                pos = m.Start + m.Length;
            }
            if (pos < text.Length) block.Inlines.Add(new Run(text.Substring(pos)));
        }

        /// <summary>
        /// 这次提交对某个文件的 diff：<paramref name="file"/> 为 null 时是当前文件（在提交的文件列表里找到它，处理改名；找不到就按当前路径）。
        /// </summary>
        public static void ShowFileDiff(VcsDocument doc, VcsCommit commit, CommitDetails details, CommitFile file)
        {
            var p = doc.Provider;
            string current = doc.Revision?.Path ?? doc.FilePath;
            if (file == null && details != null) file = MatchCurrent(p, details, current);
            var (before, after) = p.RevisionsOf(details?.Commit ?? commit, file);
            string afterPath = file?.Path ?? current;
            string beforePath = file?.OldPath ?? afterPath;
            string name = Path.GetFileName((afterPath ?? "file").Replace('/', '\\'));
            if (file == null && p.Kind == VcsKind.Git && commit.Parents.Length == 0 && doc.Blame != null)
            {
                // blame 来的提交没有父提交信息：用 porcelain 的 previous
                var prev = p.PreviousOf(doc.Blame, commit.Id, current);
                if (prev != null) { before = prev.Value.Revision; beforePath = prev.Value.Path; }
            }
            VcsDiffWindow.Run("vcs.commitDiff", c => VcsDiffWindow.ShowRevisionsAsync(p, beforePath, before, afterPath, after, VcsStrings.CommitCaption(name, p.ShortId(commit.Id)), c));
        }

        private static CommitFile MatchCurrent(IVcsProvider p, CommitDetails d, string current)
        {
            if (current == null) return null;
            if (p is GitProvider git)
            {
                string rel = git.RepoPath(current);
                return d.Files.FirstOrDefault(f => string.Equals(f.Path, rel, StringComparison.OrdinalIgnoreCase));
            }
            // P4：depot 路径和本机路径对不上，按最长的相同结尾匹配
            string local = current.Replace('\\', '/');
            CommitFile best = null;
            int bestLen = 0;
            foreach (var f in d.Files)
            {
                int n = CommonSuffix(f.Path ?? string.Empty, local);
                if (n > bestLen && (f.Path ?? string.Empty).Length >= n && n > Path.GetFileName(current).Length) { best = f; bestLen = n; }
            }
            return best;
        }

        private static int CommonSuffix(string a, string b)
        {
            int n = 0;
            while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[a.Length - 1 - n]) == char.ToLowerInvariant(b[b.Length - 1 - n])) n++;
            return n;
        }
    }
}
