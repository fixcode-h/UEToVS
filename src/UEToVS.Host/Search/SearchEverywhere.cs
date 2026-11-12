using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Query;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Engine;
using UEToVS.Host.Integration;
using UEToVS.Host.Link;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Search
{
    /// <summary>
    /// Search Everywhere (Rider): double Shift or the Go to … commands open one <see cref="CommandPalette{T}"/> with the
    /// tabs 全部 / 类型 / 符号 / 文件 / 资产（蓝图） / 操作. Created on first use and kept (instant re-open, remembered
    /// query, size and history). Double Shift while open toggles "include non-project items".
    /// </summary>
    internal sealed class SearchEverywhere
    {
        private static readonly Logger L = Log.For("Search");
        public const string FilterNonProject = "nonProject";
        public const string FilterReflected = "reflected";

        private readonly HostPackage _package;
        private SearchEverywhereSource _source;
        private CommandPalette<SearchRow> _palette;

        public SearchEverywhere(HostPackage package) => _package = package;

        public static SearchEverywhere Instance { get; private set; }

        public static void Initialize(HostPackage package) => Instance = new SearchEverywhere(package);

        public bool IsOpen => _palette?.IsVisible == true;

        /// <summary>Opens on <paramref name="tab"/>. Same tab again while open (or double Shift) toggles non-project items.</summary>
        public void Show(SearchTab tab, string text = null, string trigger = "command")
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            using (UiThread.Measure(L, "search.show", ("tab", tab.ToString()), ("trigger", trigger)))
            {
                EnsurePalette();
                string tabId = SearchEverywhereModel.TabId(tab);
                if (text == null && _palette.IsVisible && (_palette.CurrentTabId == tabId || trigger == "doubleShift"))
                {
                    _palette.ToggleFilter(FilterNonProject);
                    L.Info("search.nonProject.toggled", null, ("on", _palette.IsFilterOn(FilterNonProject)), ("trigger", trigger));
                    return;
                }
                if (text == null) text = VsEditor.SelectedText(VsEditor.ActiveTextView()); // Rider seeds with the selection
                IntPtr owner = VsMainWindow.Handle;
                _palette.OwnerHandle = owner;
                _palette.Show(owner != IntPtr.Zero ? PopupAnchor.CenterOf(owner) : null, text, tabId);
                L.Info("search.shown", null, ("tab", tabId), ("seeded", text != null), ("trigger", trigger),
                       ("engine", _package.Engine?.State.ToString() ?? "None"));
            }
        }

        private void EnsurePalette()
        {
            if (_palette != null) return;
            _source = new SearchEverywhereSource(_package, this);
            _palette = new CommandPalette<SearchRow>("uetovs.searchEverywhere", _source, new Size(820, 520));
            // Real editor view for the preview (VS + UEToVS colouring); the built-in plain preview stays the fallback.
            Safe.Execute(L, "search.preview.attach", () => EditorCodePreview.Attach(_palette.Preview));
        }

        internal void SelectTab(SearchTab tab) => _palette?.SelectTab(SearchEverywhereModel.TabId(tab));

        internal void SetText(string text) => _palette?.SetText(text);

        internal void Dismiss() => _palette?.Dismiss();

        /// <summary>Index finished etc.: refresh an open palette.</summary>
        public void Refresh() => _palette?.Refresh();
    }

    /// <summary><see cref="ICommandPaletteSource{T}"/> of Search Everywhere over the engine, the action registry and the navigation state.</summary>
    internal sealed class SearchEverywhereSource : ICommandPaletteSource<SearchRow>
    {
        private static readonly Logger L = Log.For("Search");
        private readonly HostPackage _package;
        private readonly SearchEverywhere _owner;

        public SearchEverywhereSource(HostPackage package, SearchEverywhere owner)
        {
            _package = package;
            _owner = owner;
            Tabs = SearchEverywhereModel.Tabs.Select(t =>
            {
                var tab = t;
                return new PaletteTab(SearchEverywhereModel.TabId(tab), () => SearchEverywhereModel.TabTitle(tab), tab == SearchTab.Actions ? 0 : 40)
                {
                    SupportsPreview = tab != SearchTab.Actions && tab != SearchTab.Assets,
                    Placeholder = () => FeatureStrings.SearchPlaceholder,
                    EmptyHint = () => tab == SearchTab.Assets ? FeatureStrings.AssetsEmptyHint : FeatureStrings.SearchEmptyHint,
                };
            }).ToList();
            Filters = new[]
            {
                new PaletteFilter(SearchEverywhere.FilterNonProject, () => FeatureStrings.FilterNonProject) { Shortcut = "Shift, Shift" },
                new PaletteFilter(SearchEverywhere.FilterReflected, () => FeatureStrings.FilterReflected),
            };
        }

        public IReadOnlyList<PaletteTab> Tabs { get; }

        public IReadOnlyList<PaletteFilter> Filters { get; }

        public IItemPresenter<SearchRow> Presenter => SearchRowPresenter.Instance;

        private SearchOptions Options(PaletteQuery q)
        {
            var s = _package.Settings?.Effective;
            return new SearchOptions
            {
                IncludeNonProject = q.IsFilterOn(SearchEverywhere.FilterNonProject),
                OnlyReflected = q.IsFilterOn(SearchEverywhere.FilterReflected),
                MaxResultsPerCategory = s?.SearchMaxResultsPerCategory ?? 60,
                RecentPaths = NavigationService.Instance.State.Recent.Items.Select(e => e.Path).ToList(),
                ProjectRoot = ProjectRoot(),
            };
        }

        private string ProjectRoot()
        {
            string uproject = _package.Unreal?.ProjectFile;
            return uproject != null ? System.IO.Path.GetDirectoryName(uproject) : _package.SolutionDirectory;
        }

        private List<SearchRow> ActionRows(string pattern, int max)
        {
            var ctx = _package.CreateActionContext(ActionSource.FindAction);
            return _package.Actions.Query(pattern, ctx, max)
                           .Where(m => !string.Equals(m.Action.Id, ActionIds.FindAction, StringComparison.OrdinalIgnoreCase))
                           .Select(SearchEverywhereModel.FromAction)
                           .ToList();
        }

        public async Task<PaletteResult<SearchRow>> QueryAsync(PaletteQuery query, IProgress<PaletteResult<SearchRow>> partial, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            string text = query.Text ?? string.Empty;
            var q = SearchQuery.Parse(text);
            var selected = SearchEverywhereModel.TabFromId(query.TabId) ?? SearchTab.All;
            var o = Options(query);
            SearchRowSet set;
            string outcome = "ok";

            if (q.IsPrefixCommandListing)
            {
                set = SearchEverywhereModel.PrefixRows(text);
                outcome = "prefix";
            }
            else
            {
                var tab = SearchEverywhereModel.EffectiveTab(q, selected);
                if (q.IsEmpty)
                {
                    set = tab == SearchTab.Actions
                        ? SearchEverywhereModel.BuildRows(q, tab, null, o, ActionRows(null, 200))
                        : SearchEverywhereModel.EmptyRows(tab, o);
                    outcome = "empty";
                }
                else
                {
                    var actions = tab == SearchTab.All ? ActionRows(q.Pattern, SearchEverywhereModel.AllTabGroupSize + 1)
                                : tab == SearchTab.Actions ? ActionRows(q.Pattern, 200)
                                : null;
                    var engine = _package.Engine;
                    if (!SearchEverywhereModel.NeedsEngine(tab))
                    {
                        set = SearchEverywhereModel.BuildRows(q, tab, null, o, actions);
                    }
                    else if (engine == null || !engine.IsConnected)
                    {
                        set = SearchEverywhereModel.BuildRows(q, tab == SearchTab.All ? SearchTab.All : tab, new SearchResponse(), o, actions);
                        set.Status = FeatureStrings.EngineNotReady;
                        outcome = "noEngine";
                    }
                    else
                    {
                        // Partial: actions first (instant), engine hits follow.
                        if (tab == SearchTab.All && actions != null && actions.Count > 0)
                            partial?.Report(new PaletteResult<SearchRow> { Items = SearchEverywhereModel.BuildRows(q, SearchTab.All, new SearchResponse { IndexReady = true }, o, actions).Rows, IsComplete = false });
                        var req = SearchEverywhereModel.BuildRequest(q, tab, o);
                        SearchResponse resp;
                        bool fellBack = false;
                        try
                        {
                            resp = await EngineQueries.SearchAsync(engine, req, ct).ConfigureAwait(true);
                            if (SearchEverywhereModel.ShouldFallBackToNonProject(o, resp))
                            {
                                req.IncludeNonProject = true;
                                resp = await EngineQueries.SearchAsync(engine, req, ct).ConfigureAwait(true);
                                fellBack = true;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is EngineUnavailableException || ex is TimeoutException || ex is StreamJsonRpc.RemoteInvocationException)
                        {
                            L.Warn("search.query.failed", "Engine search failed", ex, ("tab", tab.ToString()), ("len", text.Length));
                            set = SearchEverywhereModel.BuildRows(q, tab, new SearchResponse(), o, actions);
                            set.Status = FeatureStrings.SearchFailed(ex.Message);
                            return ToResult(set, "failed", query, sw);
                        }
                        set = SearchEverywhereModel.BuildRows(q, tab, resp, o, actions);
                        set.ShowingNonProjectFallback = fellBack;
                        set.Status = SearchEverywhereModel.StatusFor(resp, o, fellBack, engine.LastStatus);
                        L.Debug("search.engine", null, ("tab", tab.ToString()), ("hits", resp.Hits.Count), ("nonProject", resp.NonProjectHitCount),
                                ("engineMs", Math.Round(resp.ElapsedMs, 1)), ("indexReady", resp.IndexReady), ("fallback", fellBack));
                    }
                }
            }
            return ToResult(set, outcome, query, sw);
        }

        private static PaletteResult<SearchRow> ToResult(SearchRowSet set, string outcome, PaletteQuery query, Stopwatch sw)
        {
            L.Info("search.query", null, ("tab", query.TabId), ("len", query.Text?.Length ?? 0), ("rows", set.Rows.Count), ("outcome", outcome),
                   ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)), ("filters", query.ActiveFilters?.ToArray()));
            return new PaletteResult<SearchRow>
            {
                Items = set.Rows,
                TabCounts = set.TabCounts,
                IsComplete = true,
                Status = set.Status,
            };
        }

        public CodePreviewRequest GetPreview(SearchRow row)
        {
            if (row?.FilePath == null || row.Kind == SearchRowKind.Asset) return null;
            return new CodePreviewRequest
            {
                FilePath = row.FilePath,
                Line = Math.Max(0, row.Line) + 1,
                Column = row.Line >= 0 ? row.Column : 0,
                Length = row.Symbol?.Name != null && row.Line >= 0 ? row.Symbol.Name.Length : 0,
            };
        }

        public bool Invoke(SearchRow row, InvokeKind kind, PaletteQuery query, IReadOnlyList<SearchRow> all)
        {
            if (row == null) return false;
            L.Info("search.invoke", null, ("kind", row.Kind.ToString()), ("invoke", kind.ToString()), ("tab", query?.TabId), ("score", row.Score));
            if (kind == InvokeKind.OpenInResults && row.IsSelectable)
            {
                SearchResultsWindow.ShowResults(_package, all, query?.Text);
                return true;
            }
            switch (row.Kind)
            {
                case SearchRowKind.Header:
                case SearchRowKind.More:
                    if (row.TargetTab.HasValue) _owner.SelectTab(row.TargetTab.Value);
                    return false;
                case SearchRowKind.PrefixCommand:
                    _owner.SetText(row.Title + " ");
                    return false;
                case SearchRowKind.Hint:
                    return false;
                case SearchRowKind.Action:
                    {
                        string id = row.ActionId;
                        var ctx = _package.CreateActionContext(ActionSource.FindAction);
                        Safe.RunAsync(L, "search.action", async () =>
                        {
                            await Task.Yield(); // let the palette close first
                            if (!await _package.Actions.ExecuteAsync(id, ctx)) VsStatusBar.SetText(Strings.ActionFailed(row.Title));
                        }, ("id", id));
                        return true;
                    }
                case SearchRowKind.Asset:
                    EditorLinkService.Instance?.OpenAsset(row.Asset);
                    return true;
                default:
                    return Open(row, kind == InvokeKind.OpenInNewTab);
            }
        }

        internal static bool Open(SearchRow row, bool newTab)
        {
            if (row?.FilePath == null) return false;
            return NavigationService.Instance.JumpTo(row.FilePath, row.Line, row.Column, JumpReasons.Search, newTab);
        }
    }
}
