using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Patterns
{
    /// <summary>
    /// Title row of a floating results window: draggable title, optional subtitle, close button. Shared by
    /// <see cref="ResultsPopup{T}"/> and <see cref="TabbedTreePopup{T}"/>.
    /// </summary>
    public class PopupHeader : Border
    {
        private readonly UiText _title = new UiText((string)null, TextVariant.Strong);
        private readonly UiText _subtitle = new UiText((string)null, TextVariant.Small) { Margin = new Thickness(8, 0, 0, 0) };
        private readonly StackPanel _actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        public PopupHeader()
        {
            Padding = new Thickness(12, 5, 6, 5);
            BorderThickness = new Thickness(0, 0, 0, 1);
            this.Bind(BorderBrushProperty, Tokens.BorderSubtle);
            this.Bind(BackgroundProperty, Tokens.SurfacePopup);
            _subtitle.Bind(TextBlock.ForegroundProperty, Tokens.TextSecondary);
            var close = new IconButton(Ui.CrossMark, () => Strings.CommonClose, "Esc", 8);
            close.Click += (s, e) => Ui.Safe("ui.popupHeader.close", () => CloseRequested?.Invoke(this, EventArgs.Empty));
            _actions.Children.Add(close);
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_actions, Dock.Right);
            dock.Children.Add(_actions);
            var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(_title);
            titles.Children.Add(_subtitle);
            dock.Children.Add(titles);
            Child = dock;
            Cursor = Cursors.SizeAll;
            close.Cursor = Cursors.Arrow;
        }

        public event EventHandler CloseRequested;

        public string Title
        {
            get => _title.Text;
            set
            {
                _title.Text = value ?? string.Empty;
                AutomationProperties.SetName(this, value ?? string.Empty);
            }
        }

        public string Subtitle
        {
            get => _subtitle.Text;
            set
            {
                _subtitle.Text = value ?? string.Empty;
                _subtitle.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>Adds a button left of the close button.</summary>
        public void AddAction(UIElement action)
        {
            if (action is FrameworkElement fe) { fe.Margin = new Thickness(2, 0, 0, 0); fe.Cursor = Cursors.Arrow; }
            _actions.Children.Insert(_actions.Children.Count - 1, action);
        }
    }

    /// <summary>
    /// Floating results window (Find References, usages…) = PopupShell + <see cref="PopupHeader"/> + <see cref="ResultsPanel{T}"/>
    /// (grouped TreeList, toolbar, code preview, footer). Unlike choosers it stays open when focus returns to the editor
    /// (<see cref="PopupShell.CloseOnDeactivate"/> false) so results can be walked one by one; Esc / × closes it.
    /// Size and preview state are remembered per id. Reusable: keep one instance and call <see cref="ShowAt"/> again.
    /// </summary>
    public class ResultsPopup<T>
    {
        private readonly PopupShell _shell;
        private readonly PopupHeader _header = new PopupHeader();

        public ResultsPopup(string id, IItemPresenter<T> presenter, Size? size = null)
        {
            Panel = new ResultsPanel<T>(presenter);
            _shell = new PopupShell(id ?? "results", size ?? new Size(860, 480)) { CloseOnDeactivate = false, MinVisibleWidth = 360, MinVisibleHeight = 160 };
            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_header, Dock.Top);
            root.Children.Add(_header);
            root.Children.Add(Panel.Root);
            _shell.Body = root;
            _shell.EnableDrag(_header);
            _header.CloseRequested += (s, e) => _shell.Dismiss();
            Panel.Tree.ItemInvoked += (s, e) => Ui.Safe("ui.resultsPopup.invoke", () =>
            {
                ItemInvoked?.Invoke(this, e);
                if (CloseOnInvoke) _shell.Dismiss();
            });
            Panel.Root.Footer.SetHints(new KeyHint("Enter", () => Strings.PaletteHintOpen), new KeyHint("Esc", () => Strings.CommonClose));
        }

        public PopupShell Shell => _shell;

        public PopupHeader Header => _header;

        public ResultsPanel<T> Panel { get; }

        public TreeList<T> Tree => Panel.Tree;

        /// <summary>Close after an item was opened (Enter / double-click). Default false: walk results one by one.</summary>
        public bool CloseOnInvoke { get; set; }

        public bool IsVisible => _shell.IsVisible;

        public IntPtr OwnerHandle
        {
            get => _shell.OwnerHandle;
            set => _shell.OwnerHandle = value;
        }

        /// <summary>Raised for leaves (groups toggle on Enter).</summary>
        public event EventHandler<ListInvokeEventArgs<T>> ItemInvoked;

        public string Title
        {
            get => _header.Title;
            set
            {
                _header.Title = value;
                _shell.Title = value ?? string.Empty;
            }
        }

        public bool IsBusy
        {
            get => Panel.Root.Footer.IsBusy;
            set => Panel.Root.Footer.IsBusy = value;
        }

        public void AddToolbarItem(UIElement item, bool right = false) => Panel.Root.AddToolbarItem(item, right);

        /// <summary>Sets the tree and the footer text (count is computed from the leaves).</summary>
        public void SetResults(IReadOnlyList<TreeNode<T>> roots, string summary) => Panel.SetResults(roots, summary);

        public void ShowAt(PopupAnchor anchor)
        {
            _shell.ShowAt(anchor);
            Panel.Tree.Focus();
        }

        public void Close() => _shell.Dismiss();
    }

    /// <summary>
    /// Floating tree window with tabs (Type Hierarchy: base types / derived types) = PopupShell + <see cref="PopupHeader"/>
    /// + <see cref="TabStrip"/> + one <see cref="TreeList{T}"/> whose roots switch with the tab + footer. Tab / Shift+Tab
    /// switch tabs; Enter / double-click raise <see cref="ItemInvoked"/>. Stays open when the editor gets focus.
    /// </summary>
    public class TabbedTreePopup<T>
    {
        private readonly PopupShell _shell;
        private readonly PopupHeader _header = new PopupHeader();
        private readonly TabStrip _tabs = new TabStrip();
        private readonly TreeList<T> _tree;
        private readonly EmptyState _empty = new EmptyState { Visibility = Visibility.Collapsed };
        private readonly Dictionary<string, IReadOnlyList<TreeNode<T>>> _roots = new Dictionary<string, IReadOnlyList<TreeNode<T>>>(StringComparer.Ordinal);
        private string _emptyText;

        public TabbedTreePopup(string id, IItemPresenter<T> presenter, IReadOnlyList<TabDefinition> tabs, Size? size = null)
        {
            _tree = new TreeList<T>(presenter);
            Footer = new StatusFooter();
            _shell = new PopupShell(id ?? "tree", size ?? new Size(560, 460)) { CloseOnDeactivate = false, MinVisibleWidth = 300, MinVisibleHeight = 160 };
            var tabHost = new Border { Child = _tabs, Padding = new Thickness(8, 2, 8, 0), BorderThickness = new Thickness(0, 0, 0, 1) };
            tabHost.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            var area = new Grid();
            area.Children.Add(_tree);
            area.Children.Add(_empty);
            var root = new DockPanel { LastChildFill = true };
            root.Bind(Panel.BackgroundProperty, Tokens.SurfacePopup);
            DockPanel.SetDock(_header, Dock.Top);
            DockPanel.SetDock(tabHost, Dock.Top);
            DockPanel.SetDock(Footer, Dock.Bottom);
            root.Children.Add(_header);
            root.Children.Add(tabHost);
            root.Children.Add(Footer);
            root.Children.Add(area);
            _shell.Body = root;
            _shell.EnableDrag(_header);
            _header.CloseRequested += (s, e) => _shell.Dismiss();
            _tabs.SetTabs(tabs ?? Array.Empty<TabDefinition>());
            _tabs.SelectionChanged += (s, e) => Ui.Safe("ui.tabbedTree.tab", ShowSelected);
            _tree.ItemInvoked += (s, e) => Ui.Safe("ui.tabbedTree.invoke", () => ItemInvoked?.Invoke(this, e));
            _shell.PreviewKeyDown += (s, e) => Ui.Safe("ui.tabbedTree.key", () =>
            {
                if (e.Key != Key.Tab) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _tabs.Previous();
                else _tabs.Next();
                e.Handled = true;
            });
            Footer.SetHints(new KeyHint("Tab", () => Strings.TreeSwitchTab), new KeyHint("Enter", () => Strings.PaletteHintOpen));
        }

        public PopupShell Shell => _shell;

        public PopupHeader Header => _header;

        public TreeList<T> Tree => _tree;

        public TabStrip Tabs => _tabs;

        public StatusFooter Footer { get; }

        public bool IsVisible => _shell.IsVisible;

        public IntPtr OwnerHandle
        {
            get => _shell.OwnerHandle;
            set => _shell.OwnerHandle = value;
        }

        public event EventHandler<ListInvokeEventArgs<T>> ItemInvoked;

        public string Title
        {
            get => _header.Title;
            set
            {
                _header.Title = value;
                _shell.Title = value ?? string.Empty;
            }
        }

        /// <summary>Text shown when the selected tab has no nodes (null = generic "no results").</summary>
        public string EmptyText
        {
            get => _emptyText;
            set
            {
                _emptyText = value;
                ShowSelected();
            }
        }

        public string SelectedTabId
        {
            get => _tabs.SelectedId;
            set => _tabs.Select(value);
        }

        /// <summary>Sets the roots of one tab (and its count in the tab title: number of leaves + inner nodes at depth 1).</summary>
        public void SetTabRoots(string tabId, IReadOnlyList<TreeNode<T>> roots, int? count = null)
        {
            _roots[tabId] = roots ?? Array.Empty<TreeNode<T>>();
            _tabs.SetCount(tabId, count);
            if (tabId == _tabs.SelectedId) ShowSelected();
        }

        public void ShowAt(PopupAnchor anchor)
        {
            _shell.ShowAt(anchor);
            _tree.Focus();
        }

        public void Close() => _shell.Dismiss();

        private void ShowSelected()
        {
            string id = _tabs.SelectedId;
            var roots = id != null && _roots.TryGetValue(id, out var r) ? r : Array.Empty<TreeNode<T>>();
            _tree.SetRoots(roots);
            if (_tree.Count > 0) _tree.SelectedIndex = 0;
            _empty.Set(_emptyText ?? Strings.ListEmpty, showGlyph: false);
            _empty.Visibility = _tree.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

namespace UEToVS.UI.Molecules
{
    /// <summary>
    /// Editor navigation bar (VA / VS style): two flat scope boxes side by side, left = current type, right = current
    /// function, each with a kind glyph, text and a chevron. Clicking (or Enter) raises <see cref="LeftClicked"/> /
    /// <see cref="RightClicked"/>; the host opens a filterable chooser anchored below the box. Height follows the row
    /// height token; colors follow the tokens (surface, subtle border, secondary text).
    /// </summary>
    public class NavigationBar : Border
    {
        private readonly ScopeBox _left = new ScopeBox();
        private readonly ScopeBox _right = new ScopeBox();

        public NavigationBar()
        {
            BorderThickness = new Thickness(0, 0, 0, 1);
            Padding = new Thickness(2, 1, 2, 1);
            this.Bind(BorderBrushProperty, Tokens.BorderSubtle);
            this.Bind(BackgroundProperty, Tokens.SurfaceBase);
            ThemeManager.Instance.Attach(this);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_left, 0);
            Grid.SetColumn(_right, 2);
            grid.Children.Add(_left);
            grid.Children.Add(_right);
            Child = grid;
            _left.Click += (s, e) => Ui.Safe("ui.navbar.left", () => LeftClicked?.Invoke(this, EventArgs.Empty));
            _right.Click += (s, e) => Ui.Safe("ui.navbar.right", () => RightClicked?.Invoke(this, EventArgs.Empty));
            AutomationProperties.SetName(this, "Navigation bar");
        }

        public event EventHandler LeftClicked;
        public event EventHandler RightClicked;

        /// <summary>The left / right boxes (anchor choosers with <c>PopupAnchor.Element(bar.LeftBox)</c>).</summary>
        public FrameworkElement LeftBox => _left;

        public FrameworkElement RightBox => _right;

        public void SetLeft(SymbolKind? glyph, string text, Func<string> tooltip = null, bool dimmed = false) => _left.Set(glyph, text, tooltip, dimmed);

        public void SetRight(SymbolKind? glyph, string text, Func<string> tooltip = null, bool dimmed = false) => _right.Set(glyph, text, tooltip, dimmed);

        /// <summary>One flat drop-down-looking button: [glyph] text ˅.</summary>
        private sealed class ScopeBox : Button
        {
            private readonly TypeGlyph _glyph = new TypeGlyph { Margin = new Thickness(0, 0, 6, 0) };
            private readonly UiText _text = new UiText((string)null, TextVariant.Body) { VerticalAlignment = VerticalAlignment.Center };
            private bool _tipSet;

            public ScopeBox()
            {
                Style = Styles.Button(ButtonKind.Ghost);
                HorizontalContentAlignment = HorizontalAlignment.Stretch;
                Padding = new Thickness(6, 1, 6, 1);
                MinHeight = 0;
                Focusable = true;
                var chevron = new Path { Data = Ui.ChevronDown, StrokeThickness = 1.3, Width = 8, Height = 4, Stretch = Stretch.Uniform, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                chevron.Bind(Shape.StrokeProperty, Tokens.TextSecondary);
                var dock = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(chevron, Dock.Right);
                DockPanel.SetDock(_glyph, Dock.Left);
                dock.Children.Add(chevron);
                dock.Children.Add(_glyph);
                dock.Children.Add(_text);
                Content = dock;
            }

            public void Set(SymbolKind? glyph, string text, Func<string> tooltip, bool dimmed)
            {
                _glyph.Visibility = glyph.HasValue ? Visibility.Visible : Visibility.Collapsed;
                if (glyph.HasValue) _glyph.Kind = glyph.Value;
                _text.Text = text ?? string.Empty;
                _text.Bind(TextBlock.ForegroundProperty, dimmed ? Tokens.TextSecondary : Tokens.TextPrimary);
                AutomationProperties.SetName(this, text ?? string.Empty);
                if (tooltip != null && !_tipSet)
                {
                    Tip.Set(this, tooltip);
                    _tipSet = true;
                }
            }
        }
    }
}
