using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Query;
using UEToVS.UI;

namespace UEToVS.Search
{
    /// <summary>
    /// Rider-style Search Everywhere popup: tabs (All/Types/Files/Symbols/Actions/Text/Assets), "include non-project
    /// items", filters, preview, and "open in find window". Created once and reused so it shows instantly.
    /// </summary>
    internal sealed class SearchWindow : Window
    {
        private readonly ISearchHost _host;
        private readonly SearchController _controller;
        private readonly Action<List<SearchItem>, string> _openInToolWindow;
        private readonly TextBox _input = new TextBox();
        private readonly ResultList _list = new ResultList();
        private readonly StackPanel _tabsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly CheckBox _nonProject = new CheckBox { Content = "包含非项目项", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        private readonly Button _filterButton = new Button { Content = "筛选 ▾", Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(6, 1, 6, 1) };
        private readonly ToggleButton _previewToggle = new ToggleButton { Content = "预览", Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(6, 1, 6, 1) };
        private readonly Button _openInWindow = new Button { Content = "在窗口中打开", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6, 1, 6, 1), ToolTip = "把全部结果放到 UEToVS 搜索结果窗口" };
        private readonly TextBlock _footer = new TextBlock { Margin = new Thickness(8, 3, 8, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Border _preview = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        private readonly StackPanel _previewLines = new StackPanel();
        private readonly TextBlock _previewHeader = new TextBlock { Margin = new Thickness(0, 0, 0, 2) };
        private readonly RowDefinition _previewRow = new RowDefinition { Height = new GridLength(0) };
        private readonly System.Windows.Threading.DispatcherTimer _debounce;
        private SearchTab _tab = SearchTab.All;
        private CancellationTokenSource _cts;
        private int _generation;
        private int _historyIndex = -1;
        private bool _suppressTextChanged;
        private bool _menuOpen;

        public SearchWindow(ISearchHost host, SearchController controller, Action<List<SearchItem>, string> openInToolWindow)
        {
            _host = host;
            _controller = controller;
            _openInToolWindow = openInToolWindow;

            Title = "Search Everywhere";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            ShowActivated = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            MinWidth = 480;
            MinHeight = 240;
            SetResourceReference(BackgroundProperty, Theme.Background);
            SetResourceReference(ForegroundProperty, Theme.Foreground);
            Theme.ApplyWindowFont(this);

            _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); RunQuery(); };

            Content = BuildLayout();

            _input.TextChanged += (s, e) =>
            {
                if (_suppressTextChanged) return;
                _historyIndex = -1;
                _debounce.Stop();
                _debounce.Start();
            };
            _input.PreviewKeyDown += OnInputKeyDown;
            _list.ItemActivated += OnItemActivated;
            _list.SelectionChanged += UpdatePreview;
            _nonProject.Checked += (s, e) => RunQuery();
            _nonProject.Unchecked += (s, e) => RunQuery();
            _filterButton.Click += (s, e) => ShowFilterMenu();
            _previewToggle.Checked += (s, e) => { SetPreviewVisible(true); };
            _previewToggle.Unchecked += (s, e) => { SetPreviewVisible(false); };
            _openInWindow.Click += (s, e) => OpenAllInToolWindow();
            Deactivated += (s, e) => { if (!_menuOpen && IsVisible) Dismiss(); };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
            };
            InputMethod.SetIsInputMethodEnabled(_input, false); // English input in the search box (Chinese IME off)
        }

        public SearchTab CurrentTab => _tab;

