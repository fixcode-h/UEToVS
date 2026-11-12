using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Link;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;
using SymbolKind = UEToVS.UI.Logic.SymbolKind;

namespace UEToVS.Host.Link
{
    /// <summary>
    /// "UE 自动化测试" popup window. The tree comes from the engine's source scan (IMPLEMENT_SIMPLE / COMPLEX
    /// _AUTOMATION_TEST, BEGIN_DEFINE_SPEC, DEFINE_SPEC; dot path → tree). Running sends <c>runTests</c> through the
    /// editor link ("Automation RunTests &lt;path&gt;" in the editor) and applies the streamed results (running / passed /
    /// failed, errors, warnings, log lines). Enter / double-click runs the selection, F4 goes to the source.
    /// When the editor is offline (or its plugin too old) a banner says so instead of running.
    /// Logs link.tests.open / scan / run / finished / jump / close.
    /// </summary>
    internal sealed class AutomationTestsWindow : UtilityWindow
    {
        private static readonly Logger L = Log.For("Link");
        private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(4);

        private readonly HostPackage _package;
        private readonly EditorLinkSubscription _subscription;
        private readonly ToolWindowShell _shell = new ToolWindowShell();
        private readonly TreeList<AutomationTestNode> _tree;
        private readonly EmptyState _empty = new EmptyState();
        private readonly SearchInput _search = new SearchInput(false) { Width = 260 };
        private readonly UiButton _run;
        private readonly UiButton _rerunFailed;
        private readonly TextBox _details = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(10, 8, 10, 8),
        };
        private readonly DispatcherTimer _searchDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private AutomationTestTree _model = AutomationTestTree.Build(null);
        private Banner _banner;
        private string _runId;
        private string _runFilter;
        private DateTime _runStartedUtc;
        private DateTime _openedUtc;
        private int _filesScanned;
        private bool _structureDirty;

