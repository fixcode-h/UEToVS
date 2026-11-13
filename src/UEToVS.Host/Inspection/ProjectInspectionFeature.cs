using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Navigation;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Inspection;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Navigation;
using UEToVS.Host.Refactoring;
using UEToVS.Host.Shell;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;
using BadgeKind = UEToVS.UI.Logic.BadgeKind;
using Glyph = UEToVS.UI.Logic.SymbolKind;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Inspection
{
    /// <summary>A row of the inspection tree: a group (rule or file) or one finding.</summary>
    internal sealed class InspectionRow
    {
        public InspectionGroup Group;
        public InspectionFinding Finding;
    }

    /// <summary>
    /// v0.4.5 whole-project UE rule inspection, VS side. The run lives in the engine (BelowNormal threads, results streamed
    /// to .vs\UEToVS\inspections\*.jsonl); the host starts it, polls the status into the status bar progress, and shows one
    /// results page in a ResultsPopup (UEToVS.UI): grouped by rule or by file, text filter, "fixable only", code preview,
    /// Enter / double-click jumps, batch fix of mechanical rules through a preview (CheckListDialog) and one linked undo.
    /// </summary>
    internal static class ProjectInspectionFeature
    {
        private static readonly Logger L = Log.For("Heavy");
        private static ResultsPopup<InspectionRow> _popup;
        private static Chip _byFile, _fixableOnly;
        private static TextInput _filter;
        private static ProjectInspectionResponse _last;
        private static int _polling;

        public static async Task RunAsync(HostPackage package)
        {
            if (!HeavyFeatureActions.InspectionEnabled(package))
            {
                VsStatusBar.SetText(HeavyFeatureStrings.InspectionDisabled);
                L.Info("inspection.host.refused", null, ("reason", "disabled"));
                return;
            }
            var engine = package.Engine;
            if (engine == null)
            {
                VsStatusBar.SetText(HeavyFeatureStrings.EngineUnavailable);
                return;
            }
            bool plugins = package.Settings?.Effective.InspectionIncludePlugins ?? true;
            var r = await engine.ProjectInspectionAsync(new ProjectInspectionRequest { Action = HeavyJobAction.Start, IncludePlugins = plugins }, package.DisposalToken);
            if (r.Status != EngineCallStatus.Ok || r.Value == null)
            {
                VsStatusBar.SetText(EngineCallText.ForStatus(r.Status) ?? HeavyFeatureStrings.EngineUnavailable);
                return;
            }
            L.Info("inspection.host.start", null, ("state", r.Value.State.ToString()), ("files", r.Value.FilesTotal), ("threads", r.Value.Threads), ("includePlugins", plugins));
            VsStatusBar.SetText(InspectionView.StatusText(r.Value));
            if (r.Value.State == HeavyJobState.Running) _ = Diagnostics.Safe.RunInBackgroundAsync(L, "inspection.host.monitor", () => MonitorAsync(package, true));
        }

        /// <summary>Polls the running job into the status bar; opens the results when it ends (if asked to).</summary>
        private static async Task MonitorAsync(HostPackage package, bool showWhenDone)
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return; // one poller
            var sw = Stopwatch.StartNew();
            ProjectInspectionResponse st = null;
            try
            {
                var ct = package.DisposalToken;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(InspectionView.PollDelayMs(sw.Elapsed), ct).ConfigureAwait(false);
                    var engine = package.Engine;
                    if (engine == null) break;
                    var r = await engine.ProjectInspectionAsync(new ProjectInspectionRequest { Action = HeavyJobAction.Status }, ct).ConfigureAwait(false);
                    if (r.Status != EngineCallStatus.Ok || r.Value == null) break;
                    st = r.Value;
                    if (st.State != HeavyJobState.Running) break;
                    VsStatusBar.SetProgress(InspectionView.StatusText(st), st.FilesDone, Math.Max(1, st.FilesTotal));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
            VsStatusBar.SetProgress(InspectionView.StatusText(st), 0, 0);
            L.Info("inspection.host.finished", null, ("state", st?.State.ToString()), ("files", st?.FilesDone), ("findings", st?.Findings), ("ms", st?.ElapsedMs),
                ("peakWorkingSetMB", st == null ? 0 : st.PeakWorkingSetBytes >> 20));
            if (st != null && st.State == HeavyJobState.Completed && (showWhenDone || (_popup?.IsVisible ?? false)) && st.Findings > 0)
                await ShowResultsAsync(package);
        }

        public static async Task ShowResultsAsync(HostPackage package)
        {
            if (!HeavyFeatureActions.InspectionEnabled(package))
            {
                VsStatusBar.SetText(HeavyFeatureStrings.InspectionDisabled);
                return;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsurePopup(package);
            _popup.Title = HeavyFeatureStrings.InspectionTitle;
            _popup.IsBusy = true;
            if (!_popup.IsVisible) _popup.ShowAt(PopupAnchor.CenterOf(VsMainWindow.Handle));
            await LoadPageAsync(package);
        }

        private static async Task LoadPageAsync(HostPackage package)
        {
            var engine = package.Engine;
            var request = new ProjectInspectionRequest { Action = HeavyJobAction.Results, FixableOnly = _fixableOnly?.IsChecked == true, Limit = InspectionView.PageSize };
            var r = engine == null
                ? EngineCallResult<ProjectInspectionResponse>.Fail(EngineCallStatus.Unavailable, 0)
                : await engine.ProjectInspectionAsync(request, package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _popup.IsBusy = false;
            if (r.Status != EngineCallStatus.Ok || r.Value == null)
            {
                string msg = EngineCallText.ForStatus(r.Status) ?? HeavyFeatureStrings.EngineUnavailable;
                _popup.SetResults(Array.Empty<TreeNode<InspectionRow>>(), msg);
                return;
            }
            _last = r.Value;
            Render();
            if (_last.State == HeavyJobState.Running) _ = Diagnostics.Safe.RunInBackgroundAsync(L, "inspection.host.monitor", () => MonitorAsync(package, false));
        }

        /// <summary>Re-renders the last page with the current grouping / text filter (no engine call).</summary>
        private static void Render()
        {
            if (_popup == null || _last == null) return;
            using (L.Time("inspection.host.render", 16))
            {
                string text = _filter?.Text;
                var shown = _last.Items.Where(f => InspectionView.MatchesText(f, text)).ToList();
                var groups = InspectionView.Group(shown, _byFile?.IsChecked == true ? InspectionGrouping.File : InspectionGrouping.Rule);
                var roots = new List<TreeNode<InspectionRow>>(groups.Count);
                foreach (var g in groups)
                {
                    var children = new List<TreeNode<InspectionRow>>(g.Items.Count);
                    foreach (var f in g.Items) children.Add(new TreeNode<InspectionRow>(new InspectionRow { Finding = f }));
                    roots.Add(new TreeNode<InspectionRow>(new InspectionRow { Group = g }, children, shown.Count <= 2000 || roots.Count < 20));
                }
                int files = _last.TopFiles.Count;
                string summary = _last.State == HeavyJobState.Running ? InspectionView.StatusText(_last)
                    : HeavyFeatureStrings.Summary(_last.Findings, files, _last.Rules.Sum(x => x.Fixable));
                if (_last.Matched > _last.Items.Count) summary += " · " + HeavyFeatureStrings.MoreResults(_last.Items.Count, _last.Matched);
                if (_last.State == HeavyJobState.Idle || _last.State == HeavyJobState.Disabled || _last.State == HeavyJobState.Failed)
                    summary = InspectionView.StatusText(_last);
                _popup.SetResults(roots, summary);
                if (_popup.Tree.Count > 1) _popup.Tree.SelectedIndex = 1;
                L.Debug("inspection.host.view", null, ("shown", shown.Count), ("groups", groups.Count), ("byFile", _byFile?.IsChecked == true),
                    ("fixableOnly", _fixableOnly?.IsChecked == true), ("filter", text));
            }
        }

        private static readonly IItemPresenter<InspectionRow> Presenter = new DelegatePresenter<InspectionRow>((r, row) =>
        {
            if (r.Group != null)
            {
                bool byFile = _byFile?.IsChecked == true;
                row.Glyph = byFile ? Glyph.File : Glyph.Action;
                row.Primary = r.Group.Title;
                row.Secondary = byFile ? Path.GetDirectoryName(r.Group.Key) : null;
                int fixable = r.Group.Fixable;
                if (fixable > 0) row.AddBadge(HeavyFeatureStrings.FixableOnly + " " + fixable, BadgeKind.Module);
                row.ToolTip = r.Group.Key;
                return;
            }
            var f = r.Finding;
            row.Primary = f.LineText ?? string.Empty;
            row.Secondary = CodeDiagnosticText.Message(new CodeDiagnostic { Code = f.Code, Args = f.Args, Message = f.Message });
            row.Right = Path.GetFileName(f.FilePath) + ":" + (f.Line + 1);
            row.AddBadge(f.Code, f.Severity == CodeDiagnosticSeverity.Error ? BadgeKind.UProperty : BadgeKind.Neutral);
            if (f.Fix != null && f.Fix.Count > 0) row.AddBadge(HeavyFeatureStrings.FixDescription(f.Code, f.Args.FirstOrDefault()), BadgeKind.Module);
            row.ToolTip = f.FilePath + ":" + (f.Line + 1) + "\n" + row.Secondary;
        }, r => r.Finding != null);

        private static void EnsurePopup(HostPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_popup != null)
            {
                _popup.OwnerHandle = VsMainWindow.Handle;
                return;
            }
            _popup = new ResultsPopup<InspectionRow>("uetovs.inspection", Presenter, new System.Windows.Size(980, 600))
            {
                OwnerHandle = VsMainWindow.Handle,
            };
            var refresh = new IconButton(Ui.FrozenGeometry("M 10,2 A 5,5 0 1 0 11,7 M 10,0 L 10,3 L 7,3"), () => HeavyFeatureStrings.Refresh, null, 11);
            _byFile = new Chip(() => HeavyFeatureStrings.GroupByFile, false);
            _fixableOnly = new Chip(() => HeavyFeatureStrings.FixableOnly, false);
            _filter = new TextInput { Width = 220, Placeholder = HeavyFeatureStrings.FilterPlaceholder };
            var fixAll = new UiButton(() => HeavyFeatureStrings.FixAll);
            var cancel = new UiButton(() => HeavyFeatureStrings.Cancel);
            _popup.AddToolbarItem(refresh);
            _popup.AddToolbarItem(_byFile);
            _popup.AddToolbarItem(_fixableOnly);
            _popup.AddToolbarItem(_filter);
            _popup.AddToolbarItem(cancel, true);
            _popup.AddToolbarItem(fixAll, true);
            refresh.Click += (s, e) => Diagnostics.Safe.RunAsync(L, "inspection.host.refresh", () => ShowResultsAsync(package));
            _byFile.Click += (s, e) => Diagnostics.Safe.Execute(L, "inspection.host.group", Render);
            _fixableOnly.Click += (s, e) => Diagnostics.Safe.RunAsync(L, "inspection.host.fixable", () => ShowResultsAsync(package));
            _filter.TextChanged += (s, e) => Diagnostics.Safe.Execute(L, "inspection.host.filter", Render);
            cancel.Click += (s, e) => Diagnostics.Safe.RunAsync(L, "inspection.host.cancel", async () =>
            {
                var engine = package.Engine;
                if (engine != null) await engine.ProjectInspectionAsync(new ProjectInspectionRequest { Action = HeavyJobAction.Cancel }, package.DisposalToken);
            });
            fixAll.Click += (s, e) => Diagnostics.Safe.Execute(L, "inspection.host.fixAll", FixAll);
            Diagnostics.Safe.Execute(L, "inspection.host.preview.attach", () => Search.EditorCodePreview.Attach(_popup.Panel.Preview));
            // CodePreviewRequest.Line is 1-based; contract lines are 0-based.
            _popup.Panel.PreviewProvider = r =>
            {
                var f = r.Finding ?? r.Group?.Items.FirstOrDefault();
                return f == null ? null : new CodePreviewRequest { FilePath = f.FilePath, Line = f.Line + 1, Column = f.Column, Length = r.Finding != null ? f.Length : 0 };
            };
            _popup.ItemInvoked += (s, e) => Diagnostics.Safe.Execute(L, "inspection.host.open", () =>
            {
                var f = e.Item.Finding;
                if (f != null && !string.IsNullOrEmpty(f.FilePath)) NavigationService.Instance.JumpTo(new CodeLocation(f.FilePath, f.Line, f.Column), "inspection");
            });
        }

        /// <summary>Batch fix of the shown fixable findings: preview with check boxes, then one linked undo step.</summary>
        private static void FixAll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_last == null) return;
            string text = _filter?.Text;
            var fixable = _last.Items.Where(f => f.Fix != null && f.Fix.Count > 0 && InspectionView.MatchesText(f, text)).ToList();
            if (fixable.Count == 0)
            {
                VsStatusBar.SetText(HeavyFeatureStrings.Summary(_last.Findings, _last.TopFiles.Count, 0));
                return;
            }
            var sw = Stopwatch.StartNew();
            var dialog = new CheckListDialog<InspectionFinding>(() => HeavyFeatureStrings.FixPreviewTitle(fixable.Count), () => HeavyFeatureStrings.FixAll,
                PresentFix, f => f.FilePath + " " + f.LineText, new System.Windows.Size(820, 600), "dialog.inspectionFix");
            var rows = new List<CheckRow<InspectionFinding>>();
            foreach (var g in fixable.GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new CheckRow<InspectionFinding> { Header = Path.GetFileName(g.Key), HeaderCount = g.Count() });
                rows.AddRange(g.Select(f => new CheckRow<InspectionFinding> { Item = f, Checked = true }));
            }
            var picked = dialog.ShowModal(rows, VsMainWindow.Handle);
            if (picked == null || picked.Count == 0)
            {
                L.Info("inspection.fix", null, ("offered", fixable.Count), ("picked", 0), ("canceled", true));
                return;
            }
            var edits = InspectionView.PlanFixes(picked, out int dropped);
            var result = RefactorEdits.Apply(HeavyFeatureStrings.FixUndoName, edits);
            L.Info("inspection.fix", null, ("offered", fixable.Count), ("picked", picked.Count), ("edits", edits.Count), ("dropped", dropped), ("applied", result.Applied),
                ("skipped", result.Skipped), ("files", result.Files), ("failedFiles", result.FailedFiles.Count),
                ("rules", string.Join(",", picked.GroupBy(f => f.Code).Select(g => g.Key + "=" + g.Count()))), ("ms", sw.ElapsedMilliseconds));
            VsStatusBar.SetText(HeavyFeatureStrings.FixResult(result.Applied, result.Skipped + dropped, result.Files));
        }

        private static void PresentFix(InspectionFinding f, RowData d)
        {
            d.Primary = f.LineText ?? string.Empty;
            d.Secondary = HeavyFeatureStrings.FixDescription(f.Code, f.Args.FirstOrDefault());
            d.Right = (f.Line + 1).ToString();
            d.ToolTip = f.FilePath + ":" + (f.Line + 1);
            d.AddBadge(f.Code, BadgeKind.Neutral);
        }
    }
}
