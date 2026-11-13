using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Shells
{
    /// <summary>Where a popup goes. Coordinates are screen device pixels.</summary>
    public sealed class PopupAnchor
    {
        private PopupAnchor(RectD rect, AnchorMode mode)
        {
            Rect = rect;
            Mode = mode;
        }

        public RectD Rect { get; }
        public AnchorMode Mode { get; }

        /// <summary>Below the caret (flips above near the bottom of the screen). <paramref name="caretTopPx"/> is the caret line top.</summary>
        public static PopupAnchor Caret(Point caretTopPx, double lineHeightPx) =>
            new PopupAnchor(new RectD(caretTopPx.X, caretTopPx.Y, 1, Math.Max(1, lineHeightPx)), AnchorMode.Below);

        /// <summary>Centered in the upper part of a window (Rider Search Everywhere placement).</summary>
        public static PopupAnchor CenterOf(IntPtr ownerHwnd)
        {
            if (ownerHwnd != IntPtr.Zero && NativeMethods.GetWindowRect(ownerHwnd, out var r))
                return new PopupAnchor(r.ToRectD(), AnchorMode.CenterUpper);
            return Mouse(AnchorMode.Center);
        }

        public static PopupAnchor CenterOf(Window owner)
        {
            if (owner == null) return Mouse(AnchorMode.Center);
            return CenterOf(new WindowInteropHelper(owner).Handle);
        }

        /// <summary>Relative to an element (below it, or to its right for submenus).</summary>
        public static PopupAnchor Element(FrameworkElement element, AnchorMode mode = AnchorMode.Below)
        {
            if (element == null || PresentationSource.FromVisual(element) == null) return Mouse(AnchorMode.Below);
            var tl = element.PointToScreen(new Point(0, 0));
            var br = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return new PopupAnchor(new RectD(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y), mode);
        }

        public static PopupAnchor Screen(RectD rectPx, AnchorMode mode) => new PopupAnchor(rectPx, mode);

        public static PopupAnchor Mouse(AnchorMode mode = AnchorMode.Below)
        {
            NativeMethods.GetCursorPos(out var p);
            return new PopupAnchor(new RectD(p.X, p.Y, 1, 1), mode);
        }
    }

    /// <summary>
    /// Borderless themed window chrome shared by <see cref="PopupShell"/> and <see cref="DialogShell"/>:
    /// rounded surface + soft shadow per tokens (Layered mode: transparent window, shadow on a separate empty
    /// border so content never renders through an effect; Native mode: opaque window, Win11 DWM rounding +
    /// class drop shadow), content clipped to the rounded corners, optional resize grips, per-monitor-DPI placement.
    /// </summary>
    public class ChromeWindow : Window
    {
        private readonly Grid _root = new Grid();
        private readonly Border _shadow = new Border();
        private readonly Border _chrome = new Border { BorderThickness = new Thickness(1) };
        private readonly Grid _clip = new Grid { ClipToBounds = false };
        private readonly Grid _grips = new Grid();
        private readonly DropShadowEffect _effect = new DropShadowEffect { Direction = 270, Color = Colors.Black, RenderingBias = RenderingBias.Performance };
        private readonly PopupChromeMode _mode;
        private bool _resizable = true;
        private double _margin;

        public ChromeWindow() : this(ThemeManager.Instance.EffectiveSettings.PopupChrome) { }

        public ChromeWindow(PopupChromeMode mode)
        {
            _mode = mode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            if (mode == PopupChromeMode.Layered)
            {
                AllowsTransparency = true;
                Background = Brushes.Transparent;
            }
            else
            {
                this.Bind(BackgroundProperty, Tokens.SurfacePopup);
            }
            ThemeManager.Instance.Attach(this);

            _shadow.Bind(Border.BackgroundProperty, Tokens.SurfacePopup);
            _shadow.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusPopup);
            _shadow.Effect = _effect;
            _shadow.IsHitTestVisible = false;
            _chrome.Bind(Border.BackgroundProperty, Tokens.SurfacePopup);
            _chrome.Bind(Border.BorderBrushProperty, Tokens.BorderStrong);
            _chrome.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusPopup);
            _chrome.Child = _clip;
            _clip.SizeChanged += (s, e) => Ui.Safe("ui.chrome.clip", UpdateClip);
            if (mode == PopupChromeMode.Layered) _root.Children.Add(_shadow);
            _root.Children.Add(_chrome);
            _root.Children.Add(_grips);
            BuildGrips();
            base.Content = _root;

            ApplyTokens();
            ThemeManager.Instance.Changed += OnThemeChanged;
            Closed += (s, e) => ThemeManager.Instance.Changed -= OnThemeChanged;
            SourceInitialized += (s, e) => Ui.Safe("ui.chrome.native", ApplyNative);
        }

        public PopupChromeMode ChromeMode => _mode;

        /// <summary>The themed content (use this instead of Window.Content).</summary>
        public UIElement Body
        {
            get => _clip.Children.Count > 0 ? _clip.Children[0] : null;
            set
            {
                _clip.Children.Clear();
                if (value != null) _clip.Children.Add(value);
            }
        }

        /// <summary>Transparent margin around the visible chrome reserved for the shadow (DIPs).</summary>
        public double ChromeMargin => _margin;

        public bool Resizable
        {
            get => _resizable;
            set
            {
                _resizable = value;
                _grips.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>Owner window handle (VS main window) so the popup stays on top of it and minimizes with it.</summary>
        public IntPtr OwnerHandle
        {
            get => new WindowInteropHelper(this).Owner;
            set => new WindowInteropHelper(this).Owner = value;
        }

        /// <summary>Set while the window runs as a modal dialog (ShowDialog disables the owner until it closes).</summary>
        protected bool IsShownModally { get; set; }

        /// <summary>
        /// v0.5.1: hands the activation back to the owner (the VS main window) before this window closes or hides. Without
        /// it Windows picks the next top-level window in z-order when an owned, taskbar-less window goes away, which is often
        /// another application, so VS dropped behind it and looked hidden after OK / confirm. A modal dialog's owner is still
        /// disabled at that point (ShowDialog re-enables it only after the window is gone), so it is re-enabled first.
        /// Only done when this window is the active one, so closing a background window never steals the focus.
        /// </summary>
        protected void ReturnActivationToOwner(string reason)
        {
            try
            {
                var owner = OwnerHandle;
                if (owner == IntPtr.Zero || !NativeMethods.IsWindow(owner) || !NativeMethods.IsWindowVisible(owner)) return;
                var self = new WindowInteropHelper(this).Handle;
                bool active = IsActive || (self != IntPtr.Zero && NativeMethods.GetForegroundWindow() == self);
                if (!active) return;
                if (IsShownModally && !NativeMethods.IsWindowEnabled(owner)) NativeMethods.EnableWindow(owner, true);
                bool ok = NativeMethods.SetForegroundWindow(owner);
                Ui.Logger.Debug("ui.chrome.ownerActivated", null, ("reason", reason), ("modal", IsShownModally), ("ok", ok));
            }
            catch (Exception ex)
            {
                Ui.Logger.Warn("ui.chrome.ownerActivate.failed", "Could not return the activation to the owner window", ex);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel) return;
            Ui.Safe("ui.chrome.placement.save", SavePlacement);
            ReturnActivationToOwner("close");
        }

        /// <summary>Visible (chrome) size in DIPs, excluding the shadow margin.</summary>
        public Size VisibleSize
        {
            get => new Size(Math.Max(0, (double.IsNaN(Width) ? ActualWidth : Width) - 2 * _margin), Math.Max(0, (double.IsNaN(Height) ? ActualHeight : Height) - 2 * _margin));
            set
            {
                Width = Math.Max(MinVisibleWidth, value.Width) + 2 * _margin;
                Height = Math.Max(MinVisibleHeight, value.Height) + 2 * _margin;
            }
        }

        public double MinVisibleWidth { get; set; } = 200;
        public double MinVisibleHeight { get; set; } = 80;

        /// <summary>Lets the user drag the window by <paramref name="handle"/> (clicks on buttons inside are not affected).</summary>
        public void EnableDrag(UIElement handle)
        {
            handle.MouseLeftButtonDown += (s, e) => Ui.Safe("ui.chrome.drag", () =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                for (var d = e.OriginalSource as DependencyObject; d != null && d != handle; d = VisualTreeHelper.GetParent(d))
                    if (d is ButtonBase || d is TextBox) return;
                DragMove();
            });
        }

        /// <summary>
        /// Positions the window for <paramref name="anchor"/> (device pixels) on the anchor's monitor and keeps it on
        /// screen. Call before Show; creates the HWND if needed.
        /// </summary>
        public void PlaceAt(PopupAnchor anchor)
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            var a = anchor.Rect;
            var work = NativeMethods.WorkAreaAt(a.X + a.Width / 2, a.Y + a.Height / 2, out var monitor);
            double scale = NativeMethods.MonitorScale(monitor) ?? VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0) scale = 1;
            var visible = VisibleSize;
            var area = work ?? new RectD(0, 0, SystemParameters.WorkArea.Width * scale, SystemParameters.WorkArea.Height * scale);
            var r = PopupPlacement.Place(a, visible.Width * scale, visible.Height * scale, area, anchor.Mode);
            // The placement may have shrunk the size to fit the work area.
            if (r.Width < visible.Width * scale - 1 || r.Height < visible.Height * scale - 1)
                VisibleSize = new Size(r.Width / scale, r.Height / scale);
            int x = (int)Math.Round(r.X - _margin * scale), y = (int)Math.Round(r.Y - _margin * scale);
            if (!NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
                Ui.Logger.Warn("ui.chrome.place_failed", "SetWindowPos failed", ("x", x), ("y", y));
        }

        /// <summary>
        /// v0.5.18: grips on all four edges and corners (was right / bottom / bottom-right only), so a dialog can be made
        /// bigger towards whichever side has room. Left / top grips move the window origin with the edge
        /// (<see cref="WindowResize"/>). A thumb moves with the edge it drags, so every DragDelta is incremental.
        /// </summary>
        private void BuildGrips()
        {
            const double edge = 6, corner = 12;
            Thumb Grip(HorizontalAlignment h, VerticalAlignment v, double w, double hgt, Cursor c, int sx, int sy)
            {
                var t = new Thumb { Style = Styles.InvisibleThumb, HorizontalAlignment = h, VerticalAlignment = v, Cursor = c, Focusable = false };
                if (!double.IsNaN(w)) t.Width = w;
                if (!double.IsNaN(hgt)) t.Height = hgt;
                t.DragDelta += (s, e) => Ui.Safe("ui.chrome.resize", () => ResizeBy(sx, sy, e.HorizontalChange, e.VerticalChange));
                t.DragCompleted += (s, e) => Ui.Safe("ui.chrome.resized", () =>
                    Ui.Logger.Debug("ui.chrome.resized", null, ("id", PlacementId), ("size", VisibleSize.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                return t;
            }
            // Edges first, corners last so the corners win where they overlap.
            _grips.Children.Add(Grip(HorizontalAlignment.Left, VerticalAlignment.Stretch, edge, double.NaN, Cursors.SizeWE, -1, 0));
            _grips.Children.Add(Grip(HorizontalAlignment.Right, VerticalAlignment.Stretch, edge, double.NaN, Cursors.SizeWE, 1, 0));
            _grips.Children.Add(Grip(HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, edge, Cursors.SizeNS, 0, -1));
            _grips.Children.Add(Grip(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, edge, Cursors.SizeNS, 0, 1));
            _grips.Children.Add(Grip(HorizontalAlignment.Left, VerticalAlignment.Top, corner, corner, Cursors.SizeNWSE, -1, -1));
            _grips.Children.Add(Grip(HorizontalAlignment.Right, VerticalAlignment.Top, corner, corner, Cursors.SizeNESW, 1, -1));
            _grips.Children.Add(Grip(HorizontalAlignment.Left, VerticalAlignment.Bottom, corner, corner, Cursors.SizeNESW, -1, 1));
            _grips.Children.Add(Grip(HorizontalAlignment.Right, VerticalAlignment.Bottom, corner, corner, Cursors.SizeNWSE, 1, 1));
            // Grips must not cover the content except at the edges.
            _grips.IsHitTestVisible = true;
            _grips.Background = null;
        }

        private void ResizeBy(int sx, int sy, double dx, double dy)
        {
            var size = VisibleSize;
            double left = double.IsNaN(Left) ? 0 : Left, top = double.IsNaN(Top) ? 0 : Top;
            var r = WindowResize.Apply(new RectD(left, top, size.Width, size.Height), sx, sy, dx, dy, MinVisibleWidth, MinVisibleHeight);
            if (sx < 0 && !double.IsNaN(Left)) Left = r.X;
            if (sy < 0 && !double.IsNaN(Top)) Top = r.Y;
            VisibleSize = new Size(r.Width, r.Height);
        }

        /// <summary>
        /// Key under which this window's position + size are remembered in <see cref="DefaultPlacementStore"/> (null: not
        /// remembered). Set by <see cref="UtilityWindow"/> and by dialogs that want the user's size back next time.
        /// </summary>
        public string PlacementId { get; set; }

        /// <summary>Process-wide placement store (host plugs the VS per-user settings store in at startup).</summary>
        public static IWindowPlacementStore DefaultPlacementStore { get; set; }

        /// <summary>
        /// Applies the remembered placement of <see cref="PlacementId"/>: size clamped to [min, work area], position pulled
        /// back onto the work area of the monitor it was on (centered on the nearest monitor when that monitor is gone).
        /// False when nothing is stored. Creates the HWND if needed; call before Show.
        /// </summary>
        protected bool RestorePlacement()
        {
            var store = DefaultPlacementStore;
            if (string.IsNullOrEmpty(PlacementId) || store == null || !store.TryGetPlacement(PlacementId, out var p) || p == null) return false;
            new WindowInteropHelper(this).EnsureHandle();
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0) scale = 1;
            var areas = new List<RectD>();
            var work = NativeMethods.WorkAreaAt((p.Left + p.Width / 2) * scale, (p.Top + 10) * scale, out _);
            if (work.HasValue) areas.Add(new RectD(work.Value.X / scale, work.Value.Y / scale, work.Value.Width / scale, work.Value.Height / scale));
            else areas.Add(new RectD(SystemParameters.WorkArea.X, SystemParameters.WorkArea.Y, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height));
            var fit = p.EnsureVisible(areas, MinVisibleWidth, MinVisibleHeight);
            VisibleSize = new Size(fit.Width, fit.Height);
            Left = fit.Left - ChromeMargin;
            Top = fit.Top - ChromeMargin;
            Ui.Logger.Debug("ui.chrome.placement.restored", null, ("id", PlacementId), ("stored", p.Format()), ("placement", fit.Format()));
            return true;
        }

        /// <summary>Writes the current position + size under <see cref="PlacementId"/> (skipped when minimized / maximized).</summary>
        protected void SavePlacement()
        {
            var store = DefaultPlacementStore;
            if (string.IsNullOrEmpty(PlacementId) || store == null || WindowState != WindowState.Normal || double.IsNaN(Left) || double.IsNaN(Top)) return;
            var size = VisibleSize;
            store.SavePlacement(PlacementId, new WindowPlacement { Left = Left + ChromeMargin, Top = Top + ChromeMargin, Width = size.Width, Height = size.Height });
        }

        private void OnThemeChanged(object sender, EventArgs e) => Ui.Safe("ui.chrome.theme", ApplyTokens);

        private void ApplyTokens()
        {
            var t = ThemeManager.Instance.Current;
            if (t == null) return;
            var size = VisibleSize;
            bool hadSize = !double.IsNaN(Width);
            double shadowAlpha = t.Color(TokenNames.Shadow).A / 255.0;
            bool shadow = _mode == PopupChromeMode.Layered && t.Number(TokenNames.ShadowBlur) > 0 && shadowAlpha > 0;
            _margin = _mode == PopupChromeMode.Layered ? (shadow ? t.Number(TokenNames.ShadowMargin) : 0) : 0;
            _shadow.Visibility = shadow ? Visibility.Visible : Visibility.Collapsed;
            _effect.BlurRadius = t.Number(TokenNames.ShadowBlur);
            _effect.ShadowDepth = t.Number(TokenNames.ShadowDepth);
            _effect.Opacity = shadowAlpha;
            _root.Margin = new Thickness(_margin);
            if (hadSize) VisibleSize = size;
            _chrome.BorderThickness = new Thickness(t.IsHighContrast ? 2 : 1);
            UpdateClip();
        }

        private void UpdateClip()
        {
            var t = ThemeManager.Instance.Current;
            double r = Math.Max(0, (t?.Number(TokenNames.RadiusPopup) ?? 6) - 1);
            var w = _clip.ActualWidth;
            var h = _clip.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var g = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
            g.Freeze();
            _clip.Clip = g;
        }

        private void ApplyNative()
        {
            if (_mode != PopupChromeMode.Native) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            var t = ThemeManager.Instance.Current;
            double radius = t?.Number(TokenNames.RadiusPopup) ?? 6;
            int pref = radius <= 0 ? NativeMethods.DWMWCP_DONOTROUND : radius <= 4 ? NativeMethods.DWMWCP_ROUNDSMALL : NativeMethods.DWMWCP_ROUND;
            try
            {
                // Windows 11+: rounded corners; harmless failure (E_INVALIDARG) on Windows 10.
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch (Exception ex)
            {
                Ui.Logger.Debug("ui.chrome.dwm_unavailable", "DWM corner preference unavailable: " + ex.GetType().Name);
            }
            if (t != null && t.Number(TokenNames.ShadowBlur) > 0)
            {
                try
                {
                    // HwndWrapper window classes are unique per window, so this only affects this popup.
                    long style = NativeMethods.GetClassStyle(hwnd);
                    NativeMethods.SetClassStyle(hwnd, style | NativeMethods.CS_DROPSHADOW);
                }
                catch (Exception ex)
                {
                    Ui.Logger.Warn("ui.chrome.shadow_failed", "Could not enable native drop shadow", ex);
                }
            }
            // Native corners are drawn by DWM: keep the WPF chrome square so they don't double up.
            _chrome.CornerRadius = new CornerRadius(0);
        }
    }
}
