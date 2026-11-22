using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Vcs;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Patterns;

namespace UEToVS.Host.Vcs
{
    /// <summary>
    /// 版本信息的命令（CommandCatalog 0x01B0–0x01B7，编辑器右键 > UEToVS 版本管理，Extensions > UEToVS > 版本管理，Find Action）。
    /// 只读：没有提交、暂存、分支、签出这些（VS 自带 / P4VS 做）；唯一改东西的是“回滚这一块”，只改编辑器缓冲区、可以撤销。
    /// </summary>
    internal static class VcsActions
    {
        private static ContextMenuPopup _history;

        public static void Register(ActionRegistry registry)
        {
            Func<ActionContext, bool> editor = ctx => ctx.ActiveDocumentPath != null;
            registry.Register(new DelegateAction(VcsIds.ToggleInlineBlame, VcsStrings.CmdToggleInlineBlame, ActionCategory.Settings, ctx => ToggleInlineAsync(), null, null, VcsStrings.KwBlame));
            registry.Register(new DelegateAction(VcsIds.ToggleBlameGutter, VcsStrings.CmdToggleBlameGutter, ActionCategory.Navigation, ctx => ToggleGutterAsync(), editor, null, VcsStrings.KwBlame));
            registry.Register(new DelegateAction(VcsIds.NextChange, VcsStrings.CmdNextChange, ActionCategory.Navigation, ctx => StepAsync(+1), editor, null, VcsStrings.KwChanges));
            registry.Register(new DelegateAction(VcsIds.PreviousChange, VcsStrings.CmdPreviousChange, ActionCategory.Navigation, ctx => StepAsync(-1), editor, null, VcsStrings.KwChanges));
            registry.Register(new DelegateAction(VcsIds.ShowChange, VcsStrings.CmdShowChange, ActionCategory.Navigation, ctx => ShowChangeAsync(), editor, null, VcsStrings.KwChanges));
            registry.Register(new DelegateAction(VcsIds.CompareWithBase, VcsStrings.CmdCompareWithBase, ActionCategory.Navigation, ctx => CompareAsync(), editor, null, VcsStrings.KwChanges));
            registry.Register(new DelegateAction(VcsIds.SelectionHistory, VcsStrings.CmdSelectionHistory, ActionCategory.Navigation, ctx => HistoryAsync(false), editor, null, VcsStrings.KwHistory));
            registry.Register(new DelegateAction(VcsIds.FunctionHistory, VcsStrings.CmdFunctionHistory, ActionCategory.Navigation, ctx => HistoryAsync(true), editor, null, VcsStrings.KwHistory));
            VcsService.Instance.EnsureHooked();
        }

        /// <summary>活动编辑器和它的版本信息；没有时在状态栏说明原因并返回 null。</summary>
        private static (IWpfTextView View, VcsDocument Doc) Active(bool needBase)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = EditorServices.ActiveView();
            if (view == null || view.IsClosed) return (null, null);
            if (!VcsService.Active || !view.TextBuffer.Properties.TryGetProperty(typeof(VcsDocument), out VcsDocument doc))
            {
                VsStatusBar.SetText(VcsStrings.NotInVcs);
                return (null, null);
            }
            if (doc.Provider == null)
            {
                VsStatusBar.SetText(doc.BlameError != null ? VcsStrings.Failed(doc.BlameError) : VcsStrings.NotInVcs);
                return (null, null);
            }
            if (needBase && (doc.Revision != null || doc.BaseLines == null))
            {
                VsStatusBar.SetText(doc.Base == null && doc.Revision == null ? VcsStrings.Loading : VcsStrings.NotTracked);
                return (null, null);
            }
            return (view, doc);
        }

