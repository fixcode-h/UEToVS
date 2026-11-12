using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UEToVS.Core.Diagnostics;
using UEToVS.UI.Logic;

namespace UEToVS.UI.Theming
{
    /// <summary>Small helpers shared by all components: token binding, safe event handlers, frozen resources, fades.</summary>
    public static class Ui
    {
        private static readonly Logger L = Log.For("UI");

        /// <summary>Binds a dependency property to a token (live updating). Returns the element for chaining.</summary>
        public static T Bind<T>(this T element, DependencyProperty property, object tokenKey) where T : FrameworkElement
        {
            element.SetResourceReference(property, tokenKey);
            return element;
        }

        public static T BindC<T>(this T element, DependencyProperty property, object tokenKey) where T : FrameworkContentElement
        {
            element.SetResourceReference(property, tokenKey);
            return element;
        }

        /// <summary>Style setter whose value follows a token.</summary>
        public static Setter Dyn(DependencyProperty property, object tokenKey) => new Setter(property, new DynamicResourceExtension(tokenKey));

        public static Setter Dyn(DependencyProperty property, object tokenKey, string targetName) =>
            new Setter(property, new DynamicResourceExtension(tokenKey), targetName);

        /// <summary>Runs an event-handler body; exceptions are logged (never swallowed silently) and not rethrown into WPF.</summary>
        public static void Safe(string evt, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                L.Error(evt, "UI handler failed: " + evt, ex);
            }
        }

        public static Logger Logger => L;

        /// <summary>
        /// Runs <paramref name="apply"/> whenever tokens are rebuilt while <paramref name="owner"/> is loaded
        /// (for code that reads token values directly, e.g. OnRender). Subscribes on Loaded / unsubscribes on
        /// Unloaded so the static event never keeps elements alive.
        /// </summary>
        public static void TrackTheme(FrameworkElement owner, Action apply)
        {
            if (owner == null || apply == null) return;
            EventHandler h = (s, e) => Safe("ui.theme.track", apply);
            bool on = false;
            owner.Loaded += (s, e) =>
            {
                if (!on) { ThemeManager.Instance.Changed += h; on = true; }
                Safe("ui.theme.track", apply);
            };
            owner.Unloaded += (s, e) =>
            {
                if (on) { ThemeManager.Instance.Changed -= h; on = false; }
            };
        }

        public static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Color ToColor(this Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        public static Rgba ToRgba(this Color c) => new Rgba(c.R, c.G, c.B, c.A);

        public static Geometry FrozenGeometry(string data)
        {
            var g = Geometry.Parse(data);
            if (g.CanFreeze) g.Freeze();
            return g;
        }

        /// <summary>Opacity fade honoring the animation settings (instant when animations are off).</summary>
        public static void Fade(UIElement element, double to, Action completed = null)
        {
            var d = ThemeManager.Instance.AnimationDuration;
            if (d <= TimeSpan.Zero)
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = to;
                completed?.Invoke();
                return;
            }
            var a = new DoubleAnimation(to, new Duration(d)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            if (completed != null) a.Completed += (s, e) => Safe("ui.fade.completed", completed);
            a.Freeze();
            element.BeginAnimation(UIElement.OpacityProperty, a);
        }

        /// <summary>Small vertical slide + fade used when popups appear (≤ animation duration, transform only).</summary>
        public static void SlideIn(FrameworkElement element, double fromY = 6)
        {
            var d = ThemeManager.Instance.AnimationDuration;
            if (d <= TimeSpan.Zero)
            {
                element.Opacity = 1;
                element.RenderTransform = Transform.Identity;
                return;
            }
            var tt = new TranslateTransform(0, fromY);
            element.RenderTransform = tt;
            element.Opacity = 0;
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, new Duration(d)) { EasingFunction = ease });
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(d)) { EasingFunction = ease });
        }

        /// <summary>A frozen geometry for a right-pointing chevron (submenu, tree expander).</summary>
        public static readonly Geometry ChevronRight = FrozenGeometry("M 0,0 L 4,4 L 0,8");
        public static readonly Geometry ChevronDown = FrozenGeometry("M 0,0 L 4,4 L 8,0");
        public static readonly Geometry CheckMark = FrozenGeometry("M 0,4 L 3.5,7.5 L 10,0.5");
        public static readonly Geometry CrossMark = FrozenGeometry("M 0,0 L 8,8 M 8,0 L 0,8");
        public static readonly Geometry SearchGlass = FrozenGeometry("M 9.2,9.2 L 13,13 M 0.5,5.5 A 5,5 0 1 1 10.5,5.5 A 5,5 0 1 1 0.5,5.5");
    }
}