        private UIElement BuildLayout()
        {
            var root = new Border { BorderThickness = new Thickness(1) };
            root.SetResourceReference(Border.BorderBrushProperty, Theme.Border);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });             // tabs
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });             // input
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
            grid.RowDefinitions.Add(_previewRow);                                                // preview
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });             // footer

            var top = new DockPanel { Margin = new Thickness(6, 4, 6, 2), LastChildFill = true, Background = Brushes.Transparent };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _nonProject.SetResourceReference(ForegroundProperty, Theme.Foreground);
            right.Children.Add(_nonProject);
            right.Children.Add(_filterButton);
            right.Children.Add(_previewToggle);
            right.Children.Add(_openInWindow);
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
            top.Children.Add(_tabsPanel);
            top.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource == top) DragMove(); };
            Grid.SetRow(top, 0);
            grid.Children.Add(top);

            _input.Margin = new Thickness(6, 2, 6, 4);
            _input.Padding = new Thickness(4, 3, 4, 3);
            _input.FontSize = 15;
            _input.SetResourceReference(BackgroundProperty, Theme.InputBackground);
            _input.SetResourceReference(ForegroundProperty, Theme.InputForeground);
            _input.SetResourceReference(BorderBrushProperty, Theme.InputBorder);
            _input.SetResourceReference(TextBox.CaretBrushProperty, Theme.InputForeground);
            Grid.SetRow(_input, 1);
            grid.Children.Add(_input);

            Grid.SetRow(_list, 2);
            grid.Children.Add(_list);

            _preview.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            var previewStack = new StackPanel();
            _previewHeader.SetResourceReference(TextBlock.ForegroundProperty, Theme.Gray);
            previewStack.Children.Add(_previewHeader);
            previewStack.Children.Add(_previewLines);
            _preview.Child = new ScrollViewer { Content = previewStack, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(_preview, 3);
            grid.Children.Add(_preview);

            _footer.SetResourceReference(TextBlock.ForegroundProperty, Theme.Gray);
            Grid.SetRow(_footer, 4);
            grid.Children.Add(_footer);

            root.Child = grid;
            BuildTabs();
            return root;
        }

        private void BuildTabs()
        {
            _tabsPanel.Children.Clear();
            foreach (var tab in SearchController.Tabs)
            {
                var t = tab;
                var label = new TextBlock
                {
                    Text = SearchController.TabTitle(tab),
                    Margin = new Thickness(0, 0, 14, 0),
                    Padding = new Thickness(0, 2, 0, 3),
                    Cursor = Cursors.Hand,
                    FontWeight = tab == _tab ? FontWeights.SemiBold : FontWeights.Normal,
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, tab == _tab ? Theme.Foreground : Theme.Gray);
                if (tab == _tab) label.TextDecorations = TextDecorations.Underline;
                label.MouseLeftButtonUp += (s, e) => { SwitchTab(t); _input.Focus(); };
                _tabsPanel.Children.Add(label);
            }
            bool filterable = _tab == SearchTab.Types || _tab == SearchTab.Symbols || _tab == SearchTab.Files || _tab == SearchTab.Text;
            _filterButton.Visibility = filterable ? Visibility.Visible : Visibility.Collapsed;
            _filterButton.Content = _controller.Filters.IsDefault(_tab) ? "筛选 ▾" : "筛选 ● ▾";
        }

        /// <summary>Shows the popup on a tab. Keeps and selects the previous query, or uses the editor selection.</summary>
        public void ShowOn(SearchTab tab, string initialText = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _tab = tab;
            BuildTabs();
            _previewToggle.IsChecked = _host.Options.ShowPreview;
            SetPreviewVisible(_host.Options.ShowPreview);
            PlaceWindow();

            _suppressTextChanged = true;
            if (!string.IsNullOrEmpty(initialText)) _input.Text = initialText;
            _suppressTextChanged = false;

            if (!IsVisible)
            {
                var helper = new WindowInteropHelper(this) { Owner = VsHelpers.MainWindowHandle };
                Show();
            }
            Activate();
            _input.Focus();
            _input.SelectAll();
            RunQuery();
        }

        /// <summary>Double Shift while open: toggle "include non-project items" (Rider behaviour).</summary>
        public void OnDoubleShift()
        {
            _nonProject.IsChecked = _nonProject.IsChecked != true;
        }

        public void SwitchTab(SearchTab tab)
        {
            if (_tab == tab) return;
            _tab = tab;
            BuildTabs();
            RunQuery();
        }

        private void PlaceWindow()
        {
            var bounds = ParseBounds(_host.Options.PopupBounds);
            var hwnd = VsHelpers.MainWindowHandle;
            Rect owner = SystemParameters.WorkArea;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r))
            {
                var source = PresentationSource.FromVisual(this) ?? HwndSource.FromHwnd(hwnd);
                double scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                owner = new Rect(r.Left * scale, r.Top * scale, (r.Right - r.Left) * scale, (r.Bottom - r.Top) * scale);
            }
            double w = bounds?.Width ?? Math.Max(640, owner.Width * 0.55);
            double h = bounds?.Height ?? Math.Max(380, owner.Height * 0.55);
            Width = Math.Min(w, owner.Width);
            Height = Math.Min(h, owner.Height);
            Left = owner.Left + (owner.Width - Width) / 2;
            Top = owner.Top + owner.Height * 0.12;
        }

        private static Size? ParseBounds(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var p = s.Split('x');
            if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && w > 200 && h > 150)
                return new Size(w, h);
            return null;
        }

        private void Dismiss()
        {
            _cts?.Cancel();
            _host.Options.PopupBounds = string.Format(CultureInfo.InvariantCulture, "{0:F0}x{1:F0}", ActualWidth, ActualHeight);
            _host.Options.ShowPreview = _previewToggle.IsChecked == true;
            _host.SaveOptions();
            Hide();
        }

        private void RunQuery()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            int gen = ++_generation;
            string text = _input.Text ?? string.Empty;
            var tab = _tab;
            bool nonProject = _nonProject.IsChecked == true;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                SearchResultSet result;
                try
                {
                    result = await _controller.RunAsync(text, tab, nonProject, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    result = new SearchResultSet { Footer = "搜索出错：" + ex.Message };
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (gen != _generation) return;
                _list.SetItems(result.Items);
                _footer.Text = result.Footer ?? _host.IndexStatusText ?? FooterHint();
            }).FileAndForget("UEToVS/Search");
        }

        private string FooterHint() =>
            _tab == SearchTab.All
                ? "Tab 切换分类 · Enter 打开 · Shift+Enter 分屏打开 · Ctrl+Enter 打开不关闭 · /types /files 前缀 · 文件名:行号"
                : "Tab 切换分类 · Enter 打开 · Shift+Enter 分屏打开 · Ctrl+Enter 打开不关闭";

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Tab:
                    int i = Array.IndexOf(SearchController.Tabs, _tab);
                    int n = SearchController.Tabs.Length;
                    SwitchTab(SearchController.Tabs[(i + (mods.HasFlag(ModifierKeys.Shift) ? n - 1 : 1)) % n]);
                    e.Handled = true;
                    break;
                case Key.Down:
                    if (mods.HasFlag(ModifierKeys.Alt)) { HistoryStep(+1); e.Handled = true; break; }
                    _list.Move(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    if (mods.HasFlag(ModifierKeys.Alt)) { HistoryStep(-1); e.Handled = true; break; }
                    _list.Move(-1);
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    _list.Move(10);
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    _list.Move(-10);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    var mode = mods.HasFlag(ModifierKeys.Shift) ? OpenMode.SplitGroup
                             : mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Alt) ? OpenMode.KeepOpen
                             : OpenMode.Normal;
                    if (_debounce.IsEnabled)
                    {
                        // Enter typed faster than the debounce: run the query first, then open the top hit
                        _debounce.Stop();
                        RunQueryThenActivate(mode);
                    }
                    else _list.ActivateSelected(mode);
                    e.Handled = true;
                    break;
            }
        }

        private void RunQueryThenActivate(OpenMode mode)
        {
            RunQuery();
            int gen = _generation;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                for (int i = 0; i < 40; i++)
                {
                    await System.Threading.Tasks.Task.Delay(25);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (gen != _generation) return;
                    if (_list.SelectedItem != null) { _list.ActivateSelected(mode); return; }
                }
            }).FileAndForget("UEToVS/EnterActivate");
        }

        private void HistoryStep(int dir)
        {
            var history = (_host.Options.RecentQueries ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (history.Length == 0) return;
            _historyIndex = Math.Max(0, Math.Min(history.Length - 1, _historyIndex + dir));
            _suppressTextChanged = true;
            _input.Text = history[_historyIndex];
            _input.CaretIndex = _input.Text.Length;
            _suppressTextChanged = false;
            RunQuery();
        }

        private void RememberQuery()
        {
            string q = (_input.Text ?? "").Trim();
            if (q.Length == 0) return;
            var list = (_host.Options.RecentQueries ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            list.Remove(q);
            list.Insert(0, q);
            _host.Options.RecentQueries = string.Join("\n", list.Take(30));
        }

        private void OnItemActivated(SearchItem item, OpenMode mode)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (item.Kind)
            {
                case ItemKind.More:
                case ItemKind.Header:
                    if (item.TargetTab.HasValue) SwitchTab(item.TargetTab.Value);
                    _input.Focus();
                    return;
                case ItemKind.PrefixCommand:
                    _input.Text = item.Title + " ";
                    _input.CaretIndex = _input.Text.Length;
                    _input.Focus();
                    return;
            }
            if (item.Activate == null) return;
            RememberQuery();
            if (mode != OpenMode.KeepOpen) Dismiss();
            bool close = item.Activate(mode);
            if (mode == OpenMode.KeepOpen || !close)
            {
                if (!IsVisible) ShowOn(_tab);
                if (item.IsToggle) RunQuery();
                Activate();
                _input.Focus();
            }
        }

        private void ShowFilterMenu()
        {
            var menu = new ContextMenu { PlacementTarget = _filterButton, Placement = PlacementMode.Bottom };
            var f = _controller.Filters;
            MenuItem Check(string header, bool value, Action<bool> set)
            {
                var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = value, StaysOpenOnClick = true };
                mi.Click += (s, e) => { set(mi.IsChecked); BuildTabs(); RunQuery(); };
                return mi;
            }
            switch (_tab)
            {
                case SearchTab.Types:
                    menu.Items.Add(Check("UCLASS", f.UClass, v => f.UClass = v));
                    menu.Items.Add(Check("USTRUCT", f.UStruct, v => f.UStruct = v));
                    menu.Items.Add(Check("UENUM", f.UEnum, v => f.UEnum = v));
                    menu.Items.Add(Check("UINTERFACE", f.UInterface, v => f.UInterface = v));
                    menu.Items.Add(Check("普通 C++ 类型", f.PlainCpp, v => f.PlainCpp = v));
                    break;
                case SearchTab.Symbols:
                    menu.Items.Add(Check("只看 UFUNCTION / UPROPERTY", f.OnlyReflected, v => f.OnlyReflected = v));
                    break;
                case SearchTab.Files:
                    foreach (var group in new[] { ("全部", (string[])null), ("C++ (.h .cpp .inl)", new[] { "h", "hpp", "inl", "cpp" }), ("配置 (.ini .uproject .uplugin .Build.cs)", new[] { "ini", "uproject", "uplugin", "cs" }), ("着色器 (.usf .ush)", new[] { "usf", "ush" }) })
                    {
                        var g = group;
                        bool selected = (f.FileExtensions == null && g.Item2 == null) || (f.FileExtensions != null && g.Item2 != null && f.FileExtensions.SequenceEqual(g.Item2));
                        var mi = new MenuItem { Header = g.Item1, IsCheckable = true, IsChecked = selected };
                        mi.Click += (s, e) => { f.FileExtensions = g.Item2; BuildTabs(); RunQuery(); };
                        menu.Items.Add(mi);
                    }
                    break;
                case SearchTab.Text:
                    menu.Items.Add(Check("区分大小写", f.CaseSensitive, v => f.CaseSensitive = v));
                    menu.Items.Add(Check("全字匹配", f.WholeWord, v => f.WholeWord = v));
                    menu.Items.Add(Check("正则表达式", f.Regex, v => f.Regex = v));
                    var maskItem = new MenuItem { Header = "文件掩码：" + (string.IsNullOrEmpty(f.FileMask) ? "（全部）" : f.FileMask), StaysOpenOnClick = false };
                    maskItem.Click += (s, e) =>
                    {
                        var current = f.FileMask ?? "*.h;*.cpp";
                        f.FileMask = string.IsNullOrEmpty(f.FileMask) ? "*.h;*.cpp" : null;
                        BuildTabs();
                        RunQuery();
                    };
                    maskItem.ToolTip = "点击在“全部”和“*.h;*.cpp”之间切换";
                    menu.Items.Add(maskItem);
                    break;
            }
            _menuOpen = true;
            menu.Closed += (s, e) => { _menuOpen = false; Activate(); _input.Focus(); };
            menu.IsOpen = true;
        }

        private void SetPreviewVisible(bool visible)
        {
            _previewRow.Height = visible ? new GridLength(200) : new GridLength(0);
            _preview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) UpdatePreview(_list.SelectedItem);
        }

        private void UpdatePreview(SearchItem item)
        {
            if (_previewToggle.IsChecked != true) return;
            _previewLines.Children.Clear();
            _previewHeader.Text = string.Empty;
            if (item?.FilePath == null) return;
            int target = Math.Max(0, item.Line);
            int first = Math.Max(0, target - 4);
            var lines = VsHelpers.ReadLines(item.FilePath, first, 11);
            _previewHeader.Text = item.FilePath + (item.Line >= 0 ? ":" + (item.Line + 1) : "");
            for (int i = 0; i < lines.Length; i++)
            {
                int lineNo = first + i;
                var row = new DockPanel();
                var num = Theme.Text((lineNo + 1).ToString(CultureInfo.InvariantCulture).PadLeft(5) + "  ", Theme.Gray);
                num.FontFamily = new FontFamily("Consolas");
                row.Children.Add(num);
                var code = Theme.Text(lines[i].Replace("\t", "    "));
                code.FontFamily = new FontFamily("Consolas");
                row.Children.Add(code);
                if (lineNo == target && item.Line >= 0) row.SetResourceReference(Panel.BackgroundProperty, Theme.HeaderBackground);
                _previewLines.Children.Add(row);
            }
        }

        private void OpenAllInToolWindow()
        {
            var items = _list.Items.Where(i => i.IsSelectable && i.Kind != ItemKind.More && i.FilePath != null).ToList();
            _openInToolWindow?.Invoke(items, $"{SearchController.TabTitle(_tab)}: {_input.Text}");
            Dismiss();
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    }
}
