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
using UEToVS.Host.Logic.Fonts;
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
        /// <summary>Folder of the open .uproject (relative path settings resolve against it); null when none is open.</summary>
        string ProjectDirectory { get; }
        /// <summary>
        /// Shows the font settings in <paramref name="values"/> right away (UEToVS UI and VS's fonts) without saving them;
        /// null puts the saved settings back (Cancel / close).
        /// </summary>
        void PreviewFonts(IReadOnlyDictionary<string, string> values);
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
    internal sealed partial class SettingsWindow : UtilityWindow
    {
        private static readonly Logger L = Log.For("SettingsWindow");
        private const string OpenCurrentLogAction = "uetovs.diagnostics.openCurrentLog";
        private readonly ISettingsWindowHost _host;
        private readonly IReadOnlyList<SettingDescriptor> _schema;
        private Dictionary<string, string> _committed;
        private Dictionary<string, string> _working;
        private readonly StackPanel _nav = new StackPanel { Margin = new Thickness(10, 0, 10, 8) };
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
        /// <summary>Font rows were previewed since the last Apply: Cancel / close has to put the saved fonts back.</summary>
        private bool _fontsPreviewed;
        /// <summary>Rows with <see cref="SettingDescriptor.EnabledWhenKey"/> on the visible page (re-evaluated when that setting changes).</summary>
        private readonly List<(string Key, UIElement Row)> _dependentRows = new List<(string, UIElement)>();
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

            _search.RightContent = new Kbd("Ctrl+F") { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), IsHitTestVisible = false };
            var left = new DockPanel { Width = SettingsPageLayout.NavWidth };
            var searchHost = new Border { Child = _search, Padding = new Thickness(10, 12, 10, 10) };
            DockPanel.SetDock(searchHost, Dock.Top);
            left.Children.Add(searchHost);
            left.Children.Add(new UiScrollViewer { Content = _nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var leftHost = new Border { Child = left, BorderThickness = new Thickness(0, 0, 1, 0) };
            leftHost.Bind(Border.BackgroundProperty, Tokens.SurfaceNav);
            leftHost.Bind(Border.BorderBrushProperty, Tokens.Divider);

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
            }), onlyWhenChanged: true);
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

        private static System.Windows.Media.Geometry IconFor(string id)
        {
            switch (id)
            {
                case HostSettingsSchema.General: return SettingsIcons.General;
                case HostSettingsSchema.Appearance: return SettingsIcons.Appearance;
                case HostSettingsSchema.Indexing: return SettingsIcons.Index;
                case HostSettingsSchema.Keymap: return SettingsIcons.Keymap;
                case HostSettingsSchema.Navigation: return SettingsIcons.Navigation;
                case HostSettingsSchema.IntelliSense: return SettingsIcons.Code;
                case HostSettingsSchema.Agent: return SettingsIcons.Agent;
                case HostSettingsSchema.Logging: return SettingsIcons.Logs;
                case HostSettingsSchema.Performance: return SettingsIcons.Performance;
                case HostSettingsSchema.Updates: return SettingsIcons.Update;
                default: return SettingsIcons.General;
            }
        }

        private void RebuildNav()
        {
            _nav.Children.Clear();
            string q = _search.Text;
            foreach (var c in HostSettingsSchema.Categories)
            {
                var cat = c;
                bool selected = string.IsNullOrWhiteSpace(q) && c.Id == _category;
                // Warm-scheme rail: icon + label, neutral fill and a thin accent bar on the selected item.
                var item = new SettingsNavItem(c.Title(English), IconFor(c.Id)) { IsSelected = selected, Dimmed = !CategoryMatches(c, q) };
                item.Activated += (s, e) => Safe.Execute(L, "settings.nav.click", () => ShowCategory(cat.Id), ("category", cat.Id));
                _nav.Children.Add(item);
            }
        }

        // ------------------------------------------------------------------ pages

        private static Thickness PageMargin => new Thickness(SettingsPageLayout.PagePaddingX, SettingsPageLayout.PagePaddingTop,
                                                             SettingsPageLayout.PagePaddingX, SettingsPageLayout.PagePaddingBottom);

        private static UIElement PageTitle(string text, string description = null) => new SettingsPageHeader(text, description);

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
            body.Children.Add(PageTitle(cat.Title(English), SettingsText.PageDescription(cat.Id)));
            var custom = GetCustomPage(_category);
            if (custom == null && IsCustomCategory(_category))
                throw new InvalidOperationException(_customError ?? "custom page unavailable: " + _category);
            if (custom != null)
            {
                if (custom.Element == null) throw new InvalidOperationException("custom page has no content: " + _category);
                DetachFromParent(custom.Element);
                var pageRows = _schema.Where(d => d.Category == _category).ToList();
                if (pageRows.Count > MaxRowsAboveCustomPage)
                {
                    // Many generated rows (外观 > 字体) + the custom editor: one scrolling page, the editor (with its
                    // preview column) first, then the generated groups.
                    body.Children.Add(custom.Element);
                    AddRows(body, pageRows, false);
                    _page.Content = new UiScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                    RefreshInfo();
                    return;
                }
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

        /// <summary>Above this many generated rows a custom page scrolls together with them instead of filling the rest.</summary>
        private const int MaxRowsAboveCustomPage = 4;

        private static bool IsCustomCategory(string id) => id == HostSettingsSchema.Appearance || id == HostSettingsSchema.Keymap || id == HostSettingsSchema.Agent;

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

        /// <summary>
        /// Settings of one group share one card (<see cref="SettingsGroup"/>): rows separated by hairlines, 22px between
        /// groups. Full-card rows (the index status card) stand on their own.
        /// </summary>
        private void AddRows(Panel body, IList<SettingDescriptor> rows, bool searchResults)
        {
            string group = null;
            SettingsGroup card = null;
            _dependentRows.Clear();
            var own = SettingsModel.MergeInlineRows(rows, out var inline);
            foreach (var d in own)
            {
                var row = BuildRow(d, inline.TryGetValue(d.Key, out var extra) ? extra : null);
                if (row is IndexStatusCard)
                {
                    card = null;
                    group = null;
                    if (row is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, SettingsPageLayout.GroupGap);
                    body.Children.Add(row);
                    continue;
                }
                var g = d.Group(English) ?? string.Empty;
                if (card == null || g != group)
                {
                    group = g;
                    card = new SettingsGroup(g) { Margin = new Thickness(0, 0, 0, SettingsPageLayout.GroupGap) };
                    body.Children.Add(card);
                }
                card.Add(row);
                if (!string.IsNullOrEmpty(d.EnabledWhenKey))
                {
                    _dependentRows.Add((d.EnabledWhenKey, row));
                    UpdateDependentRow(d.EnabledWhenKey, row);
                }
            }
        }

        private void UpdateDependentRow(string key, UIElement row)
        {
            bool on = _working.TryGetValue(key, out var v) ? v == "true" : _schema.FirstOrDefault(x => x.Key == key)?.Default == "true";
            row.IsEnabled = on;
            row.Opacity = on ? 1.0 : 0.45;
        }

        /// <param name="inline">Settings drawn on this row (<see cref="SettingDescriptor.InlineWithKey"/>), right of its editor.</param>
        private UIElement BuildRow(SettingDescriptor d, IReadOnlyList<SettingDescriptor> inline = null)
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

            // ---- v0.4.5 navigation gaps: VS features after the takeover (a table under the title, no editor)
            if (d.Kind == SettingKind.Info && d.ActionId == Logic.IntelliSense.TakeoverFeatures.SettingsInfoId)
                return new SettingsRow(d.Title(English), d.Description(English)) { Below = new TakeoverFeatureTable() };

            var row = new SettingsRow(d.Title(English), d.Description(English));
            var labels = row.Labels;
            if (d.RequiresRestart)
                labels.Children.Add(new UiText(SettingsText.RestartHint, TextVariant.Description) { Margin = new Thickness(0, 2, 0, 0) });
            var editor = BuildEditor(d, labels);
            // Path lists span the row under the title; everything else sits on the right.
            if (editor is PathListEditor list)
            {
                row.Editor = list.AddButton;
                row.Below = list;
            }
            else if (inline != null && inline.Count > 0 && editor != null)
            {
                // e.g. 字体下拉框 + 字号（自动 / 数值）在同一行，少一半行数
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(editor);
                foreach (var x in inline)
                {
                    var e = BuildEditor(x, labels);
                    if (e == null) continue;
                    string label = x.InlineLabel(English);
                    if (!string.IsNullOrEmpty(label))
                        panel.Children.Add(new UiText(label, TextVariant.Secondary) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 10, 0) });
                    if (e is FrameworkElement fe)
                    {
                        fe.Margin = new Thickness(string.IsNullOrEmpty(label) ? 16 : 0, 0, 0, 0);
                        fe.VerticalAlignment = VerticalAlignment.Center;
                    }
                    panel.Children.Add(e);
                }
                row.Editor = panel;
            }
            else row.Editor = editor;
            return row;
        }

        private string Value(SettingDescriptor d) => _working.TryGetValue(d.Key, out var v) && v != null ? v : d.Default;

        private void Set(SettingDescriptor d, string value)
        {
            _working[d.Key] = SettingsModel.Normalize(d, value, out _);
            UpdateStatus();
            foreach (var (key, row) in _dependentRows)
                if (key == d.Key) UpdateDependentRow(key, row);
            if (FontSettingsSchema.IsFontKey(d.Key)) PreviewFonts("edit");
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
                    var options = d.ResolveOptions(Value(d));
                    int index = Math.Max(0, options.ToList().FindIndex(o => string.Equals(o.Value, Value(d), StringComparison.OrdinalIgnoreCase)));
                    if (options.Count <= 4 && d.OptionsSource == null)
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
                {
                    var box = new TextInput { Text = Value(d), Width = 320 };
                    System.Windows.Automation.AutomationProperties.SetName(box, d.Title(English));
                    box.TextChanged += (s, e) => Safe.Execute(L, "settings.edit", () => Set(d, box.Text));
                    return box;
                }
                case SettingKind.Path:
                {
                    var path = new PathInput(English ? "Default" : "默认") { Text = Value(d), Width = 380, AccessibleName = d.Title(English), PickerTitle = d.Title(English) };
                    path.TextChanged += (s, e) => Safe.Execute(L, "settings.edit", () => Set(d, path.Text));
                    return path;
                }
                case SettingKind.PathList:
                {
                    var list = new PathListEditor(_host.ProjectDirectory) { Value = Value(d), PickerTitle = d.Title(English) };
                    System.Windows.Automation.AutomationProperties.SetName(list, d.Title(English));
                    list.ValueChanged += (s, e) => Safe.Execute(L, "settings.edit", () =>
                    {
                        L.Info("settings.pathList.changed", null, ("key", d.Key), ("count", list.Paths.Count));
                        Set(d, list.Value);
                    });
                    return list;
                }
                case SettingKind.Action:
                {
                    if (d.ActionId == HostSettingsSchema.WindowActions.CheckForUpdates) return BuildUpdateRow(d);
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
            var reset = new UiButton(() => SettingsText.ResetPage, ButtonKind.Ghost) { Margin = new Thickness(-12, 0, 0, 0) };
            reset.Content = IconLabel(SettingsIcons.Reset, SettingsText.ResetPage);
            UEToVS.UI.Strings.Track(reset, () => reset.Content = IconLabel(SettingsIcons.Reset, SettingsText.ResetPage));
            var cancel = new UiButton(() => SettingsText.Cancel) { MinWidth = w };
            var apply = new UiButton(() => SettingsText.Apply) { MinWidth = w, Margin = new Thickness(gap, 0, 0, 0) };
            var ok = new UiButton(() => SettingsText.Ok, ButtonKind.Primary) { MinWidth = w, Margin = new Thickness(gap, 0, 0, 0) };
            foreach (var b in new[] { reset, cancel, apply, ok }) b.Height = 32;
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
            _status.Margin = new Thickness(8, 0, 12, 0);
            _status.Variant = TextVariant.Description;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(reset);
            grid.Children.Add(_status);
            grid.Children.Add(buttons);
            var host = new Border
            {
                Child = grid, BorderThickness = new Thickness(0, 1, 0, 0), Height = SettingsPageLayout.FooterHeight,
                Padding = new Thickness(SettingsPageLayout.PagePaddingX, 0, 20, 0),
            };
            host.Bind(Border.BorderBrushProperty, Tokens.Divider);
            host.Bind(Border.BackgroundProperty, Tokens.SurfaceSunken);
            return host;
        }

        private static UIElement IconLabel(System.Windows.Media.Geometry icon, string text)
        {
            var path = new System.Windows.Shapes.Path { Data = icon, Width = 14, Height = 14, Stretch = Stretch.None, StrokeThickness = 1.3, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            path.Bind(System.Windows.Shapes.Shape.StrokeProperty, Tokens.TextSecondary);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(path);
            sp.Children.Add(new UiText(text, TextVariant.Secondary));
            return sp;
        }

        private void UpdateStatus()
        {
            int pending = SettingsModel.Diff(_schema, _committed, _working).Count;
            _status.Text = pending > 0 ? SettingsText.PendingChanges(pending) : string.Empty;
        }

        /// <summary>Font changes show while choosing (like the other appearance settings); saved only with Apply / OK.</summary>
        private void PreviewFonts(string reason)
        {
            _fontsPreviewed = true;
            L.Debug("settings.fonts.preview", null, ("reason", reason));
            _host.PreviewFonts(_working);
        }

        private void Apply()
        {
            var changed = SettingsModel.Diff(_schema, _committed, _working);
            if (changed.Count > 0) _host.SaveValues(_working);
            foreach (var p in _custom.Values) Safe.Execute(L, "settings.custom.apply", p.Apply);
            _committed = new Dictionary<string, string>(_working, StringComparer.Ordinal);
            _fontsPreviewed = false;
            L.Info("settings.window.apply", null, ("changed", changed));
            _status.Text = SettingsText.Saved;
        }

        private void CancelCustomPages()
        {
            foreach (var p in _custom.Values) Safe.Execute(L, "settings.custom.cancel", p.Cancel);
            if (_fontsPreviewed)
            {
                _fontsPreviewed = false;
                Safe.Execute(L, "settings.fonts.revert", () => _host.PreviewFonts(null));
            }
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
            bool fonts = false;
            foreach (var d in targets)
            {
                _working[d.Key] = d.Default;
                n++;
                fonts |= FontSettingsSchema.IsFontKey(d.Key);
            }
            if (fonts) PreviewFonts("reset");
            if (string.IsNullOrWhiteSpace(q) && _custom.TryGetValue(_category, out var custom)) custom.ResetToDefaults();
            L.Info("settings.window.reset", null, ("category", _category), ("query", q), ("values", n));
            ShowPage();
            UpdateStatus();
            if (_status.Text.Length == 0) _status.Text = SettingsText.ResetConfirm;
        }
    }
}
