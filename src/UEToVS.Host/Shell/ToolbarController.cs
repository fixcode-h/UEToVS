using System;
using System.Reflection;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Integration;

namespace UEToVS.Host.Shell
{
    /// <summary>
    /// Shows / hides the "UEToVS" toolbar (VSCT tbUEToVS) from the setting 常规 > 工具栏 > 显示 UEToVS 工具栏.
    /// VS keeps toolbar visibility itself (right-click on the toolbar area), so UEToVS only writes it on the first run
    /// (the toolbar is shown by default) and whenever the setting changes; a toolbar the user hid through VS stays hidden
    /// until the setting is toggled. Individual buttons are hidden through DynamicVisibility in <see cref="CommandRouter"/>.
    /// CommandBars is reached late-bound (no Microsoft.VisualStudio.CommandBars reference). UI thread only.
    /// </summary>
    internal static class ToolbarController
    {
        public const string ToolbarName = "UEToVS";
        private const string Collection = "UEToVS\\Windows";
        private const string InitializedKey = "toolbarInitialized";
        private static readonly Logger L = Log.For("Toolbar");

        /// <summary>Package load: shows the toolbar once on the first run (when the setting says so).</summary>
        public static void ApplyOnFirstRun(DTE dte, bool show)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (VsUserSettings.Get(Collection, InitializedKey) == "1") return;
            if (Apply(dte, show, "firstRun")) VsUserSettings.Set(Collection, InitializedKey, "1");
        }

        /// <summary>Sets the toolbar's visibility; returns false when the toolbar cannot be found (VSCT not merged yet).</summary>
        public static bool Apply(DTE dte, bool show, string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte == null) return false;
            try
            {
                object bars = dte.GetType().InvokeMember("CommandBars", BindingFlags.GetProperty, null, dte, null);
                object bar = bars?.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, bars, new object[] { ToolbarName });
                if (bar == null) return false;
                bool visible = (bool)bar.GetType().InvokeMember("Visible", BindingFlags.GetProperty, null, bar, null);
                if (visible != show) bar.GetType().InvokeMember("Visible", BindingFlags.SetProperty, null, bar, new object[] { show });
                L.Info("toolbar.visibility", null, ("show", show), ("was", visible), ("reason", reason));
                return true;
            }
            catch (Exception ex)
            {
                // ArgumentException / TargetInvocationException when the bar does not exist (yet).
                L.Warn("toolbar.visibility.failed", "Cannot change the UEToVS toolbar visibility", ex, ("show", show), ("reason", reason));
                return false;
            }
        }
    }
}
