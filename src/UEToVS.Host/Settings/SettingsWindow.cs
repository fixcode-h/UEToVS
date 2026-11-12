using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Settings
{
    /// <summary>What the settings window needs from the package (kept narrow so the window stays testable by hand in the gallery).</summary>
    internal interface ISettingsWindowHost
    {
        IReadOnlyList<SettingDescriptor> Schema { get; }
        Dictionary<string, string> LoadValues();
        void SaveValues(IReadOnlyDictionary<string, string> values);
        /// <summary>Runs a descriptor's action; returns a short result text for the row (null = nothing to show).</summary>
        System.Threading.Tasks.Task<string> RunActionAsync(string actionId);
        /// <summary>Text of an Info row.</summary>
        string InfoText(string infoId);
        /// <summary>Custom page content (外观, 快捷键), or null.</summary>
        ISettingsCustomPage CreateCustomPage(string categoryId);
    }

    /// <summary>A page with its own editor (appearance panel, keymap). Apply/Cancel/Reset follow the window's buttons.</summary>
    internal interface ISettingsCustomPage
    {
        UIElement Element { get; }
        void Apply();
        void Cancel();
        void ResetToDefaults();
    }

    /// <summary>
    /// "UEToVS 设置" window (VS 2026 style, <see cref="UtilityWindow"/>): left category rail, search box on top (filters by
    /// setting name/description/keywords in both languages), generated rows on the right, footer "恢复默认 / 取消 / 应用 /
    /// 确定". Rows come from the declarative schema (<see cref="HostSettingsSchema"/>): adding a descriptor adds a row.
    /// Edits are kept in a working copy until Apply/OK; Info rows refresh every second while the window is open.
    /// </summary>
    internal sealed class SettingsWindow : UtilityWindow
    {
        private static readonly Logger L = Log.For("SettingsWindow");
        private readonly ISettingsWindowHost _host;
        private readonly IReadOnlyList<SettingDescriptor> _schema;
        private Dictionary<string, string> _committed;
        private Dictionary<string, string> _working;
        private readonly StackPanel _nav = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        private readonly ContentControl _page = new ContentControl { Focusable = false };
        private readonly SearchInput _search = new SearchInput(false);
        private readonly UiText _status = new UiText((string)null, TextVariant.Secondary) { VerticalAlignment = VerticalAlignment.Center };
        private readonly Dictionary<string, ISettingsCustomPage> _custom = new Dictionary<string, ISettingsCustomPage>(StringComparer.Ordinal);
        private readonly List<(SettingDescriptor D, UiText Text)> _infoRows = new List<(SettingDescriptor, UiText)>();
        private readonly DispatcherTimer _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private string _category = HostSettingsSchema.General;
        private bool _closingByButton;
        private bool English => Loc.IsEnglish;

        public SettingsWindow(ISettingsWindowHost host, string initialCategory = null)
            : base("settings", () => SettingsText.WindowTitle, new Size(900, 640))
        {
            _host = host;
            _schema = host.Schema;
            _committed = host.LoadValues();
            _working = new Dictionary<string, string>(_committed, StringComparer.Ordinal);
            MinVisibleWidth = 680;
            MinVisibleHeight = 420;
            if (!string.IsNullOrEmpty(initialCategory)) _category = initialCategory;

            _search.InputBox.Placeholder = SettingsText.SearchPlaceholder;
            _search.TextChanged += (s, q) => Safe.Execute(L, "settings.search", () => { RebuildNav(); ShowPage(); });

            var left = new DockPanel { Width = 220 };
            var searchHost = new Border { Child = _search, Padding = new Thickness(10, 10, 10, 6) };
            DockPanel.SetDock(searchHost, Dock.Top);
            left.Children.Add(searchHost);
            left.Children.Add(new UiScrollViewer { Content = _nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var leftHost = new Border { Child = left, BorderThickness = new Thickness(0, 0, 1, 0) };
            leftHost.Bind(Border.BackgroundProperty, Tokens.SurfaceNav);
            leftHost.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);

            var footer = BuildFooter();
            var right = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(footer, Dock.Bottom);
            right.Children.Add(footer);
            right.Children.Add(_page);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(right, 1);
            grid.Children.Add(leftHost);
            grid.Children.Add(right);
            var surface = new Border { Child = grid };
            surface.Bind(Border.BackgroundProperty, Tokens.SurfacePopup);
            WindowContent = surface;

            UEToVS.UI.Strings.Track(this, () => Safe.Execute(L, "settings.relabel", () =>
            {
                _search.InputBox.Placeholder = SettingsText.SearchPlaceholder;
                RebuildNav();
                ShowPage();
                UpdateStatus();
            }));
            _infoTimer.Tick += (s, e) => Safe.Execute(L, "settings.info.refresh", RefreshInfo);
            Loaded += (s, e) => Safe.Execute(L, "settings.loaded", () => { _infoTimer.Start(); _search.InputBox.Focus(); });
            Closing += (s, e) => Safe.Execute(L, "settings.closing", () =>
            {
                _infoTimer.Stop();
                if (!_closingByButton) CancelCustomPages(); // title-bar close / Esc = cancel
            });
            PreviewKeyDown += (s, e) => Safe.Execute(L, "settings.key", () =>
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.InputBox.Focus(); e.Handled = true; }
            });
            L.Info("settings.window.open", null, ("category", _category), ("descriptors", _schema.Count));
        }

        /// <summary>Selects a page (e.g. "indexing" from the performance panel).</summary>
        public void ShowCategory(string id)
        {
            if (string.IsNullOrEmpty(id) || !HostSettingsSchema.Categories.Any(c => c.Id == id)) return;
            _category = id;
            _search.Text = string.Empty;
            RebuildNav();
            ShowPage();
        }

        // ------------------------------------------------------------------ navigation

        private bool CategoryMatches(SettingCategory c, string q) =>
            string.IsNullOrWhiteSpace(q) || c.TitleZh.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || c.TitleEn.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
            || _schema.Any(d => d.Category == c.Id && SettingsModel.Matches(d, q));

        private void RebuildNav()
        {
            _nav.Children.Clear();
            string q = _search.Text;
            foreach (var c in HostSettingsSchema.Categories)
            {
                var cat = c;
                bool match = CategoryMatches(c, q);
                bool selected = string.IsNullOrWhiteSpace(q) && c.Id == _category;
                var text = new UiText(c.Title(English), selected ? TextVariant.Strong : TextVariant.Body) { VerticalAlignment = VerticalAlignment.Center };
                var indicator = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
                indicator.Bind(Border.BackgroundProperty, selected ? Tokens.SelectionIndicator : Tokens.SurfaceNav);
                indicator.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(indicator);
                row.Children.Add(text);
                var item = new Border { Child = row, Padding = new Thickness(2, 0, 6, 0), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null };
                item.Bind(FrameworkElement.HeightProperty, Tokens.ControlHeight);
                item.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
                if (selected) item.Bind(Border.BackgroundProperty, Tokens.SelectionBackground);
                else item.Background = Brushes.Transparent;
                item.Opacity = match ? 1 : 0.45;
                System.Windows.Automation.AutomationProperties.SetName(item, c.Title(English));
                item.MouseEnter += (s, e) => { if (!selected) item.Bind(Border.BackgroundProperty, Tokens.SurfaceHover); };
                item.MouseLeave += (s, e) => { if (!selected) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += (s, e) => Safe.Execute(L, "settings.nav", () => ShowCategory(cat.Id));
                // Fluent focus visual: a ring only when the item was reached with the keyboard.
                // The 2px border is always there (transparent) so focusing never shifts the text.
                item.BorderThickness = new Thickness(2);
                item.BorderBrush = Brushes.Transparent;
                item.GotKeyboardFocus += (s, e) =>
                {
                    if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) item.Bind(Border.BorderBrushProperty, Tokens.FocusStroke);
                };
                item.LostKeyboardFocus += (s, e) => item.BorderBrush = Brushes.Transparent;
                item.KeyDown += (s, e) => Safe.Execute(L, "settings.nav.key", () =>
                {
                    if (e.Key == Key.Enter || e.Key == Key.Space) { ShowCategory(cat.Id); e.Handled = true; }
                });
                _nav.Children.Add(item);
            }
        }

        // ------------------------------------------------------------------ pages

        private void ShowPage()
        {
            _infoRows.Clear();
            string q = _search.Text;
            var body = new StackPanel { Margin = new Thickness(28, 18, 28, 24) };
            if (!string.IsNullOrWhiteSpace(q))
            {
                int n = 0;
                foreach (var c in HostSettingsSchema.Categories)
                {
                    var hits = _schema.Where(d => d.Category == c.Id && SettingsModel.Matches(d, q)).ToList();
                    if (hits.Count == 0) continue;
                    body.Children.Add(new UiText(c.Title(English), TextVariant.Title) { Margin = new Thickness(0, n == 0 ? 0 : 18, 0, 6) });
                    AddRows(body, hits);
                    n += hits.Count;
                }
                if (n == 0) body.Children.Add(new UiText(SettingsText.NoMatches(q.Trim()), TextVariant.Secondary));
            }
            else
            {
                var cat = HostSettingsSchema.Categories.First(c => c.Id == _category);
                body.Children.Add(new UiText(cat.Title(English), TextVariant.Title) { Margin = new Thickness(0, 0, 0, 10) });
                var custom = GetCustomPage(_category);
                if (custom != null)
                {
                    // Custom pages fill the remaining height (they scroll themselves).
                    var dock = new DockPanel { Margin = body.Margin, LastChildFill = true };
                    body.Margin = new Thickness(0);
                    DockPanel.SetDock(body, Dock.Top);
                    dock.Children.Add(body);
                    var rows = _schema.Where(d => d.Category == _category).ToList();
                    if (rows.Count > 0) AddRows(body, rows);
                    dock.Children.Add(custom.Element);
                    _page.Content = dock;
                    RefreshInfo();
                    return;
                }
                AddRows(body, _schema.Where(d => d.Category == _category).ToList());
            }
            _page.Content = new UiScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            RefreshInfo();
        }

        private ISettingsCustomPage GetCustomPage(string id)
        {
            if (_custom.TryGetValue(id, out var p)) return p;
            p = Safe.Execute(L, "settings.custom.create", () => _host.CreateCustomPage(id), null);
            if (p != null) _custom[id] = p;
            return p;
        }

        private void AddRows(Panel body, IList<SettingDescriptor> rows)
        {
            string group = null;
            foreach (var d in rows)
            {
                var g = d.Group(English);
                if (!string.IsNullOrEmpty(g) && g != group)
                {
                    group = g;
                    var header = new UiText(g, TextVariant.Strong) { Margin = new Thickness(0, body.Children.Count > 1 ? 18 : 4, 0, 6) };
                    body.Children.Add(header);
                }
                body.Children.Add(BuildRow(d));
            }
        }

        private UIElement BuildRow(SettingDescriptor d)
        {
            var labels = new StackPanel();
            labels.Children.Add(new UiText(d.Title(English), TextVariant.Body) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
            var desc = d.Description(English);
            if (!string.IsNullOrEmpty(desc))
                labels.Children.Add(new UiText(desc, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) });
            if (d.RequiresRestart)
                labels.Children.Add(new UiText(SettingsText.RestartHint, TextVariant.Caption) { Margin = new Thickness(0, 2, 0, 0) });
            // VS 2026 settings card: 8px corners, editor right-aligned and vertically centred at its natural size.
            return new SettingsCard(labels, BuildEditor(d, labels));
        }

        private string Value(SettingDescriptor d) => _working.TryGetValue(d.Key, out var v) && v != null ? v : d.Default;

        private void Set(SettingDescriptor d, string value)
        {
            _working[d.Key] = SettingsModel.Normalize(d, value, out _);
            UpdateStatus();
        }

        private UIElement BuildEditor(SettingDescriptor d, Panel labels)
        {
            switch (d.Kind)
            {
                case SettingKind.Bool:
                {
                    var t = new UiToggle((string)null, Value(d) == "true");
                    System.Windows.Automation.AutomationProperties.SetName(t, d.Title(English));
                    t.Checked += (s, e) => Safe.Execute(L, "settings.edit", () => Set(d, "true"));
                    t.Unchecked += (s, e) => Safe.Execute(L, "settings.edit", () => Set(d, "false"));
                    return t;
                }
                case SettingKind.Int:
                {
                    int.TryParse(Value(d), out int v);
                    var panel = new StackPanel { Orientation = Orientation.Horizontal };
                    var auto = new UiText((string)null, TextVariant.Secondary) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                    void UpdateAuto(int x) => auto.Text = x == 0 && d.AutoLabelZh != null ? (English ? d.AutoLabelEn : d.AutoLabelZh) : string.Empty;
                    var stepper = new NumberStepper(d.Min == int.MinValue ? -1e9 : d.Min, d.Max == int.MaxValue ? 1e9 : d.Max, 1, v) { AccessibleName = d.Title(English) };
                    UpdateAuto(v);
                    stepper.ValueChanged += (s, e) => Safe.Execute(L, "settings.edit", () =>
                    {
                        int x = (int)Math.Round(stepper.Value);
                        UpdateAuto(x);
                        Set(d, x.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    });
                    panel.Children.Add(auto);
                    panel.Children.Add(stepper);
                    return panel;
                }
                case SettingKind.Choice:
                {
                    var options = d.Options ?? Array.Empty<SettingOption>();
                    int index = Math.Max(0, options.ToList().FindIndex(o => string.Equals(o.Value, Value(d), StringComparison.OrdinalIgnoreCase)));
                    if (options.Count <= 4)
                    {
                        var seg = new SegmentedControl(options.Select(o => English ? o.TitleEn : o.TitleZh).ToArray());
                        seg.SelectedIndex = index;
                        seg.SelectionChanged += (s, e) => Safe.Execute(L, "settings.edit", () => Set(d, options[seg.SelectedIndex].Value));
                        return seg;
                    }
                    var dd = new Dropdown { Items = options.Select(o => English ? o.TitleEn : o.TitleZh).ToList(), MinWidth = 160 };
                    dd.SelectedIndex = index;
                    dd.SelectionChanged += (s, e) => Safe.Execute(L, "settings.edit", () => Set(d, options[dd.SelectedIndex].Value));
                    return dd;
                }
                case SettingKind.Text:
                case SettingKind.Path:
                {
                    var box = new TextInput { Text = Value(d), Width = 320, Placeholder = d.Kind == SettingKind.Path ? (English ? "(default)" : "（默认）") : string.Empty };
                    System.Windows.Automation.AutomationProperties.SetName(box, d.Title(English));
                    box.TextChanged += (s, e) => Safe.Execute(L, "settings.edit", () => Set(d, box.Text));
                    return box;
                }
                case SettingKind.Action:
                {
                    var result = new UiText((string)null, TextVariant.Secondary) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                    var button = new UiButton(d.Title(English)) { MinWidth = 120 };
                    button.Click += (s, e) => Safe.RunAsync(L, "settings.action", async () =>
                    {
                        button.IsEnabled = false;
                        try
                        {
                            L.Info("settings.action.run", null, ("key", d.Key), ("action", d.ActionId));
                            result.Text = await _host.RunActionAsync(d.ActionId) ?? string.Empty;
                        }
                        finally
                        {
                            button.IsEnabled = true;
                        }
                        RefreshInfo();
                    }, ("action", d.ActionId));
                    var panel = new StackPanel { Orientation = Orientation.Horizontal };
                    panel.Children.Add(result);
                    panel.Children.Add(button);
                    return panel;
                }
                case SettingKind.Info:
                {
                    var text = new UiText((string)null, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
                    labels.Children.Add(text);
                    _infoRows.Add((d, text));
                    return null;
                }
            }
            return null;
        }

        private void RefreshInfo()
        {
            foreach (var (d, text) in _infoRows)
            {
                string t = _host.InfoText(d.ActionId) ?? string.Empty;
                if (text.Text != t) text.Text = t;
            }
        }

        // ------------------------------------------------------------------ footer

        private UIElement BuildFooter()
        {
            var reset = new UiButton(() => SettingsText.ResetDefaults, ButtonKind.Ghost) { MinWidth = 96 };
            var cancel = new UiButton(() => SettingsText.Cancel) { MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
            var apply = new UiButton(() => SettingsText.Apply) { MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
            var ok = new UiButton(() => SettingsText.Ok, ButtonKind.Primary) { MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
            reset.Click += (s, e) => Safe.Execute(L, "settings.reset", ResetPage);
            cancel.Click += (s, e) => Safe.Execute(L, "settings.cancel", () => { CancelCustomPages(); _closingByButton = true; Close(); });
            apply.Click += (s, e) => Safe.Execute(L, "settings.apply", Apply);
            ok.Click += (s, e) => Safe.Execute(L, "settings.ok", () => { Apply(); _closingByButton = true; Close(); });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            buttons.Children.Add(ok);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_status, 1);
            _status.Margin = new Thickness(12, 0, 12, 0);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(reset);
            grid.Children.Add(_status);
            grid.Children.Add(buttons);
            var host = new Border { Child = grid, Padding = new Thickness(16, 10, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
            host.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            return host;
        }

        private void UpdateStatus()
        {
            bool dirty = SettingsModel.Diff(_schema, _committed, _working).Count > 0;
            _status.Text = dirty ? SettingsText.Unsaved : string.Empty;
        }

        private void Apply()
        {
            var changed = SettingsModel.Diff(_schema, _committed, _working);
            if (changed.Count > 0) _host.SaveValues(_working);
            foreach (var p in _custom.Values) Safe.Execute(L, "settings.custom.apply", p.Apply);
            _committed = new Dictionary<string, string>(_working, StringComparer.Ordinal);
            L.Info("settings.window.apply", null, ("changed", changed));
            _status.Text = SettingsText.Saved;
        }

        private void CancelCustomPages()
        {
            foreach (var p in _custom.Values) Safe.Execute(L, "settings.custom.cancel", p.Cancel);
            L.Info("settings.window.cancel", null, ("pending", SettingsModel.Diff(_schema, _committed, _working)));
        }

        /// <summary>"恢复默认": the visible page (or the search hits) back to defaults in the working copy; applied with Apply/OK.</summary>
        private void ResetPage()
        {
            string q = _search.Text;
            var targets = string.IsNullOrWhiteSpace(q)
                ? _schema.Where(d => d.Category == _category && d.HasValue)
                : _schema.Where(d => d.HasValue && SettingsModel.Matches(d, q));
            int n = 0;
            foreach (var d in targets) { _working[d.Key] = d.Default; n++; }
            if (string.IsNullOrWhiteSpace(q) && _custom.TryGetValue(_category, out var custom)) custom.ResetToDefaults();
            L.Info("settings.window.reset", null, ("category", _category), ("query", q), ("values", n));
            ShowPage();
            UpdateStatus();
            if (_status.Text.Length == 0) _status.Text = SettingsText.ResetConfirm;
        }
    }
}