        private static async Task ToggleInlineAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!VcsService.Settings.VcsInlineBlame)
            {
                VsStatusBar.SetText(VcsStrings.InlineOffInSettings);
                return;
            }
            InlineBlameController.Hidden = !InlineBlameController.Hidden;
            var view = EditorServices.ActiveView();
            bool engineOff = view != null && !InlineBlameController.Hidden && !VcsService.Settings.VcsInlineBlameEngine &&
                             view.TextBuffer.Properties.TryGetProperty(typeof(VcsDocument), out VcsDocument d) && d.Revision == null && d.IsEngineFile;
            VsStatusBar.SetText(engineOff ? VcsStrings.InlineEngineOff : InlineBlameController.Hidden ? VcsStrings.InlineOff : VcsStrings.InlineOn);
            VcsService.L.Info("vcs.inline.toggle", null, ("hidden", InlineBlameController.Hidden));
        }

        private static async Task ToggleGutterAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var (view, doc) = Active(false);
            if (view == null) return;
            var margin = BlameGutterMargin.Of(view);
            if (margin == null) return;
            margin.SetShown(!margin.Shown);
        }

        private static async Task StepAsync(int dir)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var (view, doc) = Active(true);
            if (view == null) return;
            if (doc.Hunks.Count == 0)
            {
                VsStatusBar.SetText(VcsStrings.NoChanges(doc.Provider.Kind));
                return;
            }
            int line = doc.ToDiffLine(view.TextSnapshot, view.Caret.Position.BufferPosition.GetContainingLine().LineNumber);
            bool wrapped;
            var h = dir > 0 ? HunkMath.Next(doc.Hunks, line, out wrapped) : HunkMath.Previous(doc.Hunks, line, out wrapped);
            if (h == null)
            {
                VsStatusBar.SetText(VcsStrings.NoChanges(doc.Provider.Kind));
                return;
            }
            MoveCaretTo(view, doc, h);
            if (wrapped) VsStatusBar.SetText(dir > 0 ? VcsStrings.Wrapped : VcsStrings.WrappedBack);
        }

        /// <summary>光标移到改动块的第一行（diff 快照的行号换算到当前快照），滚到可见。</summary>
        public static void MoveCaretTo(IWpfTextView view, VcsDocument doc, DiffHunk h)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var ds = doc.DiffSnapshot ?? view.TextSnapshot;
            int n = Math.Max(0, Math.Min(ds.LineCount - 1, h.NewStart));
            var point = ds.GetLineFromLineNumber(n).Start.TranslateTo(view.TextSnapshot, PointTrackingMode.Negative);
            view.Caret.MoveTo(point);
            view.Selection.Clear();
            view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0), EnsureSpanVisibleOptions.AlwaysCenter);
        }

        private static async Task ShowChangeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var (view, doc) = Active(true);
            if (view == null) return;
            int line = doc.ToDiffLine(view.TextSnapshot, view.Caret.Position.BufferPosition.GetContainingLine().LineNumber);
            var h = HunkMath.At(doc.Hunks, line) ?? HunkMath.At(doc.Hunks, line + 1);
            if (h == null)
            {
                VsStatusBar.SetText(doc.Hunks.Count == 0 ? VcsStrings.NoChanges(doc.Provider.Kind) : VcsStrings.NoChangeAtCaret);
                return;
            }
            HunkPopup.Show(view, doc, h, EditorServices.AnchorAt(view));
        }

        private static async Task CompareAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var (view, doc) = Active(true);
            if (view == null) return;
            await VcsDiffWindow.ShowBaseAsync(doc);
        }

        // ---------------------------------------------------------------- G5 选中部分 / 函数的历史

        private static async Task HistoryAsync(bool function)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var (view, doc) = Active(true);
            if (view == null) return;
            var snapshot = view.TextSnapshot;
            int first, last;
            if (function)
            {
                var range = await FunctionRangeAsync(view);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (range == null)
                {
                    VsStatusBar.SetText(VcsStrings.NotInFunction);
                    return;
                }
                (first, last) = range.Value;
                snapshot = view.TextSnapshot;
            }
            else
            {
                var sel = view.Selection.StreamSelectionSpan.SnapshotSpan;
                first = sel.Start.GetContainingLine().LineNumber;
                var endLine = sel.End.GetContainingLine();
                last = endLine.LineNumber;
                if (last > first && sel.End.Position == endLine.Start.Position) last--; // 选到下一行行首：不算那一行
            }
            var baseRange = HunkMath.BaseRange(doc.Hunks, doc.ToDiffLine(snapshot, first), doc.ToDiffLine(snapshot, last));
            if (baseRange == null)
            {
                VsStatusBar.SetText(VcsStrings.AllUncommitted);
                return;
            }
            var (start, end) = baseRange.Value;
            var p = doc.Provider;
            string path = doc.FilePath, file = Path.GetFileName(path);
            int max = VcsService.Settings.VcsHistoryMaxCommits;
            var anchor = EditorServices.AnchorAt(view);
            var sw = Stopwatch.StartNew();
            IReadOnlyList<HistoryEntry> entries = null;
            string error = null;
            bool canceled = false;

            // 进度 + 可取消：VS 的等待对话框（1.5 秒后才出现，快的查询看不到它）
            var factory = await ServiceProvider.GetGlobalServiceAsync(typeof(SVsThreadedWaitDialogFactory)) as IVsThreadedWaitDialogFactory;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ThreadedWaitDialogHelper.Session session = factory?.StartWaitDialog(VcsStrings.HistoryWait,
                new ThreadedWaitDialogProgressData(VcsStrings.HistoryWaitText(file), VcsStrings.HistoryProgress(0), null, true), TimeSpan.FromSeconds(1.5));
            try
            {
                var ct = session != null
                    ? CancellationTokenSource.CreateLinkedTokenSource(session.UserCancellationToken, NavigationContext.Disposal).Token
                    : NavigationContext.Disposal;
                var progress = new Progress<string>(text =>
                {
                    session?.Progress.Report(new ThreadedWaitDialogProgressData(VcsStrings.HistoryWaitText(file), text, text, true));
                    VsStatusBar.SetText(text);
                });
                try
                {
                    entries = await Task.Run(() => p.GetLineHistoryAsync(path, start, end, max, progress, ct), ct);
                }
                catch (OperationCanceledException) { canceled = true; }
                catch (VcsException ex) { error = ex.Message; }
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                session?.Dispose();
            }
            VcsService.L.Info("vcs.history", error, ("kind", p.Kind.ToString()), ("file", file), ("function", function), ("start", start), ("end", end), ("max", max),
                              ("commits", entries?.Count ?? 0), ("canceled", canceled), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            if (canceled) { VsStatusBar.SetText(VcsStrings.Canceled); return; }
            if (error != null) { VsStatusBar.SetText(VcsStrings.Failed(error)); return; }
            if (entries == null || entries.Count == 0) { VsStatusBar.SetText(VcsStrings.HistoryEmpty); return; }
            VsStatusBar.SetText(VcsStrings.HistoryTitle(file, start, end, entries.Count));
            await ShowHistoryAsync(p, entries, anchor, file);
        }

        private static async Task ShowHistoryAsync(IVcsProvider p, IReadOnlyList<HistoryEntry> entries, UEToVS.UI.Shells.PopupAnchor anchor, string file)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var now = DateTimeOffset.Now;
            bool english = Loc.IsEnglish;
            var items = entries.Select((e, i) => new MenuEntry("vcs.history." + i, () =>
                p.ShortId(e.Commit.Id) + "  " + BlameText.Inline(e.Commit, now, english))
            {
                Description = () => RelativeTime.Absolute(e.Commit.Time, e.Commit.DateOnly) + "  " + (e.Path ?? string.Empty),
                Tag = e,
            }).ToList();
            if (_history == null) _history = new ContextMenuPopup("uetovs.vcs.history");
            _history.OwnerHandle = VsMainWindow.Handle;
            var chosen = await _history.ShowAsync(items, anchor);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(chosen?.Tag is HistoryEntry entry)) return;
            VcsService.L.Info("vcs.history.open", null, ("id", p.ShortId(entry.Commit.Id)));
            VcsDiffWindow.Run("vcs.history.diff", c => VcsDiffWindow.ShowRevisionsAsync(p, entry.PreviousPath ?? entry.Path, entry.PreviousRevision, entry.Path, entry.Revision,
                VcsStrings.CommitCaption(file, p.ShortId(entry.Commit.Id)), c));
        }

        /// <summary>光标所在函数的行区间：开始行来自代码引擎（导航栏同一个请求），结束行按大括号配对找。</summary>
        private static async Task<(int, int)?> FunctionRangeAsync(IWpfTextView view)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var engine = NavigationContext.Engine;
            int caretLine = view.Caret.Position.BufferPosition.GetContainingLine().LineNumber;
            var snapshot = view.TextSnapshot;
            var caret = await EditorServices.CaretRequestAsync(view);
            if (engine == null || caret == null) return null;
            var r = await engine.GetCaretContextAsync(caret, NavigationContext.Disposal);
            if (!r.Ok) return null;
            var f = ScopeModel.CurrentFunction(r.Value);
            if (f?.Location == null) return null;
            int start = f.Location.Line;
            if (start < 0 || start >= snapshot.LineCount || start > caretLine) return null;
            return await Task.Run(() =>
            {
                var lines = new List<string>(Math.Min(snapshot.LineCount - start, 20000));
                for (int i = start; i < snapshot.LineCount && i < start + 20000; i++) lines.Add(snapshot.GetLineFromLineNumber(i).GetText());
                var range = FunctionRange.Find(lines, 0);
                if (range == null) return ((int, int)?)null;
                int end = start + range.Value.End;
                return caretLine <= end ? (start, end) : ((int, int)?)null;
            });
        }
    }
}
