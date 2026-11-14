using System;
using System.IO;
using System.Net.Http;
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
    /// Settings window card "UELink" (the editor plugin shared with Agent Panel; it replaced UEToVSLink): install state
    /// (project Plugins / engine Plugins / Marketplace, old plugins), installed vs bundled vs latest release, editor online;
    /// install / update / uninstall. File work and version logic are in <see cref="LinkPluginInstaller"/> and
    /// <see cref="UELinkRelease"/> (Logic, tested). The status text is cached and refreshed in the background at most every
    /// few seconds, and only while someone asks for it (the settings window polls it once a second while open); the release
    /// manifest is fetched at most every few hours (and again right before an install).
    /// Logs link.plugin.status / link.plugin.release / link.plugin.install / link.plugin.uninstall.
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

        private static readonly TimeSpan ReleaseCheckInterval = TimeSpan.FromHours(6);
        private static UELinkManifest _release;
        private static string _releaseError;
        private static string _releaseSetting;
        private static DateTime _releaseCheckedUtc = DateTime.MinValue;
        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() => new HttpClient { Timeout = TimeSpan.FromMinutes(2) });

        public static string BundledSource => Path.Combine(HostPackage.ExtensionDirectory ?? string.Empty, "EditorPlugin", LinkPluginInstaller.PluginName);

        /// <summary>Downloaded releases, unpacked per version (shared by every VS instance of this user).</summary>
        private static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEToVS", "UELink");

        private static string SourceSetting(HostPackage package) => package?.Settings?.Effective?.UELinkSource ?? string.Empty;

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
                        lines.Add(LinkStrings.PluginInstalledAt(LocationName(st.Installed.Location), st.Installed.VersionName ?? st.Installed.Version.ToString()) +
                                  (st.Installed.InstalledBy != null ? " · " + LinkStrings.PluginInstalledBy(st.Installed.InstalledBy) : ""));
                        lines.Add(st.Installed.Directory);
                    }
                    break;
            }
            foreach (var old in st.Legacy)
                lines.Add(old.Location == LinkPluginLocation.Project ? LinkStrings.PluginLegacy(old.Name, old.Directory) : LinkStrings.PluginLegacyEngine(old.Name, old.Directory));
            if (st.BundledVersionName != null) lines.Add(LinkStrings.PluginBundled(st.BundledVersionName));
            if (st.RemoteVersionName != null) lines.Add(LinkStrings.PluginRemote(st.RemoteVersionName));
            else if (st.RemoteError != null) lines.Add(LinkStrings.PluginRemoteFailed(st.RemoteError));
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
            await CheckReleaseAsync(package, false).ConfigureAwait(false);
            var st = await Task.Run(() => LinkPluginInstaller.GetStatus(projectRoot, engineRoot, LocalSource())).ConfigureAwait(false);
            LinkPluginInstaller.ApplyRemote(st, _release?.Version, _releaseError);
            string editor = null;
            var client = EditorLinkService.Instance?.Client;
            if (client != null)
            {
                var caps = await client.GetCapabilitiesAsync().ConfigureAwait(false);
                if (caps != null) editor = (caps.PluginVersion != null ? "UELink " + caps.PluginVersion + ", " : "") + "protocol " + caps.Protocol +
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
                       ("bundled", st.BundledVersionName), ("remote", st.RemoteVersionName), ("legacy", st.Legacy.Count), ("all", st.All.Count), ("editor", editor));
            return st;
        }

        /// <summary>
        /// The newest UELink available without a download: the bundled copy, or a release unpacked earlier into the
        /// cache when it is newer.
        /// </summary>
        private static string LocalSource()
        {
            var release = _release;
            string cached = release == null ? null : UELinkRelease.Cached(CacheRoot, release.Version);
            if (cached == null) return BundledSource;
            var b = LinkPluginInstaller.ReadVersion(Path.Combine(BundledSource, LinkPluginInstaller.PluginName + ".uplugin"));
            return b == null || LinkPluginInstaller.CompareVersions(release.Version, 0, b.Value.VersionName, b.Value.Version) > 0 ? cached : BundledSource;
        }

        /// <summary>Reads the release manifest (at most every few hours unless <paramref name="force"/>); never throws.</summary>
        private static async Task CheckReleaseAsync(HostPackage package, bool force)
        {
            string setting = SourceSetting(package);
            if (!force && setting == _releaseSetting && DateTime.UtcNow - _releaseCheckedUtc < ReleaseCheckInterval) return;
            _releaseSetting = setting;
            _releaseCheckedUtc = DateTime.UtcNow;
            var src = UELinkRelease.ResolveSource(setting);
            try
            {
                string json = src.Folder != null
                    ? File.ReadAllText(Path.Combine(src.Folder, "manifest.json"))
                    : await Http.Value.GetStringAsync(src.ManifestUrl).ConfigureAwait(false);
                var m = UELinkRelease.ParseManifest(json);
                _release = m;
                _releaseError = m == null ? "manifest.json" : null;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is TaskCanceledException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                _release = null;
                _releaseError = ex.Message;
            }
            L.Info("link.plugin.release", null, ("source", src.ManifestUrl ?? src.Folder), ("version", _release?.Version), ("error", _releaseError));
        }

        /// <summary>
        /// Downloads (or copies from the local source folder) the release zip, checks sha256 and unpacks it into the cache;
        /// returns the plugin folder. Throws on failure (shown to the user, nothing is installed then).
        /// </summary>
        private static async Task<string> DownloadReleaseAsync(HostPackage package, UELinkManifest m)
        {
            string cached = UELinkRelease.Cached(CacheRoot, m.Version);
            if (cached != null) return cached;
            var src = UELinkRelease.ResolveSource(SourceSetting(package));
            byte[] zip = src.Folder != null
                ? File.ReadAllBytes(Path.Combine(src.Folder, m.Asset))
                : await Http.Value.GetByteArrayAsync(src.ZipUrl(m)).ConfigureAwait(false);
            return await Task.Run(() => UELinkRelease.Unpack(zip, m, CacheRoot)).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ actions (settings window, UI thread)

        /// <summary>Install or update; returns the row text.</summary>
        public static async Task<string> InstallAsync(HostPackage package, string source)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
            try
            {
                // Look for a newer release right before installing (the card's check may be hours old).
                await CheckReleaseAsync(package, true);
                var st = await RefreshAsync(package);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                // Update in place when it is already installed in the project (two copies would conflict in UE); an engine
                // copy is left alone and the project gets its own (the project's wins in UE).
                string target = st.Installed?.Location == LinkPluginLocation.Project ? st.Installed.Directory : st.InstallTarget;
                if (target == null)
                {
                    L.Info("link.plugin.install", null, ("source", source), ("ok", false), ("code", "noProject"));
                    return LinkStrings.PluginNoProject;
                }
                string from = LocalSource(), kind = from == BundledSource ? "bundled" : UELinkRelease.ResolveSource(SourceSetting(package)).Kind;
                if (st.RemoteVersionName != null && _release != null)
                {
                    try
                    {
                        from = await DownloadReleaseAsync(package, _release);
                        kind = UELinkRelease.ResolveSource(SourceSetting(package)).Kind;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is TaskCanceledException ||
                                               ex is InvalidDataException || ex is ArgumentException || ex is NotSupportedException)
                    {
                        L.Info("link.plugin.install", null, ("source", source), ("ok", false), ("code", "download"), ("version", _release.Version), ("error", ex.Message));
                        string failed = LinkStrings.PluginOperationFailed("UELink " + _release.Version + ": " + ex.Message);
                        VsStatusBar.SetText(failed);
                        return failed;
                    }
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                }
                string projectRoot = ProjectRoot, uproject = UProjectFile(package), installFrom = from, installKind = kind;
                var r = await Task.Run(() => LinkPluginInstaller.Install(installFrom, target,
                    new LinkPluginInstaller.InstallOptions { ProjectRoot = projectRoot, UProjectFile = uproject, Source = installKind }));
                string to = LinkPluginInstaller.ReadVersion(Path.Combine(installFrom, LinkPluginInstaller.PluginName + ".uplugin"))?.VersionName;
                L.Info("link.plugin.install", null, ("source", source), ("target", target), ("ok", r.Ok), ("code", r.Code), ("files", r.Files),
                       ("from", st.Installed?.VersionName), ("to", to), ("kind", installKind), ("removedLegacy", string.Join(";", r.RemovedLegacy)),
                       ("uprojectChanged", r.UProjectChanged), ("detail", r.Ok ? null : r.Detail));
                _refreshedUtc = DateTime.MinValue;
                string text = ResultText(r, LinkStrings.PluginInstalledDone(to, target) + (r.RemovedLegacy.Count > 0 ? LinkStrings.PluginRemovedLegacy(r.RemovedLegacy.Count) : ""));
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
                // Owned by the active UEToVS window (the settings window), so closing the box gives the activation back to it
                // instead of to another application (VS looked hidden afterwards).
                Window ownerWindow = null;
                var windows = Application.Current?.Windows;
                if (windows != null)
                    foreach (Window w in windows)
                        if (w.IsActive) { ownerWindow = w; break; }
                var answer = ownerWindow != null
                    ? MessageBox.Show(ownerWindow, LinkStrings.PluginUninstallConfirm(installed.Directory), LinkStrings.PluginCardTitle,
                                      MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
                    : MessageBox.Show(LinkStrings.PluginUninstallConfirm(installed.Directory), LinkStrings.PluginCardTitle,
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
                case "newer": return LinkStrings.PluginNewerRefused(r.Detail);
                default: return LinkStrings.PluginOperationFailed(r.Detail ?? r.Code);
            }
        }
    }
}
