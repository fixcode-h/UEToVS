using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Molecules
{
    /// <summary>
    /// Fixed-row-height virtualized list engine. Only the visible rows exist (pool ≈ viewport / row height + 1);
    /// a row is reused for item <c>i</c> in slot <c>i % pool</c>, so scrolling by one row rebinds one row.
    /// Pixel-smooth scrolling, slim overlay scrollbar, hover, click/double-click, keyboard navigation via
    /// <see cref="ListNavigator"/> (↑↓ PgUp/PgDn Home/End, Enter / Shift+Enter / Ctrl+Enter).
    /// Row height comes from the Row.Height token (live). Subclasses supply the rows and the data binding.
    /// </summary>
    public abstract class VirtualListCore : Panel
    {
        public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
            nameof(RowHeight), typeof(double), typeof(VirtualListCore),
            new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((VirtualListCore)d).OnRowHeightChanged()));

        private readonly List<FrameworkElement> _pool = new List<FrameworkElement>();
        private readonly ScrollRail _rail = new ScrollRail { Orientation = Orientation.Vertical };
        private int _hover = -1;
        private bool _alwaysActive;

        protected VirtualListCore()
        {
            Navigator = new ListNavigator();
            Navigator.IsSelectable = i => IsSelectableCore(i);
            Navigator.SelectionChanged += () => Ui.Safe("ui.list.selection", OnNavigatorSelectionChanged);
            ClipToBounds = true;
            Focusable = true;
            FocusVisualStyle = null;
            Background = Brushes.Transparent;
            this.Bind(RowHeightProperty, Tokens.RowHeight);
            Children.Add(_rail);
            Panel.SetZIndex(_rail, 10);
            _rail.UserScrolled += v => Ui.Safe("ui.list.rail", () => { Navigator.ScrollTo(v); InvalidateMeasure(); });
            KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
            IsKeyboardFocusWithinChanged += (s, e) => Ui.Safe("ui.list.focus", RefreshStates);
        }

        public ListNavigator Navigator { get; }

        public double RowHeight
        {
            get => (double)GetValue(RowHeightProperty);
            set => SetValue(RowHeightProperty, value);
        }

        public int Count => Navigator.Count;

        public int SelectedIndex
        {
            get => Navigator.SelectedIndex;
            set
            {
                Navigator.Select(value);
                InvalidateMeasure();
            }
        }

        /// <summary>Draw the selection as active even without keyboard focus (focus stays in a search box).</summary>
        public bool AlwaysActive
        {
            get => _alwaysActive;
            set
            {
                _alwaysActive = value;
                RefreshStates();
            }
        }

        /// <summary>Take keyboard focus on click (false inside popups where the search box keeps focus).</summary>
        public bool FocusOnClick { get; set; } = true;

        public bool WrapAround
        {
            get => Navigator.WrapAround;
            set => Navigator.WrapAround = value;
        }

        public event EventHandler SelectionChanged;

        /// <summary>Right-click on a row (index).</summary>
        public event EventHandler<int> RowContextRequested;

        protected abstract FrameworkElement CreateRow();

        protected abstract void BindRow(FrameworkElement row, int index);

        protected abstract bool IsSelectableCore(int index);

        /// <summary>Enter variants / double-click on a selectable row.</summary>
        protected abstract void OnInvoke(int index, InvokeKind kind);

        /// <summary>←/→ (tree expand/collapse). Return true when handled.</summary>
        protected virtual bool OnHorizontalKey(bool right) => false;

        /// <summary>Click on a row at a position (tree expander). Return true when handled.</summary>
        protected virtual bool OnRowClick(FrameworkElement row, int index, Point posInRow) => false;

        protected virtual void OnSelectionChangedCore() { }

        /// <summary>Resets the data count (selects the first selectable row).</summary>
        protected void ResetCount(int count, bool selectFirst = true)
        {
            InvalidateAllRows();
            Navigator.Reset(count, selectFirst);
            InvalidateMeasure();
        }

        /// <summary>Count changed but existing items kept their positions (streaming append).</summary>
        protected void UpdateCount(int count)
        {
            InvalidateAllRows();
            Navigator.UpdateCount(count);
            InvalidateMeasure();
        }

        /// <summary>Forces every realized row to rebind (data changed in place).</summary>
        public void InvalidateAllRows()
        {
            foreach (var r in _pool)
                if (r is IVirtualRow vr) vr.BoundIndex = -1;
            InvalidateMeasure();
        }

        public void ScrollIntoView(int index)
        {
            Navigator.EnsureVisible(index);
            InvalidateMeasure();
        }

        private void OnRowHeightChanged()
        {
            UpdateViewport(ActualHeight);
            InvalidateAllRows();
        }

        private void OnNavigatorSelectionChanged()
        {
            RefreshStates();
            InvalidateMeasure();
            OnSelectionChangedCore();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool IsActive => _alwaysActive || IsKeyboardFocusWithin;

        private void RefreshStates()
        {
            bool active = IsActive;
            foreach (var r in _pool)
                if (r is IVirtualRow vr && vr.BoundIndex >= 0)
                    vr.SetState(vr.BoundIndex == Navigator.SelectedIndex, vr.BoundIndex == _hover, active);
        }

        private void UpdateViewport(double height)
        {
            double rh = Math.Max(4, RowHeight);
            if (height > 0 && !double.IsInfinity(height)) Navigator.SetViewportRows(height / rh);
        }

        protected override Size MeasureOverride(Size available)
        {
            double rh = Math.Max(4, RowHeight);
            double width = double.IsInfinity(available.Width) ? 300 : available.Width;
            double height = double.IsInfinity(available.Height) ? Math.Min(Count, 12) * rh : available.Height;
            UpdateViewport(height);

            int need = Math.Max(1, (int)Math.Ceiling(height / rh) + 1);
            while (_pool.Count < need)
            {
                var row = CreateRow();
                if (row is IVirtualRow vr) vr.BoundIndex = -1;
                _pool.Add(row);
                Children.Add(row);
            }
            if (_pool.Count > need + 8)
            {
                // Shrink after a big resize; keep a small slack to avoid churn.
                for (int i = _pool.Count - 1; i >= need; i--)
                {
                    Children.Remove(_pool[i]);
                    _pool.RemoveAt(i);
                }
            }
            // Slot mapping depends on pool size.
            if (_lastPoolSize != _pool.Count)
            {
                _lastPoolSize = _pool.Count;
                foreach (var r in _pool)
                    if (r is IVirtualRow vr) vr.BoundIndex = -1;
            }

            int first = (int)Math.Floor(Navigator.ScrollOffsetRows);
            bool active = IsActive;
            for (int k = 0; k < _pool.Count; k++)
            {
                int idx = first + k;
                if (idx >= Count) break;
                var row = _pool[idx % _pool.Count];
                var vr = row as IVirtualRow;
                if (vr != null && vr.BoundIndex != idx)
                {
                    BindRow(row, idx);
                    vr.BoundIndex = idx;
                }
                vr?.SetState(idx == Navigator.SelectedIndex, idx == _hover, active);
                row.Measure(new Size(width, rh));
            }
            _rail.Measure(new Size(width, height));
            _rail.Update(Navigator.ScrollOffsetRows, Navigator.MaxScrollOffset, Navigator.ViewportRows);
            return new Size(double.IsInfinity(available.Width) ? width : available.Width, height);
        }

        private int _lastPoolSize = -1;

        protected override Size ArrangeOverride(Size final)
        {
            double rh = Math.Max(4, RowHeight);
            double offset = Navigator.ScrollOffsetRows;
            int first = (int)Math.Floor(offset);
            var placed = new HashSet<FrameworkElement>();
            for (int k = 0; k < _pool.Count; k++)
            {
                int idx = first + k;
                if (idx >= Count) break;
                var row = _pool[idx % _pool.Count];
                placed.Add(row);
                row.Arrange(new Rect(0, Math.Round((idx - offset) * rh), final.Width, rh));
            }
            foreach (var row in _pool)
            {
                if (placed.Contains(row)) continue;
                row.Arrange(new Rect(0, -rh * 4, final.Width, rh)); // parked out of view (clipped)
                if (row is IVirtualRow vr) vr.BoundIndex = -1;
            }
            _rail.Arrange(new Rect(Math.Max(0, final.Width - _rail.DesiredSize.Width), 0, _rail.DesiredSize.Width, final.Height));
            return final;
        }

        private int IndexAt(Point p)
        {
            double rh = Math.Max(4, RowHeight);
            int idx = (int)Math.Floor(p.Y / rh + Navigator.ScrollOffsetRows);
            return idx >= 0 && idx < Count ? idx : -1;
        }

        private FrameworkElement RowFor(int index)
        {
            foreach (var r in _pool)
                if (r is IVirtualRow vr && vr.BoundIndex == index) return r;
            return null;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Handled) return;
            Ui.Safe("ui.list.wheel", () =>
            {
                int lines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
                Navigator.ScrollBy(-e.Delta / 120.0 * lines);
                UpdateHover(e.GetPosition(this));
                InvalidateMeasure();
                e.Handled = true;
            });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Ui.Safe("ui.list.move", () => UpdateHover(e.GetPosition(this)));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            Ui.Safe("ui.list.leave", () => SetHover(-1));
        }

        private void UpdateHover(Point p)
        {
            if (p.X >= ActualWidth - _rail.ActualWidth && _rail.IsScrollable) { SetHover(-1); return; }
            int idx = IndexAt(p);
            SetHover(idx >= 0 && IsSelectableCore(idx) ? idx : -1);
        }

        private void SetHover(int idx)
        {
            if (idx == _hover) return;
            _hover = idx;
            RefreshStates();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.Handled) return;
            Ui.Safe("ui.list.click", () =>
            {
                var p = e.GetPosition(this);
                int idx = IndexAt(p);
                if (FocusOnClick) Focus();
                if (idx < 0) return;
                var row = RowFor(idx);
                if (row != null && OnRowClick(row, idx, e.GetPosition(row))) { e.Handled = true; return; }
                if (!IsSelectableCore(idx)) return;
                Navigator.Select(idx);
                InvalidateMeasure();
                if (e.ClickCount == 2)
                {
                    var kind = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? InvokeKind.OpenInNewTab : InvokeKind.Open;
                    OnInvoke(idx, kind);
                }
                e.Handled = true;
            });
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            Ui.Safe("ui.list.rclick", () =>
            {
                int idx = IndexAt(e.GetPosition(this));
                if (idx < 0 || !IsSelectableCore(idx)) return;
                Navigator.Select(idx);
                InvalidateMeasure();
                RowContextRequested?.Invoke(this, idx);
                e.Handled = true;
            });
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            Ui.Safe("ui.list.key", () =>
            {
                if (HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) e.Handled = true;
            });
        }

        /// <summary>
        /// Applies a navigation/invoke key. Owners whose focus stays elsewhere (search box) forward keys here.
        /// Returns true when the key was consumed.
        /// </summary>
        public bool HandleKey(Key key, ModifierKeys mods)
        {
            bool changed;
            switch (key)
            {
                case Key.Up: Navigator.Move(NavKey.Up); break;
                case Key.Down: Navigator.Move(NavKey.Down); break;
                case Key.PageUp: Navigator.Move(NavKey.PageUp); break;
                case Key.PageDown: Navigator.Move(NavKey.PageDown); break;
                case Key.Home: Navigator.Move(NavKey.Home); break;
                case Key.End: Navigator.Move(NavKey.End); break;
                case Key.Left:
                case Key.Right:
                    if (!OnHorizontalKey(key == Key.Right)) return false;
                    break;
                case Key.Enter:
                    {
                        int i = Navigator.SelectedIndex;
                        if (i < 0) return true;
                        var kind = (mods & ModifierKeys.Control) != 0 ? InvokeKind.OpenInResults
                            : (mods & ModifierKeys.Shift) != 0 ? InvokeKind.OpenInNewTab : InvokeKind.Open;
                        OnInvoke(i, kind);
                        return true;
                    }
                default:
                    return false;
            }
            changed = true;
            if (changed) InvalidateMeasure();
            return true;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new VirtualListAutomationPeer(this);

        private sealed class VirtualListAutomationPeer : FrameworkElementAutomationPeer
        {
            public VirtualListAutomationPeer(VirtualListCore owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

            protected override string GetClassNameCore() => "VirtualList";

            protected override List<AutomationPeer> GetChildrenCore()
            {
                // Only realized, bound rows, in visual order.
                var owner = (VirtualListCore)Owner;
                var rows = new List<FrameworkElement>();
                foreach (var r in owner._pool)
                    if (r is IVirtualRow vr && vr.BoundIndex >= 0) rows.Add(r);
                rows.Sort((a, b) => ((IVirtualRow)a).BoundIndex.CompareTo(((IVirtualRow)b).BoundIndex));
                var list = new List<AutomationPeer>();
                foreach (var r in rows)
                {
                    var p = CreatePeerForElement(r);
                    if (p != null) list.Add(p);
                }
                return list;
            }
        }
    }

    /// <summary>
    /// Virtualized list of <typeparamref name="T"/> fed by <c>IReadOnlyList&lt;T&gt;</c> + <see cref="IItemPresenter{T}"/>.
    /// 100k items: only visible rows are realized; data is read by index on demand.
    /// </summary>
    public class VirtualList<T> : VirtualListCore
    {
        private readonly RowData _scratch = new RowData();
        private IReadOnlyList<T> _items = Array.Empty<T>();
        private IItemPresenter<T> _presenter;

        public VirtualList() { }

        public VirtualList(IItemPresenter<T> presenter)
        {
            _presenter = presenter;
        }

        public event EventHandler<ListInvokeEventArgs<T>> ItemInvoked;

        /// <summary>Executed on invoke with a <see cref="ListInvokeEventArgs{T}"/> parameter (in addition to the event).</summary>
        public System.Windows.Input.ICommand InvokeCommand { get; set; }

        public IItemPresenter<T> Presenter
        {
            get => _presenter;
            set
            {
                _presenter = value;
                InvalidateAllRows();
            }
        }

        public IReadOnlyList<T> Items
        {
            get => _items;
            set => SetItems(value, false);
        }

        /// <summary>
        /// Replaces the items. <paramref name="keepPosition"/>: keep selection index/scroll (streamed partial results
        /// that only append); otherwise select the first selectable row and scroll to top.
        /// </summary>
        public void SetItems(IReadOnlyList<T> items, bool keepPosition)
        {
            _items = items ?? Array.Empty<T>();
            if (keepPosition) UpdateCount(_items.Count);
            else ResetCount(_items.Count);
        }

        public T SelectedItem
        {
            get
            {
                int i = SelectedIndex;
                return i >= 0 && i < _items.Count ? _items[i] : default;
            }
        }

        public bool HasSelection => SelectedIndex >= 0 && SelectedIndex < _items.Count;

        protected override FrameworkElement CreateRow() => new ListRow();

        protected override void BindRow(FrameworkElement row, int index)
        {
            var d = _scratch;
            d.Reset();
            if (_presenter != null && index < _items.Count)
            {
                try
                {
                    _presenter.Present(_items[index], d);
                }
                catch (Exception ex)
                {
                    Ui.Logger.Error("ui.list.present_failed", "Item presenter threw", ex, ("index", index));
                    d.Reset();
                    d.Primary = "?";
                }
            }
            ((ListRow)row).Bind(d, index, _items.Count);
        }

        protected override bool IsSelectableCore(int index)
        {
            if (index < 0 || index >= _items.Count) return false;
            if (_presenter == null) return true;
            try
            {
                if (_presenter is ISelectablePresenter<T> sp) return sp.IsSelectable(_items[index]);
                _scratch.Reset();
                _presenter.Present(_items[index], _scratch);
                return !_scratch.IsGroupHeader && _scratch.IsEnabled;
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.list.selectable_failed", "Item presenter threw", ex, ("index", index));
                return false;
            }
        }

        protected override void OnInvoke(int index, InvokeKind kind)
        {
            if (index < 0 || index >= _items.Count || !IsSelectableCore(index)) return;
            var args = new ListInvokeEventArgs<T>(_items[index], index, kind);
            ItemInvoked?.Invoke(this, args);
            if (InvokeCommand != null && InvokeCommand.CanExecute(args)) InvokeCommand.Execute(args);
        }
    }
}
