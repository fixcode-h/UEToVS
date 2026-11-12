using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Navigation
{
    /// <summary>Recent Files (Ctrl+E), Recent Locations (Ctrl+Shift+E) and File Structure (Ctrl+F12) popups.</summary>
    internal static class NavigationPopups
    {
        private static readonly Logger L = Log.For("Navigation");
        private const string RecentFilesId = "uetovs.recentFiles";
        private const string RecentLocationsId = "uetovs.recentLocations";
        private static bool _recentEditedOnly;
        private static bool _locationsEditedOnly;
        private static bool _hooked;

        private static void HookKeys()
        {
            if (_hooked) return;
            _hooked = true;
            // Pressing the shortcut again inside the popup toggles "edited only" (Rider); VS commands do not reach popups.
            RowPopups.Get(RecentFilesId).Shell.PreviewKeyDown += (s, e) => Safe.Execute(L, "nav.recent.key", () =>
            {
                if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    _recentEditedOnly = !_recentEditedOnly;
                    _ = ShowRecentFilesCoreAsync();
                }
            });
            RowPopups.Get(RecentLocationsId, new System.Windows.Size(720, 520)).Shell.PreviewKeyDown += (s, e) => Safe.Execute(L, "nav.locations.key", () =>
            {
                if (e.Key == Key.E && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    e.Handled = true;
                    _locationsEditedOnly = !_locationsEditedOnly;
                    _ = ShowRecentLocationsCoreAsync();
                }
            });
        }

        public static async Task ShowRecentFilesAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            HookKeys();
            _recentEditedOnly = RowPopups.IsVisible(RecentFilesId) && !_recentEditedOnly;
            await ShowRecentFilesCoreAsync();
        }

        private static async Task ShowRecentFilesCoreAsync()
        {
            var package = HostPackage.Instance;
            var recent = NavigationService.Instance.State.Recent;
            var entries = (_recentEditedOnly ? recent.EditedOnly : recent.Items).Where(e => File.Exists(e.Path)).ToList();
            string current = VsEditor.CurrentLocation()?.FilePath;
            // Rider pre-selects the previous file: the current one goes last.
            var ordered = entries.Where(e => !string.Equals(e.Path, current, StringComparison.OrdinalIgnoreCase))
                                 .Concat(entries.Where(e => string.Equals(e.Path, current, StringComparison.OrdinalIgnoreCase)));
            var rows = ordered.Select(e => SearchEverywhereModel.RecentFileRow(e.Path, RelatedNavigator.ProjectRoot(package), e.Edited)).ToList();
            L.Info("nav.recentFiles", null, ("count", rows.Count), ("editedOnly", _recentEditedOnly));
            var pick = await RowPopups.ShowAsync(RecentFilesId, _recentEditedOnly ? FeatureStrings.RecentEditedFilesTitle : FeatureStrings.RecentFilesTitle, rows);
            if (pick.Chosen && pick.Item != null)
                NavigationService.Instance.JumpTo(pick.Item.FilePath, -1, 0, JumpReasons.RecentFile, pick.Kind == UI.Logic.InvokeKind.OpenInNewTab);
        }

        public static async Task ShowRecentLocationsAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            HookKeys();
            _locationsEditedOnly = RowPopups.IsVisible(RecentLocationsId) && !_locationsEditedOnly;
            await ShowRecentLocationsCoreAsync();
        }

        private static async Task ShowRecentLocationsCoreAsync()
        {
            var rows = new List<SearchRow>();
            foreach (var e in NavigationService.Instance.State.Locations.Items.Reverse())
            {
                if (_locationsEditedOnly && !e.IsEdit) continue;
                var loc = e.Location;
                if (!File.Exists(loc.FilePath)) continue;
                string code = e.Context ?? VsEditor.ReadLines(loc.FilePath, loc.Line, 1).FirstOrDefault()?.Trim();
                rows.Add(new SearchRow
                {
                    Kind = SearchRowKind.Recent,
                    Title = Path.GetFileName(loc.FilePath) + ":" + (loc.Line + 1),
                    Detail = code != null && code.Length > 100 ? code.Substring(0, 100) + "…" : code,
                    Right = (e.IsEdit ? FeatureStrings.EditMark + " · " : "") + e.Time.ToString("HH:mm"),
                    Glyph = UI.Logic.SymbolKind.File,
                    FilePath = loc.FilePath,
                    Line = loc.Line,
                    Column = loc.Column,
                });
            }
            L.Info("nav.recentLocations", null, ("count", rows.Count), ("editedOnly", _locationsEditedOnly));
            var pick = await RowPopups.ShowAsync(RecentLocationsId, _locationsEditedOnly ? FeatureStrings.RecentEditLocationsTitle : FeatureStrings.RecentLocationsTitle,
                                                 rows, true, new System.Windows.Size(720, 520));
            if (pick.Chosen && pick.Item != null)
                NavigationService.Instance.JumpTo(pick.Item.FilePath, pick.Item.Line, pick.Item.Column, JumpReasons.RecentLocation, pick.Kind == UI.Logic.InvokeKind.OpenInNewTab);
        }

        /// <summary>File Structure: declarations of the current file from the index, in file order.</summary>
        public static async Task ShowFileStructureAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var cur = VsEditor.CurrentLocation();
            if (cur == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoActiveDocument);
                return;
            }
            var symbols = await EngineQueries.GetFileSymbolsAsync(package.Engine, cur.FilePath, package.DisposalToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (symbols == null)
            {
                VsStatusBar.SetText(FeatureStrings.EngineUnavailable);
                return;
            }
            var rows = symbols.Where(s => s.Kind != Contracts.SymbolKind.EnumValue && s.Location != null)
                              .OrderBy(s => s.Location.Line)
                              .Select(s =>
                              {
                                  var r = SearchEverywhereModel.FromSymbol(new SearchHit { Symbol = s, Category = ResultCategory.Symbol }, null, false);
                                  r.Detail = s.Container;
                                  r.Right = ":" + (s.Location.Line + 1);
                                  r.IsNonProject = false;
                                  return r;
                              }).ToList();
            L.Info("nav.fileStructure", null, ("file", cur.FilePath), ("symbols", rows.Count));
            // Pre-select nothing special; typing filters (Rider).
            var pick = await RowPopups.ShowAsync("uetovs.fileStructure", FeatureStrings.FileStructureTitle(Path.GetFileName(cur.FilePath)), rows, true, new System.Windows.Size(560, 520));
            if (pick.Chosen && pick.Item != null)
                NavigationService.Instance.JumpTo(pick.Item.FilePath, pick.Item.Line, pick.Item.Column, JumpReasons.FileStructure);
        }
    }
}
