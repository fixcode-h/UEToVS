using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Controls
{
    /// <summary>
    /// The visual part of the Fluent toggle switch (VS 2026 / Windows 11): a 40x20 fully rounded track and a knob that
    /// grows on hover, stretches while pressed and slides with an ease-out when the state changes. Geometry and the
    /// token used for every part come from <see cref="ToggleSwitchGeometry"/> (unit tested); this element only draws.
    /// Fixed size: it never stretches with the row it sits in.
    /// </summary>
    public sealed class ToggleSwitchTrack : FrameworkElement
    {
        public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
            nameof(IsOn), typeof(bool), typeof(ToggleSwitchTrack),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((ToggleSwitchTrack)d).OnIsOnChanged()));

        public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.Register(
            nameof(IsHovered), typeof(bool), typeof(ToggleSwitchTrack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsPressedProperty = DependencyProperty.Register(
            nameof(IsPressed), typeof(bool), typeof(ToggleSwitchTrack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Knob travel, 0 = off … 1 = on (animated).</summary>
        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(ToggleSwitchTrack), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>One brush property per token the switch can paint with, each bound to its token resource.</summary>
        private static readonly Dictionary<string, DependencyProperty> BrushProperties = new Dictionary<string, DependencyProperty>(StringComparer.Ordinal);

        private static readonly string[] BrushTokens =
        {
            TokenNames.ToggleTrackOff, TokenNames.ToggleTrackOffHover, TokenNames.ToggleTrackOffPressed, TokenNames.ToggleTrackOnDisabled,
            TokenNames.ToggleStroke, TokenNames.ToggleStrokeDisabled, TokenNames.ToggleThumb, TokenNames.ToggleThumbDisabled,
            TokenNames.Accent, TokenNames.AccentHover, TokenNames.AccentPressed,
        };

        private static readonly IEasingFunction Ease = new FluentEaseOut();

        static ToggleSwitchTrack()
        {
            foreach (var name in BrushTokens)
                BrushProperties[name] = DependencyProperty.Register("Brush_" + name.Replace('.', '_'), typeof(Brush), typeof(ToggleSwitchTrack),
                    new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        }

        public ToggleSwitchTrack()
        {
            Width = ToggleSwitchGeometry.TrackWidth;
            Height = ToggleSwitchGeometry.TrackHeight;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Center;
            SnapsToDevicePixels = true;
            Focusable = false;
            foreach (var kv in BrushProperties) SetResourceReference(kv.Value, Tokens.Key(kv.Key));
            IsEnabledChanged += (s, e) => InvalidateVisual();
        }

        public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
        public bool IsHovered { get => (bool)GetValue(IsHoveredProperty); set => SetValue(IsHoveredProperty, value); }
        public bool IsPressed { get => (bool)GetValue(IsPressedProperty); set => SetValue(IsPressedProperty, value); }
        public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

        private void OnIsOnChanged()
        {
            double target = IsOn ? 1 : 0;
            int ms = IsLoaded ? ToggleSwitchGeometry.TravelMs(SystemParameters.ClientAreaAnimation, ThemeManager.Instance.AnimationDuration.TotalMilliseconds) : 0;
            if (ms <= 0)
            {
                BeginAnimation(ProgressProperty, null);
                Progress = target;
                return;
            }
            var a = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = Ease };
            a.Freeze();
            BeginAnimation(ProgressProperty, a);
        }

        protected override Size MeasureOverride(Size availableSize) => new Size(ToggleSwitchGeometry.TrackWidth, ToggleSwitchGeometry.TrackHeight);

        protected override void OnRender(DrawingContext dc)
        {
            bool enabled = IsEnabled;
            var pointer = IsPressed ? ToggleSwitchPointer.Pressed : IsHovered ? ToggleSwitchPointer.Hover : ToggleSwitchPointer.Rest;
            var roles = ToggleSwitchGeometry.Colors(IsOn, pointer, enabled);
            var fill = BrushOf(roles.Fill);
            var stroke = roles.Stroke == null ? null : BrushOf(roles.Stroke);
            var knob = BrushOf(roles.Knob);

            const double w = ToggleSwitchGeometry.TrackWidth, h = ToggleSwitchGeometry.TrackHeight;
            if (stroke != null)
            {
                // 1px border drawn inside the 40x20 box (half-pixel inset keeps it crisp).
                double t = ToggleSwitchGeometry.TrackStroke;
                var pen = new Pen(stroke, t);
                pen.Freeze();
                var r = new Rect(t / 2, t / 2, w - t, h - t);
                dc.DrawRoundedRectangle(fill ?? Brushes.Transparent, pen, r, r.Height / 2, r.Height / 2);
            }
            else
            {
                dc.DrawRoundedRectangle(fill ?? Brushes.Transparent, null, new Rect(0, 0, w, h), ToggleSwitchGeometry.TrackRadius, ToggleSwitchGeometry.TrackRadius);
            }

            var k = ToggleSwitchGeometry.Knob(Progress, pointer, enabled);
            dc.DrawRoundedRectangle(knob ?? Brushes.Gray, null, new Rect(k.X, k.Y, k.Width, k.Height), k.Radius, k.Radius);
        }

        private Brush BrushOf(string token) => token != null && BrushProperties.TryGetValue(token, out var dp) ? GetValue(dp) as Brush : null;

        /// <summary>WPF wrapper of <see cref="ToggleSwitchGeometry.EaseOut"/> (Fluent decelerate curve).</summary>
        private sealed class FluentEaseOut : IEasingFunction
        {
            public double Ease(double normalizedTime) => ToggleSwitchGeometry.EaseOut(normalizedTime);
        }
    }

    /// <summary>
    /// Keyboard-only focus visibility (Fluent): <see cref="IsVisibleProperty"/> is true while a control has keyboard
    /// focus that arrived from the keyboard (Tab, arrows, access keys). Clicking a control focuses it too, but does not
    /// show the ring; a later key press inside it does not bring it back until focus moves by keyboard again.
    /// Control templates use it instead of <c>IsKeyboardFocused</c> for their focus ring.
    /// </summary>
    public static class FocusVisual
    {
        public static readonly DependencyProperty IsVisibleProperty = DependencyProperty.RegisterAttached(
            "IsVisible", typeof(bool), typeof(FocusVisual), new FrameworkPropertyMetadata(false));

        public static bool GetIsVisible(DependencyObject d) => (bool)d.GetValue(IsVisibleProperty);
        public static void SetIsVisible(DependencyObject d, bool value) => d.SetValue(IsVisibleProperty, value);

        private static bool _registered;

        /// <summary>Registers the class handlers (idempotent; called by <see cref="Styles"/>).</summary>
        internal static void EnsureRegistered()
        {
            if (_registered) return;
            _registered = true;
            EventManager.RegisterClassHandler(typeof(Control), UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotFocus), true);
            EventManager.RegisterClassHandler(typeof(Control), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus), true);
            EventManager.RegisterClassHandler(typeof(Control), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
            EventManager.RegisterClassHandler(typeof(Control), UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnTouchDown), true);
        }

        /// <summary>Environment.TickCount of the last mouse / touch press in any UEToVS control or window.</summary>
        private static int _pointerDownTick = Environment.TickCount - 60000;

        /// <summary>A focus change this soon after a press comes from that press, not from the keyboard.</summary>
        private const int PointerFocusWindowMs = 500;

        private static void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!ReferenceEquals(e.NewFocus, sender) || !(sender is DependencyObject d)) return;
            if (IsKeyboardFocusChange()) d.SetValue(IsVisibleProperty, true);
            else d.ClearValue(IsVisibleProperty);
        }

        /// <summary>
        /// v0.5.3: MostRecentInputDevice alone is not enough. The first click into a window (e.g. a toggle in the settings
        /// window right after typing in its search box) can move focus while the keyboard is still the most recent device,
        /// which showed the keyboard focus ring around the switch on the first click. A press seen just before, or a
        /// mouse button still down, means the focus came from the pointer.
        /// </summary>
        private static bool IsKeyboardFocusChange()
        {
            if (!(InputManager.Current.MostRecentInputDevice is KeyboardDevice)) return false;
            if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed) return false;
            return unchecked(Environment.TickCount - _pointerDownTick) > PointerFocusWindowMs;
        }

        private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (ReferenceEquals(e.OldFocus, sender) && sender is DependencyObject d) d.ClearValue(IsVisibleProperty);
        }

        private static void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _pointerDownTick = Environment.TickCount;
            if (sender is DependencyObject d && GetIsVisible(d)) d.ClearValue(IsVisibleProperty);
        }

        private static void OnTouchDown(object sender, TouchEventArgs e)
        {
            _pointerDownTick = Environment.TickCount;
            if (sender is DependencyObject d && GetIsVisible(d)) d.ClearValue(IsVisibleProperty);
        }
    }
}
