using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Matching;
using UEToVS.Search;

namespace UEToVS.UI
{
    /// <summary>
    /// Small Rider-style popup list with speed search: Recent Files, Recent Locations, File Structure, Bookmarks,
    /// related symbols and Navigate To all use it. Typing filters; Enter opens; Esc closes.
    /// </summary>
    internal sealed class ListPopup : Window
    {
        private readonly TextBox _filter = new TextBox();
        private readonly TextBlock _title = new TextBlock();
        private readonly TextBlock _footer = new TextBlock();
        private readonly ResultList _list = new ResultList();
        private Func<string, List<SearchItem>> _source;

        private ListPopup()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SetResourceReference(BackgroundProperty, Theme.Background);
            SetResourceReference(ForegroundProperty, Theme.Foreground);
            Theme.ApplyWindowFont(this);

            var root = new Border { BorderThickness = new Thickness(1) };
            root.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            var dock = new DockPanel();
            _title.Margin = new Thickness(8, 5, 8, 3);
            _title.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(_title, Dock.Top);
            dock.Children.Add(_title);
            _filter.Margin = new Thickness(6, 0, 6, 4);
            _filter.Padding = new Thickness(3, 2, 3, 2);
            _filter.SetResourceReference(BackgroundProperty, Theme.InputBackground);
            _filter.SetResourceReference(ForegroundProperty, Theme.InputForeground);
            _filter.SetResourceReference(BorderBrushProperty, Theme.InputBorder);
            _filter.SetResourceReference(TextBox.CaretBrushProperty, Theme.InputForeground);
            InputMethod.SetIsInputMethodEnabled(_filter, false);
            DockPanel.SetDock(_filter, Dock.Top);
            dock.Children.Add(_filter);
            _footer.Margin = new Thickness(8, 2, 8, 4);
            _footer.SetResourceReference(TextBlock.ForegroundProperty, Theme.Gray);
            DockPanel.SetDock(_footer, Dock.Bottom);
            dock.Children.Add(_footer);
            dock.Children.Add(_list);
            root.Child = dock;
            Content = root;

            _title.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            _filter.TextChanged += (s, e) => Refresh();
            _filter.PreviewKeyDown += OnKey;
            _list.ItemActivated += Activate;
            Deactivated += (s, e) => { if (IsVisible) Close(); };
        }

        /// <summary>Extra key handling (e.g. Ctrl+E again in Recent Files). Return true when handled.</summary>
        public Func<KeyEventArgs, bool> KeyHandler { get; set; }

        public string FilterText => _filter.Text;

        public SearchItem SelectedItem => _list.SelectedItem;

        public static ListPopup Show(string title, Func<string, List<SearchItem>> source, string footer = null,
            Func<SearchItem, UIElement> extra = null, double width = 620, double height = 440)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var p = new ListPopup { _source = source, Width = width, Height = height };
            p._title.Text = title;
            p._footer.Text = footer ?? "Enter 打开 · Shift+Enter 在新标签组打开 · Esc 关闭";
            p._list.ExtraContent = extra;
            p.Place();
            new WindowInteropHelper(p) { Owner = VsHelpers.MainWindowHandle };
            p.Refresh();
            p.Show();
            p.Activate();
            p._filter.Focus();
            return p;
        }

        public void SetTitle(string title) => _title.Text = title;

        public void Refresh()
        {
            List<SearchItem> items;
            try
            {
                items = _source?.Invoke(_filter.Text.Trim()) ?? new List<SearchItem>();
            }
            catch (Exception ex)
            {
                OutputLog.Write("List popup failed: " + ex);
                items = new List<SearchItem>();
            }
            if (items.Count == 0)
                items.Add(new SearchItem { Kind = ItemKind.Hint, Title = string.IsNullOrEmpty(_filter.Text) ? "（空）" : "没有匹配项" });
            _list.SetItems(items);
        }

        /// <summary>Replaces the item source (e.g. async results arriving) and refreshes.</summary>
        public void SetSource(Func<string, List<SearchItem>> source)
        {
            _source = source;
            Refresh();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (KeyHandler != null && KeyHandler(e))
            {
                e.Handled = true;
                return;
            }
            switch (e.Key)
            {
                case Key.Escape: Close(); e.Handled = true; break;
                case Key.Down: _list.Move(1); e.Handled = true; break;
                case Key.Up: _list.Move(-1); e.Handled = true; break;
                case Key.PageDown: _list.Move(10); e.Handled = true; break;
                case Key.PageUp: _list.Move(-10); e.Handled = true; break;
                case Key.Enter:
                    _list.ActivateSelected((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? OpenMode.SplitGroup : OpenMode.Normal);
                    e.Handled = true;
                    break;
            }
        }

        private void Activate(SearchItem item, OpenMode mode)
        {
            if (item?.Activate == null) return;
            Close();
            item.Activate(mode);
        }

        /// <summary>Filters by the fuzzy matcher, keeping the original order for equal kinds (Rider speed search).</summary>
        public static List<SearchItem> Filter(IEnumerable<SearchItem> items, string pattern, bool sortByScore = false)
        {
            if (string.IsNullOrEmpty(pattern)) return items.ToList();
            var m = new FuzzyMatcher(pattern);
            var result = new List<SearchItem>();
            foreach (var it in items)
            {
                var r = m.MatchUnreal(it.Title);
                if (!r.IsMatch) continue;
                it.Highlights = r.Ranges.SelectMany(x => new[] { x.Start, x.Length }).ToArray();
                it.Score = r.Score;
                result.Add(it);
            }
            return sortByScore ? result.OrderByDescending(x => x.Score).ToList() : result;
        }

        private void Place()
        {
            Rect owner = SystemParameters.WorkArea;
            var hwnd = VsHelpers.MainWindowHandle;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r))
            {
                var source = HwndSource.FromHwnd(hwnd);
                double scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                owner = new Rect(r.Left * scale, r.Top * scale, (r.Right - r.Left) * scale, (r.Bottom - r.Top) * scale);
            }
            Width = Math.Min(Width, owner.Width);
            Height = Math.Min(Height, owner.Height);
            Left = owner.Left + (owner.Width - Width) / 2;
            Top = owner.Top + owner.Height * 0.15;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    }
}
