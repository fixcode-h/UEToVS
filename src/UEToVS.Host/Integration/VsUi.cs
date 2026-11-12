using System;
using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Integration
{
    /// <summary>VS main window handle for owning UEToVS.UI popups and windows (UI thread).</summary>
    internal static class VsMainWindow
    {
        private static readonly Logger L = Log.For("Integration");

        /// <summary>The devenv main window HWND, or <see cref="IntPtr.Zero"/> when it cannot be determined.</summary>
        public static IntPtr Handle
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    var main = Application.Current?.MainWindow;
                    if (main != null)
                    {
                        var hwnd = new WindowInteropHelper(main).Handle;
                        if (hwnd != IntPtr.Zero) return hwnd;
                    }
                    if (Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell &&
                        ErrorHandler.Succeeded(shell.GetDialogOwnerHwnd(out IntPtr owner)))
                        return owner;
                }
                catch (Exception ex)
                {
                    L.Warn("integration.mainWindow.failed", "Cannot determine the VS main window", ex);
                }
                return IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// String values in the VS per-user settings store (collection under "UEToVS\..."). Used for UI state that is not a
    /// DialogPage property: appearance settings text and popup geometry. Never throws (failures are logged).
    /// </summary>
    internal static class VsUserSettings
    {
        private static readonly Logger L = Log.For("Integration");
        private static WritableSettingsStore _store;

        private static WritableSettingsStore Store
        {
            get
            {
                if (_store == null)
                    _store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
                return _store;
            }
        }

        public static string Get(string collection, string property)
        {
            try
            {
                var s = Store;
                return s.CollectionExists(collection) && s.PropertyExists(collection, property) ? s.GetString(collection, property) : null;
            }
            catch (Exception ex)
            {
                L.Warn("integration.settings.read.failed", "Reading the VS settings store failed", ex, ("collection", collection), ("property", property));
                return null;
            }
        }

        public static bool Set(string collection, string property, string value)
        {
            try
            {
                var s = Store;
                if (!s.CollectionExists(collection)) s.CreateCollection(collection);
                s.SetString(collection, property, value ?? string.Empty);
                return true;
            }
            catch (Exception ex)
            {
                L.Error("integration.settings.write.failed", "Writing the VS settings store failed", ex, ("collection", collection), ("property", property));
                return false;
            }
        }
    }
}
