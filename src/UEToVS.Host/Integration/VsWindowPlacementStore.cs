using System;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Integration
{
    /// <summary>
    /// <see cref="IWindowPlacementStore"/> for UEToVS.UI utility windows (settings, performance, component gallery):
    /// "left,top,width,height" per window id in the VS per-user settings store (UEToVS\Windows). Per-user, so it
    /// follows the user across solutions. Failures are logged, never thrown.
    /// </summary>
    internal sealed class VsWindowPlacementStore : IWindowPlacementStore
    {
        private const string Collection = "UEToVS\\Windows";
        private static readonly Logger L = Log.For("Integration");

        public bool TryGetPlacement(string id, out WindowPlacement placement)
        {
            placement = null;
            if (string.IsNullOrEmpty(id)) return false;
            try
            {
                string text = VsUserSettings.Get(Collection, id);
                if (string.IsNullOrEmpty(text)) return false;
                if (WindowPlacement.TryParse(text, out placement)) return true;
                L.Warn("window.placement.invalid", "Stored window placement ignored", ("id", id), ("text", text));
            }
            catch (Exception ex)
            {
                L.Warn("window.placement.read.failed", "Reading window placement failed", ex, ("id", id));
            }
            return false;
        }

        public void SavePlacement(string id, WindowPlacement placement)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                // null = reset (double-click on the title bar): an empty value is ignored by TryParse.
                VsUserSettings.Set(Collection, id, placement?.Format() ?? "");
                L.Debug("window.placement.saved", null, ("id", id), ("placement", placement?.Format()));
            }
            catch (Exception ex)
            {
                L.Warn("window.placement.write.failed", "Saving window placement failed", ex, ("id", id));
            }
        }
    }
}