        public AutomationTestsWindow(HostPackage package, EditorLinkSubscription subscription)
            : base("ueTests", () => LinkStrings.TestsWindowTitle, new Size(1000, 640))
        {
            _package = package;
            _subscription = subscription;
            _tree = new TreeList<AutomationTestNode>(new DelegatePresenter<AutomationTestNode>(Present));
            _tree.ItemInvoked += (s, e) => Safe.Execute(L, "link.tests.invoke", () => RunNode(e.Item));
            _tree.SelectionChanged += (s, e) => Safe.Execute(L, "link.tests.select", ShowDetails);

            _search.Placeholder = LinkStrings.TestsSearch;
            _search.TextChanged += (s, t) =>
            {
                _searchDelay.Stop();
                _searchDelay.Start();
            };
            _searchDelay.Tick += (s, e) => Safe.Execute(L, "link.tests.filter", () =>
            {
                _searchDelay.Stop();
                RebuildTree();
            });
            _shell.AddToolbarItem(_search);
            _run = new UiButton(() => LinkStrings.RunSelected, ButtonKind.Primary);
            _run.Click += (s, e) => Safe.Execute(L, "link.tests.runSelected", () => RunNode(_tree.SelectedNode?.Item));
            _shell.AddToolbarItem(_run);
            _rerunFailed = new UiButton(() => LinkStrings.RunFailed);
            _rerunFailed.Click += (s, e) => Safe.Execute(L, "link.tests.rerunFailed", RerunFailed);
            _shell.AddToolbarItem(_rerunFailed);
            var source = new UiButton(() => LinkStrings.GoToSource, ButtonKind.Ghost);
            source.Click += (s, e) => Safe.Execute(L, "link.tests.source", () => GoToSource(_tree.SelectedNode?.Item));
            _shell.AddToolbarItem(source);
            var refresh = new UiButton(() => LinkStrings.Refresh, ButtonKind.Ghost);
            refresh.Click += (s, e) => Safe.RunAsync(L, "link.tests.refresh", LoadAsync);
            _shell.AddToolbarItem(refresh, true);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 220 });
            var left = new Grid();
            left.Children.Add(_tree);
            left.Children.Add(_empty);
            grid.Children.Add(left);
            var splitter = new GridSplitter { Width = 1, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            splitter.SetResourceReference(BackgroundProperty, UEToVS.UI.Theming.Tokens.BorderSubtle);
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);
            _details.SetResourceReference(ForegroundProperty, UEToVS.UI.Theming.Tokens.TextPrimary);
            Grid.SetColumn(_details, 2);
            grid.Children.Add(_details);
            _shell.Body = grid;
            _shell.Footer.SetHints(new KeyHint("Enter", () => LinkStrings.RunSelected), new KeyHint("F4", () => LinkStrings.GoToSource));
            WindowContent = _shell;

            PreviewKeyDown += OnKey;
            Loaded += (s, e) => Safe.Execute(L, "link.tests.open", OnOpened);
            Closed += (s, e) => Safe.Execute(L, "link.tests.close", OnClosed);
        }

        private bool Running => _runId != null;

        private void OnOpened()
        {
            _openedUtc = DateTime.UtcNow;
            if (_subscription != null)
            {
                _subscription.EventReceived += OnLinkEvent;
                _subscription.StateChanged += OnStateChanged;
            }
            ShowDetails();
            UpdateChrome();
            L.Info("link.tests.open", null, ("state", _subscription?.State.ToString()));
            Safe.RunAsync(L, "link.tests.scan", LoadAsync);
        }

        private void OnClosed()
        {
            _cts.Cancel();
            _searchDelay.Stop();
            if (_subscription != null)
            {
                _subscription.EventReceived -= OnLinkEvent;
                _subscription.StateChanged -= OnStateChanged;
            }
            L.Info("link.tests.close", null, ("visibleSec", Math.Round((DateTime.UtcNow - _openedUtc).TotalSeconds, 1)), ("running", Running));
        }

        // ------------------------------------------------------------------ scan

        private async Task LoadAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var engine = _package?.Engine;
            if (engine == null || !engine.IsConnected)
            {
                _empty.Visibility = Visibility.Visible;
                _empty.Set(LinkStrings.EngineUnavailable);
                return;
            }
            _shell.Footer.IsBusy = true;
            _shell.Footer.Status = LinkStrings.TestsScanning;
            if (_model.TestCount == 0)
            {
                _empty.Visibility = Visibility.Visible;
                _empty.Set(LinkStrings.TestsScanning);
            }
            var r = await engine.FindAutomationTestsAsync(new AutomationTestsRequest(), _cts.Token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _shell.Footer.IsBusy = false;
            if (_cts.IsCancellationRequested) return;
            if (!r.Ok)
            {
                _shell.Footer.Status = EngineCallText.ForStatus(r.Status) ?? LinkStrings.EngineUnavailable;
                return;
            }
            var previous = _model;
            _model = AutomationTestTree.Build(r.Value.Tests);
            CarryOverResults(previous, _model);
            _filesScanned = r.Value.FilesScanned;
            L.Info("link.tests.scan", null, ("tests", _model.TestCount), ("duplicates", _model.Duplicates), ("files", _filesScanned), ("truncated", r.Value.Truncated));
            RebuildTree();
        }

        /// <summary>A refresh keeps the results of the tests that still exist.</summary>
        private static void CarryOverResults(AutomationTestTree from, AutomationTestTree to)
        {
            foreach (var old in from.Roots.SelectMany(AutomationTestTree.Walk))
            {
                if (old.State == AutomationNodeState.NotRun) continue;
                var n = old.IsTest ? to.ForReported(old.FullPath) : to.Find(old.FullPath);
                if (n == null) continue;
                n.State = old.State;
                n.DurationMs = old.DurationMs;
                n.Errors.AddRange(old.Errors);
                n.Warnings.AddRange(old.Warnings);
                n.Logs.AddRange(old.Logs);
            }
        }

        private void RebuildTree()
        {
            _structureDirty = false;
            string search = _search.Text;
            HashSet<AutomationTestNode> keep = string.IsNullOrWhiteSpace(search) ? null : _model.Matching(search);
            bool expand = keep != null;
            TreeNode<AutomationTestNode> Map(AutomationTestNode n)
            {
                var children = n.Children.Where(c => keep == null || keep.Contains(c)).Select(Map).ToList();
                return new TreeNode<AutomationTestNode>(n, children, expand || n.Parent == null);
            }
            var selected = _tree.SelectedNode?.Item?.FullPath;
            var roots = _model.Roots.Where(n => keep == null || keep.Contains(n)).Select(Map).ToList();
            _tree.SetRoots(roots);
            if (selected != null) Select(roots, selected);
            UpdateChrome();
        }

        private void Select(IReadOnlyList<TreeNode<AutomationTestNode>> roots, string path)
        {
            // Keep the selection across rebuilds when the node is visible (expanded path).
            int index = 0;
            bool Visit(IEnumerable<TreeNode<AutomationTestNode>> nodes)
            {
                foreach (var n in nodes)
                {
                    if (string.Equals(n.Item.FullPath, path, StringComparison.OrdinalIgnoreCase)) return true;
                    index++;
                    if (n.IsExpanded && n.HasChildren && Visit(n.Children)) return true;
                }
                return false;
            }
            if (Visit(roots))
            {
                _tree.SelectedIndex = index;
                _tree.ScrollIntoView(index);
            }
        }

        // ------------------------------------------------------------------ presentation

        private static void Present(AutomationTestNode n, RowData row)
        {
            row.Primary = n.Segment;
            row.Glyph = n.IsTest ? SymbolKind.Method : SymbolKind.Namespace;
            var state = n.IsTest && n.Children.Count == 0 ? n.State : n.AggregateState();
            switch (state)
            {
                case AutomationNodeState.Passed: row.AddBadge(LinkStrings.StatePassed, BadgeKind.UProperty); break;
                case AutomationNodeState.Failed: row.AddBadge(LinkStrings.StateFailed, BadgeKind.Macro); break;
                case AutomationNodeState.Running: row.AddBadge(LinkStrings.StateRunning, BadgeKind.UEnum); break;
                case AutomationNodeState.Queued: row.AddBadge(LinkStrings.StateQueued, BadgeKind.Neutral); break;
                case AutomationNodeState.Skipped: row.AddBadge(LinkStrings.StateSkipped, BadgeKind.Neutral); break;
            }
            if (n.Test != null) row.Secondary = n.Test.Macro + (string.IsNullOrEmpty(n.Test.ClassName) ? "" : "  " + n.Test.ClassName);
            else if (!n.IsTest) row.Secondary = n.CountTests().ToString();
            if (n.DurationMs > 0) row.Right = LinkStrings.Duration(n.DurationMs);
            row.IsDimmed = n.ReportedOnly && n.State == AutomationNodeState.NotRun;
            row.ToolTip = n.FullPath;
        }

        private void ShowDetails()
        {
            var n = _tree.SelectedNode?.Item;
            if (n == null)
            {
                _details.Text = LinkStrings.TestsNoDetails;
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine(n.FullPath);
            var loc = n.SourceLocation;
            if (loc != null) sb.AppendLine(Path.GetFileName(loc.FilePath) + ":" + (loc.Line + 1));
            sb.Append(StateName(n.IsTest && n.Children.Count == 0 ? n.State : n.AggregateState()));
            if (n.DurationMs > 0) sb.Append("  ·  ").Append(LinkStrings.Duration(n.DurationMs));
            sb.AppendLine();
            if (!n.IsTest)
            {
                int tests = n.CountTests();
                int failed = AutomationTestTree.Walk(n).Count(x => x.IsTest && x.State == AutomationNodeState.Failed);
                int passed = AutomationTestTree.Walk(n).Count(x => x.IsTest && x.State == AutomationNodeState.Passed);
                sb.AppendLine(LinkStrings.TestsSummary(passed, failed, tests));
            }
            Section(sb, LinkStrings.ErrorsHeader, n.Errors);
            Section(sb, LinkStrings.WarningsHeader, n.Warnings);
            Section(sb, LinkStrings.LogsHeader, n.Logs);
            _details.Text = sb.ToString();
        }

        private static void Section(StringBuilder sb, string title, List<string> lines)
        {
            if (lines.Count == 0) return;
            sb.AppendLine().Append("— ").Append(title).Append(" (").Append(lines.Count).AppendLine(") —");
            foreach (var l in lines) sb.AppendLine(l);
        }

        private static string StateName(AutomationNodeState s)
        {
            switch (s)
            {
                case AutomationNodeState.Queued: return LinkStrings.StateQueued;
                case AutomationNodeState.Running: return LinkStrings.StateRunning;
                case AutomationNodeState.Passed: return LinkStrings.StatePassed;
                case AutomationNodeState.Failed: return LinkStrings.StateFailed;
                case AutomationNodeState.Skipped: return LinkStrings.StateSkipped;
                default: return LinkStrings.StateNotRun;
            }
        }

        private void UpdateChrome()
        {
            var (total, passed, failed, pending) = _model.Summary();
            _shell.Footer.Count = LinkStrings.TestsSummary(passed, failed, total);
            if (!_shell.Footer.IsBusy)
                _shell.Footer.Status = Running ? LinkStrings.TestsRunning(_runFilter) : LinkStrings.TestsFound(_model.TestCount, _filesScanned);
            _run.IsEnabled = !Running;
            _rerunFailed.IsEnabled = !Running && failed > 0;
            if (_model.Roots.Count == 0)
            {
                _empty.Visibility = Visibility.Visible;
                _empty.Set(LinkStrings.TestsNone, LinkStrings.TestsNoneHint);
            }
            else _empty.Visibility = Visibility.Collapsed;
        }

        private void ShowBanner(string text, bool offerPluginUpdate)
        {
            if (_banner != null) _banner.Visibility = Visibility.Collapsed;
            _banner = new Banner(Severity.Warning, text) { CanClose = true };
            if (offerPluginUpdate)
                _banner.AddAction(() => LinkStrings.UpdatePlugin, () => HostPackage.Instance?.ExecuteAction(M1ActionIds.InstallEditorPlugin));
            _shell.ShowBanner(_banner);
        }

        private void HideBanner()
        {
            if (_banner == null) return;
            _banner.Visibility = Visibility.Collapsed;
            _banner = null;
        }

        // ------------------------------------------------------------------ run

        private void OnKey(object sender, KeyEventArgs e) => Safe.Execute(L, "link.tests.key", () =>
        {
            if (e.Key == Key.F4)
            {
                GoToSource(_tree.SelectedNode?.Item);
                e.Handled = true;
            }
        });

        private void RunNode(AutomationTestNode node)
        {
            if (node == null) return;
            Safe.RunAsync(L, "link.tests.run", () => RunAsync(node.FullPath, node));
        }

        private void RerunFailed()
        {
            var failed = _model.Roots.SelectMany(AutomationTestTree.Walk).Where(n => n.IsTest && n.State == AutomationNodeState.Failed).ToList();
            if (failed.Count == 0) return;
            // "Automation RunTests A+B" runs several filters in one go.
            string filter = string.Join("+", failed.Select(n => n.FullPath));
            Safe.RunAsync(L, "link.tests.run", () => RunAsync(filter, null, failed));
        }

        private async Task RunAsync(string filter, AutomationTestNode node, List<AutomationTestNode> nodes = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Running)
            {
                VsStatusBar.SetText(LinkStrings.TestsBusy);
                return;
            }
            var client = EditorLinkService.Instance?.Client;
            if (_subscription == null || client == null) return;

            // The "tests" topic is subscribed while this window is open; wait briefly for a fresh connection.
            bool connected = await _subscription.WaitConnectedAsync(ConnectWait, _cts.Token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!connected)
            {
                bool tooOld = _subscription.State == LinkSubscriptionState.PluginTooOld;
                L.Info("link.tests.offline", null, ("state", _subscription.State.ToString()), ("filter", filter));
                ShowBanner(tooOld ? LinkStrings.TestsPluginTooOld : LinkStrings.TestsEditorOffline, tooOld);
                return;
            }
            HideBanner();

            string runId = "r" + DateTime.UtcNow.Ticks.ToString("x");
            if (nodes == null) _model.MarkQueued(node);
            else foreach (var n in nodes) _model.MarkQueued(n);
            _runId = runId;
            _runFilter = filter;
            _runStartedUtc = DateTime.UtcNow;
            _tree.InvalidateAllRows();
            UpdateChrome();
            L.Info("link.tests.run", null, ("id", runId), ("filter", filter), ("tests", nodes?.Count ?? node?.CountTests()));

            var reply = await client.RunTestsAsync(runId, filter, _cts.Token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (reply.Ok) return;
            L.Info("link.tests.rejected", null, ("id", runId), ("reached", reply.Reached), ("message", reply.Message));
            _runId = null;
            _model.FinishRun();
            _tree.InvalidateAllRows();
            string text = !reply.Reached ? LinkStrings.TestsEditorOffline
                : reply.Message != null && reply.Message.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0 ? LinkStrings.TestsBusy
                : LinkStrings.TestsRunFailed(reply.Message ?? "?");
            ShowBanner(text, false);
            UpdateChrome();
        }

        private void GoToSource(AutomationTestNode node)
        {
            var loc = node?.SourceLocation;
            if (loc?.FilePath == null) return;
            L.Info("link.tests.jump", null, ("test", node.FullPath), ("file", Path.GetFileName(loc.FilePath)), ("line", loc.Line));
            NavigationService.Instance.JumpTo(loc, JumpReasons.Other);
        }

        // ------------------------------------------------------------------ link events (background thread)

        private void OnLinkEvent(LinkEvent e)
        {
            if (!(e is TestEvent te)) return;
            Safe.RunAsync(L, "link.tests.event", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_cts.IsCancellationRequested) return;
                Apply(te);
            });
        }

        private void Apply(TestEvent e)
        {
            // Events of another run (started elsewhere, or a run this window no longer tracks) still update the tree.
            switch (e.Phase)
            {
                case TestEventPhase.RunStarted:
                    break;
                case TestEventPhase.TestStarted:
                case TestEventPhase.TestFinished:
                {
                    bool known = _model.Find(e.Name) != null;
                    var n = _model.Apply(e);
                    if (n == null) break;
                    if (!known) _structureDirty = true;
                    if (e.Phase == TestEventPhase.TestFinished && n.State == AutomationNodeState.Failed)
                        L.Info("link.tests.failed", null, ("test", n.FullPath), ("errors", n.Errors.Count));
                    break;
                }
                case TestEventPhase.RunFinished:
                {
                    if (e.RunId != _runId && _runId != null && !string.IsNullOrEmpty(e.RunId)) break;
                    int unfinished = _model.FinishRun();
                    L.Info("link.tests.finished", null, ("id", e.RunId), ("passed", e.Passed), ("failed", e.Failed), ("unreported", unfinished),
                           ("message", e.Message), ("durationMs", Math.Round((DateTime.UtcNow - _runStartedUtc).TotalMilliseconds)));
                    _runId = null;
                    string text = string.IsNullOrEmpty(e.Message) ? LinkStrings.TestsRunDone(e.Passed, e.Failed) : LinkStrings.TestsRunMessage(e.Message);
                    VsStatusBar.SetText(text);
                    if (!string.IsNullOrEmpty(e.Message)) ShowBanner(text, false);
                    break;
                }
            }
            if (_structureDirty) RebuildTree();
            else _tree.InvalidateAllRows();
            ShowDetails();
            UpdateChrome();
        }

        private void OnStateChanged(LinkSubscriptionState state) =>
            Safe.RunAsync(L, "link.tests.state", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_cts.IsCancellationRequested || !Running || state == LinkSubscriptionState.Connected || state == LinkSubscriptionState.Connecting) return;
                // The editor went away mid-run: nothing more will be reported.
                L.Info("link.tests.aborted", null, ("id", _runId), ("state", state.ToString()));
                _runId = null;
                _model.FinishRun();
                _tree.InvalidateAllRows();
                ShowBanner(LinkStrings.TestsEditorOffline, false);
                UpdateChrome();
            });
    }
}
