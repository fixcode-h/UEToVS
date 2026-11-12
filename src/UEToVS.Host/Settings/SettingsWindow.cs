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
using UEToVS.UI.Molecules;
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
        /// <summary>Plain values behind the index status card (engine state, phase, counts, last error, threads).</summary>
        IndexStatusInput IndexStatus();
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
    /// "UEToVS 设置" window (VS 2026 / Fluent settings layout, <see cref="UtilityWindow"/>): left category rail with a
    /// search box on top (filters by setting name/description/keywords in both languages), generated setting cards on the
    /// right (page title, group headers, 4px between cards, 24px between groups — <see cref="SettingsPageLayout"/>), and a
    /// separated footer ("恢复默认" as a subtle button on the left; 取消 / 应用 / 确定 on the right). Rows come from the
    /// declarative schema (<see cref="HostSettingsSchema"/>): adding a descriptor adds a card. Edits are kept in a working
    /// copy until Apply/OK; Info rows and the index status card refresh every second while the window is open.
    ///
    /// The selected category and the visible page are always in sync: the page is built first and the rail is redrawn
    /// after it; a page that fails to build is replaced by an inline error card (settings.page.show is logged with the
    /// category, the time and the exception), never silently left on the previous page.
    /// </summary>
    internal sealed class SettingsWindow : UtilityWindow
    {
        private static readonly Logger L = Log.For("SettingsWindow");
        private const string OpenCurrentLogAction = "uetovs.diagnostics.openCurrentLog";
        private readonly ISettingsWindowHost _host;
        private readonly IReadOnlyList<SettingDescriptor> _schema;
        private Dictionary<string, string> _committed;
        private Dictionary<string, string> _working;
        private readonly StackPanel _nav = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
        private readonly ContentControl _page = new ContentControl { Focusable = false };
        private readonly SearchInput _search = new SearchInput(false);
        private readonly UiText _status = new UiText((string)null, TextVariant.Secondary) { VerticalAlignment = VerticalAlignment.Center };
        private readonly Dictionary<string, ISettingsCustomPage> _custom = new Dictionary<string, ISettingsCustomPage>(StringComparer.Ordinal);
        private readonly List<(SettingDescriptor D, UiText Text)> _infoRows = new List<(SettingDescriptor, UiText)>();
        private readonly List<IndexStatusCard> _statusCards = new List<IndexStatusCard>();
        private readonly DispatcherTimer _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private string _category = HostSettingsSchema.General;
        private string _customError;
        private bool _closingByButton;
        private bool English => Loc.IsEnglish;

        public SettingsWindow(ISettingsWindowHost host, string initialCategory = null)
            : base("settings", () => SettingsText.WindowTitle, new Size(960, 680))
        {
            _host = host;
            _schema = host.Schema;
            _committed = host.LoadValues();
            _working = new Dictionary<string, string>(_committed, StringComparer.Ordinal);
            MinVisibleWidth = 720;
            MinVisibleHeight = 440;
            if (!string.IsNullOrEmpty(initialCategory) && HostSettingsSchema.Categories.Any(c => c.Id == initialCategory)) _category = initialCategory;

            _search.InputBox.Placeholder = SettingsText.SearchPlaceholder;
            _search.TextChanged += (s, q) => Safe.Execute(L, "settings.search", () => { ShowPage(); RebuildNav(); });

            var left = new DockPanel { Width = 232 };
            var searchHost = new Border { Child = _search, Padding = new Thickness(12, 12, 12, 10) };
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
                ShowPage();
                RebuildNav();
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
            if (string.IsNullOrEmpty(id) || !HostSettingsSchema.Categories.Any(c => c.Id == id))
            {
                L.Warn("settings.nav.unknown", null, ("category", id));
                return;
            }
            string from = _category;
            _category = id;
            _search.Text = string.Empty;
            // Page first, then the rail: ShowPage never leaves the old page up (it shows an error card instead), so the
            // highlighted category and the visible page always match.
            ShowPage();
            RebuildNav();
            L.Info("settings.nav", null, ("from", from), ("to", id));
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
                // Fluent NavigationView: 3x16 accent pill + selection fill on the selected item; hover = subtle fill only
                // (kept distinct so a hovered item never looks selected).
                var indicator = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                indicator.Bind(Border.BackgroundProperty, Tokens.SelectionIndicator);
                indicator.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(indicator);
                row.Children.Add(text);
                var item = new Border { Child = row, Padding = new Thickness(2, 0, 8, 0), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null };
                item.MinHeight = 32;
                item.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
                if (selected) item.Bind(Border.BackgroundProperty, Tokens.SelectionBackground);
                else item.Background = Brushes.Transparent;
                item.Opacity = match ? 1 : 0.45;
                System.Windows.Automation.AutomationProperties.SetName(item, c.Title(English));
                System.Windows.Automation.AutomationProperties.SetItemStatus(item, selected ? "selected" : string.Empty);
                item.MouseEnter += (s, e) => { if (!selected) item.Bind(Border.BackgroundProperty, Tokens.SurfaceHover); };
                item.MouseLeave += (s, e) => { if (!selected) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += (s, e) => Safe.Execute(L, "settings.nav.click", () => ShowCategory(cat.Id), ("category", cat.Id));
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

        private static Thickness PageMargin => new Thickness(SettingsPageLayout.PagePaddingX, SettingsPageLayout.PagePaddingTop,
                                                             SettingsPageLayout.PagePaddingX, SettingsPageLayout.PagePaddingBottom);

        private static UiText PageTitle(string text) =>
            new UiText(text, TextVariant.Display) { Margin = new Thickness(0, 0, 0, SettingsPageLayout.TitleGapBelow) };

        private static StackPanel PageBody() => new StackPanel
        {
            Margin = PageMargin, MaxWidth = SettingsPageLayout.MaxContentWidth + 2 * SettingsPageLayout.PagePaddingX,
        };

        /// <summary>
        /// Shows the selected category (or the search hits). Never throws and never keeps the previous page: a failure
        /// is logged (settings.page.show, with category, ms and the exception) and an inline error card replaces the page.
        /// </summary>
        private void ShowPage()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string category = string.IsNullOrWhiteSpace(_search.Text) ? _category : "search";
            try
            {
                ShowPageCore();
                L.Write(LogLevel.Debug, "settings.page.show", null, null, sw.Elapsed.TotalMilliseconds,
                        new (string, object)[] { ("category", category), ("ok", true) });
            }
            catch (Exception ex)
            {
                L.Write(LogLevel.Error, "settings.page.show", "page failed to build", ex, sw.Elapsed.TotalMilliseconds,
                        new (string, object)[] { ("category", category), ("ok", false), ("error", ex.GetType().Name + ": " + ex.Message) });
                Safe.Execute(L, "settings.page.errorCard", () => ShowErrorPage(ex));
            }
        }

        /// <summary>Inline error card for a page that could not be built (keeps the window usable; retry rebuilds it).</summary>
        private void ShowErrorPage(Exception ex)
        {
            _infoRows.Clear();
            _statusCards.Clear();
            var cat = HostSettingsSchema.Categories.FirstOrDefault(c => c.Id == _category);
            var body = PageBody();
            if (cat != null) body.Children.Add(PageTitle(cat.Title(English)));
            var banner = new Banner(Severity.Error, SettingsText.PageFailed(ex?.Message)) { CanClose = false };
            banner.AddAction(() => SettingsText.Retry, () => Safe.Execute(L, "settings.page.retry", () =>
            {
                DropCustomPage(_category);
                ShowPage();
            }, ("category", _category)));
            banner.AddAction(() => SettingsText.ViewLog, () => Safe.RunAsync(L, "settings.page.openLog", async () =>
            {
                await _host.RunActionAsync(OpenCurrentLogAction);
            }));
            body.Children.Add(banner);
            _page.Content = new UiScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        private void DropCustomPage(string id)
        {
            if (!_custom.TryGetValue(id, out var p)) return;
            _custom.Remove(id);
            Safe.Execute(L, "settings.custom.drop", p.Cancel, ("category", id));
            if (p.Element != null) DetachFromParent(p.Element);
        }

        /// <summary>
        /// Removes <paramref name="element"/> from its current parent. Custom pages are cached and re-added to a new
        /// DockPanel each time their category is shown; WPF refuses to add an element that is still the child of the
        /// previous (now off-screen) DockPanel ("Specified element is already the logical child of another element").
        /// That exception used to escape ShowPage after the rail had already been redrawn: 外观 highlighted, old page kept.
        /// </summary>
        private static void DetachFromParent(UIElement element)
        {
            DependencyObject parent = LogicalTreeHelper.GetParent(element) ?? VisualTreeHelper.GetParent(element);
            switch (parent)
            {
                case Panel p: p.Children.Remove(element); break;
                case Decorator d when ReferenceEquals(d.Child, element): d.Child = null; break;
                case ContentControl c when ReferenceEquals(c.Content, element): c.Content = null; break;
                case ContentPresenter cp when ReferenceEquals(cp.Content, element): cp.Content = null; break;
            }
        }

        private void ShowPageCore()
        {
            _infoRows.Clear();
            _statusCards.Clear();
            string q = _search.Text;
            var body = PageBody();
            if (!string.IsNullOrWhiteSpace(q))
            {
                int n = 0;
                foreach (var c in HostSettingsSchema.Categories)
                {
                    var hits = _schema.Where(d => d.Category == c.Id && SettingsModel.Matches(d, q)).ToList();
                    if (hits.Count == 0) continue;
                    body.Children.Add(new UiText(c.Title(English), TextVariant.Title)
                    {
                        Margin = new Thickness(0, SettingsPageLayout.SearchCategoryTop(n == 0), 0, SettingsPageLayout.GroupHeaderGapBelow),
                    });
                    AddRows(body, hits, true);
                    n += hits.Count;
                }
                if (n == 0) body.Children.Add(new UiText(SettingsText.NoMatches(q.Trim()), TextVariant.Secondary));
                _page.Content = new UiScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                RefreshInfo();
                return;
            }

            var cat = HostSettingsSchema.Categories.First(c => c.Id == _category);
            body.Children.Add(PageTitle(cat.Title(English)));
            var custom = GetCustomPage(_category);
            if (custom == null && IsCustomCategory(_category))
                throw new InvalidOperationException(_customError ?? "custom page unavailable: " + _category);
            if (custom != null)
            {
                if (custom.Element == null) throw new InvalidOperationException("custom page has no content: " + _category);
                DetachFromParent(custom.Element);
                // Custom pages fill the remaining height (they scroll themselves); the page padding is shared.
                var m = PageMargin;
                var dock = new DockPanel { Margin = new Thickness(m.Left, m.Top, m.Right, 0), LastChildFill = true };
                body.Margin = new Thickness(0);
                body.MaxWidth = double.PositiveInfinity;
                DockPanel.SetDock(body, Dock.Top);
                dock.Children.Add(body);
                var rows = _schema.Where(d => d.Category == _category).ToList();
                if (rows.Count > 0) AddRows(body, rows, false);
                dock.Children.Add(custom.Element);
                _page.Content = dock;
                RefreshInfo();
                return;
            }
            AddRows(body, _schema.Where(d => d.Category == _category).ToList(), false);
            _page.Content = new UiScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            RefreshInfo();
        }

        private static bool IsCustomCategory(string id) => id == HostSettingsSchema.Appearance || id == HostSettingsSchema.Keymap;

        private ISettingsCustomPage GetCustomPage(string id)
        {
            if (_custom.TryGetValue(id, out var p)) return p;
            _customError = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                p = _host.CreateCustomPage(id);
                if (p == null && IsCustomCategory(id)) _customError = "no provider for " + id;
                L.Info("settings.custom.create", null, ("category", id), ("ok", p != null), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            }
            catch (Exception ex)
            {
                _customError = ex.GetType().Name + ": " + ex.Message;
                L.Error("settings.custom.create", "custom page failed", ex, ("category", id), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
                p = null;
            }
            if (p != null) _custom[id] = p;
            return p;
        }

        /// <summary>Cards grouped under semibold group headers: more space above a header than below it.</summary>
        private void AddRows(Panel body, IList<SettingDescriptor> rows, bool searchResults)
        {
            string group = null;
            bool firstGroup = true;
            foreach (var d in rows)
            {
                var g = d.Group(English);
                if (!string.IsNullOrEmpty(g) && g != group)
                {
                    group = g;
                    var header = new UiText(g, searchResults ? TextVariant.Strong : TextVariant.Subtitle)
                    {
                        Margin = new Thickness(0, SettingsPageLayout.GroupHeaderTop(firstGroup), 0, SettingsPageLayout.GroupHeaderGapBelow),
                    };
                    body.Children.Add(header);
                    firstGroup = false;
                }
                body.Children.Add(BuildRow(d));
            }
        }

        private UIElement BuildRow(SettingDescriptor d)
        {
            if (d.Kind == SettingKind.Info && d.ActionId == HostSettingsSchema.WindowActions.IndexStatus)
            {
                var card = new IndexStatusCard(d.Title(English));
                card.ViewLogRequested += (s, e) => Safe.RunAsync(L, "settings.index.viewLog", async () => { await _host.RunActionAsync(OpenCurrentLogAction); });
                card.RebuildRequested += (s, e) => Safe.RunAsync(L, "settings.index.rebuild", async () =>
                {
                    L.Info("settings.index.rebuild", null, ("from", "statusCard"));
                    card.SetActionResult(await _host.RunActionAsync(HostSettingsSchema.WindowActions.RebuildAllIndexes));
                    RefreshInfo();
                });
                _statusCards.Add(card);
                return card;
            }

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
                    return AutoNumberModel.SupportsAuto(d) ? BuildAutoNumber(d) : BuildNumber(d);
                case SettingKind.Choice:
                {
                    var options = d.Options ?? Array.Empty<SettingOption>();
                    int index = Math.Max(0, options.ToList().FindIndex(o => string.Equals(o.Value, Value(d), StringComparison.OrdinalIgnoreCase)));
                    if (options.Count <= 4)
                    {
                        var seg = new SegmentedControl(options.Select(o => English ? o.TitleEn : o.TitleZh).ToArray());
                        seg.SelectedIndex = index;
                        System.Windows.Automation.AutomationProperties.SetName(seg, d.Title(English));
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
                    var result = new UiText((string)null, TextVariant.Secondary)
                    {
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MaxWidth = 260,
                        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
                    };
                    // The card already shows the title; the button gets a short verb ("重建", "打开"…) when the schema has one.
                    var button = new UiButton(d.ActionLabel(English) ?? d.Title(English)) { MinWidth = SettingsPageLayout.FooterButtonMinWidth };
                    System.Windows.Automation.AutomationProperties.SetName(button, d.Title(English));
                    button.Click += (s, e) => Safe.RunAsync(L, "settings.action", async () =>
                    {
                        button.IsEnabled = false;
                        try
                        {
                            L.Info("settings.action.run", null, ("key", d.Key), ("action", d.ActionId));
                            // "索引 Lua 脚本" works on the folders shown on the page: commit a just-typed path first.
                            if (d.ActionId == HostSettingsSchema.WindowActions.IndexLua && SettingsModel.Diff(_schema, _committed, _working).Count > 0) Apply();
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
                    var text = new UiText((string)null, TextVariant.Secondary) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) };
                    labels.Children.Add(text);
                    _infoRows.Add((d, text));
                    return null;
                }
            }
            return null;
        }

        private NumberStepper BuildNumber(SettingDescriptor d)
        {
            int.TryParse(Value(d), out int v);
            var stepper = new NumberStepper(d.Min == int.MinValue ? -1e9 : d.Min, d.Max == int.MaxValue ? 1e9 : d.Max, 1, v) { AccessibleName = d.Title(English) };
            stepper.ValueChanged += (s, e) => Safe.Execute(L, "settings.edit", () =>
                Set(d, ((int)Math.Round(stepper.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return stepper;
        }

        /// <summary>
        /// "自动" check box + number box (thread counts, editor-link port): while auto is on the box is disabled and shows
        /// what auto resolves to (empty when unknown). Stored value: 0 = auto, otherwise the number (≥ 1).
        /// </summary>
        private UIElement BuildAutoNumber(SettingDescriptor d)
        {
            bool auto = AutoNumberModel.IsAuto(Value(d));
            var stepper = new NumberStepper(AutoNumberModel.ManualMin(d), AutoNumberModel.ManualMax(d), 1, AutoNumberModel.ManualSeed(d))
            {
                AccessibleName = d.Title(English),
            };
            var check = new UiCheckBox(SettingsText.Auto, auto) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            System.Windows.Automation.AutomationProperties.SetName(check, d.Title(English) + " — " + SettingsText.Auto);
            Tip.Set(check, English ? d.AutoLabelEn : d.AutoLabelZh);
            bool updating = false;

            void Show(bool isAuto)
            {
                updating = true;
                try
                {
                    stepper.IsEnabled = !isAuto;
                    int? shown = AutoNumberModel.DisplayValue(d, Value(d));
                    if (shown.HasValue) stepper.SetSilently(shown.Value);
                    else stepper.ShowBlank();
                }
                finally
                {
                    updating = false;
                }
            }

            Show(auto);
            check.Checked += (s, e) => Safe.Execute(L, "settings.edit.auto", () =>
            {
                Set(d, AutoNumberModel.ToStored(d, true, stepper.Value));
                Show(true);
            }, ("key", d.Key), ("auto", true));
            check.Unchecked += (s, e) => Safe.Execute(L, "settings.edit.auto", () =>
            {
                Set(d, AutoNumberModel.ToStored(d, false, AutoNumberModel.ManualSeed(d)));
                Show(false);
                stepper.FocusInput();
            }, ("key", d.Key), ("auto", false));
            stepper.ValueChanged += (s, e) => Safe.Execute(L, "settings.edit", () =>
            {
                if (updating || check.IsChecked == true) return;
                Set(d, AutoNumberModel.ToStored(d, false, stepper.Value));
            });
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(check);
            panel.Children.Add(stepper);
            return panel;
        }

        private void RefreshInfo()
        {
            foreach (var (d, text) in _infoRows)
            {
                string t = _host.InfoText(d.ActionId) ?? string.Empty;
                if (text.Text != t) text.Text = t;
            }
            if (_statusCards.Count == 0) return;
            var vm = IndexStatusView.From(_host.IndexStatus());
            foreach (var card in _statusCards) card.Update(vm);
        }

        // ------------------------------------------------------------------ footer

        private UIElement BuildFooter()
        {
            double w = SettingsPageLayout.FooterButtonMinWidth, gap = SettingsPageLayout.FooterButtonGap;
            var reset = new UiButton(() => SettingsText.ResetDefaults, ButtonKind.Ghost) { MinWidth = w };
            var cancel = new UiButton(() => SettingsText.Cancel) { MinWidth = w, Margin = new Thickness(gap, 0, 0, 0) };
            var apply = new UiButton(() => SettingsText.Apply) { MinWidth = w, Margin = new Thickness(gap, 0, 0, 0) };
            var ok = new UiButton(() => SettingsText.Ok, ButtonKind.Primary) { MinWidth = w, Margin = new Thickness(gap, 0, 0, 0) };
            reset.Click += (s, e) => Safe.Execute(L, "settings.reset", ResetPage);
            cancel.Click += (s, e) => Safe.Execute(L, "settings.cancel", () => { CancelCustomPages(); _closingByButton = true; Close(); });
            apply.Click += (s, e) => Safe.Execute(L, "settings.apply", Apply);
            ok.Click += (s, e) => Safe.Execute(L, "settings.ok", () => { Apply(); _closingByButton = true; Close(); });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            cancel.Margin = new Thickness(0);
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            buttons.Children.Add(ok);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_status, 1);
            _status.Margin = new Thickness(12, 0, 12, 0);
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(reset);
            grid.Children.Add(_status);
            grid.Children.Add(buttons);
            // Separated from the scrolling page: own background (the rail's surface) and a 1px top divider.
            var host = new Border
            {
                Child = grid, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(SettingsPageLayout.PagePaddingX - 8, SettingsPageLayout.FooterPaddingY, 16, SettingsPageLayout.FooterPaddingY),
            };
            host.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            host.Bind(Border.BackgroundProperty, Tokens.SurfaceNav);
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
