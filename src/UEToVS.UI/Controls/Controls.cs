using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Imaging.Interop;
using UEToVS.UI.Atoms;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Controls
{
    /// <summary>Button (Primary / Secondary / Ghost). Content is usually a string.</summary>
    public class UiButton : Button
    {
        private ButtonKind _kind;

        public UiButton() : this((string)null, ButtonKind.Secondary) { }

        /// <summary>Localized label (re-read on language change).</summary>
        public UiButton(Func<string> text, ButtonKind kind = ButtonKind.Secondary) : this(text?.Invoke(), kind)
        {
            if (text != null) Strings.Track(this, () => { var t = text(); Content = t; AutomationProperties.SetName(this, t ?? string.Empty); });
        }

        public UiButton(string text, ButtonKind kind = ButtonKind.Secondary)
        {
            Content = text;
            Kind = kind;
            if (text != null) AutomationProperties.SetName(this, text);
        }

        public ButtonKind Kind
        {
            get => _kind;
            set
            {
                _kind = value;
                Style = Styles.Button(value);
            }
        }
    }

    /// <summary>Builds icon content: a VS moniker or a stroked vector glyph.</summary>
    internal static class IconContent
    {
        public static UIElement Glyph(Geometry glyph, double size = 10)
        {
            var p = new Path
            {
                Data = glyph,
                StrokeThickness = 1.4,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = false,
            };
            // Follow the button foreground (hover/checked states recolor it).
            p.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1),
            });
            return p;
        }
    }

    /// <summary>Square ghost button with an icon (moniker or vector glyph) and a tooltip.</summary>
    public class IconButton : UiButton
    {
        public IconButton(ImageMoniker moniker, string tooltip, string shortcut = null) : base((string)null, ButtonKind.Ghost)
        {
            Init(tooltip, shortcut);
            Content = new UiIcon(moniker);
        }

        public IconButton(Geometry glyph, string tooltip, string shortcut = null, double glyphSize = 10) : base((string)null, ButtonKind.Ghost)
        {
            Init(tooltip, shortcut);
            Content = IconContent.Glyph(glyph, glyphSize);
        }

        /// <summary>Localized tooltip variant.</summary>
        public IconButton(Geometry glyph, Func<string> tooltip, string shortcut = null, double glyphSize = 10) : base((string)null, ButtonKind.Ghost)
        {
            Init(null, null);
            Content = IconContent.Glyph(glyph, glyphSize);
            Tip.Set(this, tooltip, shortcut);
        }

        private void Init(string tooltip, string shortcut)
        {
            Width = 24;
            Height = 24;
            MinHeight = 0;
            Padding = new Thickness(0);
            Foreground = null;
            this.Bind(ForegroundProperty, Tokens.TextSecondary);
            if (!string.IsNullOrEmpty(tooltip))
            {
                AutomationProperties.SetName(this, tooltip);
                Tip.Set(this, tooltip, shortcut);
            }
        }
    }

    /// <summary>Icon toggle (e.g. preview on/off) — ghost look, accent tint when checked.</summary>
    public class IconToggle : ToggleButton
    {
        /// <summary>Localized tooltip variant.</summary>
        public IconToggle(Geometry glyph, Func<string> tooltip, string shortcut = null, double glyphSize = 11) : this(glyph, (string)null, null, glyphSize)
        {
            Tip.Set(this, tooltip, shortcut);
        }

        public IconToggle(Geometry glyph, string tooltip, string shortcut = null, double glyphSize = 11)
        {
            Style = Styles.ToggleButtonStyle(ButtonKind.Ghost);
            Width = 24;
            Height = 24;
            MinHeight = 0;
            Padding = new Thickness(0);
            Content = IconContent.Glyph(glyph, glyphSize);
            AutomationProperties.SetName(this, tooltip ?? string.Empty);
            if (!string.IsNullOrEmpty(tooltip)) Tip.Set(this, tooltip, shortcut);
        }

        public IconToggle(ImageMoniker moniker, string tooltip, string shortcut = null)
        {
            Style = Styles.ToggleButtonStyle(ButtonKind.Ghost);
            Width = 24;
            Height = 24;
            MinHeight = 0;
            Padding = new Thickness(0);
            Content = new UiIcon(moniker);
            AutomationProperties.SetName(this, tooltip ?? string.Empty);
            if (!string.IsNullOrEmpty(tooltip)) Tip.Set(this, tooltip, shortcut);
        }
    }

    /// <summary>On/off switch with a label.</summary>
    public class UiToggle : ToggleButton
    {
        public UiToggle() : this((string)null) { }

        public UiToggle(Func<string> label, bool isOn = false) : this(label?.Invoke(), isOn)
        {
            if (label != null) Strings.Track(this, () => { Content = label(); AutomationProperties.SetName(this, label() ?? string.Empty); });
        }

        public UiToggle(string label, bool isOn = false)
        {
            Style = Styles.Toggle;
            Content = label;
            IsChecked = isOn;
            AutomationProperties.SetName(this, label ?? string.Empty);
        }

        public bool IsOn
        {
            get => IsChecked == true;
            set => IsChecked = value;
        }
    }

    /// <summary>Themed check box.</summary>
    public class UiCheckBox : CheckBox
    {
        public UiCheckBox() : this((string)null) { }

        public UiCheckBox(Func<string> label, bool isChecked = false) : this(label?.Invoke(), isChecked)
        {
            if (label != null) Strings.Track(this, () => { Content = label(); AutomationProperties.SetName(this, label() ?? string.Empty); });
        }

        public UiCheckBox(string label, bool isChecked = false)
        {
            Style = Styles.CheckBox;
            Content = label;
            IsChecked = isChecked;
            AutomationProperties.SetName(this, label ?? string.Empty);
        }
    }

    /// <summary>Filter chip: pill toggle with optional count ("Blueprint 12").</summary>
    public class Chip : ToggleButton
    {
        private readonly TextBlock _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), Visibility = Visibility.Collapsed };

        public Chip() : this((string)null) { }

        public Chip(Func<string> text, bool isChecked = false) : this(text?.Invoke(), isChecked)
        {
            if (text != null) Strings.Track(this, () => Text = text());
        }

        public Chip(string text, bool isChecked = false)
        {
            Style = Styles.Chip;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            _count.Bind(TextBlock.ForegroundProperty, Tokens.TextSecondary);
            sp.Children.Add(_label);
            sp.Children.Add(_count);
            Content = sp;
            Text = text;
            IsChecked = isChecked;
        }

        public string Text
        {
            get => _label.Text;
            set
            {
                _label.Text = value ?? string.Empty;
                AutomationProperties.SetName(this, value ?? string.Empty);
            }
        }

        /// <summary>Null hides the count.</summary>
        public int? Count
        {
            set
            {
                _count.Text = value.HasValue ? PerfMath.FormatCount(value.Value) : string.Empty;
                _count.Visibility = value.HasValue ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Mutually exclusive options (radio group), VS 2026 style: a rounded-rectangle track (Segment.Track, subtle
    /// border, control radius, 2px inset) holding rounded-rectangle segments; the selected one is accent-filled.
    /// ←/→ change the selection.
    /// </summary>
    public class SegmentedControl : Border
    {
        private readonly StackPanel _panel = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly List<Chip> _chips = new List<Chip>();
        private int _selected = -1;
        private bool _updating;

        public SegmentedControl(params string[] options)
        {
            Child = _panel;
            Padding = new Thickness(2);
            BorderThickness = new Thickness(1);
            HorizontalAlignment = HorizontalAlignment.Left;
            SnapsToDevicePixels = true;
            this.Bind(BackgroundProperty, Tokens.SegmentTrack);
            this.Bind(BorderBrushProperty, Tokens.BorderSubtle);
            this.Bind(CornerRadiusProperty, Tokens.CornerRadiusControl);
            // Exactly the standard control height (buttons, text boxes, combo boxes): segments stretch inside the
            // 2px-inset track instead of growing it with the text line height.
            this.Bind(HeightProperty, Tokens.ControlHeight);
            KeyboardNavigation.SetDirectionalNavigation(_panel, KeyboardNavigationMode.None);
            SetOptions(options);
            Focusable = false;
        }

        public event EventHandler SelectionChanged;

        public void SetOptions(IEnumerable<string> options)
        {
            _panel.Children.Clear();
            _chips.Clear();
            int i = 0;
            if (options != null)
                foreach (var o in options)
                {
                    int idx = i++;
                    var chip = new Chip(o) { Style = Styles.Segment, Margin = new Thickness(idx == 0 ? 0 : 2, 0, 0, 0) };
                    chip.Checked += (s, e) => Ui.Safe("ui.segmented.checked", () => { if (!_updating) SelectedIndex = idx; });
                    chip.Unchecked += (s, e) => Ui.Safe("ui.segmented.unchecked", () =>
                    {
                        // Radio semantics: clicking the selected chip keeps it selected.
                        if (!_updating && _selected == idx) { _updating = true; chip.IsChecked = true; _updating = false; }
                    });
                    chip.PreviewKeyDown += (s, e) => Ui.Safe("ui.segmented.key", () =>
                    {
                        if (e.Key == Key.Left && idx > 0) { SelectedIndex = idx - 1; _chips[idx - 1].Focus(); e.Handled = true; }
                        else if (e.Key == Key.Right && idx < _chips.Count - 1) { SelectedIndex = idx + 1; _chips[idx + 1].Focus(); e.Handled = true; }
                    });
                    _chips.Add(chip);
                    _panel.Children.Add(chip);
                }
            _selected = -1;
            if (_chips.Count > 0) SelectedIndex = 0;
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value < 0 || value >= _chips.Count) return;
                bool changed = value != _selected;
                _selected = value;
                _updating = true;
                for (int i = 0; i < _chips.Count; i++) _chips[i].IsChecked = i == value;
                _updating = false;
                if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Text box with placeholder and token styling.</summary>
    public class TextInput : TextBox
    {
        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
            nameof(Placeholder), typeof(string), typeof(TextInput), new PropertyMetadata(string.Empty));

        public TextInput()
        {
            Style = Styles.TextInput;
        }

        public string Placeholder
        {
            get => (string)GetValue(PlaceholderProperty);
            set
            {
                SetValue(PlaceholderProperty, value ?? string.Empty);
                if (string.IsNullOrEmpty(AutomationProperties.GetName(this))) AutomationProperties.SetName(this, value ?? string.Empty);
            }
        }
    }

    /// <summary>Tooltip helper: themed ToolTip with optional keycap shortcut.</summary>
    public static class Tip
    {
        /// <summary>Localized tooltip: re-created when the UI language changes.</summary>
        public static void Set(FrameworkElement element, Func<string> text, string shortcut = null)
        {
            if (element == null || text == null) return;
            Strings.Track(element, () =>
            {
                Set(element, text(), shortcut);
                AutomationProperties.SetName(element, text() ?? string.Empty);
            });
        }

        public static void Set(FrameworkElement element, string text, string shortcut = null)
        {
            if (element == null) return;
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(shortcut)) { element.ToolTip = null; return; }
            var tip = new ToolTip { Style = Styles.ToolTip };
            ThemeManager.Instance.Attach(tip);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = text ?? string.Empty, VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrEmpty(shortcut)) sp.Children.Add(new Kbd(shortcut) { Margin = new Thickness(8, 0, 0, 0) });
            tip.Content = sp;
            element.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(element, 450);
            ToolTipService.SetShowOnDisabled(element, true);
        }
    }

    /// <summary>Clickable link text (Enter/Space when focused).</summary>
    public class LinkText : TextBlock
    {
        public LinkText(Func<string> text) : this(text?.Invoke())
        {
            if (text != null) Strings.Track(this, () => { Text = text() ?? string.Empty; AutomationProperties.SetName(this, Text); });
        }

        public LinkText(string text)
        {
            Text = text ?? string.Empty;
            Focusable = true;
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;
            this.Bind(ForegroundProperty, Tokens.TextLink);
            FocusVisualStyle = null;
            AutomationProperties.SetName(this, text ?? string.Empty);
            MouseEnter += (s, e) => Ui.Safe("ui.link.enter", () => { TextDecorations = System.Windows.TextDecorations.Underline; this.Bind(ForegroundProperty, Tokens.TextLinkHover); });
            MouseLeave += (s, e) => Ui.Safe("ui.link.leave", () => { if (!IsKeyboardFocused) TextDecorations = null; this.Bind(ForegroundProperty, Tokens.TextLink); });
            GotKeyboardFocus += (s, e) => TextDecorations = System.Windows.TextDecorations.Underline;
            LostKeyboardFocus += (s, e) => { if (!IsMouseOver) TextDecorations = null; };
            MouseLeftButtonUp += (s, e) => Ui.Safe("ui.link.click", () => { e.Handled = true; Click?.Invoke(this, EventArgs.Empty); });
            KeyDown += (s, e) => Ui.Safe("ui.link.key", () =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space) { e.Handled = true; Click?.Invoke(this, EventArgs.Empty); }
            });
        }

        public event EventHandler Click;
    }

    /// <summary>Numeric field with −/+ steppers; ↑/↓ step, Enter / focus loss commit.</summary>
    public class NumberStepper : Grid
    {
        // Box width: room for 6 digits at the 8px Fluent inset; the box takes any extra width the owner gives the
        // stepper, and the stepper never gets narrower than box + both buttons (so −/+ are never clipped).
        private const double BoxMinWidth = 64, ButtonSize = 24, ButtonGap = 4;
        private readonly TextInput _box = new TextInput { MinWidth = BoxMinWidth };
        private double _value;

        public NumberStepper(double min, double max, double step, double value, string format = "0")
        {
            Minimum = min;
            Maximum = max;
            Step = step;
            Format = format;
            MinWidth = BoxMinWidth + ButtonGap + 2 * ButtonSize;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = BoxMinWidth });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var minus = new IconButton(Ui.FrozenGeometry("M 0,4 L 8,4"), "−") { Margin = new Thickness(ButtonGap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var plus = new IconButton(Ui.FrozenGeometry("M 0,4 L 8,4 M 4,0 L 4,8"), "+") { VerticalAlignment = VerticalAlignment.Center };
            minus.Focusable = false;
            plus.Focusable = false;
            SetColumn(_box, 0);
            SetColumn(minus, 1);
            SetColumn(plus, 2);
            Children.Add(_box);
            Children.Add(minus);
            Children.Add(plus);
            minus.Click += (s, e) => Ui.Safe("ui.stepper.minus", () => Value = _value - Step);
            plus.Click += (s, e) => Ui.Safe("ui.stepper.plus", () => Value = _value + Step);
            _box.PreviewKeyDown += (s, e) => Ui.Safe("ui.stepper.key", () =>
            {
                if (e.Key == Key.Up) { Commit(); Value = _value + Step; e.Handled = true; }
                else if (e.Key == Key.Down) { Commit(); Value = _value - Step; e.Handled = true; }
                else if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            });
            _box.LostKeyboardFocus += (s, e) => Ui.Safe("ui.stepper.blur", Commit);
            _value = double.NaN;
            Value = value;
        }

        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public double Step { get; set; }
        public string Format { get; set; }

        public event EventHandler ValueChanged;

        public string AccessibleName
        {
            set => AutomationProperties.SetName(_box, value ?? string.Empty);
        }

        public double Value
        {
            get => _value;
            set
            {
                double v = Math.Max(Minimum, Math.Min(Maximum, value));
                if (Step > 0) v = Math.Round(v / Step) * Step;
                v = Math.Max(Minimum, Math.Min(Maximum, v));
                _box.Text = v.ToString(Format, CultureInfo.InvariantCulture);
                if (v.Equals(_value)) return;
                _value = v;
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Shows an empty box (e.g. a disabled "auto" value that is not known); <see cref="Value"/> is kept.</summary>
        public void ShowBlank() => _box.Text = string.Empty;

        public void FocusInput()
        {
            _box.Focus();
            _box.SelectAll();
        }

        /// <summary>Sets the value without raising <see cref="ValueChanged"/>.</summary>
        public void SetSilently(double v)
        {
            _value = Math.Max(Minimum, Math.Min(Maximum, v));
            _box.Text = _value.ToString(Format, CultureInfo.InvariantCulture);
        }

        private void Commit()
        {
            if (double.TryParse(_box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
                double.TryParse(_box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                Value = v;
            else
                _box.Text = _value.ToString(Format, CultureInfo.InvariantCulture);
        }
    }
}
