using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editing;
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
    /// G3 改动块弹窗：点色条（或 Ctrl+Shift+Alt+U, Z）弹出这一块的旧内容（红底 -）和新内容（绿底 +）对照；按钮“回滚这一块”
    /// （改编辑器缓冲区，一次编辑、可撤销，不碰 Git / P4 状态）和“复制旧内容”，↑ / ↓ 换到上一块 / 下一块。不做“暂存这一块”。
    /// </summary>
    internal static class HunkPopup
    {
        private const int MaxShownLines = 300;
        private static PopupShell _shell;

        public static void Show(IWpfTextView view, VcsDocument doc, DiffHunk hunk, PopupAnchor anchor)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view == null || doc?.BaseLines == null || hunk == null || doc.DiffSnapshot == null) return;
            if (_shell == null) _shell = new PopupShell("uetovs.vcs.hunk", new Size(640, 280)) { MinVisibleWidth = 320, MinVisibleHeight = 120 };
            _shell.OwnerHandle = VsMainWindow.Handle;
            var snapshot = doc.DiffSnapshot;
            var oldLines = doc.BaseLines.Skip(hunk.OldStart).Take(hunk.OldCount).ToList();
            var newLines = new List<string>();
            for (int i = hunk.NewStart; i < hunk.NewStart + hunk.NewCount && i < snapshot.LineCount; i++) newLines.Add(snapshot.GetLineFromLineNumber(i).GetText());

            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(12, 8, 12, 10) };
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            int first = hunk.NewStart + 1, last = Math.Max(first, hunk.NewStart + hunk.NewCount);
            var title = new UiText(VcsStrings.HunkTitle(hunk.Kind, first, hunk.Kind == ChangeKind.Deleted ? first : last), TextVariant.Strong);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            var prevBtn = new UiButton("↑", ButtonKind.Ghost) { ToolTip = VcsStrings.PrevHunkTip, Margin = new Thickness(0, 0, 2, 0), MinWidth = 28 };
            var nextBtn = new UiButton("↓", ButtonKind.Ghost) { ToolTip = VcsStrings.NextHunkTip, Margin = new Thickness(0, 0, 8, 0), MinWidth = 28 };
            var copy = new UiButton(VcsStrings.CopyOld, ButtonKind.Secondary) { Margin = new Thickness(0, 0, 6, 0), IsEnabled = oldLines.Count > 0 };
            var rollback = new UiButton(VcsStrings.RollBack, ButtonKind.Primary) { ToolTip = VcsStrings.RollBackTip };
            actions.Children.Add(prevBtn);
            actions.Children.Add(nextBtn);
            actions.Children.Add(copy);
            actions.Children.Add(rollback);
            header.Children.Add(actions);
            header.Children.Add(title);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var code = new StackPanel();
            AddLines(code, oldLines, '-', DeletedBack);
            AddLines(code, newLines, '+', AddedBack);
            var scroll = new ScrollViewer { Content = code, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            var frame = new Border { Child = scroll, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(0, 2, 0, 2) };
            frame.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle).Bind(Border.BackgroundProperty, Tokens.SurfaceSunken);
            root.Children.Add(frame);

            copy.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.hunk.copy", () =>
            {
                Clipboard.SetText(string.Join(LineBreak(snapshot), oldLines));
                VsStatusBar.SetText(VcsStrings.Copied);
                VcsService.L.Info("vcs.hunk.copy", null, ("lines", oldLines.Count));
            });
            rollback.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.hunk.rollback", () =>
            {
                _shell.Dismiss();
                RollBack(view, doc, hunk);
            });
            prevBtn.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.hunk.prev", () => Step(view, doc, hunk, -1));
            nextBtn.Click += (s, e) => Safe.Execute(VcsService.L, "vcs.hunk.next", () => Step(view, doc, hunk, +1));

            _shell.Body = root;
            _shell.Title = title.Text;
            _shell.ShowAt(anchor);
            VcsService.L.Debug("vcs.hunk.show", null, ("kind", hunk.Kind.ToString()), ("old", hunk.OldCount), ("new", hunk.NewCount));
        }

        private static void Step(IWpfTextView view, VcsDocument doc, DiffHunk current, int dir)
        {
            var hunks = doc.Hunks;
            int i = -1;
            for (int k = 0; k < hunks.Count; k++) if (ReferenceEquals(hunks[k], current)) { i = k; break; }
            if (i < 0 || hunks.Count < 2) return;
            var next = hunks[(i + dir + hunks.Count) % hunks.Count];
            VcsActions.MoveCaretTo(view, doc, next);
            Show(view, doc, next, Editor.EditorServices.AnchorAt(view));
        }

        private static readonly Brush DeletedBack = Frozen(Color.FromArgb(0x40, 0xE0, 0x50, 0x50));
        private static readonly Brush AddedBack = Frozen(Color.FromArgb(0x40, 0x50, 0xB0, 0x50));

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static void AddLines(StackPanel panel, List<string> lines, char sign, Brush back)
        {
            int n = Math.Min(lines.Count, MaxShownLines);
            for (int i = 0; i < n; i++)
            {
                var t = new UiText(sign + " " + (lines[i] ?? string.Empty).Replace("\t", "    "), TextVariant.Mono) { TextTrimming = TextTrimming.None, Padding = new Thickness(6, 0, 6, 0) };
                panel.Children.Add(new Border { Background = back, Child = t });
            }
            if (lines.Count > n) panel.Children.Add(new UiText(VcsStrings.MoreLines(lines.Count - n), TextVariant.Small) { Padding = new Thickness(6, 2, 6, 2) });
        }

        private static string LineBreak(ITextSnapshot s)
        {
            for (int i = 0; i < Math.Min(s.LineCount, 50); i++)
            {
                string lb = s.GetLineFromLineNumber(i).GetLineBreakText();
                if (!string.IsNullOrEmpty(lb)) return lb;
            }
            return "\r\n";
        }

        /// <summary>把这一块改回基准版本：一次 ITextEdit（一步撤销）。块对应的是 diff 快照，先换算到当前快照，变过了就不动。</summary>
        public static void RollBack(IWpfTextView view, VcsDocument doc, DiffHunk hunk)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var buffer = view.TextBuffer;
            var ds = doc.DiffSnapshot;
            if (ds == null || doc.BaseLines == null) return;
            if (EngineReadOnlyState.For(buffer).Locked) { VsStatusBar.SetText(VcsStrings.ReadOnly); return; }
            string lb = LineBreak(ds);
            var oldLines = doc.BaseLines.Skip(hunk.OldStart).Take(hunk.OldCount).ToList();
            Span span;
            string replacement;
            if (hunk.NewCount == 0)
            {
                // 删除块：在 NewStart 行前面插回旧行
                if (hunk.NewStart >= ds.LineCount)
                {
                    span = new Span(ds.Length, 0);
                    replacement = lb + string.Join(lb, oldLines);
                }
                else
                {
                    span = new Span(ds.GetLineFromLineNumber(hunk.NewStart).Start.Position, 0);
                    replacement = string.Join(lb, oldLines) + lb;
                }
            }
            else
            {
                var firstLine = ds.GetLineFromLineNumber(hunk.NewStart);
                var lastLine = ds.GetLineFromLineNumber(Math.Min(ds.LineCount - 1, hunk.NewStart + hunk.NewCount - 1));
                bool atEnd = lastLine.LineNumber == ds.LineCount - 1;
                if (hunk.OldCount == 0)
                {
                    // 新增块：整行删掉（在文件末尾时连前一个换行一起删）
                    if (atEnd && firstLine.LineNumber > 0)
                    {
                        var before = ds.GetLineFromLineNumber(firstLine.LineNumber - 1);
                        span = Span.FromBounds(before.End.Position, lastLine.End.Position);
                    }
                    else span = Span.FromBounds(firstLine.Start.Position, lastLine.EndIncludingLineBreak.Position);
                    replacement = string.Empty;
                }
                else
                {
                    span = Span.FromBounds(firstLine.Start.Position, lastLine.End.Position);
                    replacement = string.Join(lb, oldLines);
                }
            }
            var current = buffer.CurrentSnapshot;
            var target = ds == current ? new SnapshotSpan(current, span) : new SnapshotSpan(ds, span).TranslateTo(current, SpanTrackingMode.EdgeExclusive);
            if (ds != current && !string.Equals(target.GetText(), new SnapshotSpan(ds, span).GetText(), StringComparison.Ordinal))
            {
                VsStatusBar.SetText(VcsStrings.Stale);
                return;
            }
            if (buffer.IsReadOnly(target.Span)) { VsStatusBar.SetText(VcsStrings.ReadOnly); return; }
            using (var edit = buffer.CreateEdit())
            {
                edit.Replace(target.Span, replacement);
                edit.Apply();
            }
            VsStatusBar.SetText(VcsStrings.RolledBack);
            VcsService.L.Info("vcs.hunk.rollback", null, ("kind", hunk.Kind.ToString()), ("old", hunk.OldCount), ("new", hunk.NewCount));
        }
    }
}
