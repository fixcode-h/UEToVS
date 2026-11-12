using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Link;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Link
{
    /// <summary>
    /// Settings window card "UEToVSLink": install state (project Plugins / engine Plugins / Marketplace), installed vs
    /// bundled version, editor online; install / update / uninstall. File work and version logic are in
    /// <see cref="LinkPluginInstaller"/> (Logic, tested). The status text is cached and refreshed in the background at
    /// most every few seconds, and only while someone asks for it (the settings window polls it once a second while open).
    /// Logs link.plugin.status / link.plugin.install / link.plugin.uninstall.
    /// </summary>
    internal static class LinkPluginManager
    {
        private static readonly Logger L = Log.For("Link");
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
        private static readonly object Gate = new object();
        private static LinkPluginStatus _status;
        private static string _editorInfo;
        private static DateTime _refreshedUtc = DateTime.MinValue;
        private static int _refreshing;
        private static int _busy;

        public static string BundledSource => Path.Combine(HostPackage.ExtensionDirectory ?? string.Empty, "EditorPlugin", LinkPluginInstaller.PluginName);

        private static string ProjectRoot => EditorLinkService.Instance?.ProjectRoot;

        private static string EngineRoot(HostPackage package) => package?.Unreal?.EngineRoot;

        private static string UProjectFile(HostPackage package) => package?.Unreal?.ProjectFile;

        // ------------------------------------------------------------------ status card

        /// <summary>Card text; never blocks (returns the cached text and refreshes in the background).</summary>
        public static string StatusText(HostPackage package)
        {
            LinkPluginStatus st;
            string editor;
            lock (Gate)
            {
                st = _status;
                editor = _editorInfo;
            }
            if (DateTime.UtcNow - _refreshedUtc > RefreshInterval) StartRefresh(package);
            if (st == null) return "…";
            return Describe(st, editor);
        }

        internal static string Describe(LinkPluginStatus st, string editorInfo)
        {
            var lines = new System.Collections.Generic.List<string>();
            switch (st.Kind)
            {
                case LinkPluginStatusKind.NoProject when st.Installed == null:
                    lines.Add(LinkStrings.PluginNoProject);
                    break;
                case LinkPluginStatusKind.NotInstalled:
                    lines.Add(LinkStrings.PluginNotInstalled);
                    break;
                default:
                    if (st.Installed != null)
                    {
                        lines.Add(LinkStrings.PluginInstalledAt(LocationName(st.Installed.Location), st.Installed.VersionName ?? st.Installed.Version.ToString()));
                        lines.Add(st.Installed.Directory);
                    }
                    break;
            }
            if (st.BundledVersionName != null) lines.Add(LinkStrings.PluginBundled(st.BundledVersionName));
            if (st.Kind == LinkPluginStatusKind.UpdateAvailable) lines.Add(LinkStrings.PluginUpdateAvailable);
            else if (st.Kind == LinkPluginStatusKind.Newer) lines.Add(LinkStrings.PluginNewer);
            lines.Add(editorInfo != null ? LinkStrings.PluginEditorOnline(editorInfo) : LinkStrings.PluginEditorOffline);
            return string.Join("\n", lines);
        }

        private static string LocationName(LinkPluginLocation location)
        {
            switch (location)
            {
                case LinkPluginLocation.Project: return LinkStrings.PluginLocationProject;
                case LinkPluginLocation.EngineMarketplace: return LinkStrings.PluginLocationMarketplace;
                default: return LinkStrings.PluginLocationEngine;
            }
        }

        private static void StartRefresh(HostPackage package)
        {
            if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
            _ = Safe.RunInBackgroundAsync(L, "link.plugin.status", async () =>
            {
                try
                {
                    await RefreshAsync(package).ConfigureAwait(false);
                }
                finally
                {
                    _refreshedUtc = DateTime.UtcNow;
                    Interlocked.Exchange(ref _refreshing, 0);
                }
            });
        }

        private static async Task<LinkPluginStatus> RefreshAsync(HostPackage package)
        {
            string projectRoot = ProjectRoot, engineRoot = EngineRoot(package);
            var st = await Task.Run(() => LinkPluginInstaller.GetStatus(projectRoot, engineRoot, BundledSource)).ConfigureAwait(false);
            string editor = null;
            var client = EditorLinkService.Instance?.Client;
            if (client != null)
            {
                var caps = await client.GetCapabilitiesAsync().ConfigureAwait(false);
                if (caps != null) editor = (caps.PluginVersion != null ? "UEToVSLink " + caps.PluginVersion + ", " : "") + "protocol " + caps.Protocol +
                                           (caps.EngineVersion != null ? ", UE " + caps.EngineVersion : "");
            }
            bool changed;
            lock (Gate)
            {
                changed = _status == null || _status.Kind != st.Kind || _status.Installed?.Directory != st.Installed?.Directory || _editorInfo != editor;
                _status = st;
                _editorInfo = editor;
            }
            if (changed)
                L.Info("link.plugin.status", null, ("kind", st.Kind.ToString()), ("installed", st.Installed?.Directory), ("version", st.Installed?.VersionName),
                       ("bundled", st.BundledVersionName), ("all", st.All.Count), ("editor", editor));
            return st;
        }

        // ------------------------------------------------------------------ actions (settings window, UI thread)

        /// <summary>Install or update; returns the row text.</summary>
        public static async Task<string> InstallAsync(HostPackage package, string source)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
            try
            {
                var st = await RefreshAsync(package);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                // Update in place when it is already installed somewhere (two copies would conflict in UE).
                string target = st.Installed?.Directory ?? st.InstallTarget;
                if (target == null)
                {
                    L.Info("link.plugin.install", null, ("source", source), ("ok", false), ("code", "noProject"));
                    return LinkStrings.PluginNoProject;
                }
                var r = await Task.Run(() => LinkPluginInstaller.Install(BundledSource, target));
                L.Info("link.plugin.install", null, ("source", source), ("target", target), ("ok", r.Ok), ("code", r.Code), ("files", r.Files),
                       ("from", st.Installed?.VersionName), ("to", st.BundledVersionName), ("detail", r.Ok ? null : r.Detail));
                _refreshedUtc = DateTime.MinValue;
                string text = ResultText(r, LinkStrings.PluginInstalledDone(target));
                VsStatusBar.SetText(text);
                return text;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>Uninstall after a confirmation; returns the row text (null when cancelled).</summary>
        public static async Task<string> UninstallAsync(HostPackage package)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
            try
            {
                var st = await RefreshAsync(package);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var installed = st.Installed;
                if (installed == null) return LinkStrings.PluginNotInstalled;
                var answer = MessageBox.Show(LinkStrings.PluginUninstallConfirm(installed.Directory), LinkStrings.PluginCardTitle,
                                             MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (answer != MessageBoxResult.OK)
                {
                    L.Info("link.plugin.uninstall", null, ("dir", installed.Directory), ("ok", false), ("code", "cancelled"));
                    return null;
                }
                string uproject = UProjectFile(package);
                var r = await Task.Run(() => LinkPluginInstaller.Uninstall(installed.Directory, uproject));
                L.Info("link.plugin.uninstall", null, ("dir", installed.Directory), ("location", installed.Location.ToString()), ("ok", r.Ok), ("code", r.Code),
                       ("files", r.Files), ("uprojectChanged", r.UProjectChanged), ("detail", r.Ok ? null : r.Detail));
                _refreshedUtc = DateTime.MinValue;
                string text = ResultText(r, LinkStrings.PluginUninstalledDone);
                VsStatusBar.SetText(text);
                return text;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static string ResultText(LinkPluginOperationResult r, string ok)
        {
            if (r.Ok) return ok;
            switch (r.Code)
            {
                case "locked": return LinkStrings.PluginLocked(Path.GetFileName(r.Detail ?? ""));
                case "missingSource": return LinkStrings.PluginMissingSource(r.Detail);
                case "noProject": return LinkStrings.PluginNoProject;
                case "notInstalled": return LinkStrings.PluginNotInstalled;
                default: return LinkStrings.PluginOperationFailed(r.Detail ?? r.Code);
            }
        }
    }
}
