using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Bookmarks;
using UEToVS.Navigation;
using UEToVS.Search;
using UEToVS.UI;

namespace UEToVS.Bookmarks
{
    /// <summary>Rider bookmarks: F11 toggle, Ctrl+Shift+digit set mnemonic, Ctrl+digit jump, Shift+F11 list.</summary>
    internal static class BookmarkService
    {
        public static BookmarkStore Store { get; } = new BookmarkStore();

        public static void Toggle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var loc = VsHelpers.CurrentLocation();
            if (loc == null) return;
            bool added = Store.Toggle(loc);
            OutputLog.Status(added ? "UEToVS: 已添加书签" : "UEToVS: 已移除书签");
        }

        public static void SetDigit(int digit)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var loc = VsHelpers.CurrentLocation();
            if (loc == null) return;
            bool set = Store.SetMnemonic(loc, (char)('0' + digit));
            OutputLog.Status(set ? $"UEToVS: 书签 {digit} 已设置" : $"UEToVS: 书签 {digit} 已移除");
        }

        public static void GoToDigit(int digit)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var b = Store.Get((char)('0' + digit));
            if (b == null)
            {
                OutputLog.Status($"UEToVS: 没有书签 {digit}（Ctrl+Shift+{digit} 设置）");
                return;
            }
            NavigationService.Instance.NavigateTo(b.Location.FilePath, b.Location.Line, 0);
        }

        public static void ShowList(UEToVSPackage pkg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ListPopup popup = null;
            popup = ListPopup.Show("书签", filter =>
            {
                var items = Store.Items
                    .OrderBy(b => b.Mnemonic == null ? 1 : 0).ThenBy(b => b.Mnemonic)
                    .ThenBy(b => b.Location.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Location.Line)
                    .Select(b =>
                    {
                        string code = VsHelpers.ReadLines(b.Location.FilePath, b.Location.Line, 1).FirstOrDefault()?.Trim() ?? "";
                        var bm = b;
                        return new SearchItem
                        {
                            Kind = ItemKind.Recent,
                            Title = Path.GetFileName(b.Location.FilePath) + ":" + (b.Location.Line + 1),
                            Detail = code.Length > 80 ? code.Substring(0, 80) + "…" : code,
                            Badge = b.Mnemonic?.ToString() ?? "✓",
                            BadgeColor = b.Mnemonic != null ? BadgeColor.Orange : BadgeColor.Gray,
                            FilePath = b.Location.FilePath,
                            Line = b.Location.Line,
                            Symbol = null,
                            Activate = m =>
                            {
                                ThreadHelper.ThrowIfNotOnUIThread();
                                NavigationService.Instance.NavigateTo(bm.Location.FilePath, bm.Location.Line, 0, m == OpenMode.SplitGroup);
                                return true;
                            },
                        };
                    }).ToList();
                return ListPopup.Filter(items, filter);
            }, "Enter 跳转 · Delete 删除书签");
            popup.KeyHandler = e =>
            {
                if (e.Key != System.Windows.Input.Key.Delete) return false;
                var sel = popup.SelectedItem;
                var b = sel == null ? null : Store.Items.FirstOrDefault(x => x.Location.FilePath == sel.FilePath && x.Location.Line == sel.Line);
                if (b != null)
                {
                    Store.Remove(b);
                    popup.Refresh();
                }
                return true;
            };
        }

        public static void Load(string stateDir)
        {
            try
            {
                string path = stateDir == null ? null : Path.Combine(stateDir, "bookmarks.txt");
                if (path != null && File.Exists(path))
                    using (var r = new StreamReader(path)) Store.Load(r);
                else
                    Store.Load(new StringReader(""));
            }
            catch (Exception ex)
            {
                OutputLog.Write("Loading bookmarks failed: " + ex.Message);
            }
        }

        public static void Save(string stateDir)
        {
            if (stateDir == null) return;
            try
            {
                using (var w = new StreamWriter(Path.Combine(stateDir, "bookmarks.txt"))) Store.Save(w);
            }
            catch (Exception ex)
            {
                OutputLog.Write("Saving bookmarks failed: " + ex.Message);
            }
        }
    }
}
