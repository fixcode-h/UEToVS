using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Imaging.Interop;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;
using TextRange = UEToVS.UI.Logic.TextRange;

namespace UEToVS.UI.Patterns
{
    public sealed class ChooserResult<T>
    {
        public bool Chosen { get; internal set; }
        public T Item { get; internal set; }
        public InvokeKind Kind { get; internal set; }
    }

    /// <summary>
    /// Small chooser = PopupShell + (optional) SearchInput + VirtualList, for "go to implementation" with several
    /// targets, Navigate To lists, recent files… Typing filters locally (subsequence match, highlighted).
    /// Reusable: keep one instance per id and call <see cref="ShowAsync"/>.
    /// </summary>
    public class ChooserPopup<T>
    {
        private readonly PopupShell _shell;
        private readonly SearchInput _search = new SearchInput(true);
        private readonly UiText _title = new UiText((string)null, TextVariant.Small) { Margin = new Thickness(12, 6, 12, 2) };
        private readonly VirtualList<T> _list;
        private readonly FilteringPresenter _presenter;
        private readonly EmptyState _empty = new EmptyState { Visibility = Visibility.Collapsed };
        private IReadOnlyList<T> _all = Array.Empty<T>();
        private Func<T, string> _filterText;
        private TaskCompletionSource<ChooserResult<T>> _tcs;

        public ChooserPopup(string id, IItemPresenter<T> presenter, Size? size = null)
        {
            _presenter = new FilteringPresenter(presenter);
            _list = new VirtualList<T>(_presenter) { AlwaysActive = true, FocusOnClick = false };
            _shell = new PopupShell(id ?? "chooser", size ?? new Size(480, 320)) { MinVisibleWidth = 240, MinVisibleHeight = 100 };
            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_title, Dock.Top);
            DockPanel.SetDock(_search, Dock.Top);
            var div = new Divider();
            DockPanel.SetDock(div, Dock.Top);
            root.Children.Add(_title);
            root.Children.Add(_search);
            root.Children.Add(div);
            var area = new Grid();
            area.Children.Add(_list);
            area.Children.Add(_empty);
            root.Children.Add(area);
            _shell.Body = root;
            _shell.EnableDrag(_title);
            _search.Padding = new Thickness(12, 6, 8, 6);

