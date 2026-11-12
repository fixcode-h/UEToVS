using System;
using System.Threading;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Engine;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Options;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// What the navigation features (commands and MEF editor parts) need from the package, tolerant of the package not
    /// being loaded yet (MEF parts are created by the editor independently of the package): effective settings (defaults
    /// until the package is up), the engine client (null = unavailable), whether a UE solution is open, and a
    /// <see cref="SettingsChanged"/> event for live toggles (highlighting, navigation bar, Ctrl+click, colors).
    /// </summary>
    internal static class NavigationContext
    {
        private static readonly Logger L = Log.For("Navigation");
        private static readonly HostSettings Defaults = new HostSettings();
        private static SettingsService _hooked;

        public static HostSettings Settings
        {
            get
            {
                EnsureHooked();
                return HostPackage.Instance?.Settings?.Effective ?? Defaults;
            }
        }

        public static EngineClient Engine => HostPackage.Instance?.Engine;

        public static bool IsUnrealSolution => HostPackage.Instance?.Unreal.IsUnreal == true;

        public static CancellationToken Disposal => HostPackage.Instance?.DisposalToken ?? CancellationToken.None;

        /// <summary>Raised (on the thread that applied the settings, usually the UI thread) after the effective settings changed.</summary>
        public static event EventHandler<SettingsChangedEventArgs> SettingsChanged;

        /// <summary>Subscribes once to the package's SettingsService (called at registration and lazily on access).</summary>
        public static void EnsureHooked()
        {
            var svc = HostPackage.Instance?.Settings;
            if (svc == null || ReferenceEquals(svc, _hooked)) return;
            _hooked = svc;
            svc.Changed += (s, e) =>
            {
                var o = e.Old;
                var n = e.New;
                if (o.CtrlClickNavigation != n.CtrlClickNavigation || o.ReferenceHighlighting != n.ReferenceHighlighting ||
                    o.NavigationBar != n.NavigationBar || o.ReferenceHighlightReadColor != n.ReferenceHighlightReadColor ||
                    o.ReferenceHighlightWriteColor != n.ReferenceHighlightWriteColor)
                    L.Info("nav.settings.changed", null, ("ctrlClick", n.CtrlClickNavigation), ("highlight", n.ReferenceHighlighting),
                           ("navigationBar", n.NavigationBar), ("readColor", n.ReferenceHighlightReadColor), ("writeColor", n.ReferenceHighlightWriteColor));
                try
                {
                    SettingsChanged?.Invoke(null, e);
                }
                catch (Exception ex)
                {
                    L.Error("nav.settings.changed.failed", "A navigation settings listener threw", ex);
                }
            };
        }
    }
}
