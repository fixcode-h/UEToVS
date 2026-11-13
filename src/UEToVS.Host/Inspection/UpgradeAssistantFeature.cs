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
    /// <summary>A row of the upgrade tree: a deprecation version, a deprecated API, or one usage.</summary>
    internal sealed class UpgradeRow
    {
        public DeprecationVersionGroup Version;
        public DeprecatedApi Api;
        public DeprecatedUsage Usage;
    }

    /// <summary>
    /// v0.4.5 engine upgrade assistant, VS side: starts the engine scan (deprecated APIs from the index markers), polls it,
    /// then shows the project's usages grouped by deprecation version → API → usage in a ResultsPopup. "Replace all"
    /// previews the signature-compatible replacements (CheckListDialog: one row per API) and applies them as one linked undo.
    /// </summary>
    internal static class UpgradeAssistantFeature
    {
        private static readonly Logger L = Log.For("Heavy");
        private static ResultsPopup<UpgradeRow> _popup;
        private static DeprecationScanResponse _last;
        private static int _polling;

        public static async Task RunAsync(HostPackage package)
        {
            if (!HeavyFeatureActions.UpgradeEnabled(package))
            {
                VsStatusBar.SetText(HeavyFeatureStrings.UpgradeDisabled);
                L.Info("upgrade.host.refused", null, ("reason", "disabled"));
                return;
            }
            var engine = package.Engine;
            if (engine == null)
            {
                VsStatusBar.SetText(HeavyFeatureStrings.EngineUnavailable);
                return;
            }
            bool plugins = package.Settings?.Effective.InspectionIncludePlugins ?? true;
            var r = await engine.DeprecationScanAsync(new DeprecationScanRequest { Action = HeavyJobAction.Start, IncludePlugins = plugins }, package.DisposalToken);
            if (r.Status != EngineCallStatus.Ok || r.Value == null)
            {
                VsStatusBar.SetText(EngineCallText.ForStatus(r.Status) ?? HeavyFeatureStrings.EngineUnavailable);
                return;
            }
            L.Info("upgrade.host.start", null, ("state", r.Value.State.ToString()), ("includePlugins", plugins));
            VsStatusBar.SetText(UpgradeView.StatusText(r.Value));
            if (r.Value.State == HeavyJobState.Running) _ = Diagnostics.Safe.RunInBackgroundAsync(L, "upgrade.host.monitor", () => MonitorAsync(package));
        }

        private static async Task MonitorAsync(HostPackage package)
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;
            var sw = Stopwatch.StartNew();
            DeprecationScanResponse st = null;
            try
            {
                var ct = package.DisposalToken;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(InspectionView.PollDelayMs(sw.Elapsed), ct).ConfigureAwait(false);
                    var engine = package.Engine;
                    if (engine == null) break;
                    var r = await engine.DeprecationScanAsync(new DeprecationScanRequest { Action = HeavyJobAction.Status }, ct).ConfigureAwait(false);
                    if (r.Status != EngineCallStatus.Ok || r.Value == null) break;
                    st = r.Value;
                    if (st.State != HeavyJobState.Running) break;
                    VsStatusBar.SetProgress(UpgradeView.StatusText(st), st.SymbolsDone, Math.Max(1, st.DeprecatedSymbols));
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
            VsStatusBar.SetProgress(UpgradeView.StatusText(st), 0, 0);
            L.Info("upgrade.host.finished", null, ("state", st?.State.ToString()), ("usages", st?.Usages), ("ms", st?.ElapsedMs));
            if (st != null && st.State == HeavyJobState.Completed) await ShowResultsAsync(package);
        }

        public static async Task ShowResultsAsync(HostPackage package)
        {
            if (!HeavyFeatureActions.UpgradeEnabled(package))
            {
                VsStatusBar.SetText(HeavyFeatureStrings.UpgradeDisabled);
                return;
            }
            var engine = package.Engine;
            var r = engine == null
                ? EngineCallResult<DeprecationScanResponse>.Fail(EngineCallStatus.Unavailable, 0)
                : await engine.DeprecationScanAsync(new DeprecationScanRequest { Action = HeavyJobAction.Results }, package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (r.Status != EngineCallStatus.Ok || r.Value == null)
            {
                VsStatusBar.SetText(EngineCallText.ForStatus(r.Status) ?? HeavyFeatureStrings.EngineUnavailable);
                return;
            }
            _last = r.Value;
            if (_last.State == HeavyJobState.Idle || _last.State == HeavyJobState.Disabled)
            {
                VsStatusBar.SetText(UpgradeView.StatusText(_last));
                return;
            }
            EnsurePopup(package);
            _popup.Title = HeavyFeatureStrings.UpgradeTitle;
            Render();
            if (!_popup.IsVisible) _popup.ShowAt(PopupAnchor.CenterOf(VsMainWindow.Handle));
        }

        private static void Render()
        {
            if (_popup == null || _last == null) return;
            var groups = UpgradeView.GroupByVersion(_last.Apis);
            var roots = new List<TreeNode<UpgradeRow>>(groups.Count);
            foreach (var g in groups)
            {
                var apis = new List<TreeNode<UpgradeRow>>(g.Apis.Count);
                foreach (var a in g.Apis)
                {
                    var usages = a.Usages.Select(u => new TreeNode<UpgradeRow>(new UpgradeRow { Api = a, Usage = u })).ToList();
                    apis.Add(new TreeNode<UpgradeRow>(new UpgradeRow { Api = a }, usages, a.Usages.Count <= 50));
                }
                roots.Add(new TreeNode<UpgradeRow>(new UpgradeRow { Version = g }, apis, true));
            }
            string summary = _last.State == HeavyJobState.Completed && _last.IndexHasDeprecations
                ? HeavyFeatureStrings.UpgradeSummary(_last.Apis.Count, _last.Usages, UpgradeView.Replaceable(_last.Apis))
                : UpgradeView.StatusText(_last);
            _popup.SetResults(roots, summary);
            if (_popup.Tree.Count > 1) _popup.Tree.SelectedIndex = 1;
        }

        private static readonly IItemPresenter<UpgradeRow> Presenter = new DelegatePresenter<UpgradeRow>((r, row) =>
        {
            if (r.Version != null)
            {
                row.Glyph = Glyph.Module;
                row.Primary = r.Version.Title;
                return;
            }
            var a = r.Api;
            var s = a.Symbol;
            if (r.Usage == null)
            {
                row.Glyph = SymbolPresentation.GlyphFor(s);
                row.Primary = SymbolPresentation.QualifiedName(s);
                row.Secondary = a.Message;
                row.Right = a.Usages.Count.ToString();
                if (!string.IsNullOrEmpty(a.Replacement)) row.AddBadge(HeavyFeatureStrings.ReplaceWith(a.Replacement), BadgeKind.Module);
                row.ToolTip = a.Message + "\n" + (string.IsNullOrEmpty(a.Replacement) ? HeavyFeatureStrings.NoReplacement(a.ReplacementNote) : HeavyFeatureStrings.ReplaceWith(a.Replacement)) +
                              (s.Location == null ? string.Empty : "\n" + s.Location.FilePath + ":" + (s.Location.Line + 1));
                return;
            }
            var u = r.Usage;
            row.Primary = u.LineText ?? string.Empty;
            row.Right = Path.GetFileName(u.FilePath) + ":" + (u.Line + 1);
            bool nameOnly = u.Confidence != ResolutionConfidence.Exact;
            if (nameOnly) row.AddBadge(HeavyFeatureStrings.NameOnly, BadgeKind.Neutral);
            row.IsDimmed = nameOnly;
            row.ToolTip = u.FilePath + ":" + (u.Line + 1);
        }, r => r.Version == null);

        private static void EnsurePopup(HostPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_popup != null)
            {
                _popup.OwnerHandle = VsMainWindow.Handle;
                return;
            }
            _popup = new ResultsPopup<UpgradeRow>("uetovs.upgrade", Presenter, new System.Windows.Size(980, 600))
            {
                OwnerHandle = VsMainWindow.Handle,
            };
            var rescan = new IconButton(Ui.FrozenGeometry("M 10,2 A 5,5 0 1 0 11,7 M 10,0 L 10,3 L 7,3"), () => HeavyFeatureStrings.Refresh, null, 11);
            var replaceAll = new UiButton(() => HeavyFeatureStrings.ReplaceAll);
            _popup.AddToolbarItem(rescan);
            _popup.AddToolbarItem(replaceAll, true);
            rescan.Click += (s, e) => Diagnostics.Safe.RunAsync(L, "upgrade.host.rescan", () => RunAsync(package));
            replaceAll.Click += (s, e) => Diagnostics.Safe.Execute(L, "upgrade.host.replaceAll", ReplaceAll);
            Diagnostics.Safe.Execute(L, "upgrade.host.preview.attach", () => Search.EditorCodePreview.Attach(_popup.Panel.Preview));
            _popup.Panel.PreviewProvider = r =>
            {
                if (r.Usage != null) return new CodePreviewRequest { FilePath = r.Usage.FilePath, Line = r.Usage.Line + 1, Column = r.Usage.Column, Length = r.Usage.Length };
                var loc = r.Api?.Symbol?.Location;
                return loc == null ? null : new CodePreviewRequest { FilePath = loc.FilePath, Line = loc.Line + 1 };
            };
            _popup.ItemInvoked += (s, e) => Diagnostics.Safe.Execute(L, "upgrade.host.open", () =>
            {
                var u = e.Item.Usage;
                if (u != null) NavigationService.Instance.JumpTo(new CodeLocation(u.FilePath, u.Line, u.Column), "upgrade");
                else if (e.Item.Api?.Symbol?.Location != null) NavigationService.Instance.JumpTo(NavigationPresenters.ToLocation(e.Item.Api.Symbol.Location), "upgrade");
            });
        }

        /// <summary>Preview of every replaceable API (checked), then the renames of their Exact usages as one undo step.</summary>
        private static void ReplaceAll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_last == null) return;
            var candidates = _last.Apis.Where(a => !string.IsNullOrEmpty(a.Replacement) && a.Usages.Any(u => u.Confidence == ResolutionConfidence.Exact)).ToList();
            int count = UpgradeView.Replaceable(candidates);
            if (count == 0)
            {
                VsStatusBar.SetText(HeavyFeatureStrings.UpgradeSummary(_last.Apis.Count, _last.Usages, 0));
                return;
            }
            var sw = Stopwatch.StartNew();
            var dialog = new CheckListDialog<DeprecatedApi>(() => HeavyFeatureStrings.ReplacePreviewTitle(count), () => HeavyFeatureStrings.ReplaceAll,
                PresentReplacement, a => SymbolPresentation.QualifiedName(a.Symbol) + " " + a.Replacement, new System.Windows.Size(820, 600));
            var rows = new List<CheckRow<DeprecatedApi>>();
            foreach (var g in UpgradeView.GroupByVersion(candidates))
            {
                rows.Add(new CheckRow<DeprecatedApi> { Header = g.Title, HeaderCount = g.Apis.Count });
                rows.AddRange(g.Apis.Select(a => new CheckRow<DeprecatedApi> { Item = a, Checked = true }));
            }
            var picked = dialog.ShowModal(rows, VsMainWindow.Handle);
            if (picked == null || picked.Count == 0)
            {
                L.Info("upgrade.replace", null, ("offered", candidates.Count), ("picked", 0), ("canceled", true));
                return;
            }
            var edits = UpgradeView.PlanReplacements(picked);
            var result = RefactorEdits.Apply(HeavyFeatureStrings.ReplaceUndoName, edits);
            L.Info("upgrade.replace", null, ("offered", candidates.Count), ("picked", picked.Count), ("edits", edits.Count), ("applied", result.Applied),
                ("skipped", result.Skipped), ("files", result.Files), ("failedFiles", result.FailedFiles.Count), ("ms", sw.ElapsedMilliseconds));
            VsStatusBar.SetText(HeavyFeatureStrings.FixResult(result.Applied, result.Skipped, result.Files));
        }

        private static void PresentReplacement(DeprecatedApi a, RowData d)
        {
            d.Primary = SymbolPresentation.QualifiedName(a.Symbol) + "  →  " + a.Replacement;
            d.Secondary = a.Message;
            d.Right = a.Usages.Count(u => u.Confidence == ResolutionConfidence.Exact).ToString();
            d.ToolTip = string.Join("\n", a.Usages.Where(u => u.Confidence == ResolutionConfidence.Exact).Take(20).Select(u => u.FilePath + ":" + (u.Line + 1)));
        }
    }
}
