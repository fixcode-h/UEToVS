using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Search
{
    /// <summary>
    /// Choosing an asset (Blueprint) row in Search Everywhere: copy its object path (paste it into the Content Browser)
    /// and select the .uasset in Explorer. UEToVS does not talk to the running editor (opening assets in UE is the Agent
    /// panel's job), so this is the whole action.
    /// </summary>
    internal static class AssetReveal
    {
        private static readonly Logger L = Log.For("Search");

        public static void Reveal(AssetItem asset)
        {
            if (asset == null) return;
            L.Info("search.asset.reveal", null, ("asset", asset.ObjectPath), ("class", asset.ClassName));
            try
            {
                Clipboard.SetText(asset.ObjectPath ?? asset.FilePath ?? string.Empty);
            }
            catch (Exception ex)
            {
                L.Warn("search.asset.clipboard.failed", "Copying the asset path failed", ex);
            }
            if (File.Exists(asset.FilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + asset.FilePath + "\"") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    L.Warn("search.asset.explorer.failed", "Showing the asset in Explorer failed", ex, ("file", asset.FilePath));
                }
            }
            VsStatusBar.SetText(FeatureStrings.AssetPathCopied(asset.ObjectPath));
        }
    }
}
