using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Link;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Search;
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
    /// "UE 日志" popup window: the editor / PIE log streamed by UEToVSLink. Level chips, category drop-down, search
    /// (all words), auto-scroll that doubles as pause (off = the view stops following and shows how many new lines
    /// wait), clear. Lines live in <see cref="UeLogStore"/> (ring buffer); a 100 ms UI timer appends the new ones to the
    /// filtered <see cref="UeLogView"/>, so a burst costs one list update per tick. Enter / double-click jumps to the
    /// UE_LOG that wrote the line, only when its format matches exactly (<see cref="UeLogSourceMatcher"/>).
    /// Logs link.log.open / close / jump / source.
    /// </summary>
    internal sealed class UeLogWindow : UtilityWindow
    {
        private static readonly Logger L = Log.For("Link");
        private static readonly TimeSpan SiteCacheTime = TimeSpan.FromSeconds(30);

        private readonly UeLogStore _store;
        private readonly EditorLinkSubscription _subscription;
        private readonly UeLogView _view;
        private readonly ToolWindowShell _shell = new ToolWindowShell();
        private readonly VirtualList<UeLogLine> _list;
        private readonly EmptyState _empty = new EmptyState();
        private readonly SearchInput _search = new SearchInput(false) { Width = 260 };
        private readonly Dropdown _category = new Dropdown { MinWidth = 160 };
        private readonly UiToggle _autoScroll;
        private readonly Dictionary<UeLogLevel, Chip> _levelChips = new Dictionary<UeLogLevel, Chip>();
        private readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _searchDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly UeLogSourceMatcher _matcher = new UeLogSourceMatcher();
        private readonly Dictionary<string, (DateTime At, List<LogSiteInfo> Sites)> _sites = new Dictionary<string, (DateTime, List<LogSiteInfo>)>(StringComparer.Ordinal);
        private List<string> _categories = new List<string>();
        private bool _suppressCategory;
        private Banner _stateBanner;
        private LinkSubscriptionState _shownState = (LinkSubscriptionState)(-1);
        private DateTime _openedUtc;
        private CancellationTokenSource _jumpCts;

        public UeLogWindow(UeLogStore store, EditorLinkSubscription subscription)
            : base("ueLog", () => LinkStrings.LogWindowTitle, new Size(1040, 640))
        {
            _store = store;
            _subscription = subscription;
            lock (_store.Gate) _view = new UeLogView(_store.Buffer);

            _list = new VirtualList<UeLogLine>(new DelegatePresenter<UeLogLine>(Present)) { FocusOnClick = true };
            _list.ItemInvoked += (s, e) => Safe.Execute(L, "link.log.invoke", () => JumpToSource(e.Item));

            foreach (UeLogLevel level in Enum.GetValues(typeof(UeLogLevel)))
            {
                var lv = level;
                var chip = new Chip(() => LevelName(lv), _view.Filter.IsLevelShown(lv));
                chip.Checked += (s, e) => SetLevel(lv, true);
                chip.Unchecked += (s, e) => SetLevel(lv, false);
                _levelChips[lv] = chip;
                _shell.AddToolbarItem(chip);
            }
            _category.Items = new[] { LinkStrings.AllCategories };
            _category.SelectedIndex = 0;
            _category.SelectionChanged += (s, e) => Safe.Execute(L, "link.log.category", OnCategoryChanged);
            _shell.AddToolbarItem(_category);
            _search.Placeholder = LinkStrings.LogSearch;
            _search.TextChanged += (s, text) =>
            {
                _searchDelay.Stop();
                _searchDelay.Start();
            };
            _searchDelay.Tick += (s, e) => Safe.Execute(L, "link.log.search", () =>
            {
                _searchDelay.Stop();
                _view.Filter.Search = _search.Text;
                Rebuild();
            });
            _shell.AddToolbarItem(_search);

            var clear = new UiButton(() => LinkStrings.Clear, ButtonKind.Ghost);
            clear.Click += (s, e) => Safe.Execute(L, "link.log.clear", Clear);
            _shell.AddToolbarItem(clear, true);
            _autoScroll = new UiToggle(() => LinkStrings.AutoScroll, true);
            _autoScroll.Checked += (s, e) => Safe.Execute(L, "link.log.follow", () => Tick());
            _shell.AddToolbarItem(_autoScroll, true);

            var body = new Grid();
            body.Children.Add(_list);
            body.Children.Add(_empty);
            _shell.Body = body;
            _shell.Footer.SetHints(new KeyHint("Enter", () => LinkStrings.HintJump), new KeyHint("Ctrl+C", () => LinkStrings.CopyLine));
            WindowContent = _shell;

            PreviewKeyDown += OnKey;
            _timer.Tick += (s, e) => Safe.Execute(L, "link.log.tick", Tick);
            Loaded += (s, e) => Safe.Execute(L, "link.log.open", OnOpened);
            Closed += (s, e) => Safe.Execute(L, "link.log.close", OnClosed);
        }

        private bool Following => _autoScroll.IsOn;

        private static string LevelName(UeLogLevel level)
        {
            switch (level)
            {
                case UeLogLevel.Error: return LinkStrings.LevelError;
                case UeLogLevel.Warning: return LinkStrings.LevelWarning;
                case UeLogLevel.Display: return LinkStrings.LevelDisplay;
                case UeLogLevel.Log: return LinkStrings.LevelLog;
                default: return LinkStrings.LevelVerbose;
            }
        }

        private static void Present(UeLogLine line, RowData row)
        {
            string msg = line.Message ?? string.Empty;
            int nl = msg.IndexOf('\n');
            row.Primary = LogCategoryText.Shorten(nl >= 0 ? msg.Substring(0, nl).TrimEnd('\r') : msg, 400);
            row.Secondary = line.Category;
            row.Right = line.Received.ToString("HH:mm:ss");
            if (line.Level == UeLogLevel.Error) row.AddBadge(LinkStrings.LevelError, BadgeKind.Macro);
            else if (line.Level == UeLogLevel.Warning) row.AddBadge(LinkStrings.LevelWarning, BadgeKind.UEnum);
            row.IsDimmed = line.Level == UeLogLevel.Verbose;
            if (nl >= 0 || msg.Length > 400) row.ToolTip = LogCategoryText.Shorten(msg, 4000);
        }

        private void OnOpened()
        {
            _openedUtc = DateTime.UtcNow;
            lock (_store.Gate) _view.Rebuild();
            _list.SetItems(_view, false);
            if (_view.Count > 0) _list.ScrollIntoView(_view.Count - 1);
            if (_subscription != null) _subscription.StateChanged += OnStateChanged;
            UpdateState();
            UpdateChrome();
            _timer.Start();
            L.Info("link.log.open", null, ("lines", _view.Count), ("capacity", _store.Buffer.Capacity), ("state", _subscription?.State.ToString()));
        }

        private void OnClosed()
        {
            _timer.Stop();
            _searchDelay.Stop();
            _jumpCts?.Cancel();
            if (_subscription != null) _subscription.StateChanged -= OnStateChanged;
            L.Info("link.log.close", null, ("visibleSec", Math.Round((DateTime.UtcNow - _openedUtc).TotalSeconds, 1)), ("lines", _view.Count),
                   ("dropped", _store.Dropped));
        }

        private void OnStateChanged(LinkSubscriptionState state) =>
            Safe.RunAsync(L, "link.log.state", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (IsLoaded) UpdateState();
            });

        private void UpdateState()
        {
            var state = _subscription?.State ?? LinkSubscriptionState.Idle;
            if (state == _shownState) return;
            _shownState = state;
            if (_stateBanner != null)
            {
                _stateBanner.CanClose = true;
                _stateBanner.Visibility = Visibility.Collapsed;
                _stateBanner = null;
            }
            if (state == LinkSubscriptionState.PluginTooOld)
            {
                _stateBanner = new Banner(Severity.Warning, LinkStrings.StateTooOld);
                _stateBanner.AddAction(() => LinkStrings.UpdatePlugin, () => HostPackage.Instance?.ExecuteAction(M1ActionIds.InstallEditorPlugin));
                _shell.ShowBanner(_stateBanner);
            }
            UpdateChrome();
        }

        private static string StateText(LinkSubscriptionState state)
        {
            switch (state)
            {
                case LinkSubscriptionState.Connected: return LinkStrings.StateConnected;
                case LinkSubscriptionState.Connecting: return LinkStrings.StateConnecting;
                case LinkSubscriptionState.Waiting: return LinkStrings.StateWaiting;
                case LinkSubscriptionState.PluginTooOld: return LinkStrings.StateTooOld;
                default: return LinkStrings.StateIdle;
            }
        }

        // ------------------------------------------------------------------ data

        private void Tick()
        {
            if (!Following)
            {
                UpdateChrome();
                return;
            }
            (int Appended, int Removed) change;
            using (UiThread.Measure(L, "link.log.sync"))
            {
                lock (_store.Gate) change = _view.Sync();
            }
            if (change.Appended == 0 && change.Removed == 0)
            {
                UpdateChrome();
                return;
            }
            _list.SetItems(_view, true);
            if (_view.Count > 0) _list.ScrollIntoView(_view.Count - 1);
            UpdateCategories();
            UpdateChrome();
        }

        private void Rebuild()
        {
            using (UiThread.Measure(L, "link.log.rebuild"))
            {
                lock (_store.Gate) _view.Rebuild();
            }
            _list.SetItems(_view, false);
            if (_view.Count > 0 && Following) _list.ScrollIntoView(_view.Count - 1);
            UpdateChrome();
            L.Debug("link.log.filter", null, ("filter", _view.Filter.ToString()), ("shown", _view.Count));
        }

        private void Clear()
        {
            _store.Clear();
            lock (_store.Gate) _view.Clear();
            _list.SetItems(_view, false);
            _sites.Clear();
            UpdateCategories();
            UpdateChrome();
            L.Info("link.log.clear", null);
        }

        private void SetLevel(UeLogLevel level, bool shown) => Safe.Execute(L, "link.log.level", () =>
        {
            if (_view.Filter.IsLevelShown(level) == shown) return;
            _view.Filter.SetLevelShown(level, shown);
            Rebuild();
        });

        private void OnCategoryChanged()
        {
            if (_suppressCategory) return;
            int i = _category.SelectedIndex;
            _view.Filter.Category = i <= 0 || i > _categories.Count ? null : _categories[i - 1];
            Rebuild();
        }

        private void UpdateCategories()
        {
            List<string> names;
            lock (_store.Gate)
            {
                if (_store.Buffer.CategoryCounts.Count == _categories.Count) return;
                names = _store.Buffer.CategoryCounts.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            }
            string selected = _view.Filter.Category;
            _categories = names;
            _suppressCategory = true;
            try
            {
                var items = new List<string>(names.Count + 1) { LinkStrings.AllCategories };
                items.AddRange(names);
                _category.Items = items;
                int idx = selected == null ? 0 : names.IndexOf(selected) + 1;
                _category.SelectedIndex = Math.Max(0, idx);
            }
            finally
            {
                _suppressCategory = false;
            }
        }

        private void UpdateChrome()
        {
            int total, errors, warnings;
            lock (_store.Gate)
            {
                total = _store.Buffer.Count;
                errors = _store.Buffer.ErrorCount;
                warnings = _store.Buffer.WarningCount;
            }
            _levelChips[UeLogLevel.Error].Count = errors;
            _levelChips[UeLogLevel.Warning].Count = warnings;
            _shell.Footer.Count = LinkStrings.LogCount(_view.Count, total);

            string status = StateText(_subscription?.State ?? LinkSubscriptionState.Idle);
            if (!Following)
            {
                long pending;
                lock (_store.Gate) pending = _view.Pending;
                status = LinkStrings.LogPaused(pending) + " · " + status;
            }
            long dropped = _store.Dropped;
            if (dropped > 0) status += " · " + LinkStrings.LogDropped(dropped);
            _shell.Footer.Status = status;

            if (_view.Count > 0) _empty.Visibility = Visibility.Collapsed;
            else
            {
                _empty.Visibility = Visibility.Visible;
                if (total == 0) _empty.Set(LinkStrings.LogEmpty, LinkStrings.LogEmptyHint);
                else _empty.Set(LinkStrings.LogNoMatch);
            }
        }

        // ------------------------------------------------------------------ keys / copy

        private void OnKey(object sender, KeyEventArgs e) => Safe.Execute(L, "link.log.key", () =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && !_search.InputBox.IsKeyboardFocusWithin)
            {
                var line = _list.SelectedItem;
                if (line == null) return;
                try
                {
                    Clipboard.SetText(line.Category + ": " + line.Verbosity + ": " + line.Message);
                }
                catch (Exception ex)
                {
                    L.Warn("link.log.copy.failed", null, ex);
                }
                e.Handled = true;
            }
        });

        // ------------------------------------------------------------------ jump to source

        private void JumpToSource(UeLogLine line)
        {
            if (line == null || string.IsNullOrEmpty(line.Category)) return;
            _jumpCts?.Cancel();
            _jumpCts = new CancellationTokenSource();
            var ct = _jumpCts.Token;
            Safe.RunAsync(L, "link.log.jump", () => JumpToSourceAsync(line, ct), ("category", line.Category));
        }

        private async Task JumpToSourceAsync(UeLogLine line, CancellationToken ct)
        {
            var engine = HostPackage.Instance?.Engine;
            if (engine == null || !engine.IsConnected)
            {
                VsStatusBar.SetText(LinkStrings.EngineUnavailable);
                return;
            }
            _shell.Footer.IsBusy = true;
            _shell.Footer.Status = LinkStrings.SourceSearching;
            List<LogSiteInfo> sites;
            try
            {
                sites = await GetSitesAsync(engine, line.Category, ct);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _shell.Footer.IsBusy = false;
                UpdateChrome();
            }
            if (ct.IsCancellationRequested) return;
            if (sites == null)
            {
                VsStatusBar.SetText(LinkStrings.EngineUnavailable);
                return;
            }
            var match = _matcher.Find(sites, line.Category, line.Verbosity, line.Message);
            L.Info("link.log.source", null, ("category", line.Category), ("verbosity", line.Verbosity.ToString()), ("outcome", match.Outcome),
                   ("candidates", match.Candidates), ("matched", match.Matched), ("literals", match.LiteralChars), ("sites", match.Sites.Count));
            if (!match.IsExact)
            {
                string text = match.Outcome == "noSites" ? LinkStrings.SourceNoSites(line.Category)
                    : match.Outcome == "ambiguousFormat" ? LinkStrings.SourceAmbiguous
                    : LinkStrings.SourceNotFound(line.Category);
                VsStatusBar.SetText(text);
                Toast.Show(_list, text, Severity.Info);
                return;
            }
            if (match.Sites.Count == 1)
            {
                Jump(match.Sites[0]);
                return;
            }
            var rows = match.Sites.Select(s => new SearchRow
            {
                Kind = SearchRowKind.Symbol,
                Title = LogCategoryText.Title(s),
                Detail = Path.GetFileName(s.Location?.FilePath ?? "") + ":" + ((s.Location?.Line ?? 0) + 1),
                Glyph = SymbolKind.Macro,
                FilePath = s.Location?.FilePath,
                Line = s.Location?.Line ?? 0,
                Column = s.Location?.Column ?? 0,
            }).ToList();
            await RelatedNavigator.PickAndJumpAsync("uetovs.ueLog.source", LinkStrings.SourcePickTitle, rows);
        }

        private static void Jump(LogSiteInfo site)
        {
            if (site?.Location?.FilePath == null) return;
            L.Info("link.log.jump", null, ("file", Path.GetFileName(site.Location.FilePath)), ("line", site.Location.Line));
            NavigationService.Instance.JumpTo(site.Location, JumpReasons.Other);
        }

        private async Task<List<LogSiteInfo>> GetSitesAsync(Engine.EngineClient engine, string category, CancellationToken ct)
        {
            if (_sites.TryGetValue(category, out var cached) && DateTime.UtcNow - cached.At < SiteCacheTime) return cached.Sites;
            var r = await engine.FindLogSitesAsync(new LogSitesRequest { Category = category, IncludeUsages = true, IncludeDeclarations = false }, ct).ConfigureAwait(false);
            if (!r.Ok) return null;
            var list = r.Value.Sites ?? new List<LogSiteInfo>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _sites[category] = (DateTime.UtcNow, list);
            return list;
        }
    }
}
