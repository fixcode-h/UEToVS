using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Perf
{
    /// <summary>A column of a <see cref="DataTable{T}"/>.</summary>
    public sealed class TableColumn<T>
    {
        public TableColumn(Func<string> header, Func<T, string> text)
        {
            Header = header ?? (() => string.Empty);
            Text = text ?? (_ => string.Empty);
        }

        public Func<string> Header { get; }
        public Func<T, string> Text { get; }

        /// <summary>Sort comparison; null = not sortable.</summary>
        public Comparison<T> Sort { get; set; }

        /// <summary>Right-aligned (numbers).</summary>
        public bool Numeric { get; set; }

        /// <summary>Star weight (when <see cref="FixedWidth"/> is 0).</summary>
        public double Star { get; set; } = 1;

        /// <summary>Fixed width in DIPs (0 = star sized). Should fit the English header too.</summary>
        public double FixedWidth { get; set; }

        public double MinWidth { get; set; } = 40;

        /// <summary>Per-cell state (colors the cell text), e.g. p95 over budget → Error.</summary>
        public Func<T, Severity?> CellState { get; set; }
    }

    /// <summary>
    /// Virtualized, sortable table built on the list engine: clickable headers (▲/▼), right-aligned numeric
    /// columns, row highlight (e.g. over budget). <see cref="SetItems"/> keeps the sort and only rebinds the
    /// visible rows, so a 1 Hz update is cheap. Height: auto up to <see cref="MaxVisibleRows"/>, then it scrolls.
    /// </summary>
    public class DataTable<T> : DockPanel
    {
        private readonly List<TableColumn<T>> _columns = new List<TableColumn<T>>();
        private readonly HeaderRow _header;
        private readonly Body _body;
        private readonly ColumnLayout _layout = new ColumnLayout();
        private IReadOnlyList<T> _items = Array.Empty<T>();
        private int[] _order = Array.Empty<int>();
        private int _sortColumn = -1;
        private bool _sortDescending;

        public DataTable()
        {
            LastChildFill = true;
            _header = new HeaderRow(this);
            _body = new Body(this);
            SetDock(_header, Dock.Top);
            Children.Add(_header);
            Children.Add(_body);
            SizeChanged += (s, e) => Ui.Safe("ui.table.size", () => { _layout.Compute(_columns, ActualWidth); _header.InvalidateArrange(); _body.InvalidateAllRows(); });
            Strings.Track(this, () => _header.Relabel());
        }

        public int MaxVisibleRows { get; set; } = 12;

        /// <summary>Row highlight (Severity background), e.g. over-budget operations → Error.</summary>
        public Func<T, Severity?> RowState { get; set; }

        public event EventHandler<ListInvokeEventArgs<T>> ItemInvoked;

        public IReadOnlyList<T> Items => _items;

        public void AddColumn(TableColumn<T> column)
        {
            _columns.Add(column);
            _header.Rebuild();
            _layout.Compute(_columns, ActualWidth);
        }

        /// <summary>Sorts by a column (index), toggling direction when it is already the sort column.</summary>
        public void SortBy(int column, bool? descending = null)
        {
            if (column < 0 || column >= _columns.Count || _columns[column].Sort == null) return;
            _sortDescending = descending ?? (_sortColumn == column ? !_sortDescending : _columns[column].Numeric);
            _sortColumn = column;
            Reorder();
            _header.Relabel();
            _body.InvalidateAllRows();
        }

        public void SetItems(IReadOnlyList<T> items)
        {
            _items = items ?? Array.Empty<T>();
            Reorder();
            _body.SetCount(_items.Count);
        }

        private void Reorder()
        {
            Comparison<int> cmp = null;
            if (_sortColumn >= 0)
            {
                var c = _columns[_sortColumn].Sort;
                var items = _items;
                cmp = (a, b) => c(items[a], items[b]);
            }
            try
            {
                _order = PerfMath.SortPermutation(_items.Count, cmp, _sortDescending);
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.table.sort_failed", "Table sort comparison threw", ex);
                _order = PerfMath.SortPermutation(_items.Count, null, false);
            }
        }

        private T ItemAt(int viewIndex) => _items[_order[viewIndex]];

        /// <summary>Column x/width cache shared by header and rows.</summary>
        private sealed class ColumnLayout
        {
            public double[] X = Array.Empty<double>();
            public double[] W = Array.Empty<double>();
            private const double Pad = 12;

            public void Compute(List<TableColumn<T>> cols, double width)
            {
                int n = cols.Count;
                X = new double[n];
                W = new double[n];
                double avail = Math.Max(0, width - 2 * Pad - 10);
                double fixedSum = 0, stars = 0;
                foreach (var c in cols)
                {
                    if (c.FixedWidth > 0) fixedSum += c.FixedWidth;
                    else stars += Math.Max(0.01, c.Star);
                }
                double starUnit = stars > 0 ? Math.Max(0, avail - fixedSum) / stars : 0;
                double x = Pad;
                for (int i = 0; i < n; i++)
                {
                    var c = cols[i];
                    double w = c.FixedWidth > 0 ? c.FixedWidth : Math.Max(c.MinWidth, starUnit * Math.Max(0.01, c.Star));
                    X[i] = x;
                    W[i] = w;
                    x += w;
                }
            }
        }

        private sealed class HeaderRow : Panel
        {
            private readonly DataTable<T> _t;
            private readonly List<TextBlock> _cells = new List<TextBlock>();

            public HeaderRow(DataTable<T> t)
            {
                _t = t;
                Height = 26;
                this.Bind(BackgroundProperty, Tokens.SurfaceRaised);
            }

            public void Rebuild()
            {
                Children.Clear();
                _cells.Clear();
                for (int i = 0; i < _t._columns.Count; i++)
                {
                    int idx = i;
                    var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold, Focusable = _t._columns[i].Sort != null };
                    tb.Bind(TextBlock.ForegroundProperty, Tokens.TextSecondary);
                    tb.Bind(TextBlock.FontSizeProperty, Tokens.FontSizeSmall);
                    if (_t._columns[i].Numeric) tb.TextAlignment = TextAlignment.Right;
                    if (_t._columns[i].Sort != null)
                    {
                        tb.Cursor = Cursors.Hand;
                        tb.MouseLeftButtonUp += (s, e) => Ui.Safe("ui.table.sort", () => _t.SortBy(idx));
                        tb.KeyDown += (s, e) => Ui.Safe("ui.table.sortkey", () => { if (e.Key == Key.Enter || e.Key == Key.Space) { _t.SortBy(idx); e.Handled = true; } });
                    }
                    _cells.Add(tb);
                    Children.Add(tb);
                }
                Relabel();
            }

            public void Relabel()
            {
                for (int i = 0; i < _cells.Count; i++)
                {
                    string h = _t._columns[i].Header();
                    if (i == _t._sortColumn) h = _t._columns[i].Numeric ? (_t._sortDescending ? "▼ " : "▲ ") + h : h + (_t._sortDescending ? " ▼" : " ▲");
                    _cells[i].Text = h;
                    AutomationProperties.SetName(_cells[i], h);
                }
            }

            protected override Size MeasureOverride(Size a)
            {
                foreach (UIElement c in Children) c.Measure(new Size(double.PositiveInfinity, Height));
                return new Size(double.IsInfinity(a.Width) ? 0 : a.Width, Height);
            }

            protected override Size ArrangeOverride(Size f)
            {
                var L = _t._layout;
                for (int i = 0; i < _cells.Count && i < L.X.Length; i++)
                    _cells[i].Arrange(new Rect(L.X[i] + 4, 0, Math.Max(0, L.W[i] - 8), f.Height));
                return f;
            }
        }

        private sealed class Body : VirtualListCore
        {
            private readonly DataTable<T> _t;

            public Body(DataTable<T> t)
            {
                _t = t;
                WrapAround = false;
                Navigator.AutoSelectFirst = false;
            }

            public void SetCount(int n)
            {
                if (n == Count) InvalidateAllRows();
                else if (Count == 0) ResetCount(n, false);
                else UpdateCount(n);
                InvalidateMeasure();
            }

            protected override Size MeasureOverride(Size available)
            {
                double rh = Math.Max(4, RowHeight);
                double natural = Math.Max(1, Math.Min(Count, _t.MaxVisibleRows)) * rh;
                double h = double.IsInfinity(available.Height) ? natural : Math.Min(available.Height, natural);
                base.MeasureOverride(new Size(available.Width, h));
                return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, h);
            }

            protected override FrameworkElement CreateRow() => new TableRow(_t);

            protected override void BindRow(FrameworkElement row, int index) => ((TableRow)row).Bind(index);

            protected override bool IsSelectableCore(int index) => index >= 0 && index < Count;

            protected override void OnInvoke(int index, InvokeKind kind)
            {
                if (index < 0 || index >= _t._items.Count) return;
                _t.ItemInvoked?.Invoke(_t, new ListInvokeEventArgs<T>(_t.ItemAt(index), index, kind));
            }
        }

        private sealed class TableRow : Panel, IVirtualRow
        {
            private readonly DataTable<T> _t;
            private readonly List<TextBlock> _cells = new List<TextBlock>();
            private bool _selected, _hover, _active;
            private Severity? _state;

            public TableRow(DataTable<T> t)
            {
                _t = t;
                ClipToBounds = true;
                Background = Brushes.Transparent;
            }

            public int BoundIndex { get; set; } = -1;

            public void Bind(int viewIndex)
            {
                var cols = _t._columns;
                while (_cells.Count < cols.Count)
                {
                    var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                    _cells.Add(tb);
                    Children.Add(tb);
                }
                if (viewIndex >= _t._items.Count) return;
                var item = _t.ItemAt(viewIndex);
                var names = new List<string>();
                for (int i = 0; i < cols.Count; i++)
                {
                    var c = cols[i];
                    var tb = _cells[i];
                    string text;
                    Severity? cs = null;
                    try
                    {
                        text = c.Text(item);
                        cs = c.CellState?.Invoke(item);
                    }
                    catch (Exception ex)
                    {
                        Ui.Logger.Error("ui.table.cell_failed", "Table cell accessor threw", ex, ("column", i));
                        text = "?";
                    }
                    if (tb.Text != text) tb.Text = text ?? string.Empty;
                    tb.TextAlignment = c.Numeric ? TextAlignment.Right : TextAlignment.Left;
                    tb.Bind(TextBlock.ForegroundProperty, cs.HasValue && cs.Value >= Severity.Warning ? Tokens.Severity(cs.Value) : i == 0 ? Tokens.TextPrimary : Tokens.TextSecondary);
                    tb.FontWeight = cs.HasValue && cs.Value >= Severity.Warning ? FontWeights.SemiBold : FontWeights.Normal;
                    names.Add(c.Header() + " " + text);
                }
                try { _state = _t.RowState?.Invoke(item); }
                catch (Exception ex) { Ui.Logger.Error("ui.table.rowstate_failed", "RowState threw", ex); _state = null; }
                // The column layout may have changed since this row was last arranged (e.g. the table was collapsed, then
                // shown at full width) while its texts did not, which would not re-arrange it on its own.
                InvalidateArrange();
                AutomationProperties.SetName(this, string.Join(", ", names) + (_state == Severity.Error ? ", " + Strings.PerfOverBudget : ""));
                ApplyBackground();
            }

            public void SetState(bool selected, bool hover, bool active)
            {
                if (_selected == selected && _hover == hover && _active == active) return;
                _selected = selected;
                _hover = hover;
                _active = active;
                ApplyBackground();
            }

            private void ApplyBackground()
            {
                if (_selected) SetResourceReference(BackgroundProperty, _active ? Tokens.SelectionBackground : Tokens.SelectionInactiveBackground);
                else if (_state.HasValue && _state.Value >= Severity.Warning) SetResourceReference(BackgroundProperty, Tokens.SeverityBackground(_state.Value));
                else if (_hover) SetResourceReference(BackgroundProperty, Tokens.SurfaceHover);
                else Background = Brushes.Transparent;
            }

            protected override Size MeasureOverride(Size a)
            {
                foreach (UIElement c in Children) c.Measure(new Size(double.PositiveInfinity, a.Height));
                return new Size(double.IsInfinity(a.Width) ? 0 : a.Width, double.IsInfinity(a.Height) ? 24 : a.Height);
            }

            protected override Size ArrangeOverride(Size f)
            {
                var L = _t._layout;
                for (int i = 0; i < _cells.Count; i++)
                {
                    if (i >= L.X.Length) { _cells[i].Arrange(new Rect(0, 0, 0, 0)); continue; }
                    _cells[i].Arrange(new Rect(L.X[i] + 4, 0, Math.Max(0, L.W[i] - 8), f.Height));
                }
                return f;
            }

            protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this);

            private sealed class RowPeer : FrameworkElementAutomationPeer
            {
                public RowPeer(TableRow r) : base(r) { }

                protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

                protected override List<AutomationPeer> GetChildrenCore() => null;
            }
        }
    }
}
