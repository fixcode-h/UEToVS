using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Protocol;
using UEToVS.Search;
using UEToVS.UI;

namespace UEToVS.Navigation
{
    /// <summary>Recent Files (Ctrl+E), Recent Locations (Ctrl+Shift+E) and File Structure (Ctrl+F12) popups.</summary>
    internal static class NavigationPopups
    {
        private static ListPopup _recentPopup;
        private static bool _editedOnly;

        public static string Relative(string path, string root)
        {
            if (path == null) return null;
            string dir = Path.GetDirectoryName(path) ?? "";
            if (!string.IsNullOrEmpty(root) && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                dir = dir.Substring(root.Length).TrimStart('\\', '/');
            return dir;
        }

        public static void ShowRecentFiles(UEToVSPackage pkg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_recentPopup != null && _recentPopup.IsVisible)
            {
                // Ctrl+E while open: toggle "show edited only" (Rider).
                _editedOnly = !_editedOnly;
                UpdateRecentTitle();
                _recentPopup.Refresh();
                return;
            }
            _editedOnly = false;
            var nav = NavigationService.Instance;
            string current = VsHelpers.CurrentLocation()?.FilePath;
            _recentPopup = ListPopup.Show("最近的文件", filter =>
            {
                var entries = (_editedOnly ? nav.Recent.EditedOnly : nav.Recent.Items).Where(e => File.Exists(e.Path)).ToList();
                // Rider pre-selects the previous file: put the current one last.
                var ordered = entries.Where(e => !string.Equals(e.Path, current, StringComparison.OrdinalIgnoreCase))
                    .Concat(entries.Where(e => string.Equals(e.Path, current, StringComparison.OrdinalIgnoreCase)));
                var items = ordered.Select(e => FileItem(pkg, e.Path, e.Edited)).ToList();
                return ListPopup.Filter(items, filter);
            }, "Enter 打开 · 再按 Ctrl+E 切换“仅显示已编辑” · Delete 从列表移除");
            UpdateRecentTitle();
            _recentPopup.KeyHandler = e =>
            {
                if (e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    _editedOnly = !_editedOnly;
                    UpdateRecentTitle();
                    _recentPopup.Refresh();
                    return true;
                }
                return false;
            };
        }

        private static void UpdateRecentTitle() =>
            _recentPopup?.SetTitle(_editedOnly ? "最近编辑的文件" : "最近的文件");

        public static SearchItem FileItem(UEToVSPackage pkg, string path, bool edited = false)
        {
            var (badge, color) = Badges.ForFile(path);
            return new SearchItem
            {
                Kind = ItemKind.File,
                Title = Path.GetFileName(path),
                Detail = Relative(path, pkg.ProjectRoot),
                Right = edited ? "已编辑" : null,
                Badge = badge,
                BadgeColor = color,
                FilePath = path,
                Line = -1,
                Activate = mode =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    NavigationService.Instance.NavigateTo(path, -1, 0, mode == OpenMode.SplitGroup);
                    return true;
                },
            };
        }

