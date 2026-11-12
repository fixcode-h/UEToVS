using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Bookmarks;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Navigation;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;
using UiBadge = UEToVS.UI.Logic.BadgeKind;

namespace UEToVS.Host.Bookmarks
{
    /// <summary>
    /// Rider bookmarks: toggle (F11), mnemonic 0–9 set (Ctrl+Shift+digit) / jump (Ctrl+digit), list (Shift+F11, Delete
    /// removes). Glyphs in the editor margin follow edits (<see cref="BookmarkTagger"/>). Persisted per solution in
    /// &lt;solution&gt;/.vs/UEToVS/Navigation/bookmarks.txt. UI thread.
    /// </summary>
    internal static class BookmarkService
    {
        private static readonly Logger L = Log.For("Bookmarks");
        private const string ListId = "uetovs.bookmarks";
        private static string _file;
        private static bool _hooked;

        public static BookmarkStore Store { get; } = new BookmarkStore();

        public static void Toggle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var loc = VsEditor.CurrentLocation();
            if (loc == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoActiveDocument);
                return;
            }
            bool added = Store.Toggle(loc);
            L.Info("bookmark.toggle", null, ("file", loc.FilePath), ("line", loc.Line), ("added", added), ("count", Store.Items.Count));
            VsStatusBar.SetText(added ? FeatureStrings.BookmarkAdded : FeatureStrings.BookmarkRemoved);
            Save();
        }

        public static void SetDigit(int digit)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var loc = VsEditor.CurrentLocation();
            if (loc == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoActiveDocument);
                return;
            }
            bool set = Store.SetMnemonic(loc, (char)('0' + digit));
            L.Info("bookmark.digit.set", null, ("digit", digit), ("file", loc.FilePath), ("line", loc.Line), ("set", set));
            VsStatusBar.SetText(set ? FeatureStrings.BookmarkDigitSet(digit) : FeatureStrings.BookmarkDigitRemoved(digit));
            Save();
        }

        public static void GoToDigit(int digit)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var b = Store.Get((char)('0' + digit));
            if (b == null)
            {
                VsStatusBar.SetText(FeatureStrings.BookmarkDigitMissing(digit));
                return;
            }
            NavigationService.Instance.JumpTo(b.Location.FilePath, b.Location.Line, 0, JumpReasons.Bookmark);
        }

        private static List<SearchRow> Rows()
        {
            return Store.Items
                .OrderBy(b => b.Mnemonic == null ? 1 : 0).ThenBy(b => b.Mnemonic)
                .ThenBy(b => b.Location.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Location.Line)
                .Select(b =>
                {
                    string code = VsEditor.ReadLines(b.Location.FilePath, b.Location.Line, 1).FirstOrDefault()?.Trim() ?? string.Empty;
                    var row = new SearchRow
                    {
                        Kind = SearchRowKind.Recent,
                        Title = Path.GetFileName(b.Location.FilePath) + ":" + (b.Location.Line + 1),
                        Detail = code.Length > 100 ? code.Substring(0, 100) + "…" : code,
                        Right = b.Description,
                        Glyph = UI.Logic.SymbolKind.File,
                        FilePath = b.Location.FilePath,
                        Line = b.Location.Line,
                    };
                    row.Badges.Add(new KeyValuePair<string, UiBadge>(b.Mnemonic?.ToString() ?? "✓", b.Mnemonic != null ? UiBadge.Macro : UiBadge.Neutral));
                    return row;
                }).ToList();
        }


        public static async Task ShowListAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!_hooked)
            {
                _hooked = true;
                var popup = RowPopups.Get(ListId);
                popup.Shell.PreviewKeyDown += (s, e) => Safe.Execute(L, "bookmark.list.key", () =>
                {
                    if (e.Key != Key.Delete) return;
                    var row = popup.SelectedItem;
                    var b = row == null ? null : Store.Items.FirstOrDefault(x =>
                        string.Equals(x.Location.FilePath, row.FilePath, StringComparison.OrdinalIgnoreCase) && x.Location.Line == row.Line);
                    e.Handled = true;
                    if (b == null) return;
                    Store.Remove(b);
                    Save();
                    L.Info("bookmark.removed", null, ("file", b.Location.FilePath), ("line", b.Location.Line), ("left", Store.Items.Count));
                    if (Store.Items.Count == 0) popup.Shell.Dismiss();
                    else _ = ShowListAsync(); // re-show the updated list (the pending pick completes as "not chosen")
                });
            }
            var rows = Rows();
            L.Info("bookmark.list", null, ("count", rows.Count));
            if (rows.Count == 0)
            {
                VsStatusBar.SetText(FeatureStrings.NoBookmarks);
                return;
            }
            var pick = await RowPopups.ShowAsync(ListId, FeatureStrings.BookmarksTitle, rows);
            if (!pick.Chosen || pick.Item == null) return;
            NavigationService.Instance.JumpTo(pick.Item.FilePath, pick.Item.Line, 0, JumpReasons.Bookmark, pick.Kind == UI.Logic.InvokeKind.OpenInNewTab);
        }

        // ------------------------------------------------------------------ persistence

        public static void Open(string solutionDir)
        {
            _file = string.IsNullOrEmpty(solutionDir) ? null : Path.Combine(StoragePaths.ForSolution(solutionDir).Root, "Navigation", "bookmarks.txt");
            try
            {
                if (_file != null && File.Exists(_file))
                    using (var r = new StreamReader(_file)) Store.Load(r);
                else Store.Load(new StringReader(string.Empty));
                L.Info("bookmark.loaded", null, ("file", _file), ("count", Store.Items.Count));
            }
            catch (Exception ex)
            {
                L.Error("bookmark.load.failed", "Loading bookmarks failed", ex, ("file", _file));
            }
        }

        public static void Save()
        {
            if (_file == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                using (var w = new StreamWriter(_file)) Store.Save(w);
            }
            catch (Exception ex)
            {
                L.Error("bookmark.save.failed", "Saving bookmarks failed", ex, ("file", _file));
            }
        }

        public static void Close()
        {
            Save();
            _file = null;
            Store.Load(new StringReader(string.Empty));
        }
    }
}
