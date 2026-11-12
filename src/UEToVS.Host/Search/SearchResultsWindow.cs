using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Link;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Search;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;

namespace UEToVS.Host.Search
{
    /// <summary>
    /// "UEToVS 搜索结果" tool window (Rider "Open in Find Tool Window", Ctrl+Enter in Search Everywhere): the results of a
    /// search grouped by kind in UEToVS.UI's <see cref="ResultsPanel{T}"/>, with preview. Items stay after the palette closes.
    /// </summary>
    [Guid(GuidString)]
    public sealed class SearchResultsWindow : ToolWindowPane
    {
        public const string GuidString = "5f0f1f53-6a43-4f63-9a64-0b1f6b3c2d7e";
        private static readonly Logger L = Log.For("Search");
        private readonly ResultsPanel<SearchRow> _panel;

        public SearchResultsWindow() : base(null)
        {
            Caption = FeatureStrings.ResultsCaption;
            _panel = new ResultsPanel<SearchRow>(SearchRowPresenter.Instance)
            {
                PreviewProvider = r => r?.FilePath == null || r.Kind == SearchRowKind.Asset ? null : new CodePreviewRequest
                {
                    FilePath = r.FilePath,
                    Line = Math.Max(0, r.Line) + 1,
                    Column = r.Line >= 0 ? r.Column : 0,
                },
            };
            Safe.Execute(L, "search.results.preview.attach", () => EditorCodePreview.Attach(_panel.Preview));
            _panel.ItemInvoked += (s, e) => Safe.Execute(L, "search.results.invoke", () =>
            {
                var row = e.Item;
                if (row == null || !row.IsSelectable) return;
                if (row.Kind == SearchRowKind.Asset) EditorLinkService.Instance?.OpenAsset(row.Asset);
                else SearchEverywhereSource.Open(row, e.Kind == InvokeKind.OpenInNewTab);
            });
            Content = _panel.Root;
        }

        internal void SetResults(IReadOnlyList<SearchRow> rows, string query)
        {
            var roots = new List<TreeNode<SearchRow>>();
            TreeNode<SearchRow> group = null;
            foreach (var r in rows ?? Array.Empty<SearchRow>())
            {
                if (r.Kind == SearchRowKind.Header)
                {
                    group = new TreeNode<SearchRow>(r, new List<TreeNode<SearchRow>>());
                    roots.Add(group);
                }
                else if (r.IsSelectable && r.Kind != SearchRowKind.More && r.Kind != SearchRowKind.PrefixCommand)
                {
                    if (group != null) group.Children.Add(new TreeNode<SearchRow>(r));
                    else roots.Add(new TreeNode<SearchRow>(r));
                }
            }
            foreach (var g in roots) g.InvalidateCount();
            roots.RemoveAll(n => n.Item.Kind == SearchRowKind.Header && !n.HasChildren);
            int count = rows?.Count(r => r.IsSelectable && r.Kind != SearchRowKind.More) ?? 0;
            _panel.SetResults(roots, FeatureStrings.ResultsSummary(query ?? string.Empty, count));
        }

        /// <summary>Opens the window with <paramref name="rows"/> (UI thread).</summary>
        internal static void ShowResults(HostPackage package, IReadOnlyList<SearchRow> rows, string query)
        {
            Safe.RunAsync(L, "search.results.show", async () =>
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                var window = await package.ShowToolWindowAsync(typeof(SearchResultsWindow), 0, true, package.DisposalToken) as SearchResultsWindow;
                if (window == null)
                {
                    L.Error("search.results.create.failed", "Cannot create the search results tool window", null);
                    return;
                }
                window.SetResults(rows?.ToList(), query);
                L.Info("search.results.shown", null, ("rows", rows?.Count ?? 0));
            });
        }
    }
}
