using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Controls
{
    public enum ButtonKind
    {
        Secondary = 0,
        Primary = 1,
        Ghost = 2,
    }

    /// <summary>
    /// Code-built styles + control templates (the library ships no .xaml). Every visual value is a token reference,
    /// so the styles are created once and never rebuilt. Styles are frozen-safe to share across instances.
    /// </summary>
    public static class Styles
    {
        private static Style _ghostToggle, _secondaryToggle;
        private static Style _segment;
        private static Style _primary, _secondary, _ghost, _toggle, _check, _chip, _input, _bareInput, _tooltip, _scroll, _thumb;

        public static Style Button(ButtonKind kind)
        {
            switch (kind)
            {
                case ButtonKind.Primary: return _primary ?? (_primary = BuildButton(kind));
                case ButtonKind.Ghost: return _ghost ?? (_ghost = BuildButton(kind));
                default: return _secondary ?? (_secondary = BuildButton(kind));
            }
        }

        /// <summary>Same look as <see cref="Button"/> for a ToggleButton; checked state shows the accent tint.</summary>
        public static Style ToggleButtonStyle(ButtonKind kind) => kind == ButtonKind.Ghost
            ? _ghostToggle ?? (_ghostToggle = BuildButton(kind, true))
            : _secondaryToggle ?? (_secondaryToggle = BuildButton(ButtonKind.Secondary, true));

        /// <summary>
        /// VS 2026 (Fluent) control template: a rounded <c>Bd</c> border plus a <c>Focus</c> ring 3px outside it that
        /// only shows for keyboard focus (Fluent focus visual), so mouse users never see a ring.
        /// </summary>
        private static ControlTemplate BorderTemplate(Type target, object radiusKey, bool contentPresenter = true, bool focusRing = true)
        {
            var root = new FrameworkElementFactory(typeof(Grid));
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            bd.SetResourceReference(Border.CornerRadiusProperty, radiusKey);
            if (contentPresenter)
            {
                var cp = new FrameworkElementFactory(typeof(ContentPresenter), "Content");
                cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
                cp.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
                cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
                bd.AppendChild(cp);
            }
            root.AppendChild(bd);
            var t = new ControlTemplate(target) { VisualTree = root };
            if (focusRing)
            {
                root.AppendChild(FocusRing(Tokens.CornerRadiusFocus));
                AddFocusRingTrigger(t);
            }
            return t;
        }

        /// <summary>Keyboard focus ring element (named "Focus"), hidden until <see cref="AddFocusRingTrigger"/> shows it.</summary>
        internal static FrameworkElementFactory FocusRing(object radiusKey)
        {
            var ring = new FrameworkElementFactory(typeof(Border), "Focus");
            ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
            ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
            ring.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            ring.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            ring.SetResourceReference(Border.BorderThicknessProperty, Tokens.ThicknessFocus);
            ring.SetResourceReference(Border.BorderBrushProperty, Tokens.FocusStroke);
            ring.SetResourceReference(Border.CornerRadiusProperty, radiusKey);
            return ring;
        }

        internal static void AddFocusRingTrigger(ControlTemplate t)
        {
            FocusVisual.EnsureRegistered();
            var kf = new Trigger { Property = FocusVisual.IsVisibleProperty, Value = true };
            kf.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Focus"));
            t.Triggers.Add(kf);
        }

        private static Style BuildButton(ButtonKind kind, bool toggle = false)
        {
            var target = toggle ? typeof(ToggleButton) : typeof(ButtonBase);
            var s = new Style(target);
            s.Setters.Add(new Setter(Control.TemplateProperty, BorderTemplate(target, Tokens.CornerRadiusControl)));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            s.Setters.Add(Ui.Dyn(Control.PaddingProperty, Tokens.ThicknessControlPadding));
            s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
            s.Setters.Add(Ui.Dyn(FrameworkElement.MinHeightProperty, Tokens.ControlHeight));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeBody));

            object bg, fg, border, hover, pressed;
            switch (kind)
            {
                case ButtonKind.Primary:
                    bg = Tokens.Accent; fg = Tokens.TextOnAccent; border = Tokens.Accent;
                    hover = Tokens.AccentHover; pressed = Tokens.AccentPressed;
                    break;
                case ButtonKind.Ghost:
                    bg = null; fg = Tokens.TextPrimary; border = null;
                    hover = Tokens.ButtonGhostHover; pressed = Tokens.ButtonGhostPressed;
                    break;
                default:
                    bg = Tokens.ButtonSecondaryBackground; fg = Tokens.TextPrimary; border = Tokens.ButtonSecondaryBorder;
                    hover = Tokens.ButtonSecondaryHover; pressed = Tokens.ButtonSecondaryPressed;
                    break;
            }
            s.Setters.Add(bg == null ? new Setter(Control.BackgroundProperty, Brushes.Transparent) : Ui.Dyn(Control.BackgroundProperty, bg));
            s.Setters.Add(border == null ? new Setter(Control.BorderBrushProperty, Brushes.Transparent) : Ui.Dyn(Control.BorderBrushProperty, border));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, fg));
            // Medium 而不是 SemiBold：内置中文字体 Noto Sans SC 只带 Regular / Medium，要 SemiBold 时汉字会被系统合成加粗，笔画糊在一起
            if (kind == ButtonKind.Primary) s.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Medium));

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(Ui.Dyn(Control.BackgroundProperty, hover));
            s.Triggers.Add(over);
            var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(Ui.Dyn(Control.BackgroundProperty, pressed));
            s.Triggers.Add(down);
            if (kind == ButtonKind.Secondary)
            {
                // Fluent pressed state: the text dims slightly instead of the fill getting much darker.
                var downFg = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
                downFg.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextSecondary));
                s.Triggers.Add(downFg);
            }
            if (toggle)
            {
                var checkedT = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
                checkedT.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.AccentSubtle));
                checkedT.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, Tokens.Accent));
                checkedT.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextPrimary));
                s.Triggers.Add(checkedT);
            }
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            s.Triggers.Add(dis);
            return s;
        }

        /// <summary>
        /// Fluent toggle switch (VS 2026 / Windows 11): fixed 40x20 <see cref="ToggleSwitchTrack"/>, vertically centred,
        /// content (if any) 12px to its right. The whole control is the hit target; Space toggles; the focus ring only
        /// shows for keyboard focus.
        /// </summary>
        public static Style Toggle => _toggle ?? (_toggle = BuildToggle());

        private static Style BuildToggle()
        {
            var root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            root.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var trackGrid = new FrameworkElementFactory(typeof(Grid));
            trackGrid.SetValue(FrameworkElement.WidthProperty, Logic.ToggleSwitchGeometry.TrackWidth);
            trackGrid.SetValue(FrameworkElement.HeightProperty, Logic.ToggleSwitchGeometry.TrackHeight);
            trackGrid.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            trackGrid.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            var track = new FrameworkElementFactory(typeof(ToggleSwitchTrack), "Track");
            track.SetValue(ToggleSwitchTrack.IsOnProperty, new Binding(nameof(ToggleButton.IsChecked))
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Converter = NullableBoolConverter.Instance,
            });
            track.SetValue(ToggleSwitchTrack.IsHoveredProperty, new TemplateBindingExtension(UIElement.IsMouseOverProperty));
            track.SetValue(ToggleSwitchTrack.IsPressedProperty, new TemplateBindingExtension(ButtonBase.IsPressedProperty));
            trackGrid.AppendChild(track);
            var ring = FocusRing(Tokens.CornerRadiusPill);
            trackGrid.AppendChild(ring);
            root.AppendChild(trackGrid);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter), "Content");
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(Logic.ToggleSwitchGeometry.ContentGap, 0, 0, 0));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            root.AppendChild(cp);

            var t = new ControlTemplate(typeof(ToggleButton)) { VisualTree = root };
            AddFocusRingTrigger(t);
            // No label (settings rows put the label in their own column): no gap either, so the switch sits flush right.
            var noContent = new Trigger { Property = ContentControl.ContentProperty, Value = null };
            noContent.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "Content"));
            t.Triggers.Add(noContent);

            var s = new Style(typeof(ToggleButton));
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            s.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left));
            s.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextPrimary));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeBody));
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextDisabled));
            s.Triggers.Add(dis);
            return s;
        }

        /// <summary>Check box: rounded box with a check mark glyph.</summary>
        public static Style CheckBox => _check ?? (_check = BuildCheck());

        private static Style BuildCheck()
        {
            var root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var box = new FrameworkElementFactory(typeof(Border), "Box");
            box.SetValue(FrameworkElement.WidthProperty, 16.0);
            box.SetValue(FrameworkElement.HeightProperty, 16.0);
            box.SetResourceReference(Border.CornerRadiusProperty, Tokens.CornerRadiusInner);
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetResourceReference(Border.BackgroundProperty, Tokens.InputBackground);
            box.SetResourceReference(Border.BorderBrushProperty, Tokens.TextSecondary);
            var mark = new FrameworkElementFactory(typeof(Path), "Mark");
            mark.SetValue(Path.DataProperty, Ui.CheckMark);
            mark.SetValue(Shape.StrokeThicknessProperty, 1.8);
            mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            mark.SetResourceReference(Shape.StrokeProperty, Tokens.TextOnAccent);
            box.AppendChild(mark);
            root.AppendChild(box);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            root.AppendChild(cp);

            var t = new ControlTemplate(typeof(System.Windows.Controls.CheckBox)) { VisualTree = root };
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(Ui.Dyn(Border.BackgroundProperty, Tokens.Accent, "Box"));
            on.Setters.Add(Ui.Dyn(Border.BorderBrushProperty, Tokens.Accent, "Box"));
            on.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Mark"));
            t.Triggers.Add(on);
            // Fluent check box hover: the fill lightens/darkens, the border stays neutral (no accent outline).
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(Ui.Dyn(Border.BackgroundProperty, Tokens.SurfaceHover, "Box"));
            t.Triggers.Add(over);
            var onOver = new MultiTrigger();
            onOver.Conditions.Add(new Condition(ToggleButton.IsCheckedProperty, true));
            onOver.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
            onOver.Setters.Add(Ui.Dyn(Border.BackgroundProperty, Tokens.AccentHover, "Box"));
            onOver.Setters.Add(Ui.Dyn(Border.BorderBrushProperty, Tokens.AccentHover, "Box"));
            t.Triggers.Add(onOver);
            FocusVisual.EnsureRegistered();
            var kf = new Trigger { Property = FocusVisual.IsVisibleProperty, Value = true };
            kf.Setters.Add(Ui.Dyn(Border.BorderBrushProperty, Tokens.FocusStroke, "Box"));
            t.Triggers.Add(kf);

            var s = new Style(typeof(System.Windows.Controls.CheckBox));
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextPrimary));
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            s.Triggers.Add(dis);
            return s;
        }

        /// <summary>Filter chip: rounded-rectangle toggle (VS 2026, no pill shapes). Selected = accent tint + accent outline.</summary>
        public static Style Chip => _chip ?? (_chip = BuildChip());

        private static Style BuildChip()
        {
            var s = new Style(typeof(ToggleButton));
            s.Setters.Add(new Setter(Control.TemplateProperty, BorderTemplate(typeof(ToggleButton), Tokens.CornerRadiusControl)));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 1, 8, 1)));
            s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 22.0));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            s.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, Tokens.InputBorder));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextSecondary));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeSmall));
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.ButtonSecondaryHover));
            s.Triggers.Add(over);
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.AccentSubtle));
            on.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, Tokens.AccentBorder));
            on.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextLink));
            s.Triggers.Add(on);
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            s.Triggers.Add(dis);
            return s;
        }

        /// <summary>
        /// One segment of a <see cref="SegmentedControl"/>: borderless rounded rectangle inside the track; the selected
        /// segment is filled with the accent color (VS 2026 / Fluent selector).
        /// </summary>
        public static Style Segment => _segment ?? (_segment = BuildSegment());

        private static Style BuildSegment()
        {
            var s = new Style(typeof(ToggleButton));
            s.Setters.Add(new Setter(Control.TemplateProperty, BorderTemplate(typeof(ToggleButton), Tokens.CornerRadiusInner)));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 12, 0)));
            s.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            s.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeBody));
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.SurfaceHover));
            s.Triggers.Add(over);
            // Warm scheme: the selected segment is a raised neutral tile (not an accent fill); others are quieter text.
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextSecondary));
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.SegmentSelected));
            on.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextPrimary));
            on.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Medium));
            s.Triggers.Add(on);
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            s.Triggers.Add(dis);
            return s;
        }

        /// <summary>
        /// Placeholder text positioned exactly where the first typed character goes: the control's Padding (which the
        /// text view also uses) plus the text view's 2px line inset.
        /// </summary>
        private static FrameworkElementFactory Placeholder()
        {
            var wrap = new FrameworkElementFactory(typeof(Border), "Placeholder");
            wrap.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            wrap.SetValue(UIElement.IsHitTestVisibleProperty, false);
            wrap.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            var ph = new FrameworkElementFactory(typeof(TextBlock));
            ph.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(Controls.TextInput.PlaceholderProperty));
            ph.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
            ph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            ph.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            ph.SetResourceReference(TextBlock.ForegroundProperty, Tokens.InputPlaceholder);
            wrap.AppendChild(ph);
            return wrap;
        }

        /// <summary>
        /// PART_ContentHost with its own bare template: VS installs implicit ScrollViewer styles (themed scroll bars,
        /// padding) that would otherwise apply to the text host and shift or clip the text.
        /// </summary>
        private static FrameworkElementFactory ContentHost()
        {
            var host = new FrameworkElementFactory(typeof(System.Windows.Controls.ScrollViewer), "PART_ContentHost");
            host.SetValue(FrameworkElement.StyleProperty, BareHostStyle);
            host.SetValue(System.Windows.Controls.ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(System.Windows.Controls.ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            host.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            host.SetValue(Control.PaddingProperty, new Thickness(0));
            host.SetValue(UIElement.FocusableProperty, false);
            host.SetValue(Control.FocusVisualStyleProperty, null);
            return host;
        }

        private static Style _bareHost;

        private static Style BareHostStyle => _bareHost ?? (_bareHost = BuildBareHost());

        private static Style BuildBareHost()
        {
            var presenter = new FrameworkElementFactory(typeof(ScrollContentPresenter), "PART_ScrollContentPresenter");
            presenter.SetValue(ScrollContentPresenter.CanContentScrollProperty, new TemplateBindingExtension(System.Windows.Controls.ScrollViewer.CanContentScrollProperty));
            var s = new Style(typeof(System.Windows.Controls.ScrollViewer));
            s.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(System.Windows.Controls.ScrollViewer)) { VisualTree = presenter }));
            return s;
        }

        /// <summary>Password box with the same chrome and metrics as <see cref="TextInput"/> (no placeholder).</summary>
        public static Style PasswordInput => _password ?? (_password = BuildPassword());

        private static Style _password;

        private static Style BuildPassword()
        {
            var root = new FrameworkElementFactory(typeof(Grid));
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            bd.SetResourceReference(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
            bd.AppendChild(ContentHost());
            root.AppendChild(bd);
            root.AppendChild(Underline());
            var t = new ControlTemplate(typeof(PasswordBox)) { VisualTree = root };
            AddInputTriggers(t);
            var s = new Style(typeof(PasswordBox));
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            AddInputSetters(s);
            s.Setters.Add(Ui.Dyn(PasswordBox.CaretBrushProperty, Tokens.InputForeground));
            s.Setters.Add(Ui.Dyn(PasswordBox.SelectionBrushProperty, Tokens.Accent));
            return s;
        }

        private static FrameworkElementFactory Underline()
        {
            var underline = new FrameworkElementFactory(typeof(Border), "Underline");
            underline.SetValue(FrameworkElement.HeightProperty, 2.0);
            underline.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
            underline.SetValue(FrameworkElement.MarginProperty, new Thickness(1, 0, 1, 0));
            underline.SetValue(Border.CornerRadiusProperty, new CornerRadius(0, 0, 2, 2));
            underline.SetValue(UIElement.IsHitTestVisibleProperty, false);
            underline.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
            underline.SetResourceReference(Border.BackgroundProperty, Tokens.InputFocusUnderline);
            return underline;
        }

        private static void AddInputTriggers(ControlTemplate t)
        {
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Underline"));
            focus.Setters.Add(Ui.Dyn(Border.BackgroundProperty, Tokens.SurfacePopup, "Bd"));
            t.Triggers.Add(focus);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(Ui.Dyn(Border.BorderBrushProperty, Tokens.BorderStrong, "Bd"));
            t.Triggers.Add(hover);
        }

        /// <summary>Shared text-field metrics: 10px text inset (Fluent), control height, input colors.</summary>
        private static void AddInputSetters(Style s)
        {
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            s.Setters.Add(new Setter(Control.PaddingProperty, InputPadding));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            s.Setters.Add(Ui.Dyn(FrameworkElement.MinHeightProperty, Tokens.ControlHeight));
            s.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.InputBackground));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.InputForeground));
            s.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, Tokens.InputBorder));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeBody));
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
            s.Triggers.Add(dis);
        }

        /// <summary>Text inset of every boxed text field (left edge to the caret = 1px border + 8 + 2px line inset).</summary>
        public static readonly Thickness InputPadding = new Thickness(8, 3, 8, 3);

        /// <summary>Text box: rounded border, focus ring, placeholder (via <see cref="TextInput.Placeholder"/>).</summary>
        public static Style TextInput => _input ?? (_input = BuildInput());

        private static Style BuildInput()
        {
            // VS 2026 / Fluent text box: rounded subtle border; when focused, a 2px accent bar along the bottom edge.
            var root = new FrameworkElementFactory(typeof(Grid));
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            bd.SetResourceReference(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
            var underline = Underline();
            // TextBoxBase already applies Padding to the text itself (the render scope inside PART_ContentHost gets it as a
            // margin), so the template must NOT apply it again: doing so doubled the inset (caret/text ~20px from the left
            // edge, numbers clipped in narrow boxes). Only the placeholder, which is outside the text view, uses it.
            var grid = new FrameworkElementFactory(typeof(Grid));
            var ph = Placeholder();
            var host = ContentHost();
            grid.AppendChild(ph);
            grid.AppendChild(host);
            bd.AppendChild(grid);
            root.AppendChild(bd);
            root.AppendChild(underline);

            var t = new ControlTemplate(typeof(Controls.TextInput)) { VisualTree = root };
            var empty = new Trigger { Property = TextBox.TextProperty, Value = string.Empty };
            empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Placeholder"));
            t.Triggers.Add(empty);
            AddInputTriggers(t);

            var s = new Style(typeof(Controls.TextInput));
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            AddInputSetters(s);
            s.Setters.Add(Ui.Dyn(TextBoxBase.CaretBrushProperty, Tokens.InputForeground));
            s.Setters.Add(Ui.Dyn(TextBoxBase.SelectionBrushProperty, Tokens.Accent));
            return s;
        }

        /// <summary>
        /// Chrome-less text input (placeholder + text only; no border, fill, focus underline or min height) for text
        /// boxes hosted inside another control's chrome, e.g. the boxed <see cref="SearchInput"/>: the host draws the
        /// border and the focus underline, so nothing overlaps the caret or the text.
        /// </summary>
        public static Style BareTextInput => _bareInput ?? (_bareInput = BuildBareInput());

        private static Style BuildBareInput()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var ph = Placeholder();
            var host = ContentHost();
            grid.AppendChild(ph);
            grid.AppendChild(host);
            var t = new ControlTemplate(typeof(Controls.TextInput)) { VisualTree = grid };
            var empty = new Trigger { Property = TextBox.TextProperty, Value = string.Empty };
            empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Placeholder"));
            t.Triggers.Add(empty);

            var s = new Style(typeof(Controls.TextInput));
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.InputForeground));
            s.Setters.Add(Ui.Dyn(TextBoxBase.CaretBrushProperty, Tokens.InputForeground));
            s.Setters.Add(Ui.Dyn(TextBoxBase.SelectionBrushProperty, Tokens.Accent));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeBody));
            return s;
        }

        /// <summary>Tooltip chrome (Tooltip.* tokens, control radius).</summary>
        public static Style ToolTip => _tooltip ?? (_tooltip = BuildToolTip());

        private static Style BuildToolTip()
        {
            var s = new Style(typeof(System.Windows.Controls.ToolTip));
            s.Setters.Add(new Setter(Control.TemplateProperty, BorderTemplate(typeof(System.Windows.Controls.ToolTip), Tokens.CornerRadiusControl, true, false)));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 5)));
            s.Setters.Add(new Setter(System.Windows.Controls.ToolTip.HasDropShadowProperty, false));
            s.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.TooltipBackground));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TooltipForeground));
            s.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, Tokens.TooltipBorder));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeSmall));
            return s;
        }

        /// <summary>ScrollViewer with the slim token-colored <see cref="ScrollRail"/> instead of the classic ScrollBar.</summary>
        public static Style ScrollViewer => _scroll ?? (_scroll = BuildScroll());

        private static Style BuildScroll()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));
            var c0 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            grid.AppendChild(c0);
            grid.AppendChild(c1);
            var r0 = new FrameworkElementFactory(typeof(RowDefinition));
            r0.SetValue(RowDefinition.HeightProperty, new GridLength(1, GridUnitType.Star));
            var r1 = new FrameworkElementFactory(typeof(RowDefinition));
            r1.SetValue(RowDefinition.HeightProperty, GridLength.Auto);
            grid.AppendChild(r0);
            grid.AppendChild(r1);
            var presenter = new FrameworkElementFactory(typeof(ScrollContentPresenter), "PART_ScrollContentPresenter");
            presenter.SetValue(ScrollContentPresenter.CanContentScrollProperty, new TemplateBindingExtension(System.Windows.Controls.ScrollViewer.CanContentScrollProperty));
            presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            grid.AppendChild(presenter);
            var v = new FrameworkElementFactory(typeof(ScrollRail), "PART_VRail");
            v.SetValue(Grid.ColumnProperty, 1);
            grid.AppendChild(v);
            var h = new FrameworkElementFactory(typeof(ScrollRail), "PART_HRail");
            h.SetValue(Grid.RowProperty, 1);
            h.SetValue(ScrollRail.OrientationProperty, Orientation.Horizontal);
            grid.AppendChild(h);
            var s = new Style(typeof(UiScrollViewer));
            s.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(UiScrollViewer)) { VisualTree = grid }));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            return s;
        }

        /// <summary>Transparent thumb used for resize grips.</summary>
        public static Style InvisibleThumb => _thumb ?? (_thumb = BuildThumb());

        private static Style BuildThumb()
        {
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var s = new Style(typeof(Thumb));
            s.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Thumb)) { VisualTree = bd }));
            return s;
        }
    }
}

namespace UEToVS.UI.Controls
{
    /// <summary>bool? → bool (null = false) for IsChecked bindings.</summary>
    internal sealed class NullableBoolConverter : IValueConverter
    {
        public static readonly NullableBoolConverter Instance = new NullableBoolConverter();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is bool b && b;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is bool b && b;
    }
}
