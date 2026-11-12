using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        private static Style _primary, _secondary, _ghost, _toggle, _check, _chip, _input, _tooltip, _scroll, _thumb;

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

        private static ControlTemplate BorderTemplate(Type target, object radiusKey, bool contentPresenter = true)
        {
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
            return new ControlTemplate(target) { VisualTree = bd };
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
            s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 24.0));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeBody));

            object bg, fg, border, hover, pressed, focus;
            switch (kind)
            {
                case ButtonKind.Primary:
                    bg = Tokens.Accent; fg = Tokens.TextOnAccent; border = Tokens.Accent;
                    hover = Tokens.AccentHover; pressed = Tokens.AccentPressed; focus = Tokens.TextOnAccent;
                    break;
                case ButtonKind.Ghost:
                    bg = null; fg = Tokens.TextPrimary; border = null;
                    hover = Tokens.ButtonGhostHover; pressed = Tokens.ButtonGhostPressed; focus = Tokens.BorderFocus;
                    break;
                default:
                    bg = Tokens.ButtonSecondaryBackground; fg = Tokens.TextPrimary; border = Tokens.ButtonSecondaryBorder;
                    hover = Tokens.ButtonSecondaryHover; pressed = Tokens.ButtonSecondaryPressed; focus = Tokens.BorderFocus;
                    break;
            }
            s.Setters.Add(bg == null ? new Setter(Control.BackgroundProperty, Brushes.Transparent) : Ui.Dyn(Control.BackgroundProperty, bg));
            s.Setters.Add(border == null ? new Setter(Control.BorderBrushProperty, Brushes.Transparent) : Ui.Dyn(Control.BorderBrushProperty, border));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, fg));

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(Ui.Dyn(Control.BackgroundProperty, hover));
            s.Triggers.Add(over);
            var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(Ui.Dyn(Control.BackgroundProperty, pressed));
            s.Triggers.Add(down);
            var kf = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            kf.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, focus));
            s.Triggers.Add(kf);
            if (toggle)
            {
                var checkedT = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
                checkedT.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.AccentSubtle));
                checkedT.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextPrimary));
                s.Triggers.Add(checkedT);
            }
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            s.Triggers.Add(dis);
            return s;
        }

        /// <summary>Switch-style toggle: pill track + round thumb, content on the right.</summary>
        public static Style Toggle => _toggle ?? (_toggle = BuildToggle());

        private static Style BuildToggle()
        {
            var root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

            var trackGrid = new FrameworkElementFactory(typeof(Grid));
            trackGrid.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var track = new FrameworkElementFactory(typeof(Border), "Track");
            track.SetValue(FrameworkElement.WidthProperty, 28.0);
            track.SetValue(FrameworkElement.HeightProperty, 16.0);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            track.SetResourceReference(Border.BackgroundProperty, Tokens.ToggleTrackOff);
            track.SetResourceReference(Border.BorderBrushProperty, Tokens.ToggleTrackOff);
            var thumb = new FrameworkElementFactory(typeof(Ellipse), "Thumb");
            thumb.SetValue(FrameworkElement.WidthProperty, 12.0);
            thumb.SetValue(FrameworkElement.HeightProperty, 12.0);
            thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
            thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            thumb.SetResourceReference(Shape.FillProperty, Tokens.ToggleThumb);
            trackGrid.AppendChild(track);
            trackGrid.AppendChild(thumb);
            root.AppendChild(trackGrid);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            root.AppendChild(cp);

            var t = new ControlTemplate(typeof(ToggleButton)) { VisualTree = root };
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(Ui.Dyn(Border.BackgroundProperty, Tokens.Accent, "Track"));
            on.Setters.Add(Ui.Dyn(Border.BorderBrushProperty, Tokens.Accent, "Track"));
            on.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "Thumb"));
            t.Triggers.Add(on);
            var kf = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            kf.Setters.Add(Ui.Dyn(Border.BorderBrushProperty, Tokens.BorderFocus, "Track"));
            t.Triggers.Add(kf);
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(UIElement.OpacityProperty, 0.9, "Track"));
            t.Triggers.Add(over);

            var s = new Style(typeof(ToggleButton));
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextPrimary));
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
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
            box.SetValue(FrameworkElement.WidthProperty, 14.0);
            box.SetValue(FrameworkElement.HeightProperty, 14.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetResourceReference(Border.BackgroundProperty, Tokens.InputBackground);
            box.SetResourceReference(Border.BorderBrushProperty, Tokens.InputBorder);
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
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(Ui.Dyn(Border.BorderBrushProperty, Tokens.BorderFocus, "Box"));
            t.Triggers.Add(over);
            var kf = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            kf.Setters.Add(Ui.Dyn(Border.BorderBrushProperty, Tokens.BorderFocus, "Box"));
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

        /// <summary>Pill-shaped toggle (filter chip / segmented item).</summary>
        public static Style Chip => _chip ?? (_chip = BuildChip());

        private static Style BuildChip()
        {
            var s = new Style(typeof(ToggleButton));
            s.Setters.Add(new Setter(Control.TemplateProperty, BorderTemplate(typeof(ToggleButton), Tokens.CornerRadiusPill)));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 1, 9, 1)));
            s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 22.0));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.ButtonSecondaryBackground));
            s.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, Tokens.BorderSubtle));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextSecondary));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeSmall));
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.ButtonSecondaryHover));
            s.Triggers.Add(over);
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.AccentSubtle));
            on.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, Tokens.Accent));
            on.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.TextPrimary));
            s.Triggers.Add(on);
            var kf = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            kf.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, Tokens.BorderFocus));
            s.Triggers.Add(kf);
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            s.Triggers.Add(dis);
            return s;
        }

        /// <summary>Text box: rounded border, focus ring, placeholder (via <see cref="TextInput.Placeholder"/>).</summary>
        public static Style TextInput => _input ?? (_input = BuildInput());

        private static Style BuildInput()
        {
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            bd.SetResourceReference(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var ph = new FrameworkElementFactory(typeof(TextBlock), "Placeholder");
            ph.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(Controls.TextInput.PlaceholderProperty));
            ph.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 0, 0));
            ph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            ph.SetValue(UIElement.IsHitTestVisibleProperty, false);
            ph.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            ph.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            ph.SetResourceReference(TextBlock.ForegroundProperty, Tokens.InputPlaceholder);
            var host = new FrameworkElementFactory(typeof(System.Windows.Controls.ScrollViewer), "PART_ContentHost");
            host.SetValue(System.Windows.Controls.ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(System.Windows.Controls.ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            host.SetValue(Control.FocusVisualStyleProperty, null);
            grid.AppendChild(ph);
            grid.AppendChild(host);
            bd.AppendChild(grid);

            var t = new ControlTemplate(typeof(Controls.TextInput)) { VisualTree = bd };
            var empty = new Trigger { Property = TextBox.TextProperty, Value = string.Empty };
            empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Placeholder"));
            t.Triggers.Add(empty);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(Ui.Dyn(Border.BorderBrushProperty, Tokens.BorderFocus, "Bd"));
            t.Triggers.Add(focus);

            var s = new Style(typeof(Controls.TextInput));
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
            s.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            s.Setters.Add(Ui.Dyn(Control.BackgroundProperty, Tokens.InputBackground));
            s.Setters.Add(Ui.Dyn(Control.ForegroundProperty, Tokens.InputForeground));
            s.Setters.Add(Ui.Dyn(Control.BorderBrushProperty, Tokens.InputBorder));
            s.Setters.Add(Ui.Dyn(TextBoxBase.CaretBrushProperty, Tokens.InputForeground));
            s.Setters.Add(Ui.Dyn(TextBoxBase.SelectionBrushProperty, Tokens.Accent));
            s.Setters.Add(Ui.Dyn(Control.FontSizeProperty, Tokens.FontSizeBody));
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
            s.Triggers.Add(dis);
            return s;
        }

        /// <summary>Tooltip chrome (Tooltip.* tokens, control radius).</summary>
        public static Style ToolTip => _tooltip ?? (_tooltip = BuildToolTip());

        private static Style BuildToolTip()
        {
            var s = new Style(typeof(System.Windows.Controls.ToolTip));
            s.Setters.Add(new Setter(Control.TemplateProperty, BorderTemplate(typeof(System.Windows.Controls.ToolTip), Tokens.CornerRadiusControl)));
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
