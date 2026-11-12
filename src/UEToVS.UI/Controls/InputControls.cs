using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UEToVS.UI.Atoms;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Controls
{
    /// <summary>
    /// Search box: magnifier or prefix label ("类型:"), borderless text input, clear button, optional right-side
    /// content (chips/toggles) and history (Alt+↑ / Alt+↓). <see cref="Embedded"/> = Rider's full-width top bar
    /// inside popups; otherwise a boxed field. Navigation keys are not handled here: the owner (list/palette)
    /// reads PreviewKeyDown so focus can stay in the box while ↑/↓ move the list.
    /// </summary>
    public class SearchInput : Border
    {
        private readonly TextInput _box = new TextInput();
        private readonly UiText _prefix = new UiText((string)null, TextVariant.Secondary) { Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed };
        private readonly Path _glass;
        private readonly IconButton _clear;
        private readonly ContentControl _right = new ContentControl { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        private bool _suppress;
        private bool _embedded;
        // Boxed mode: Fluent focus underline drawn by this control (the inner text box is chrome-less).
        private readonly Border _underline = new Border
        {
            Height = 2, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Hidden,
            CornerRadius = new CornerRadius(0, 0, 2, 2), SnapsToDevicePixels = true,
        };

        public SearchInput(bool embedded = true)
        {
            _glass = new Path
            {
                Data = Ui.SearchGlass,
                StrokeThickness = 1.4,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            _glass.Bind(Shape.StrokeProperty, Tokens.TextSecondary);
            _clear = new IconButton(Ui.CrossMark, () => Strings.CommonClear, null, 8) { Visibility = Visibility.Collapsed, Focusable = false, Width = 20, Height = 20 };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_glass, 0);
            Grid.SetColumn(_prefix, 1);
            Grid.SetColumn(_box, 2);
            Grid.SetColumn(_clear, 3);
            Grid.SetColumn(_right, 4);
            grid.Children.Add(_glass);
            grid.Children.Add(_prefix);
            grid.Children.Add(_box);
            grid.Children.Add(_clear);
            grid.Children.Add(_right);
            var root = new Grid();
            root.Children.Add(grid);
            root.Children.Add(_underline);
            _underline.Bind(BackgroundProperty, Tokens.InputFocusUnderline);
            Child = root;
            IsKeyboardFocusWithinChanged += (s, e) => Ui.Safe("ui.search.focus", UpdateFocusVisual);
            MouseEnter += (s, e) => Ui.Safe("ui.search.hover", UpdateFocusVisual);
            MouseLeave += (s, e) => Ui.Safe("ui.search.hover", UpdateFocusVisual);

            _box.BorderThickness = new Thickness(0);
            _box.Background = Brushes.Transparent;
            _box.Padding = new Thickness(0, 2, 0, 2);
            _box.MinHeight = 0; // the search box chrome (this Border) sets the height, not the inner text box
            _box.Bind(Control.FontSizeProperty, Tokens.FontSizeInput);
            _box.VerticalAlignment = VerticalAlignment.Center;
            Placeholder = Strings.SearchPlaceholder;

            Embedded = embedded;
            History = new SearchHistory();

            _box.TextChanged += (s, e) => Ui.Safe("ui.search.text", () =>
            {
                _clear.Visibility = string.IsNullOrEmpty(_box.Text) ? Visibility.Collapsed : Visibility.Visible;
                if (_suppress) return;
                History?.ResetBrowsing();
                TextChanged?.Invoke(this, _box.Text);
            });
            _clear.Click += (s, e) => Ui.Safe("ui.search.clear", () =>
            {
                Text = string.Empty;
                TextChanged?.Invoke(this, string.Empty);
                FocusInput();
            });
            _box.PreviewKeyDown += (s, e) => Ui.Safe("ui.search.key", () => OnKey(e));
            MouseLeftButtonDown += (s, e) => Ui.Safe("ui.search.click", () => { if (!_box.IsKeyboardFocusWithin) FocusInput(); });
        }

        /// <summary>Raised on every edit (debouncing is the owner's job).</summary>
        public event EventHandler<string> TextChanged;

        /// <summary>Enter pressed with no owner handling it (standalone use).</summary>
        public event EventHandler<string> Submitted;

        public SearchHistory History { get; set; }

        /// <summary>When true, plain ↑/↓ browse history (standalone box without a list).</summary>
        public bool HistoryOnPlainArrows { get; set; }

        public TextInput InputBox => _box;

        public bool Embedded
        {
            get => _embedded;
            set
            {
                _embedded = value;
                if (value)
                {
                    BorderThickness = new Thickness(0);
                    this.Bind(BackgroundProperty, Tokens.SurfacePopup);
                    Padding = new Thickness(12, 8, 8, 8);
                    CornerRadius = new CornerRadius(0);
                    ClearValue(MinHeightProperty);
                    _box.Style = Styles.TextInput;
                    _box.BorderThickness = new Thickness(0);
                    _box.Background = Brushes.Transparent;
                    _box.Padding = new Thickness(0, 2, 0, 2);
                    _box.MinHeight = 0;
                }
                else
                {
                    BorderThickness = new Thickness(1);
                    this.Bind(BackgroundProperty, Tokens.InputBackground);
                    this.Bind(BorderBrushProperty, Tokens.InputBorder);
                    this.Bind(CornerRadiusProperty, Tokens.CornerRadiusControl);
                    // Fluent text box metrics: 10px text inset, text vertically centred in the control height, the
                    // clear button inset 4px from the right edge. The inner box has no chrome of its own (no second
                    // focus underline over the caret, no inner padding that clips the glyphs).
                    Padding = new Thickness(10, 0, 4, 0);
                    this.Bind(MinHeightProperty, Tokens.ControlHeight);
                    _box.Style = Styles.BareTextInput;
                    _box.ClearValue(Control.BorderThicknessProperty);
                    _box.ClearValue(Control.BackgroundProperty);
                    _box.ClearValue(Control.PaddingProperty);
                    _box.ClearValue(FrameworkElement.MinHeightProperty);
                    _underline.Margin = new Thickness(-Padding.Left, 0, -Padding.Right, 0); // full inner width, on the bottom border
                }
            }
        }

        /// <summary>Boxed mode: hover border and the accent focus underline (Fluent TextBox states).</summary>
        private void UpdateFocusVisual()
        {
            if (_embedded)
            {
                _underline.Visibility = Visibility.Hidden;
                return;
            }
            bool focused = IsKeyboardFocusWithin;
            _underline.Visibility = focused ? Visibility.Visible : Visibility.Hidden;
            this.Bind(BackgroundProperty, focused ? Tokens.SurfacePopup : Tokens.InputBackground);
            this.Bind(BorderBrushProperty, IsMouseOver && !focused ? Tokens.BorderStrong : Tokens.InputBorder);
        }

        public string Text
        {
            get => _box.Text;
            set
            {
                _suppress = true;
                try
                {
                    _box.Text = value ?? string.Empty;
                    _box.CaretIndex = _box.Text.Length;
                }
                finally
                {
                    _suppress = false;
                }
            }
        }

        public string Placeholder
        {
            get => _box.Placeholder;
            set
            {
                _box.Placeholder = value;
                AutomationProperties.SetName(_box, value ?? string.Empty);
            }
        }

        /// <summary>Prefix label shown before the text (e.g. the active tab "类型:"). Null hides it and shows the magnifier.</summary>
        public string Prefix
        {
            get => _prefix.Text;
            set
            {
                _prefix.Text = value ?? string.Empty;
                bool has = !string.IsNullOrEmpty(value);
                _prefix.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public bool ShowGlass
        {
            get => _glass.Visibility == Visibility.Visible;
            set => _glass.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Extra content at the right end (filter chips, toggles).</summary>
        public UIElement RightContent
        {
            get => _right.Content as UIElement;
            set => _right.Content = value;
        }

        /// <summary>Turns the IME off so typing goes straight into the search (Chinese IME users search identifiers).</summary>
        public bool DisableIme
        {
            set => InputMethod.SetIsInputMethodEnabled(_box, !value);
        }

        public void FocusInput()
        {
            _box.Focus();
            Keyboard.Focus(_box);
        }

        public void SelectAll() => _box.SelectAll();

        /// <summary>Records the current text in the history (call when the user opens a result).</summary>
        public void CommitToHistory() => History?.Add(_box.Text);

        private void OnKey(KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool historyKey = (mods == ModifierKeys.Alt) || (HistoryOnPlainArrows && mods == ModifierKeys.None);
            if (historyKey && (key == Key.Up || key == Key.Down) && History != null)
            {
                string t = key == Key.Up ? History.Older(_box.Text) : History.Newer();
                if (t != null)
                {
                    _suppress = true;
                    try
                    {
                        _box.Text = t;
                        _box.CaretIndex = t.Length;
                    }
                    finally
                    {
                        _suppress = false;
                    }
                    TextChanged?.Invoke(this, t);
                }
                e.Handled = true;
                return;
            }
            if (key == Key.Enter && mods == ModifierKeys.None && Submitted != null)
            {
                Submitted(this, _box.Text);
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Dropdown selector built from library parts (button + themed popup list), not a re-templated ComboBox.
    /// Keyboard: ↑/↓ change selection directly, Alt+↓ / Enter / Space open, Esc closes.
    /// </summary>
    public class Dropdown : ToggleButton
    {
        private readonly TextBlock _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Popup _popup = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
        private readonly StackPanel _list = new StackPanel();
        private readonly List<Border> _rows = new List<Border>();
        private IReadOnlyList<string> _items = Array.Empty<string>();
        private int _selected = -1;
        private int _hot = -1;
        private int _closedAt = int.MinValue / 2;

        public Dropdown()
        {
            // Plain secondary-button look (Fluent ComboBox): no accent tint while the list is open.
            Style = Styles.Button(ButtonKind.Secondary);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            MinWidth = 120;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var chevron = new Path { Data = Ui.ChevronDown, StrokeThickness = 1.3, Width = 8, Height = 4, Stretch = Stretch.Uniform, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            chevron.Bind(Shape.StrokeProperty, Tokens.TextSecondary);
            Grid.SetColumn(chevron, 1);
            grid.Children.Add(_label);
            grid.Children.Add(chevron);
            Content = grid;

            var chrome = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 2, 0, 0),
                Child = new UiScrollViewer { Content = _list, MaxHeight = 320 },
            };
            chrome.Bind(Border.BackgroundProperty, Tokens.SurfacePopup);
            chrome.Bind(Border.BorderBrushProperty, Tokens.BorderStrong);
            chrome.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusPopup);
            ThemeManager.Instance.Attach(chrome);
            _popup.Child = chrome;
            _popup.PlacementTarget = this;
            _popup.Opened += (s, e) => Ui.Safe("ui.dropdown.opened", () => { _hot = _selected; Paint(); chrome.MinWidth = ActualWidth; });
            _popup.Closed += (s, e) => Ui.Safe("ui.dropdown.closed", () => { _closedAt = Environment.TickCount; IsChecked = false; Focus(); });
            Checked += (s, e) => Ui.Safe("ui.dropdown.open", () =>
            {
                // A click on the button while open first closes the StaysOpen=false popup; don't reopen it.
                if (IsMouseOver && unchecked(Environment.TickCount - _closedAt) < 250) { IsChecked = false; return; }
                _popup.IsOpen = true;
            });
            Unchecked += (s, e) => Ui.Safe("ui.dropdown.close", () => _popup.IsOpen = false);
            PreviewKeyDown += (s, e) => Ui.Safe("ui.dropdown.key", () => OnKey(e));
        }

        public event EventHandler SelectionChanged;

        public IReadOnlyList<string> Items
        {
            get => _items;
            set
            {
                _items = value ?? Array.Empty<string>();
                Rebuild();
                if (_selected >= _items.Count) _selected = _items.Count - 1;
                if (_selected < 0 && _items.Count > 0) _selected = 0;
                UpdateLabel();
            }
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value < -1 || value >= _items.Count || value == _selected) return;
                _selected = value;
                UpdateLabel();
                Paint();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Replaces the item texts (language change) keeping the selection, without raising SelectionChanged.</summary>
        public void Relabel(IReadOnlyList<string> items)
        {
            int keep = _selected;
            _items = items ?? Array.Empty<string>();
            Rebuild();
            _selected = Math.Min(keep, _items.Count - 1);
            UpdateLabel();
        }

        private void UpdateLabel()
        {
            _label.Text = _selected >= 0 && _selected < _items.Count ? _items[_selected] : string.Empty;
            AutomationProperties.SetName(this, _label.Text);
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            _rows.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                int idx = i;
                var t = new TextBlock { Text = _items[i], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var row = new Border { Child = t, Padding = new Thickness(10, 0, 10, 0), Background = Brushes.Transparent };
                row.Bind(FrameworkElement.HeightProperty, Tokens.RowHeight);
                row.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
                AutomationProperties.SetName(row, _items[i]);
                row.MouseEnter += (s, e) => Ui.Safe("ui.dropdown.hover", () => { _hot = idx; Paint(); });
                row.MouseLeftButtonUp += (s, e) => Ui.Safe("ui.dropdown.pick", () => { SelectedIndex = idx; _popup.IsOpen = false; });
                _rows.Add(row);
                _list.Children.Add(row);
            }
            Paint();
        }

        private void Paint()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                // Fluent list flyout: the selected item keeps the soft selection fill, the hot (hover / arrow-key) item
                // gets the subtle hover fill.
                if (i == _selected) _rows[i].Bind(Border.BackgroundProperty, i == _hot ? Tokens.SelectionBackground : Tokens.SelectionInactiveBackground);
                else if (i == _hot) _rows[i].Bind(Border.BackgroundProperty, Tokens.SurfaceHover);
                else _rows[i].Background = Brushes.Transparent;
                if (_rows[i].Child is TextBlock tb)
                {
                    tb.FontWeight = FontWeights.Normal;
                    tb.Bind(TextBlock.ForegroundProperty, i == _selected ? Tokens.SelectionForeground : Tokens.TextPrimary);
                }
            }
        }

        private void OnKey(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (!_popup.IsOpen)
            {
                if ((key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt) || key == Key.Enter || key == Key.Space || key == Key.F4)
                {
                    IsChecked = true;
                    e.Handled = true;
                }
                else if (key == Key.Down && _selected < _items.Count - 1) { SelectedIndex = _selected + 1; e.Handled = true; }
                else if (key == Key.Up && _selected > 0) { SelectedIndex = _selected - 1; e.Handled = true; }
                return;
            }
            switch (key)
            {
                case Key.Down: _hot = Math.Min(_items.Count - 1, _hot + 1); Paint(); e.Handled = true; break;
                case Key.Up: _hot = Math.Max(0, _hot - 1); Paint(); e.Handled = true; break;
                case Key.Enter:
                case Key.Space:
                    if (_hot >= 0) SelectedIndex = _hot;
                    _popup.IsOpen = false;
                    e.Handled = true;
                    break;
                case Key.Escape: _popup.IsOpen = false; e.Handled = true; break;
            }
        }
    }

    /// <summary>
    /// Color setting: swatch + hex field + reset. Empty value = "auto" (derived); <see cref="AutoColor"/> shows
    /// what auto resolves to. Clicking the swatch opens a small preset palette.
    /// </summary>
    public class ColorField : StackPanel
    {
        private static readonly string[] Presets =
        {
            "#3574F0", "#548AF7", "#2EA39E", "#4EA652", "#8CC152", "#E0A21B",
            "#D88B2E", "#E5484D", "#C94F8F", "#9C6AD6", "#6C707E", "#A8ADBD",
        };

        private readonly Border _swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Focusable = true, FocusVisualStyle = null };
        private readonly TextInput _hex = new TextInput { Width = 84, Margin = new Thickness(6, 0, 0, 0) };
        private readonly IconButton _reset;
        private readonly Popup _popup = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
        private string _value;
        private Color _auto = Colors.Gray;

        public ColorField(string accessibleName)
        {
            Orientation = Orientation.Horizontal;
            _swatch.Bind(Border.BorderBrushProperty, Tokens.BorderStrong);
            _reset = new IconButton(Ui.CrossMark, () => Strings.CommonAuto, null, 8) { Margin = new Thickness(2, 0, 0, 0) };
            _hex.Placeholder = Strings.CommonAuto;
            Strings.Track(_hex, () => _hex.Placeholder = Strings.CommonAuto);
            AutomationProperties.SetName(_hex, accessibleName ?? string.Empty);
            AutomationProperties.SetName(_swatch, accessibleName ?? string.Empty);
            Children.Add(_swatch);
            Children.Add(_hex);
            Children.Add(_reset);

            var grid = new UniformGrid { Columns = 6 };
            foreach (var p in Presets)
            {
                var hex = p;
                Rgba.TryParse(p, out var c);
                var cell = new Border { Width = 20, Height = 20, Margin = new Thickness(2), CornerRadius = new CornerRadius(4), Background = Ui.Frozen(c.ToColor()), Cursor = Cursors.Hand };
                AutomationProperties.SetName(cell, hex);
                cell.MouseLeftButtonUp += (s, e) => Ui.Safe("ui.color.pick", () => { Value = hex; _popup.IsOpen = false; });
                grid.Children.Add(cell);
            }
            var chrome = new Border { Child = grid, Padding = new Thickness(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 0) };
            chrome.Bind(Border.BackgroundProperty, Tokens.SurfacePopup);
            chrome.Bind(Border.BorderBrushProperty, Tokens.BorderStrong);
            chrome.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusPopup);
            ThemeManager.Instance.Attach(chrome);
            _popup.Child = chrome;
            _popup.PlacementTarget = _swatch;

            _swatch.MouseLeftButtonUp += (s, e) => Ui.Safe("ui.color.open", () => _popup.IsOpen = true);
            _swatch.KeyDown += (s, e) => Ui.Safe("ui.color.key", () => { if (e.Key == Key.Enter || e.Key == Key.Space) { _popup.IsOpen = true; e.Handled = true; } });
            _reset.Click += (s, e) => Ui.Safe("ui.color.reset", () => Value = null);
            _hex.LostKeyboardFocus += (s, e) => Ui.Safe("ui.color.blur", CommitText);
            _hex.KeyDown += (s, e) => Ui.Safe("ui.color.enter", () => { if (e.Key == Key.Enter) { CommitText(); e.Handled = true; } });
            Paint();
        }

        public event EventHandler ValueChanged;

        /// <summary>#RRGGBB, or null for auto.</summary>
        public string Value
        {
            get => _value;
            set
            {
                string v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (v != null && !Rgba.TryParse(v, out _)) v = _value;
                if (v != null && Rgba.TryParse(v, out var c)) v = c.ToHex();
                bool changed = v != _value;
                _value = v;
                Paint();
                if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SetSilently(string value)
        {
            _value = string.IsNullOrWhiteSpace(value) || !Rgba.TryParse(value, out _) ? null : value.Trim();
            Paint();
        }

        public Color AutoColor
        {
            get => _auto;
            set
            {
                _auto = value;
                Paint();
            }
        }

        private void CommitText()
        {
            var t = _hex.Text;
            if (string.IsNullOrWhiteSpace(t)) Value = null;
            else if (Rgba.TryParse(t, out _)) Value = t;
            else Paint();
        }

        private void Paint()
        {
            Color c = _auto;
            if (_value != null && Rgba.TryParse(_value, out var r)) c = r.ToColor();
            _swatch.Background = Ui.Frozen(c);
            _hex.Text = _value ?? string.Empty;
            _reset.Visibility = _value == null ? Visibility.Hidden : Visibility.Visible;
        }
    }
}
