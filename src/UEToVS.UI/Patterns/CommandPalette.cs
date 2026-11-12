using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Patterns
{
    /// <summary>A tab of the palette (All / Types / Files / Symbols / Actions / Text …).</summary>
    public sealed class PaletteTab
    {
        public PaletteTab(string id, Func<string> title, int debounceMs = 40)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Title = title ?? (() => id);
            DebounceMs = Math.Max(0, debounceMs);
        }

        public string Id { get; }
        public Func<string> Title { get; }

        /// <summary>Debounce before querying after a keystroke (slow sources such as full-text search use more).</summary>
        public int DebounceMs { get; }

        /// <summary>Placeholder for the search box on this tab.</summary>
        public Func<string> Placeholder { get; set; }

        /// <summary>Empty-query text (e.g. "输入类型名").</summary>
        public Func<string> EmptyHint { get; set; }

        /// <summary>Whether this tab shows the preview pane at all (Actions: no).</summary>
        public bool SupportsPreview { get; set; } = true;
    }

    /// <summary>A header toggle chip (e.g. "包含非项目项").</summary>
    public sealed class PaletteFilter
    {
        public PaletteFilter(string id, Func<string> title, bool defaultOn = false)
        {
            Id = id;
            Title = title;
            DefaultOn = defaultOn;
        }

        public string Id { get; }
        public Func<string> Title { get; }
        public bool DefaultOn { get; }
        public string Shortcut { get; set; }
    }

    public sealed class PaletteQuery
    {
        public string Text { get; internal set; }
        public string TabId { get; internal set; }
        public int Generation { get; internal set; }
        public IReadOnlyCollection<string> ActiveFilters { get; internal set; }

        public bool IsFilterOn(string id)
        {
            if (ActiveFilters == null) return false;
            foreach (var f in ActiveFilters)
                if (f == id) return true;
            return false;
        }
    }

    public sealed class PaletteResult<T>
    {
        public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();

        /// <summary>Counts per tab id shown next to the tab titles (optional).</summary>
        public IReadOnlyDictionary<string, int> TabCounts { get; set; }

        /// <summary>False while streaming / when the source truncated the results.</summary>
        public bool IsComplete { get; set; } = true;

        /// <summary>Optional footer status (e.g. "索引中 63%").</summary>
        public string Status { get; set; }
    }

    /// <summary>Data side of a <see cref="CommandPalette{T}"/>. All methods are called on the UI thread.</summary>
    public interface ICommandPaletteSource<T>
    {
        IReadOnlyList<PaletteTab> Tabs { get; }

        /// <summary>Header chips; may be empty.</summary>
        IReadOnlyList<PaletteFilter> Filters { get; }

        IItemPresenter<T> Presenter { get; }

        /// <summary>
        /// Runs a query. Cancelled when the user types again. Report streamed partial results through
        /// <paramref name="partial"/> (each report replaces the list; later reports should only append).
        /// The palette drops results of superseded generations.
        /// </summary>
        Task<PaletteResult<T>> QueryAsync(PaletteQuery query, IProgress<PaletteResult<T>> partial, CancellationToken cancellationToken);

        /// <summary>Preview of an item; null for none. Must be cheap (the file is loaded by the preview itself).</summary>
        CodePreviewRequest GetPreview(T item);

        /// <summary>
        /// Opens an item. <paramref name="all"/> = every current result (for <see cref="InvokeKind.OpenInResults"/>).
        /// Return true to close the palette.
        /// </summary>
        bool Invoke(T item, InvokeKind kind, PaletteQuery query, IReadOnlyList<T> all);
    }

    /// <summary>
    /// Rider "Search Everywhere"-style palette = PopupShell + TabStrip + SearchInput + VirtualList + CodePreview +
    /// StatusFooter. The feature only implements <see cref="ICommandPaletteSource{T}"/>. Create once, keep, call
    /// <see cref="Show"/> each time (instant, state kept). Keys (§8.4): ↑↓ PgUp/PgDn, Ctrl+Home/End, Enter /
    /// Shift+Enter / Ctrl+Enter, Tab / Shift+Tab switch tabs, Ctrl+P preview, Alt+↑/↓ history, Esc close.
    /// </summary>
    public class CommandPalette<T>
    {
        private readonly ICommandPaletteSource<T> _source;
        private readonly TabStrip _tabs = new TabStrip();
        private readonly SearchInput _search = new SearchInput(true);
        private readonly VirtualList<T> _list;
        private readonly CodePreview _preview = new CodePreview();
        private readonly StatusFooter _footer = new StatusFooter();
        private readonly EmptyState _empty = new EmptyState { Visibility = Visibility.Collapsed };
        private readonly Grid _content = new Grid();
        private readonly GridSplitter _splitter = new GridSplitter { ResizeBehavior = GridResizeBehavior.PreviousAndNext, Focusable = false };
        private readonly IconToggle _previewToggle;
        private readonly StackPanel _filterPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly Dictionary<string, Chip> _filterChips = new Dictionary<string, Chip>();
        private readonly QueryGate _gate = new QueryGate();
        private readonly DispatcherTimer _debounce;
        private readonly DispatcherTimer _previewDebounce;
        private PaletteQuery _lastQuery;
        private bool _previewVisible;
        private PreviewPosition _previewPos;
        private double _split = double.NaN;
        private bool _hasResults;

        public CommandPalette(string id, ICommandPaletteSource<T> source, Size? defaultSize = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _list = new VirtualList<T>(source.Presenter) { AlwaysActive = true, FocusOnClick = false };
            Shell = new PopupShell(id ?? "palette", defaultSize ?? new Size(760, 500)) { MinVisibleWidth = 420, MinVisibleHeight = 220 };
            _previewToggle = new IconToggle(Ui.FrozenGeometry("M 0,0 L 12,0 L 12,10 L 0,10 Z M 0,6 L 12,6"), () => Strings.PaletteTogglePreview, "Ctrl+P");
            _debounce = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
            _debounce.Tick += (s, e) => Ui.Safe("ui.palette.debounce", () => { _debounce.Stop(); RunQuery(); });
            _previewDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
            _previewDebounce.Tick += (s, e) => Ui.Safe("ui.palette.preview", () => { _previewDebounce.Stop(); UpdatePreview(); });

            Build();
            Wire();

            var settings = ThemeManager.Instance.EffectiveSettings;
            _previewVisible = settings.PreviewVisible;
            _previewPos = settings.PreviewPosition;
            ApplyPreviewLayout();
        }

        public PopupShell Shell { get; }

        public CodePreview Preview => _preview;

        public VirtualList<T> List => _list;

        public SearchHistory History => _search.History;

        public IntPtr OwnerHandle
        {
            get => Shell.OwnerHandle;
            set => Shell.OwnerHandle = value;
        }

        public string CurrentTabId => _tabs.SelectedId;

        public string Text => _search.Text;

        /// <summary>Creates the window and lays it out while idle so the first Show is instant.</summary>
        public void Prewarm() => Shell.Prewarm();

        /// <summary>Shows the palette. Null <paramref name="initialText"/> keeps (and selects) the previous query.</summary>
        public void Show(PopupAnchor anchor = null, string initialText = null, string tabId = null)
        {
            using (Ui.Logger.Time("ui.palette.show", 50))
            {
                if (tabId != null) _tabs.Select(tabId);
                if (initialText != null) _search.Text = initialText;
                bool wasVisible = Shell.IsVisible;
                Shell.ShowAt(anchor ?? PopupAnchor.CenterOf(Shell.OwnerHandle));
                if (!wasVisible) RestoreSplit(); // state is loaded by ShowAt
                _search.FocusInput();
                _search.SelectAll();
                RunQuery();
            }
        }

        public void Dismiss() => Shell.Dismiss();

        /// <summary>Re-runs the current query (e.g. index finished).</summary>
        public void Refresh()
        {
            if (Shell.IsVisible) RunQuery();
        }

        public bool IsVisible => Shell.IsVisible;

        /// <summary>Switches the tab while open (source Invoke on a group header / "more" row); the query re-runs.</summary>
        public void SelectTab(string tabId)
        {
            if (tabId != null) _tabs.Select(tabId);
            _search.FocusInput();
        }

        /// <summary>Replaces the query text (caret at the end) and re-runs it.</summary>
        public void SetText(string text)
        {
            _search.Text = text ?? string.Empty;
            _search.InputBox.CaretIndex = _search.Text.Length;
            _search.FocusInput();
            ScheduleQuery();
        }

        /// <summary>Flips a header chip (e.g. Rider's double Shift → "include non-project items").</summary>
        public void ToggleFilter(string filterId)
        {
            if (filterId != null && _filterChips.TryGetValue(filterId, out var chip)) chip.IsChecked = chip.IsChecked != true;
        }

        public bool IsFilterOn(string filterId) => filterId != null && _filterChips.TryGetValue(filterId, out var chip) && chip.IsChecked == true;

        private void Build()
        {
            _tabs.SetTabs(MapTabs(_source.Tabs));
            if (_source.Filters != null)
                foreach (var f in _source.Filters)
                {
                    var chip = new Chip(f.Title, f.DefaultOn) { Margin = new Thickness(4, 0, 0, 0), Focusable = false };
                    if (f.Shortcut != null) Tip.Set(chip, f.Title, f.Shortcut);
                    chip.Checked += (s, e) => Ui.Safe("ui.palette.filter", RunQuery);
                    chip.Unchecked += (s, e) => Ui.Safe("ui.palette.filter", RunQuery);
                    _filterChips[f.Id] = chip;
                    _filterPanel.Children.Add(chip);
                }
            _previewToggle.Focusable = false;
            _previewToggle.Margin = new Thickness(6, 0, 0, 0);

            var headerRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerRight.Children.Add(_filterPanel);
            headerRight.Children.Add(_previewToggle);
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 2, 8, 0), Background = System.Windows.Media.Brushes.Transparent };
            DockPanel.SetDock(headerRight, Dock.Right);
            header.Children.Add(headerRight);
            header.Children.Add(_tabs);
            Shell.EnableDrag(header);

            _search.DisableIme = true;
            _footer.SetHints(
                new KeyHint("Enter", () => Strings.PaletteHintOpen),
                new KeyHint("Shift+Enter", () => Strings.PaletteHintNewTab),
                new KeyHint("Ctrl+Enter", () => Strings.PaletteHintResults),
                new KeyHint("Tab", () => Strings.PaletteHintTabs));

            var listArea = new Grid();
            listArea.Children.Add(_list);
            listArea.Children.Add(_empty);
            _listArea = listArea;
            _splitter.Bind(Control.BackgroundProperty, Tokens.BorderSubtle);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var div2 = new Divider();
            Grid.SetRow(header, 0);
            Grid.SetRow(_search, 1);
            Grid.SetRow(div2, 2);
            Grid.SetRow(_content, 3);
            Grid.SetRow(_footer, 4);
            root.Children.Add(header);
            root.Children.Add(_search);
            root.Children.Add(div2);
            root.Children.Add(_content);
            root.Children.Add(_footer);
            Shell.Body = root;
            Strings.Track(root, () =>
            {
                Shell.Title = TabTitle();
                AutomationProperties.SetName(_list, TabTitle());
                UpdatePlaceholder();
                UpdateEmpty();
            });
        }

        private Grid _listArea;

        private static IReadOnlyList<TabDefinition> MapTabs(IReadOnlyList<PaletteTab> tabs)
        {
            var list = new List<TabDefinition>();
            if (tabs != null)
                foreach (var t in tabs) list.Add(new TabDefinition(t.Id, t.Title));
            return list;
        }

        private void Wire()
        {
            _search.TextChanged += (s, text) => Ui.Safe("ui.palette.text", ScheduleQuery);
            _tabs.SelectionChanged += (s, e) => Ui.Safe("ui.palette.tab", () =>
            {
                UpdatePlaceholder();
                ApplyPreviewLayout();
                if (Shell.IsVisible) RunQuery();
            });
            _list.SelectionChanged += (s, e) => Ui.Safe("ui.palette.selection", () =>
            {
                UpdateCount();
                if (_previewVisible) { _previewDebounce.Stop(); _previewDebounce.Start(); }
            });
            _list.ItemInvoked += (s, e) => Ui.Safe("ui.palette.invoke", () => Invoke(e.Item, e.Kind));
            _previewToggle.Checked += (s, e) => Ui.Safe("ui.palette.preview_on", () => SetPreviewVisible(true));
            _previewToggle.Unchecked += (s, e) => Ui.Safe("ui.palette.preview_off", () => SetPreviewVisible(false));
            Shell.PreviewKeyDown += (s, e) => Ui.Safe("ui.palette.key", () => OnKey(e));
            Shell.SavingState += (s, e) => Ui.Safe("ui.palette.save", SaveSplit);
            Shell.Dismissed += (s, e) => Ui.Safe("ui.palette.dismissed", () => { _gate.CancelAll(); _debounce.Stop(); _footer.IsBusy = false; });
            ThemeManager.Instance.Changed += (s, e) => Ui.Safe("ui.palette.theme", () =>
            {
                // Settings page changed the preview position: follow it.
                var pos = ThemeManager.Instance.EffectiveSettings.PreviewPosition;
                if (pos != _previewPos) { _previewPos = pos; _split = double.NaN; ApplyPreviewLayout(); }
            });
        }

        private PaletteTab CurrentTab
        {
            get
            {
                var id = _tabs.SelectedId;
                if (_source.Tabs != null)
                    foreach (var t in _source.Tabs)
                        if (t.Id == id) return t;
                return null;
            }
        }

        private string TabTitle() => CurrentTab?.Title?.Invoke() ?? string.Empty;

        private void UpdatePlaceholder()
        {
            var t = CurrentTab;
            _search.Placeholder = t?.Placeholder?.Invoke() ?? Strings.SearchPlaceholder;
        }

        private void OnKey(KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                case Key.PageUp:
                case Key.PageDown:
                    if ((mods & ModifierKeys.Alt) != 0) return; // history (SearchInput handles Alt+↑/↓)
                    e.Handled = _list.HandleKey(key, mods);
                    return;
                case Key.Home:
                case Key.End:
                    if ((mods & ModifierKeys.Control) != 0) e.Handled = _list.HandleKey(key, ModifierKeys.None);
                    return;
                case Key.Enter:
                    e.Handled = true;
                    FlushPendingQuery();
                    _list.HandleKey(Key.Enter, mods);
                    return;
                case Key.Tab:
                    if ((mods & ModifierKeys.Shift) != 0) _tabs.Previous();
                    else _tabs.Next();
                    e.Handled = true;
                    return;
                case Key.P:
                    if (mods == ModifierKeys.Control)
                    {
                        _previewToggle.IsChecked = !(_previewToggle.IsChecked == true);
                        e.Handled = true;
                    }
                    return;
            }
        }

        /// <summary>Enter right after typing: run the pending query first so Enter opens the up-to-date top hit.</summary>
        private void FlushPendingQuery()
        {
            if (!_debounce.IsEnabled) return;
            _debounce.Stop();
            RunQuery();
        }

        private void ScheduleQuery()
        {
            _debounce.Stop();
            int ms = CurrentTab?.DebounceMs ?? 40;
            if (ms <= 0) { RunQuery(); return; }
            _debounce.Interval = TimeSpan.FromMilliseconds(ms);
            _debounce.Start();
        }

        private List<string> ActiveFilters()
        {
            var list = new List<string>();
            foreach (var kv in _filterChips)
                if (kv.Value.IsChecked == true) list.Add(kv.Key);
            return list;
        }

        private async void RunQuery()
        {
            var (gen, token) = _gate.Begin();
            var q = new PaletteQuery { Text = _search.Text ?? string.Empty, TabId = _tabs.SelectedId, Generation = gen, ActiveFilters = ActiveFilters() };
            _lastQuery = q;
            _footer.IsBusy = true;
            _footer.Status = Strings.PaletteSearching;
            bool gotPartial = false;
            var partial = new Progress<PaletteResult<T>>(r => Ui.Safe("ui.palette.partial", () =>
            {
                if (!_gate.IsCurrent(gen) || r == null) return;
                Apply(r, keepPosition: gotPartial, final: false);
                gotPartial = true;
            }));
            try
            {
                PaletteResult<T> result;
                using (Ui.Logger.Time("ui.palette.query", 100, ("tab", q.TabId), ("len", q.Text.Length)))
                    result = await _source.QueryAsync(q, partial, token);
                if (!_gate.IsCurrent(gen)) return;
                Apply(result ?? new PaletteResult<T>(), keepPosition: gotPartial, final: true);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer query
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.palette.query_failed", "Palette source query failed", ex, ("tab", q.TabId));
                if (!_gate.IsCurrent(gen)) return;
                _footer.IsBusy = false;
                _footer.Status = Strings.PaletteError(ex.Message);
            }
        }

        private void Apply(PaletteResult<T> r, bool keepPosition, bool final)
        {
            _list.SetItems(r.Items ?? Array.Empty<T>(), keepPosition);
            _hasResults = _list.Count > 0;
            if (r.TabCounts != null)
            {
                _tabs.ClearCounts();
                foreach (var kv in r.TabCounts) _tabs.SetCount(kv.Key, kv.Value);
            }
            if (final)
            {
                _footer.IsBusy = false;
                _footer.Status = r.Status ?? (r.IsComplete ? string.Empty : Strings.PaletteIncomplete);
            }
            else if (r.Status != null) _footer.Status = r.Status;
            UpdateCount();
            UpdateEmpty();
            if (_previewVisible) UpdatePreview();
        }

        private void UpdateCount()
        {
            int n = _list.Count;
            _footer.Count = n == 0 ? string.Empty : Strings.ListPosition(Math.Max(0, _list.SelectedIndex) + 1, PerfMath.FormatCount(n));
        }

        private void UpdateEmpty()
        {
            if (_hasResults)
            {
                _empty.Visibility = Visibility.Collapsed;
                return;
            }
            var text = _search.Text ?? string.Empty;
            if (text.Length == 0) _empty.Set(CurrentTab?.EmptyHint?.Invoke() ?? Strings.PaletteTypeToSearch, showGlyph: true);
            else _empty.Set(Strings.PaletteNoResults(text), showGlyph: true);
            _empty.Visibility = _footer.IsBusy && text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Invoke(T item, InvokeKind kind)
        {
            bool close;
            try
            {
                close = _source.Invoke(item, kind, _lastQuery, _list.Items);
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.palette.invoke_failed", "Palette source Invoke failed", ex, ("kind", kind));
                Toast.Show(_listArea, ex.Message, Severity.Error);
                return;
            }
            _search.CommitToHistory();
            if (close) Shell.Dismiss();
        }

        private void UpdatePreview()
        {
            if (!_previewVisible || !(CurrentTab?.SupportsPreview ?? true)) return;
            if (!_list.HasSelection) { _preview.Clear(); return; }
            CodePreviewRequest req;
            try
            {
                req = _source.GetPreview(_list.SelectedItem);
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.palette.preview_failed", "Palette source GetPreview failed", ex);
                req = null;
            }
            if (req == null) _preview.Clear();
            else _preview.Show(req);
        }

        private void SetPreviewVisible(bool visible)
        {
            if (_previewVisible == visible) return;
            if (!visible) SaveSplit();
            _previewVisible = visible;
            ApplyPreviewLayout();
            if (visible) UpdatePreview();
        }

        private void ApplyPreviewLayout()
        {
            bool show = _previewVisible && (CurrentTab?.SupportsPreview ?? true);
            _previewToggle.IsChecked = _previewVisible;
            _previewToggle.Visibility = (CurrentTab?.SupportsPreview ?? true) ? Visibility.Visible : Visibility.Collapsed;
            _content.Children.Clear();
            _content.RowDefinitions.Clear();
            _content.ColumnDefinitions.Clear();
            _content.Children.Add(_listArea);
            Grid.SetRow(_listArea, 0);
            Grid.SetColumn(_listArea, 0);
            if (!show) return;
            double f = double.IsNaN(_split) ? 0.55 : Math.Max(0.15, Math.Min(0.85, _split));
            if (_previewPos == PreviewPosition.Bottom)
            {
                _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(f, GridUnitType.Star), MinHeight = 60 });
                _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
                _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - f, GridUnitType.Star), MinHeight = 40 });
                _splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                _splitter.VerticalAlignment = VerticalAlignment.Stretch;
                _splitter.ResizeDirection = GridResizeDirection.Rows;
                _splitter.Height = double.NaN;
                _splitter.Width = double.NaN;
                Grid.SetRow(_splitter, 1);
                Grid.SetColumn(_splitter, 0);
                Grid.SetRow(_preview, 2);
                Grid.SetColumn(_preview, 0);
            }
            else
            {
                _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star), MinWidth = 160 });
                _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star), MinWidth = 120 });
                _splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                _splitter.VerticalAlignment = VerticalAlignment.Stretch;
                _splitter.ResizeDirection = GridResizeDirection.Columns;
                Grid.SetRow(_splitter, 0);
                Grid.SetColumn(_splitter, 1);
                Grid.SetRow(_preview, 0);
                Grid.SetColumn(_preview, 2);
            }
            _content.Children.Add(_splitter);
            _content.Children.Add(_preview);
        }

        private void SaveSplit()
        {
            if (!_previewVisible) { Shell.State.PreviewVisible = false; return; }
            double a, b;
            if (_previewPos == PreviewPosition.Bottom && _content.RowDefinitions.Count == 3)
            {
                a = _content.RowDefinitions[0].ActualHeight;
                b = _content.RowDefinitions[2].ActualHeight;
            }
            else if (_content.ColumnDefinitions.Count == 3)
            {
                a = _content.ColumnDefinitions[0].ActualWidth;
                b = _content.ColumnDefinitions[2].ActualWidth;
            }
            else return;
            if (a + b > 0) _split = a / (a + b);
            Shell.State.PreviewSplit = _split;
            Shell.State.PreviewVisible = _previewVisible;
        }

        private void RestoreSplit()
        {
            var s = Shell.State;
            if (s == null) return;
            bool changed = false;
            if (!double.IsNaN(s.PreviewSplit) && Math.Abs(s.PreviewSplit - (double.IsNaN(_split) ? -1 : _split)) > 1e-3) { _split = s.PreviewSplit; changed = true; }
            if (s.PreviewVisible.HasValue && s.PreviewVisible.Value != _previewVisible) { _previewVisible = s.PreviewVisible.Value; changed = true; }
            if (changed) ApplyPreviewLayout();
        }
    }
}
