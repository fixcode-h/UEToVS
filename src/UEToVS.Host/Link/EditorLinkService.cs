using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Link;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Link
{
    /// <summary>
    /// VS side of the UE editor link (ue/UEToVSLink plugin): open an asset (Blueprint) from Search Everywhere, trigger
    /// Live Coding. When the editor is not reachable, opening an asset falls back to copying its object path and
    /// selecting the .uasset in Explorer (prototype behaviour).
    /// </summary>
    internal sealed class EditorLinkService
    {
        private static readonly Logger L = Log.For("EditorLink");
        private readonly HostPackage _package;

        private EditorLinkService(HostPackage package)
        {
            _package = package;
            Client = new UeEditorLinkClient(() => ProjectRoot, () => _package.Settings?.Effective?.EditorLinkPort ?? 0);
        }

        public static EditorLinkService Instance { get; private set; }

        public static void Initialize(HostPackage package) => Instance = new EditorLinkService(package);

        public UeEditorLinkClient Client { get; }

        /// <summary>Folder of the .uproject, or null (engine solutions have none).</summary>
        public string ProjectRoot
        {
            get
            {
                string p = _package.Unreal?.ProjectFile;
                return string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            }
        }

        public void OpenAsset(AssetItem asset)
        {
            if (asset == null) return;
            Safe.RunAsync(L, "link.openAsset", async () =>
            {
                L.Info("link.openAsset", null, ("asset", asset.ObjectPath), ("class", asset.ClassName));
                var r = await Client.OpenAssetAsync(asset.ObjectPath).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (r.Ok)
                {
                    VsStatusBar.SetText(FeatureStrings.AssetOpened(asset.Name));
                    return;
                }
                // No editor: copy the object path (paste into the Content Browser) and show the file.
                try
                {
                    Clipboard.SetText(asset.ObjectPath ?? asset.FilePath ?? string.Empty);
                }
                catch (Exception ex)
                {
                    L.Warn("link.clipboard.failed", "Copying the asset path failed", ex);
                }
                if (!r.Reached && File.Exists(asset.FilePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + asset.FilePath + "\"") { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        L.Warn("link.explorer.failed", "Showing the asset in Explorer failed", ex, ("file", asset.FilePath));
                    }
                }
                VsStatusBar.SetText(r.Reached ? FeatureStrings.AssetOpenFailed(r.Message ?? "?") : FeatureStrings.AssetPathCopied(asset.ObjectPath));
            }, ("asset", asset.ObjectPath));
        }

        /// <summary>Saves all documents, then asks the editor to run a Live Coding compile.</summary>
        public async Task LiveCodingAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsEditor.TryExecute("File.SaveAll");
            VsStatusBar.SetText(FeatureStrings.LiveCodingRequesting);
            var r = await Client.LiveCodingAsync().ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (r.Ok) VsStatusBar.SetText(FeatureStrings.LiveCodingStarted(r.Message));
            else if (r.Reached) VsStatusBar.SetText(FeatureStrings.LiveCodingFailed(r.Message ?? "?"));
            else
            {
                VsStatusBar.SetText(FeatureStrings.EditorNotConnected);
                Banners.Show("link.liveCoding", FeatureStrings.EditorNotConnected, Integration.BannerKind.Warning,
                    (FeatureStrings.CmdInstallEditorPlugin, () => _package.ExecuteAction(M1ActionIds.InstallEditorPlugin)));
            }
        }
    }
}
