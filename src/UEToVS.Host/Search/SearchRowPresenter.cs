using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Search;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;
using TextRange = UEToVS.UI.Logic.TextRange;

namespace UEToVS.Host.Search
{
    /// <summary>Maps a <see cref="SearchRow"/> to a UEToVS.UI list row (used by Search Everywhere and every list popup).</summary>
    internal sealed class SearchRowPresenter : IItemPresenter<SearchRow>, ISelectablePresenter<SearchRow>
    {
        public static readonly SearchRowPresenter Instance = new SearchRowPresenter();

        public void Present(SearchRow r, RowData row)
        {
            if (r == null) return;
            row.Primary = r.Title;
            row.PrimaryRanges = ToRanges(r.Highlights);
            row.Secondary = r.Detail;
            row.Right = r.Right;
            row.Shortcut = r.Shortcut;
            row.Glyph = r.Glyph;
            row.IsDimmed = r.IsNonProject || r.Kind == SearchRowKind.Hint;
            row.ToolTip = r.FilePath ?? r.Asset?.ObjectPath ?? r.ActionId;
            if (r.Kind == SearchRowKind.Header)
            {
                row.IsGroupHeader = true;
                row.GroupCount = r.GroupCount;
            }
            else if (r.Kind == SearchRowKind.Hint) row.IsEnabled = false;
            foreach (var b in r.Badges) row.AddBadge(b.Key, b.Value);
        }

        public bool IsSelectable(SearchRow r) => r != null && r.IsSelectable;

        private static IReadOnlyList<TextRange> ToRanges(int[] pairs)
        {
            if (pairs == null || pairs.Length < 2) return null;
            var list = new List<TextRange>(pairs.Length / 2);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                if (pairs[i + 1] > 0) list.Add(new TextRange(pairs[i], pairs[i + 1]));
            return list.Count == 0 ? null : list;
        }
    }

    /// <summary>
    /// List popups over <see cref="ChooserPopup{T}"/> (recent files/locations, bookmarks, related, file structure,
    /// Navigate To, Build.cs fixes). One instance per id is kept so re-opening is instant and the size is remembered.
    /// </summary>
    internal static class RowPopups
    {
        private static readonly Logger L = Log.For("Popups");
        private static readonly Dictionary<string, ChooserPopup<SearchRow>> Popups = new Dictionary<string, ChooserPopup<SearchRow>>();

        public static ChooserPopup<SearchRow> Get(string id, Size? size = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Popups.TryGetValue(id, out var p))
            {
                p = new ChooserPopup<SearchRow>(id, SearchRowPresenter.Instance, size ?? new Size(620, 400));
                Popups[id] = p;
            }
            return p;
        }

        public static bool IsVisible(string id) => Popups.TryGetValue(id, out var p) && p.Shell.IsVisible;

        /// <summary>Shows rows; completes with the pick (Chosen false when dismissed or replaced by a newer Show).</summary>
        public static Task<ChooserResult<SearchRow>> ShowAsync(string id, string title, IReadOnlyList<SearchRow> rows, bool filterable = true, Size? size = null) =>
            ShowAtAsync(id, title, rows, null, filterable ? (Func<SearchRow, string>)(r => (r.Title ?? string.Empty) + " " + (r.Detail ?? string.Empty)) : null, size);

        /// <summary>
        /// Like <see cref="ShowAsync"/> at <paramref name="anchor"/> (null = centered on VS, Rider's Search Everywhere spot);
        /// <paramref name="filterText"/> null hides the filter box. <paramref name="fitMaxWidth"/> above 0 sizes the popup to its
        /// rows (no cut paths) up to that width instead of the remembered size.
        /// </summary>
        public static async Task<ChooserResult<SearchRow>> ShowAtAsync(string id, string title, IReadOnlyList<SearchRow> rows, PopupAnchor anchor,
                                                                      Func<SearchRow, string> filterText, Size? size = null, double fitMaxWidth = 0)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var p = Get(id, size);
            p.FitToContentMaxWidth = fitMaxWidth;
            IntPtr owner = VsMainWindow.Handle;
            p.OwnerHandle = owner;
            L.Info("popup.show", null, ("id", id), ("rows", rows?.Count ?? 0), ("anchored", anchor != null));
            return await p.ShowAsync(rows, anchor ?? (owner != IntPtr.Zero ? PopupAnchor.CenterOf(owner) : null), title, filterText);
        }
    }
}
