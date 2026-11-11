using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UEToVS.Search;

namespace UEToVS.UI
{
    /// <summary>Keyboard-driven result list with headers, badges and highlighted matches.</summary>
    internal sealed class ResultList : ScrollViewer
    {
        private readonly StackPanel _panel = new StackPanel();
        private readonly List<Border> _rows = new List<Border>();
        private List<SearchItem> _items = new List<SearchItem>();
        private int _selected = -1;

        public ResultList()
        {
            Content = _panel;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Focusable = false;
        }

        public event Action<SearchItem, OpenMode> ItemActivated;
        public event Action<SearchItem> SelectionChanged;

        /// <summary>Extra lines under the title (Recent Locations shows code snippets).</summary>
        public Func<SearchItem, UIElement> ExtraContent { get; set; }

        public IReadOnlyList<SearchItem> Items => _items;

        public SearchItem SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        public int SelectedIndex => _selected;

        public void SetItems(List<SearchItem> items, int keepSelectionIndex = 0)
        {
            _items = items ?? new List<SearchItem>();
            _panel.Children.Clear();
            _rows.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                var row = BuildRow(_items[i], i);
                _rows.Add(row);
                _panel.Children.Add(row);
            }
            _selected = -1;
            int first = NextSelectable(Math.Max(0, keepSelectionIndex) - 1, 1);
            Select(first);
            ScrollToTop();
        }

        private Border BuildRow(SearchItem item, int index)
        {
            var border = new Border { Padding = new Thickness(8, 3, 8, 3), Tag = index };
            if (item.Kind == ItemKind.Header)
            {
                border.Padding = new Thickness(8, 6, 8, 2);
                var header = Theme.Text(item.Title, Theme.Gray, bold: true);
                header.FontSize = Math.Max(10, header.FontSize - 1);
                border.Child = header;
                return border;
            }
            if (item.Kind == ItemKind.Hint)
            {
                border.Child = Theme.Text(item.Title, Theme.Gray);
                border.Padding = new Thickness(12, 8, 8, 8);
                return border;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = Theme.Badge(item.Badge, item.BadgeColor);
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var middle = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            var line = new DockPanel { LastChildFill = true };
            var title = Theme.Highlighted(item.Title, item.Highlights, item.Dimmed);
            DockPanel.SetDock(title, Dock.Left);
            line.Children.Add(title);
            if (!string.IsNullOrEmpty(item.Detail))
            {
                var detail = Theme.Text("  " + item.Detail, Theme.Gray);
                line.Children.Add(detail);
            }
            middle.Children.Add(line);
            var extra = ExtraContent?.Invoke(item);
            if (extra != null) middle.Children.Add(extra);
            Grid.SetColumn(middle, 1);
            grid.Children.Add(middle);

            if (!string.IsNullOrEmpty(item.Right))
            {
                var right = Theme.Text(item.Right, Theme.Gray);
                right.Margin = new Thickness(12, 0, 0, 0);
                right.MaxWidth = 260;
                Grid.SetColumn(right, 2);
                grid.Children.Add(right);
            }

            border.Child = grid;
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonUp += (s, e) =>
            {
                Select(index);
                var mode = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? OpenMode.SplitGroup
                         : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? OpenMode.KeepOpen : OpenMode.Normal;
                ItemActivated?.Invoke(item, mode);
                e.Handled = true;
            };
            return border;
        }

        public void Select(int index)
        {
            if (_selected >= 0 && _selected < _rows.Count) SetRowSelected(_rows[_selected], false);
            _selected = index;
            if (_selected >= 0 && _selected < _rows.Count)
            {
                SetRowSelected(_rows[_selected], true);
                _rows[_selected].BringIntoView();
            }
            SelectionChanged?.Invoke(SelectedItem);
        }

        private static void SetRowSelected(Border row, bool selected)
        {
            if (selected) row.SetResourceReference(Border.BackgroundProperty, Theme.SelectedBackground);
            else row.ClearValue(Border.BackgroundProperty);
            foreach (var tb in FindTextBlocks(row))
            {
                if (selected) tb.SetResourceReference(TextBlock.ForegroundProperty, Theme.SelectedForeground);
                else if (tb.Tag is object key) tb.SetResourceReference(TextBlock.ForegroundProperty, key);
            }
        }

        private static IEnumerable<TextBlock> FindTextBlocks(UIElement root)
        {
            switch (root)
            {
                case TextBlock tb:
                    if (tb.Tag != null) yield return tb;
                    break;
                case Border b when b.Width != 18 && b.Child != null: // 18 = badge, keep its white text
                    foreach (var t in FindTextBlocks(b.Child)) yield return t;
                    break;
                case Panel p:
                    foreach (UIElement c in p.Children)
                        foreach (var t in FindTextBlocks(c)) yield return t;
                    break;
            }
        }

        public void Move(int delta)
        {
            if (_items.Count == 0) return;
            int next = NextSelectable(_selected, Math.Sign(delta));
            for (int i = 1; i < Math.Abs(delta) && next >= 0; i++)
            {
                int n = NextSelectable(next, Math.Sign(delta));
                if (n < 0) break;
                next = n;
            }
            if (next < 0)
            {
                // wrap around like Rider
                next = delta > 0 ? NextSelectable(-1, 1) : NextSelectable(_items.Count, -1);
            }
            if (next >= 0) Select(next);
        }

        private int NextSelectable(int from, int dir)
        {
            for (int i = from + dir; i >= 0 && i < _items.Count; i += dir)
                if (_items[i].IsSelectable) return i;
            return -1;
        }

        public void ActivateSelected(OpenMode mode)
        {
            var item = SelectedItem;
            if (item != null) ItemActivated?.Invoke(item, mode);
        }
    }
}
