using System;
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
            if (_window == null)
            {
                var window = new ComponentGallery();
                window.Closed += Safe.Handler(L, "gallery.closed", () =>
                {
                    if (ReferenceEquals(_window, window)) _window = null;
                    L.Info("gallery.closed", null);
                });
                _window = window;
            }
            IntPtr owner = VsMainWindow.Handle;
            _window.ShowOwned(owner); // remembered position/size (UtilityWindow "componentGallery")
            L.Info("gallery.opened", null, ("owned", owner != IntPtr.Zero));
        }
    }
}
