using System;

namespace UEToVS.UI.Logic
{
    public enum NavKey
    {
        Up,
        Down,
        PageUp,
        PageDown,
        Home,
        End,
    }

    /// <summary>
    /// Keyboard-navigation and scroll state of a fixed-row-height list (no WPF). Skips non-selectable rows
    /// (group headers), keeps the selection visible, optional wrap-around at the ends for ↑/↓.
    /// </summary>
    public sealed class ListNavigator
    {
        private Func<int, bool> _isSelectable = _ => true;

        public int Count { get; private set; }

        /// <summary>-1 when nothing is selected.</summary>
        public int SelectedIndex { get; private set; } = -1;

        /// <summary>Index of the first fully or partially visible row.</summary>
        public double ScrollOffsetRows { get; private set; }

        /// <summary>Number of rows that fit in the viewport (may be fractional).</summary>
        public double ViewportRows { get; private set; } = 10;

        public bool WrapAround { get; set; } = true;

        public event Action SelectionChanged;

        public Func<int, bool> IsSelectable
        {
            get => _isSelectable;
            set => _isSelectable = value ?? (_ => true);
        }

        public double MaxScrollOffset => Math.Max(0, Count - ViewportRows);

        /// <summary>Resets for a new data set. Selects the first selectable row when <paramref name="selectFirst"/>.</summary>
        public void Reset(int count, bool selectFirst = true)
        {
            Count = Math.Max(0, count);
            ScrollOffsetRows = 0;
            SelectedIndex = selectFirst ? FindSelectable(0, +1) : -1;
            // Always notify: the item at the selected index is new even if the index is the same.
            SelectionChanged?.Invoke();
        }

        /// <summary>Updates the count keeping the selection when still valid (streaming results).</summary>
        public void UpdateCount(int count)
        {
            Count = Math.Max(0, count);
            ScrollOffsetRows = Clamp(ScrollOffsetRows, 0, MaxScrollOffset);
            bool invalid = SelectedIndex >= Count || (SelectedIndex >= 0 && !_isSelectable(SelectedIndex));
            if (invalid)
            {
                int i = FindSelectable(Math.Min(SelectedIndex, Count - 1), -1);
                if (i < 0) i = FindSelectable(0, +1);
                if (i < 0) { SelectedIndex = -1; SelectionChanged?.Invoke(); }
                else Select(i);
            }
            else if (SelectedIndex < 0 && Count > 0)
            {
                int i = FindSelectable(0, +1);
                if (i >= 0) Select(i);
            }
        }

        public void SetViewportRows(double rows)
        {
            ViewportRows = Math.Max(1, rows);
            ScrollOffsetRows = Clamp(ScrollOffsetRows, 0, MaxScrollOffset);
        }

        public void ScrollTo(double offsetRows)
        {
            ScrollOffsetRows = Clamp(offsetRows, 0, MaxScrollOffset);
        }

        public void ScrollBy(double rows) => ScrollTo(ScrollOffsetRows + rows);

        /// <summary>Selects <paramref name="index"/> (ignored if not selectable) and scrolls it into view.</summary>
        public bool Select(int index)
        {
            if (index < -1 || index >= Count) return false;
            if (index >= 0 && !_isSelectable(index)) return false;
            if (index == SelectedIndex) { EnsureVisible(index); return false; }
            SelectedIndex = index;
            EnsureVisible(index);
            SelectionChanged?.Invoke();
            return true;
        }

        public void EnsureVisible(int index)
        {
            if (index < 0 || index >= Count) return;
            int fullRows = Math.Max(1, (int)Math.Floor(ViewportRows));
            if (index < ScrollOffsetRows) ScrollTo(index);
            else if (index + 1 > ScrollOffsetRows + fullRows) ScrollTo(index + 1 - fullRows);
            // Show the group header above the first item of a group when scrolling up to it.
            if (index > 0 && index - 1 < ScrollOffsetRows && !_isSelectable(index - 1) && index - 1 >= 0 && ScrollOffsetRows > index - 1)
                ScrollTo(index - 1);
        }

        /// <summary>Applies a navigation key. Returns true when the selection changed.</summary>
        public bool Move(NavKey key)
        {
            if (Count == 0) return false;
            int page = Math.Max(1, (int)Math.Floor(ViewportRows) - 1);
            int cur = SelectedIndex;
            int target;
            switch (key)
            {
                case NavKey.Up:
                    if (cur < 0) target = FindSelectable(Count - 1, -1);
                    else
                    {
                        target = FindSelectable(cur - 1, -1);
                        if (target < 0) target = WrapAround ? FindSelectable(Count - 1, -1) : cur;
                    }
                    break;
                case NavKey.Down:
                    if (cur < 0) target = FindSelectable(0, +1);
                    else
                    {
                        target = FindSelectable(cur + 1, +1);
                        if (target < 0) target = WrapAround ? FindSelectable(0, +1) : cur;
                    }
                    break;
                case NavKey.PageUp:
                    target = FindSelectable(Math.Max(0, cur - page), -1);
                    if (target < 0) target = FindSelectable(0, +1);
                    break;
                case NavKey.PageDown:
                    target = FindSelectable(Math.Min(Count - 1, Math.Max(cur, 0) + page), +1);
                    if (target < 0) target = FindSelectable(Count - 1, -1);
                    break;
                case NavKey.Home:
                    target = FindSelectable(0, +1);
                    if (target >= 0) { ScrollTo(0); }
                    break;
                case NavKey.End:
                    target = FindSelectable(Count - 1, -1);
                    break;
                default:
                    return false;
            }
            if (target < 0) return false;
            if (target == cur) { EnsureVisible(cur); return false; }
            return Select(target);
        }

        /// <summary>First selectable index starting at <paramref name="from"/> going in <paramref name="step"/> direction, or -1.</summary>
        public int FindSelectable(int from, int step)
        {
            if (Count == 0) return -1;
            for (int i = from; i >= 0 && i < Count; i += step)
                if (_isSelectable(i)) return i;
            return -1;
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    }
}
