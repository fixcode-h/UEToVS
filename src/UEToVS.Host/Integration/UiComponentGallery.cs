using System;
using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.UI.Gallery;

namespace UEToVS.Host.Integration
{
    /// <summary>Component gallery command (§8.7): one <see cref="ComponentGallery"/> window owned by the VS main window.</summary>
    internal sealed class UiComponentGalleryProvider : IComponentGalleryProvider
    {
        private static readonly Logger L = Log.For("Gallery");
        private ComponentGallery _window;

        public void Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_window != null)
            {
                if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
                _window.Activate();
                return;
            }
            var window = new ComponentGallery();
            IntPtr owner = VsMainWindow.Handle;
            if (owner != IntPtr.Zero)
            {
                new WindowInteropHelper(window).Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            window.Closed += Safe.Handler(L, "gallery.closed", () =>
            {
                if (ReferenceEquals(_window, window)) _window = null;
                L.Info("gallery.closed", null);
            });
            _window = window;
            window.Show();
            L.Info("gallery.opened", null, ("owned", owner != IntPtr.Zero));
        }
    }
}