        public static void ShowRecentLocations(UEToVSPackage pkg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var nav = NavigationService.Instance;
            bool editedOnly = false;
            var snippets = new Dictionary<SearchItem, string[]>();
            ListPopup popup = null;
            popup = ListPopup.Show("最近的位置", filter =>
            {
                snippets.Clear();
                var items = new List<SearchItem>();
                foreach (var e in nav.Locations.Items.Reverse())
                {
                    if (editedOnly && !e.IsEdit) continue;
                    var loc = e.Location;
                    if (!File.Exists(loc.FilePath)) continue;
                    var lines = VsHelpers.ReadLines(loc.FilePath, Math.Max(0, loc.Line - 1), 3);
                    var (badge, color) = Badges.ForFile(loc.FilePath);
                    var item = new SearchItem
                    {
                        Kind = ItemKind.Recent,
                        Title = Path.GetFileName(loc.FilePath),
                        Detail = ":" + (loc.Line + 1),
                        Right = (e.IsEdit ? "编辑 · " : "") + e.Time.ToString("HH:mm"),
                        Badge = badge,
                        BadgeColor = color,
                        FilePath = loc.FilePath,
                        Line = loc.Line,
                        Activate = mode =>
                        {
                            ThreadHelper.ThrowIfNotOnUIThread();
                            nav.NavigateTo(loc.FilePath, loc.Line, loc.Column, mode == OpenMode.SplitGroup);
                            return true;
                        },
                    };
                    string all = string.Join("\n", lines);
                    if (!string.IsNullOrEmpty(filter) &&
                        item.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        all.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    snippets[item] = lines;
                    items.Add(item);
                }
                return items;
            }, "Enter 打开 · 再按 Ctrl+Shift+E 切换“仅显示编辑位置” · 输入可按文件名或代码过滤",
            item =>
            {
                if (!snippets.TryGetValue(item, out var lines) || lines.Length == 0) return null;
                var tb = new TextBlock
                {
                    Text = string.Join("\n", lines.Select(l => l.Replace("\t", "    "))),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Margin = new Thickness(22, 1, 0, 3),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                tb.SetResourceReference(TextBlock.ForegroundProperty, Theme.Gray);
                return tb;
            }, 720, 560);
            popup.KeyHandler = e =>
            {
                if (e.Key == Key.E && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    editedOnly = !editedOnly;
                    popup.SetTitle(editedOnly ? "最近编辑的位置" : "最近的位置");
                    popup.Refresh();
                    return true;
                }
                return false;
            };
        }

        /// <summary>File Structure: symbols of the current file from the index, in file order, with UE badges.</summary>
        public static void ShowFileStructure(UEToVSPackage pkg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cur = VsHelpers.CurrentLocation();
            if (cur == null)
            {
                OutputLog.Status("UEToVS: 没有打开的文件");
                return;
            }
            var symbols = new List<SymbolDto>();
            ListPopup popup = null;
            popup = ListPopup.Show("文件结构 — " + Path.GetFileName(cur.FilePath), filter => Items(), "正在读取…");

            List<SearchItem> Items()
            {
                var items = symbols
                    .Where(s => s.Kind != SymbolKind.EnumValue)
                    .OrderBy(s => s.Line)
                    .Select(s => SymbolItem(s, cur.FilePath))
                    .ToList();
                return ListPopup.Filter(items, popup?.FilterText?.Trim());
            }

            _ = pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                var index = await pkg.GetIndexAsync();
                List<SymbolDto> result = null;
                if (index != null)
                {
                    try
                    {
                        result = await index.GetFileSymbolsAsync(cur.FilePath, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        OutputLog.Write("File structure failed: " + ex.Message);
                    }
                }
                await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (result != null) symbols.AddRange(result);
                if (!popup.IsVisible) return;
                popup.SetTitle("文件结构 — " + Path.GetFileName(cur.FilePath) + (index == null ? "（索引未就绪）" : ""));
                popup.Refresh();
            });
        }

        public static SearchItem SymbolItem(SymbolDto s, string currentFile = null)
        {
            var (badge, color) = Badges.For(s);
            string ue = (s.Ue & UeFlags.UFunction) != 0 ? "UFUNCTION" :
                (s.Ue & UeFlags.UProperty) != 0 ? "UPROPERTY" :
                (s.Ue & (UeFlags.UClass | UeFlags.UStruct | UeFlags.UEnum)) != 0 ? "UE" : null;
            bool sameFile = currentFile != null && string.Equals(s.FilePath, currentFile, StringComparison.OrdinalIgnoreCase);
            return new SearchItem
            {
                Kind = ItemKind.Symbol,
                Title = s.Name,
                Detail = (string.IsNullOrEmpty(s.Container) ? "" : s.Container + "  ") + (sameFile ? "" : Path.GetFileName(s.FilePath)),
                Right = (ue != null ? ue + " " : "") + ":" + (s.Line + 1),
                Badge = badge,
                BadgeColor = color,
                FilePath = s.FilePath,
                Line = s.Line,
                Column = s.Column,
                Symbol = s,
                Activate = mode =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    NavigationService.Instance.NavigateTo(s.FilePath, s.Line, s.Column, mode == OpenMode.SplitGroup);
                    return true;
                },
            };
        }
    }
}
