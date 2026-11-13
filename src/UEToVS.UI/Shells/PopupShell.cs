using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Shells
{
    /// <summary>Remembered geometry of one popup (by id).</summary>
    public sealed class PopupState
    {
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>Preview splitter position (fraction 0..1 of the list+preview area), NaN = default.</summary>
        public double PreviewSplit { get; set; } = double.NaN;

        public bool? PreviewVisible { get; set; }

        public PopupState Clone() => (PopupState)MemberwiseClone();
    }

    /// <summary>Persists popup sizes/splits. The host plugs one backed by its settings store.</summary>
    public interface IPopupStateStore
    {
        bool TryGet(string id, out PopupState state);

        void Save(string id, PopupState state);
    }

    /// <summary>Default store: process memory only.</summary>
    public sealed class InMemoryPopupStateStore : IPopupStateStore
    {
        private readonly Dictionary<string, PopupState> _states = new Dictionary<string, PopupState>(StringComparer.Ordinal);

        public bool TryGet(string id, out PopupState state)
        {
            lock (_states)
            {
                if (id != null && _states.TryGetValue(id, out var s)) { state = s.Clone(); return true; }
            }
            state = null;
            return false;
        }

        public void Save(string id, PopupState state)
        {
            if (id == null || state == null) return;
            lock (_states) _states[id] = state.Clone();
        }
    }

    /// <summary>
    /// Reusable popup window (create once, Show/Dismiss many times — the HWND and visual tree stay alive so it
    /// appears instantly). Closes on deactivation and Esc, remembers its size per <see cref="Id"/> through
    /// <see cref="StateStore"/>, positions itself from a <see cref="PopupAnchor"/>.
    /// </summary>
    public class PopupShell : ChromeWindow
    {
        private int _suppressAutoClose;
        private bool _dismissing;
        private bool _closingForReal;
        private bool _cloaked;
        private bool _revealPending;
        private int _revealGen;
        private DispatcherTimer _revealTimer;

        /// <summary>Process-wide default store (host replaces it at startup).</summary>
        public static IPopupStateStore DefaultStateStore { get; set; } = new InMemoryPopupStateStore();

        public PopupShell(string id, Size defaultSize, PopupChromeMode? chrome = null)
            : base(chrome ?? ThemeManager.Instance.EffectiveSettings.PopupChrome)
        {
            Id = id ?? "popup";
            DefaultSize = defaultSize;
            ShowActivated = true;
            Topmost = false;
            VisibleSize = defaultSize;
            Deactivated += (s, e) => Ui.Safe("ui.popup.deactivated", OnDeactivatedCore);
            KeyDown += (s, e) => Ui.Safe("ui.popup.key", () =>
            {
                if (e.Key == Key.Escape && CloseOnEscape) { e.Handled = true; Dismiss(); }
            });
            Closing += OnClosingCore;
        }

        public string Id { get; }

        public Size DefaultSize { get; set; }

        public IPopupStateStore StateStore { get; set; }

        private IPopupStateStore Store => StateStore ?? DefaultStateStore;

        public bool CloseOnDeactivate { get; set; } = true;

        public bool CloseOnEscape { get; set; } = true;

        /// <summary>
        /// When true, <see cref="ShowAt"/> keeps a newly shown popup invisible until the content calls <see cref="Reveal"/>
        /// (e.g. after the first query results are in), at most <see cref="RevealTimeoutMs"/>. Otherwise it is revealed
        /// right away.
        /// </summary>
        public bool DeferReveal { get; set; }

        public int RevealTimeoutMs { get; set; } = 100;

        /// <summary>Slide the body up a few pixels while it fades in (false: fade only).</summary>
        public bool SlideOnShow { get; set; } = true;

        /// <summary>Extra per-popup state (e.g. preview split) stored with the size.</summary>
        public PopupState State { get; private set; } = new PopupState();

        public event EventHandler Shown;

        /// <summary>Raised after the popup was hidden (Esc, deactivation, <see cref="Dismiss"/>).</summary>
        public event EventHandler Dismissed;

        /// <summary>Raised before saving state so content can write its own parts (split position).</summary>
        public event EventHandler SavingState;

        /// <summary>
        /// Keeps the popup open while a child window (submenu, dialog) is active. Dispose to re-enable.
        /// </summary>
        public IDisposable SuppressAutoClose()
        {
            _suppressAutoClose++;
            return new Releaser(() => _suppressAutoClose = Math.Max(0, _suppressAutoClose - 1));
        }

        /// <summary>Creates the HWND and lays out the content offscreen so the first Show is instant.</summary>
        public void Prewarm()
        {
            using (Ui.Logger.Time("ui.popup.prewarm", 50, ("id", Id)))
            {
                RestoreState();
                new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
                var s = VisibleSize;
                if (Content is UIElement root)
                {
                    root.Measure(new Size(s.Width + 2 * ChromeMargin, s.Height + 2 * ChromeMargin));
                    root.Arrange(new Rect(0, 0, s.Width + 2 * ChromeMargin, s.Height + 2 * ChromeMargin));
                }
            }
        }

        public void ShowAt(PopupAnchor anchor)
        {
            using (Ui.Logger.Time("ui.popup.show", 50, ("id", Id)))
            {
                if (!IsVisible) RestoreState();
                PlaceAt(anchor ?? PopupAnchor.Mouse());
                if (!IsVisible) ShowCloaked();
                Activate();
                Shown?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dismiss()
        {
            if (_dismissing || !IsVisible) return;
            _dismissing = true;
            try
            {
                SaveState();
                CancelReveal();
                ReturnActivationToOwner("dismiss");
                Hide();
                SetCloak(false); // hidden now: nothing stale can show, and a plain Show() later must be visible
                Dismissed?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _dismissing = false;
            }
        }

        /// <summary>Really closes (disposes) the window instead of hiding it.</summary>
        public void CloseForReal()
        {
            _closingForReal = true;
            SaveState();
            Close();
        }

        /// <summary>
        /// Makes a popup shown with <see cref="DeferReveal"/> visible (no-op when already revealed): starts the fade-in
        /// and uncloaks once that frame has been rendered.
        /// </summary>
        public void Reveal()
        {
            if (!_revealPending) return;
            _revealPending = false;
            _revealTimer?.Stop();
            if (Body is FrameworkElement body) Ui.SlideIn(body, SlideOnShow ? 4 : 0);
            if (_cloaked) UncloakAfterRender(_revealGen);
        }

        /// <summary>
        /// A hidden WPF window keeps its last frame: Show() puts that stale frame (old results, old preview, full opacity)
        /// on screen until WPF renders again, and only then the fade-in starts from transparent — the popup visibly
        /// flickers ("抽搐") on every open. So the window is shown cloaked (DWM keeps it out of composition, it still
        /// activates and takes focus), the new content is laid out and rendered, and only then it is uncloaked.
        /// </summary>
        private void ShowCloaked()
        {
            SetCloak(true);
            _revealPending = true;
            _revealGen++;
            Show();
            if (!DeferReveal || !_cloaked)
            {
                Reveal();
                return;
            }
            if (_revealTimer == null)
            {
                _revealTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
                _revealTimer.Tick += (s, e) => Ui.Safe("ui.popup.reveal_timeout", () =>
                {
                    _revealTimer.Stop();
                    if (_revealPending) Ui.Logger.Debug("ui.popup.reveal_timeout", "Popup revealed before its content was ready: " + Id);
                    Reveal();
                });
            }
            _revealTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, RevealTimeoutMs));
            _revealTimer.Stop();
            _revealTimer.Start();
        }

        private void CancelReveal()
        {
            _revealGen++;
            _revealPending = false;
            _revealTimer?.Stop();
        }

        /// <summary>Uncloaks after WPF has rendered two frames, so the first composed frame is the new content.</summary>
        private void UncloakAfterRender(int gen)
        {
            int frames = 0;
            EventHandler onRendering = null;
            onRendering = (s, e) => Ui.Safe("ui.popup.uncloak", () =>
            {
                if (gen != _revealGen || !IsVisible)
                {
                    CompositionTarget.Rendering -= onRendering;
                    return;
                }
                if (++frames < 2) return;
                CompositionTarget.Rendering -= onRendering;
                SetCloak(false);
            });
            CompositionTarget.Rendering += onRendering;
            InvalidateVisual(); // make sure a frame is rendered even when nothing else changes
            // Safety net: never leave a shown popup invisible, even if no frame callback arrives.
            var fallback = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            fallback.Tick += (s, e) => Ui.Safe("ui.popup.uncloak_fallback", () =>
            {
                fallback.Stop();
                CompositionTarget.Rendering -= onRendering;
                if (gen == _revealGen && IsVisible) SetCloak(false);
            });
            fallback.Start();
        }

        private void SetCloak(bool cloak)
        {
            if (_cloaked == cloak) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int value = cloak ? 1 : 0;
                int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAK, ref value, sizeof(int));
                if (hr == 0) _cloaked = cloak;
                else if (cloak) Ui.Logger.Debug("ui.popup.cloak_unavailable", "DWM cloak failed: 0x" + hr.ToString("X8"));
                else Ui.Logger.Warn("ui.popup.uncloak_failed", "DWM uncloak failed: 0x" + hr.ToString("X8"));
            }
            catch (Exception ex)
            {
                // Windows 7 / no DWM: show without cloaking (old behaviour).
                Ui.Logger.Debug("ui.popup.cloak_unavailable", "DWM cloak unavailable: " + ex.GetType().Name);
                if (!cloak) _cloaked = false;
            }
        }

        private void OnDeactivatedCore()
        {
            if (!CloseOnDeactivate || _suppressAutoClose > 0 || !IsVisible) return;
            // Defer: activation may be moving to one of our own child windows that bumps the counter.
            Dispatcher.BeginInvoke(new Action(() => Ui.Safe("ui.popup.deactivated.deferred", () =>
            {
                if (_suppressAutoClose > 0 || IsActive || !IsVisible) return;
                foreach (Window w in OwnedWindows)
                    if (w.IsActive) return;
                Dismiss();
            })), DispatcherPriority.Input);
        }

        private void OnClosingCore(object sender, CancelEventArgs e)
        {
            if (_closingForReal) return;
            // Alt+F4 etc.: keep the instance for reuse.
            e.Cancel = true;
            Ui.Safe("ui.popup.closing", Dismiss);
        }

        private void RestoreState()
        {
            try
            {
                if (Store.TryGet(Id, out var s) && s != null)
                {
                    State = s;
                    if (s.Width > 50 && s.Height > 50) VisibleSize = new Size(s.Width, s.Height);
                    return;
                }
            }
            catch (Exception ex)
            {
                Ui.Logger.Warn("ui.popup.restore_failed", "Popup state store threw", ex, ("id", Id));
            }
            State = new PopupState();
            VisibleSize = DefaultSize;
        }

        private void SaveState()
        {
            try
            {
                var size = VisibleSize;
                State.Width = size.Width;
                State.Height = size.Height;
                SavingState?.Invoke(this, EventArgs.Empty);
                Store.Save(Id, State);
            }
            catch (Exception ex)
            {
                Ui.Logger.Warn("ui.popup.save_failed", "Popup state store threw", ex, ("id", Id));
            }
        }

        private sealed class Releaser : IDisposable
        {
            private Action _a;

            public Releaser(Action a) => _a = a;

            public void Dispose()
            {
                _a?.Invoke();
                _a = null;
            }
        }
    }
}
