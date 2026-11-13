using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UEToVS.UI.Atoms;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Controls
{
    /// <summary>
    /// A settings group (warm-scheme mockup): a small semibold label above one rounded card that holds the group's
    /// rows, separated by hairline dividers inset 16px. Replaces one-card-per-setting so related settings read as one
    /// block and the page stays dense.
    /// </summary>
    public class SettingsGroup : StackPanel
    {
        private readonly StackPanel _rows = new StackPanel();
        private readonly UiText _label = new UiText((string)null, TextVariant.GroupLabel) { Margin = new Thickness(2, 0, 0, 8) };

        public SettingsGroup(string label)
        {
            Label = label;
            Children.Add(_label);
            var card = new Border { Child = _rows, BorderThickness = new Thickness(1), SnapsToDevicePixels = true, ClipToBounds = false };
            card.Bind(Border.BackgroundProperty, Tokens.SurfaceRaised);
            card.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            card.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusPopup);
            Children.Add(card);
        }

        public string Label
        {
            get => _label.Text;
            set
            {
                _label.Text = value ?? string.Empty;
                _label.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public int Count { get; private set; }

        /// <summary>Adds a row (any element; <see cref="SettingsRow"/> for the standard title/description/editor row).</summary>
        public void Add(UIElement row)
        {
            if (row == null) return;
            if (Count > 0) _rows.Children.Add(new SettingsDivider());
            _rows.Children.Add(row);
            Count++;
        }
    }

    /// <summary>1px hairline between rows of a <see cref="SettingsGroup"/>, inset 16px on both sides.</summary>
    public class SettingsDivider : Border
    {
        public SettingsDivider()
        {
            Height = 1;
            Margin = new Thickness(16, 0, 16, 0);
            SnapsToDevicePixels = true;
            this.Bind(BackgroundProperty, Tokens.Divider);
        }
    }

    /// <summary>
    /// One row of a <see cref="SettingsGroup"/>: title (body) with an optional description (small, tertiary) on the left,
    /// the editor on the right at its natural size, vertically centred; at least 52px tall, 16px side padding.
    /// <see cref="Below"/> takes full-width content under the title (path lists, grids).
    /// </summary>
    public class SettingsRow : Border
    {
        public const double MinRowHeight = 52;
        private readonly Grid _grid = new Grid();
        private readonly StackPanel _labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        private UIElement _editor, _below;

        public SettingsRow(string title, string description = null, UIElement editor = null)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _grid.MinHeight = MinRowHeight - 16;
            _labels.Children.Add(new UiText(title, TextVariant.Body) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
            if (!string.IsNullOrEmpty(description))
                _labels.Children.Add(new UiText(description, TextVariant.Description) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) });
            _grid.Children.Add(_labels);
            Child = _grid;
            Padding = new Thickness(16, 8, 16, 8);
            MinHeight = MinRowHeight;
            Editor = editor;
        }

        /// <summary>The text column (extra notes such as "restart needed" or a live info line go here).</summary>
        public StackPanel Labels => _labels;

        public UIElement Editor
        {
            get => _editor;
            set
            {
                if (_editor != null) _grid.Children.Remove(_editor);
                _editor = value;
                if (value == null) return;
                if (value is FrameworkElement fe)
                {
                    fe.VerticalAlignment = VerticalAlignment.Center;
                    fe.HorizontalAlignment = HorizontalAlignment.Right;
                }
                Grid.SetColumn(value, 1);
                _grid.Children.Add(value);
            }
        }

        /// <summary>Full-width content under the title row.</summary>
        public UIElement Below
        {
            get => _below;
            set
            {
                if (_below != null) _grid.Children.Remove(_below);
                _below = value;
                if (value == null) return;
                if (value is FrameworkElement fe && fe.Margin == default(Thickness)) fe.Margin = new Thickness(0, 10, 0, 4);
                Grid.SetRow(value, 1);
                Grid.SetColumnSpan(value, 2);
                _grid.Children.Add(value);
            }
        }
    }

    /// <summary>
    /// Settings page heading: serif title (≈ 2× body) and an optional one-line description under it.
    /// </summary>
    public class SettingsPageHeader : StackPanel
    {
        public SettingsPageHeader(string title, string description = null)
        {
            Children.Add(new UiText(title, TextVariant.PageTitle));
            if (!string.IsNullOrEmpty(description))
                Children.Add(new UiText(description, TextVariant.Secondary) { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
            Margin = new Thickness(0, 0, 0, 22);
        }
    }

    /// <summary>
    /// Item of the settings navigation (warm-scheme mockup): 16px stroke icon + label, 34px tall, rounded; selected =
    /// neutral fill, medium weight, accent icon and a 3x16 accent bar at the left edge; hover = subtle fill.
    /// Keyboard: focusable, Enter/Space activate, focus ring only for keyboard focus.
    /// </summary>
    public class SettingsNavItem : Border
    {
        private readonly Path _icon;
        private readonly UiText _text;
        private readonly Border _bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-10, 0, 0, 0) };
        private bool _selected;

        public SettingsNavItem(string text, Geometry icon)
        {
            _icon = new Path
            {
                Data = icon, Width = 16, Height = 16, Stretch = Stretch.None, StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                Visibility = icon == null ? Visibility.Collapsed : Visibility.Visible,
            };
            _text = new UiText(text, TextVariant.Body) { VerticalAlignment = VerticalAlignment.Center };
            _bar.Bind(Border.BackgroundProperty, Tokens.Accent);
            var row = new Grid();
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(_icon);
            line.Children.Add(_text);
            row.Children.Add(line);
            row.Children.Add(_bar);
            Child = row;
            Height = 34;
            Padding = new Thickness(10, 0, 10, 0);
            Margin = new Thickness(0, 1, 0, 1);
            Cursor = Cursors.Hand;
            Focusable = true;
            FocusVisualStyle = null;
            BorderThickness = new Thickness(1);
            BorderBrush = Brushes.Transparent;
            this.Bind(CornerRadiusProperty, Tokens.CornerRadiusControl);
            AutomationProperties.SetName(this, text ?? string.Empty);
            MouseEnter += (s, e) => Ui.Safe("ui.nav.hover", Paint);
            MouseLeave += (s, e) => Ui.Safe("ui.nav.hover", Paint);
            MouseLeftButtonUp += (s, e) => Ui.Safe("ui.nav.click", () => { e.Handled = true; Activated?.Invoke(this, EventArgs.Empty); });
            KeyDown += (s, e) => Ui.Safe("ui.nav.key", () =>
            {
                if (e.Key != Key.Enter && e.Key != Key.Space) return;
                e.Handled = true;
                Activated?.Invoke(this, EventArgs.Empty);
            });
            GotKeyboardFocus += (s, e) => { if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) this.Bind(BorderBrushProperty, Tokens.FocusStroke); };
            LostKeyboardFocus += (s, e) => BorderBrush = Brushes.Transparent;
            Paint();
        }

        public event EventHandler Activated;

        public bool IsSelected
        {
            get => _selected;
            set
            {
                _selected = value;
                AutomationProperties.SetItemStatus(this, value ? "selected" : string.Empty);
                Paint();
            }
        }

        /// <summary>Dimmed when a search has no hit in this category.</summary>
        public bool Dimmed
        {
            set => Opacity = value ? 0.45 : 1;
        }

        private void Paint()
        {
            _bar.Visibility = _selected ? Visibility.Visible : Visibility.Hidden;
            _text.FontWeight = _selected ? FontWeights.Medium : FontWeights.Normal;
            _text.Bind(TextBlock.ForegroundProperty, _selected ? Tokens.TextPrimary : Tokens.TextSecondary);
            _icon.Bind(Shape.StrokeProperty, _selected ? Tokens.TextLink : Tokens.TextSecondary);
            if (_selected) this.Bind(BackgroundProperty, Tokens.NavSelected);
            else if (IsMouseOver) this.Bind(BackgroundProperty, Tokens.SurfaceHover);
            else Background = Brushes.Transparent;
        }
    }

    /// <summary>16px stroke icons of the settings navigation (drawn in a 16x16 box).</summary>
    public static class SettingsIcons
    {
        public static readonly Geometry General = Ui.FrozenGeometry("M 8,5.8 A 2.2,2.2 0 1 1 7.99,5.8 M 8,1.5 V 3.5 M 8,12.5 V 14.5 M 1.5,8 H 3.5 M 12.5,8 H 14.5 M 3.4,3.4 L 4.8,4.8 M 11.2,11.2 L 12.6,12.6 M 3.4,12.6 L 4.8,11.2 M 11.2,4.8 L 12.6,3.4");
        public static readonly Geometry Appearance = Ui.FrozenGeometry("M 8,1.8 A 6.2,6.2 0 1 0 8,14.2 C 9,14.2 9.4,13.6 9.4,12.9 C 9.4,12 8.6,11.7 8.6,10.9 C 8.6,10.2 9.2,9.7 9.9,9.7 H 11.5 A 2.7,2.7 0 0 0 14.2,7 C 14.2,4.1 11.4,1.8 8,1.8 Z M 5,6.6 V 7.4 M 7.6,4.4 V 5.2 M 10.6,5.2 V 6");
        public static readonly Geometry Index = Ui.FrozenGeometry("M 3,4 A 5,2 0 1 0 13,4 A 5,2 0 1 0 3,4 Z M 3,4 V 12 C 3,13.1 5.2,14 8,14 C 10.8,14 13,13.1 13,12 V 4 M 3,8 C 3,9.1 5.2,10 8,10 C 10.8,10 13,9.1 13,8");
        public static readonly Geometry Keymap = Ui.FrozenGeometry("M 3.3,4 H 12.7 A 1.8,1.8 0 0 1 14.5,5.8 V 10.2 A 1.8,1.8 0 0 1 12.7,12 H 3.3 A 1.8,1.8 0 0 1 1.5,10.2 V 5.8 A 1.8,1.8 0 0 1 3.3,4 Z M 4,7 H 5 M 7,7 H 8 M 10,7 H 12 M 5,9.5 H 11");
        public static readonly Geometry Navigation = Ui.FrozenGeometry("M 8,1.5 L 14.5,8 L 8,14.5 L 1.5,8 Z M 6,8 H 10 M 8.5,6.5 L 10,8 L 8.5,9.5");
        public static readonly Geometry Code = Ui.FrozenGeometry("M 5.5,4 L 2,8 L 5.5,12 M 10.5,4 L 14,8 L 10.5,12");
        public static readonly Geometry Agent = Ui.FrozenGeometry("M 8,1.5 L 9.4,6.6 L 14.5,8 L 9.4,9.4 L 8,14.5 L 6.6,9.4 L 1.5,8 L 6.6,6.6 Z");
        public static readonly Geometry Logs = Ui.FrozenGeometry("M 3,2.5 H 10 L 13,5.5 V 13.5 H 3 Z M 5.5,8 H 10.5 M 5.5,10.5 H 10.5");
        public static readonly Geometry Performance = Ui.FrozenGeometry("M 1.5,9 H 4.5 L 6.5,4 L 9.5,13 L 11.5,9 H 14.5");
        public static readonly Geometry Update = Ui.FrozenGeometry("M 8,1.5 V 10 M 4.5,6.5 L 8,10 L 11.5,6.5 M 2.5,11.5 V 14 H 13.5 V 11.5");
        /// <summary>联合编译: three linked machines.</summary>
        public static readonly Geometry Network = Ui.FrozenGeometry("M 6,1.5 H 10 V 5 H 6 Z M 1.5,11 H 5.5 V 14.5 H 1.5 Z M 10.5,11 H 14.5 V 14.5 H 10.5 Z M 8,5 V 8 M 3.5,11 V 8 H 12.5 V 11");
        public static readonly Geometry Folder = Ui.FrozenGeometry("M 1.5,4 A 1,1 0 0 1 2.5,3 H 5.5 L 7,4.5 H 12.5 A 1,1 0 0 1 13.5,5.5 V 11 A 1,1 0 0 1 12.5,12 H 2.5 A 1,1 0 0 1 1.5,11 Z");
        public static readonly Geometry Reset = Ui.FrozenGeometry("M 2,6.5 A 4.5,4.5 0 1 0 3.3,3.3 M 2,1.5 V 4 H 4.5");
        public static readonly Geometry Plus = Ui.FrozenGeometry("M 6,1.5 V 10.5 M 1.5,6 H 10.5");
    }
}