            _search.TextChanged += (s, t) => Ui.Safe("ui.chooser.filter", ApplyFilter);
            _list.ItemInvoked += (s, e) => Ui.Safe("ui.chooser.invoke", () => Complete(true, e.Item, e.Kind));
            _shell.PreviewKeyDown += (s, e) => Ui.Safe("ui.chooser.key", () =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Up || key == Key.Down || key == Key.PageUp || key == Key.PageDown || key == Key.Enter)
                    e.Handled = _list.HandleKey(key, Keyboard.Modifiers);
            });
            _shell.Dismissed += (s, e) => Ui.Safe("ui.chooser.dismissed", () => Complete(false, default, InvokeKind.Open));
        }

        public PopupShell Shell => _shell;

        public IntPtr OwnerHandle
        {
            get => _shell.OwnerHandle;
            set => _shell.OwnerHandle = value;
        }

        /// <summary>
        /// Shows <paramref name="items"/>; completes when the user picks one (Enter/double-click) or dismisses.
        /// <paramref name="filterText"/> null hides the search box.
        /// </summary>
        public Task<ChooserResult<T>> ShowAsync(IReadOnlyList<T> items, PopupAnchor anchor, string title = null, Func<T, string> filterText = null)
        {
            Complete(false, default, InvokeKind.Open);
            _tcs = new TaskCompletionSource<ChooserResult<T>>();
            _all = items ?? Array.Empty<T>();
            _filterText = filterText;
            _title.Text = title ?? string.Empty;
            _title.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
            _shell.Title = title ?? string.Empty;
            _search.Visibility = filterText != null ? Visibility.Visible : Visibility.Collapsed;
            _search.Text = string.Empty;
            ApplyFilter();
            // Fit the height to the content (up to the remembered/default height).
            double rows = Math.Min(Math.Max(_all.Count, 1), 14);
            var size = _shell.VisibleSize;
            double chromeH = (filterText != null ? 44 : 0) + (string.IsNullOrEmpty(title) ? 0 : 26) + 10;
            _shell.ShowAt(anchor);
            _shell.VisibleSize = new Size(size.Width, Math.Min(size.Height, rows * ThemeManager.Instance.RowHeight + chromeH));
            if (filterText != null) _search.FocusInput();
            else _list.Focus();
            return _tcs.Task;
        }

        private void ApplyFilter()
        {
            string q = _search.Text ?? string.Empty;
            if (_filterText == null || q.Length == 0)
            {
                _presenter.Query = null;
                _list.Items = _all;
            }
            else
            {
                var filtered = new List<T>();
                foreach (var it in _all)
                {
                    string t = null;
                    try { t = _filterText(it); }
                    catch (Exception ex) { Ui.Logger.Error("ui.chooser.filter_text_failed", "filterText threw", ex); }
                    if (HighlightRanges.TryMatchSubsequence(t, q, out _)) filtered.Add(it);
                }
                _presenter.Query = q;
                _list.Items = filtered;
            }
            _empty.Set(Strings.ListEmpty, showGlyph: false);
            _empty.Visibility = _list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Complete(bool chosen, T item, InvokeKind kind)
        {
            var tcs = _tcs;
            if (tcs == null) return;
            _tcs = null;
            tcs.TrySetResult(new ChooserResult<T> { Chosen = chosen, Item = item, Kind = kind });
            if (chosen) _shell.Dismiss();
        }

        /// <summary>Adds subsequence highlights for the local filter on top of the feature's presenter.</summary>
        private sealed class FilteringPresenter : IItemPresenter<T>, ISelectablePresenter<T>
        {
            private readonly IItemPresenter<T> _inner;

            public FilteringPresenter(IItemPresenter<T> inner) => _inner = inner;

            public string Query { get; set; }

            public void Present(T item, RowData row)
            {
                _inner?.Present(item, row);
                if (!string.IsNullOrEmpty(Query) && row.PrimaryRanges == null && HighlightRanges.TryMatchSubsequence(row.Primary, Query, out var r))
                    row.PrimaryRanges = r;
            }

            public bool IsSelectable(T item)
            {
                if (_inner is ISelectablePresenter<T> sp) return sp.IsSelectable(item);
                var r = new RowData();
                _inner?.Present(item, r);
                return !r.IsGroupHeader && r.IsEnabled;
            }
        }
    }

    /// <summary>An entry of a <see cref="ContextMenuPopup"/> (Alt+Enter actions, context menus).</summary>
    public sealed class MenuEntry
    {
        public MenuEntry(string id, Func<string> text)
        {
            Id = id;
            Text = text ?? (() => id);
        }

        public string Id { get; }
        public Func<string> Text { get; }
        public string Shortcut { get; set; }
        public ImageMoniker Icon { get; set; }
        public SymbolKind? Glyph { get; set; }

        /// <summary>Entries with the same group are listed together under a header with this title.</summary>
        public Func<string> Group { get; set; }

        public bool IsEnabled { get; set; } = true;
        public Func<string> Description { get; set; }

        /// <summary>Submenu entries (shown with ›, opened with → / Enter).</summary>
        public IReadOnlyList<MenuEntry> Children { get; set; }

        /// <summary>Run when chosen (optional; the awaiting caller also receives the entry).</summary>
        public Action Execute { get; set; }

        public object Tag { get; set; }
    }

    /// <summary>
    /// Context / quick-action menu = PopupShell + filter box + VirtualList (groups as headers, keycap shortcuts,
    /// submenus to the right). Type to filter, ↑↓ Enter, → opens a submenu, ← / Esc closes a submenu.
    /// </summary>
    public class ContextMenuPopup
    {
        private readonly PopupShell _shell;
        private readonly SearchInput _filter = new SearchInput(true);
        private readonly VirtualList<Row> _list;
        private IReadOnlyList<MenuEntry> _entries = Array.Empty<MenuEntry>();
        private TaskCompletionSource<MenuEntry> _tcs;
        private ContextMenuPopup _child;
        private IDisposable _suppress;
        private readonly ContextMenuPopup _parent;
        private bool _closedByKeyboard;

        public ContextMenuPopup(string id = "contextmenu") : this(id, null) { }

        private ContextMenuPopup(string id, ContextMenuPopup parent)
        {
            _parent = parent;
            _shell = new PopupShell(id, new Size(340, 300)) { Resizable = false, MinVisibleWidth = 200, MinVisibleHeight = 40 };
            _list = new VirtualList<Row>(new DelegatePresenter<Row>(Present, r => r.Entry != null && r.Entry.IsEnabled)) { AlwaysActive = true, FocusOnClick = false };
            _filter.Padding = new Thickness(10, 5, 8, 5);
            _filter.ShowGlyphless();
            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_filter, Dock.Top);
            var div = new Divider();
            DockPanel.SetDock(div, Dock.Top);
            root.Children.Add(_filter);
            root.Children.Add(div);
            root.Children.Add(_list);
            _shell.Body = root;
            Strings.Track(_filter, () => _filter.Placeholder = Strings.MenuFilter);

            _filter.TextChanged += (s, t) => Ui.Safe("ui.menu.filter", Rebuild);
            _list.ItemInvoked += (s, e) => Ui.Safe("ui.menu.invoke", () => Choose(e.Item));
            _shell.PreviewKeyDown += (s, e) => Ui.Safe("ui.menu.key", () => OnKey(e));
            _shell.Dismissed += (s, e) => Ui.Safe("ui.menu.dismissed", () =>
            {
                _child?._shell.Dismiss();
                Finish(null);
            });
        }

        public PopupShell Shell => _shell;

        public IntPtr OwnerHandle
        {
            get => _shell.OwnerHandle;
            set => _shell.OwnerHandle = value;
        }

        /// <summary>Shows the menu; returns the chosen entry (after running its Execute) or null.</summary>
        public Task<MenuEntry> ShowAsync(IReadOnlyList<MenuEntry> entries, PopupAnchor anchor)
        {
            Finish(null);
            _tcs = new TaskCompletionSource<MenuEntry>();
            _closedByKeyboard = false;
            _entries = entries ?? Array.Empty<MenuEntry>();
            _filter.Text = string.Empty;
            Rebuild();
            double rows = Math.Min(Math.Max(_list.Count, 1), 16);
            _shell.VisibleSize = new Size(Math.Max(_shell.VisibleSize.Width, 260), rows * ThemeManager.Instance.RowHeight + 46);
            _shell.ShowAt(anchor);
            _filter.FocusInput();
            return _tcs.Task;
        }

        private sealed class Row
        {
            public MenuEntry Entry;
            public string Header;
            public IReadOnlyList<TextRange> Ranges;
        }

        private static void Present(Row r, RowData d)
        {
            if (r.Entry == null)
            {
                d.IsGroupHeader = true;
                d.Primary = r.Header;
                return;
            }
            var e = r.Entry;
            d.Primary = e.Text?.Invoke();
            d.PrimaryRanges = r.Ranges;
            d.Moniker = e.Icon;
            d.Glyph = e.Glyph;
            d.Shortcut = e.Shortcut;
            d.HasSubmenu = e.Children != null && e.Children.Count > 0;
            d.IsEnabled = e.IsEnabled;
            d.ToolTip = e.Description?.Invoke();
        }

        private void Rebuild()
        {
            string q = _filter.Text ?? string.Empty;
            var rows = new List<Row>();
            string lastGroup = null;
            foreach (var e in _entries)
            {
                if (e == null) continue;
                IReadOnlyList<TextRange> ranges = null;
                if (q.Length > 0)
                {
                    if (!HighlightRanges.TryMatchSubsequence(e.Text?.Invoke(), q, out var r)) continue;
                    ranges = r;
                }
                string g = e.Group?.Invoke();
                if (!string.IsNullOrEmpty(g) && g != lastGroup && q.Length == 0) rows.Add(new Row { Header = g });
                lastGroup = g;
                rows.Add(new Row { Entry = e, Ranges = ranges });
            }
            _list.Items = rows;
        }

        private void OnKey(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var sel = _list.SelectedItem;
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                case Key.PageUp:
                case Key.PageDown:
                case Key.Enter:
                    e.Handled = _list.HandleKey(key, Keyboard.Modifiers);
                    break;
                case Key.Right:
                    if (sel?.Entry?.Children != null && sel.Entry.Children.Count > 0 && _filter.InputBox.CaretIndex >= (_filter.Text ?? "").Length)
                    {
                        OpenSubmenu(sel.Entry);
                        e.Handled = true;
                    }
                    break;
                case Key.Left:
                    if (_parent != null && string.IsNullOrEmpty(_filter.Text))
                    {
                        _closedByKeyboard = true;
                        _shell.Dismiss();
                        e.Handled = true;
                    }
                    break;
                case Key.Escape:
                    if (_parent != null)
                    {
                        _closedByKeyboard = true;
                        _shell.Dismiss();
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void Choose(Row row)
        {
            var entry = row?.Entry;
            if (entry == null || !entry.IsEnabled) return;
            if (entry.Children != null && entry.Children.Count > 0)
            {
                OpenSubmenu(entry);
                return;
            }
            Finish(entry);
            _shell.Dismiss();
            if (entry.Execute != null) Ui.Safe("ui.menu.execute", entry.Execute);
        }

        private async void OpenSubmenu(MenuEntry entry)
        {
            try
            {
                if (_child == null) _child = new ContextMenuPopup(_shell.Id + ".sub", this) { OwnerHandle = new System.Windows.Interop.WindowInteropHelper(_shell).Handle };
                _suppress?.Dispose();
                _suppress = _shell.SuppressAutoClose();
                var rowAnchor = SelectedRowAnchor();
                var chosen = await _child.ShowAsync(entry.Children, rowAnchor);
                _suppress?.Dispose();
                _suppress = null;
                if (chosen != null)
                {
                    Finish(chosen);
                    _shell.Dismiss();
                }
                else if (_shell.IsVisible && _child._closedByKeyboard)
                {
                    _shell.Activate();
                    _filter.FocusInput();
                }
                else
                {
                    // The submenu lost activation to another window: close the whole menu chain.
                    _shell.Dismiss();
                }
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.menu.submenu_failed", "Submenu failed", ex);
            }
        }

        private PopupAnchor SelectedRowAnchor()
        {
            // Right of the popup, vertically at the selected row.
            var a = PopupAnchor.Element(_list, AnchorMode.Right);
            double rh = ThemeManager.Instance.RowHeight;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_list).DpiScaleY;
            double y = a.Rect.Y + (_list.SelectedIndex - _list.Navigator.ScrollOffsetRows) * rh * dpi;
            return PopupAnchor.Screen(new RectD(a.Rect.X, y, a.Rect.Width, rh * dpi), AnchorMode.Right);
        }

        private void Finish(MenuEntry result)
        {
            var tcs = _tcs;
            if (tcs == null) return;
            _tcs = null;
            tcs.TrySetResult(result);
        }
    }

    internal static class SearchInputExtensions
    {
        /// <summary>Menu filter: no magnifier, smaller text.</summary>
        public static void ShowGlyphless(this SearchInput s)
        {
            s.ShowGlass = false;
            s.InputBox.Bind(Control.FontSizeProperty, Tokens.FontSizeBody);
        }
    }

    /// <summary>
    /// Results tool-window content = ToolWindowShell + TreeList (grouped) + CodePreview, with expand/collapse-all
    /// actions and a summary in the footer. Host puts <see cref="Root"/> into its ToolWindowPane.
    /// </summary>
    public class ResultsPanel<T>
    {
        private readonly TreeList<T> _tree;
        private readonly CodePreview _preview = new CodePreview();
        private readonly Grid _split = new Grid();
        private readonly GridSplitter _splitter = new GridSplitter { ResizeBehavior = GridResizeBehavior.PreviousAndNext, Focusable = false };
        private readonly EmptyState _empty = new EmptyState();
        private readonly System.Windows.Threading.DispatcherTimer _previewDebounce;
        private PreviewPosition _pos;
        private bool _previewVisible;

        public ResultsPanel(IItemPresenter<T> presenter)
        {
            _tree = new TreeList<T>(presenter);
            Root = new ToolWindowShell();
            var expand = new IconButton(Ui.FrozenGeometry("M 0,0 L 4,4 L 8,0 M 0,5 L 4,9 L 8,5"), () => Strings.TreeExpandAll, null, 9);
            var collapse = new IconButton(Ui.FrozenGeometry("M 0,4 L 4,0 L 8,4 M 0,9 L 4,5 L 8,9"), () => Strings.TreeCollapseAll, null, 9);
            var previewToggle = new IconToggle(Ui.FrozenGeometry("M 0,0 L 12,0 L 12,10 L 0,10 Z M 6,0 L 6,10"), () => Strings.PaletteTogglePreview, "Ctrl+P");
            expand.Click += (s, e) => Ui.Safe("ui.results.expand", () => _tree.ExpandAll(true));
            collapse.Click += (s, e) => Ui.Safe("ui.results.collapse", () => _tree.ExpandAll(false));
            Root.AddToolbarItem(expand);
            Root.AddToolbarItem(collapse);
            Root.AddToolbarItem(previewToggle, right: true);
            _splitter.Bind(Control.BackgroundProperty, Tokens.BorderSubtle);
            var listArea = new Grid();
            listArea.Children.Add(_tree);
            listArea.Children.Add(_empty);
            _listArea = listArea;
            Root.Body = _split;
            _previewDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _previewDebounce.Tick += (s, e) => Ui.Safe("ui.results.preview", () => { _previewDebounce.Stop(); UpdatePreview(); });

            var settings = ThemeManager.Instance.EffectiveSettings;
            _pos = PreviewPosition.Right; // tool windows are wide: preview at the right by default
            _previewVisible = settings.PreviewVisible;
            previewToggle.IsChecked = _previewVisible;
            previewToggle.Checked += (s, e) => Ui.Safe("ui.results.preview_on", () => { _previewVisible = true; Layout(); UpdatePreview(); });
            previewToggle.Unchecked += (s, e) => Ui.Safe("ui.results.preview_off", () => { _previewVisible = false; Layout(); });
            Root.PreviewKeyDown += (s, e) => Ui.Safe("ui.results.key", () =>
            {
                if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) { previewToggle.IsChecked = !(previewToggle.IsChecked == true); e.Handled = true; }
            });
            _tree.SelectionChanged += (s, e) => Ui.Safe("ui.results.selection", () => { if (_previewVisible) { _previewDebounce.Stop(); _previewDebounce.Start(); } });
            _tree.ItemInvoked += (s, e) => Ui.Safe("ui.results.invoke", () => ItemInvoked?.Invoke(this, e));
            Layout();
            SetResults(Array.Empty<TreeNode<T>>(), null);
        }

        private readonly Grid _listArea;

        public ToolWindowShell Root { get; }

        public TreeList<T> Tree => _tree;

        public CodePreview Preview => _preview;

        /// <summary>Preview of a node's item (null = none).</summary>
        public Func<T, CodePreviewRequest> PreviewProvider { get; set; }

        public event EventHandler<ListInvokeEventArgs<T>> ItemInvoked;

        /// <summary>Preview at the right (wide tool windows) or bottom.</summary>
        public PreviewPosition PreviewPosition
        {
            get => _pos;
            set
            {
                _pos = value;
                Layout();
            }
        }

        public void SetResults(IReadOnlyList<TreeNode<T>> roots, string summary)
        {
            _tree.SetRoots(roots);
            int leaves = 0;
            if (roots != null)
                foreach (var r in roots) leaves += r.HasChildren ? r.LeafCount : 1;
            Root.Footer.Status = summary ?? string.Empty;
            Root.Footer.Count = Strings.ResultsCount(PerfMath.FormatCount(leaves));
            _empty.Visibility = _tree.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _empty.Set(Strings.ListEmpty, showGlyph: false);
            if (_previewVisible) UpdatePreview();
        }

        private void UpdatePreview()
        {
            var node = _tree.SelectedNode;
            if (node == null || PreviewProvider == null) { _preview.Clear(); return; }
            CodePreviewRequest req = null;
            try { req = PreviewProvider(node.Item); }
            catch (Exception ex) { Ui.Logger.Error("ui.results.preview_failed", "PreviewProvider threw", ex); }
            if (req == null) _preview.Clear();
            else _preview.Show(req);
        }

        private void Layout()
        {
            _split.Children.Clear();
            _split.RowDefinitions.Clear();
            _split.ColumnDefinitions.Clear();
            _split.Children.Add(_listArea);
            Grid.SetRow(_listArea, 0);
            Grid.SetColumn(_listArea, 0);
            if (!_previewVisible) return;
            if (_pos == PreviewPosition.Right)
            {
                _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
                _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
                _splitter.ResizeDirection = GridResizeDirection.Columns;
                Grid.SetColumn(_splitter, 1);
                Grid.SetRow(_splitter, 0);
                Grid.SetColumn(_preview, 2);
                Grid.SetRow(_preview, 0);
            }
            else
            {
                _split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
                _split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
                _split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 40 });
                _splitter.ResizeDirection = GridResizeDirection.Rows;
                Grid.SetRow(_splitter, 1);
                Grid.SetColumn(_splitter, 0);
                Grid.SetRow(_preview, 2);
                Grid.SetColumn(_preview, 0);
            }
            _split.Children.Add(_splitter);
            _split.Children.Add(_preview);
        }
    }
}
