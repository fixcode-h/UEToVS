using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Update;

namespace UEToVS.Host.Update
{
    /// <summary>
    /// "检查更新" in the settings window: asks GitHub for the latest release, downloads its VSIX to
    /// %TEMP%\UEToVS\Updates and starts VS's own VSIXInstaller (next to devenv.exe), which asks to close VS and then
    /// installs. Nothing runs in the background or at startup; every step is logged (update.*).
    /// </summary>
    internal static class UpdateService
    {
        private static readonly Logger L = Log.For("Update");
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>Checks, downloads and starts the installer. Returns the text shown next to the button.</summary>
        public static async Task<string> CheckAndInstallAsync(string installedVersionText, CancellationToken ct = default)
        {
            if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) return SettingsText.UpdateBusy;
            var sw = Stopwatch.StartNew();
            try
            {
                var installed = UpdateCheck.ParseVersion(installedVersionText);
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    // Generic client name only (GitHub requires a User-Agent).
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("UEToVS-Updater");
                    ReleaseInfo latest;
                    using (var req = new HttpRequestMessage(HttpMethod.Get, UpdateCheck.LatestReleaseApi))
                    {
                        req.Headers.Accept.ParseAdd("application/vnd.github+json");
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            cts.CancelAfter(TimeSpan.FromSeconds(20));
                            var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
                            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode)
                            {
                                L.Warn("update.check.failed", "GitHub latest-release request failed", ("status", (int)resp.StatusCode));
                                return SettingsText.UpdateCheckFailed(((int)resp.StatusCode).ToString());
                            }
                            latest = UpdateCheck.Parse(body);
                        }
                    }
                    L.Info("update.check", null, ("installed", installedVersionText), ("latest", latest?.Version?.ToString()), ("asset", latest?.VsixName),
                           ("ms", Math.Round(sw.Elapsed.TotalMilliseconds)));
                    if (latest == null) return SettingsText.UpdateCheckFailed("no VSIX");
                    if (!UpdateCheck.IsNewer(latest.Version, installed)) return SettingsText.UpdateUpToDate(installed?.ToString() ?? installedVersionText);

                    string dir = Path.Combine(Path.GetTempPath(), "UEToVS", "Updates");
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, UpdateCheck.SafeFileName(latest));
                    bool cached = File.Exists(file) && latest.VsixSize > 0 && new FileInfo(file).Length == latest.VsixSize;
                    if (!cached)
                    {
                        string tmp = file + ".part";
                        using (var resp = await http.GetAsync(latest.VsixUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                        {
                            resp.EnsureSuccessStatusCode();
                            using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                                await src.CopyToAsync(dst, 81920, ct).ConfigureAwait(false);
                        }
                        long size = new FileInfo(tmp).Length;
                        if (latest.VsixSize > 0 && size != latest.VsixSize)
                        {
                            File.Delete(tmp);
                            L.Warn("update.download.sizeMismatch", "Downloaded VSIX has the wrong size", ("expected", latest.VsixSize), ("actual", size));
                            return SettingsText.UpdateDownloadFailed;
                        }
                        if (File.Exists(file)) File.Delete(file);
                        File.Move(tmp, file);
                    }
                    L.Info("update.download", null, ("version", latest.Version.ToString()), ("file", file), ("cached", cached), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds)));

                    string installer = InstallerPath();
                    if (installer == null)
                    {
                        L.Warn("update.installer.missing", "VSIXInstaller.exe not found next to devenv.exe; showing the downloaded file");
                        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = false });
                    }
                    else
                    {
                        // VSIXInstaller.exe directly (not the .vsix file association, which goes through VSLauncher.exe: a console
                        // program that opens an empty terminal window when Windows Terminal is the default terminal).
                        Process.Start(new ProcessStartInfo(installer, "\"" + file + "\"") { UseShellExecute = false, CreateNoWindow = true });
                    }
                    L.Info("update.install.started", null, ("version", latest.Version.ToString()), ("installer", installer ?? "shell"));
                    return SettingsText.UpdateInstallerStarted(latest.Version.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                L.Warn("update.timeout", "Update check timed out");
                return SettingsText.UpdateCheckFailed("timeout");
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception
                                       || ex is WebException)
            {
                L.Warn("update.failed", "Update failed", ex);
                return SettingsText.UpdateCheckFailed(ex.Message);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>VSIXInstaller.exe of the running VS (Common7\IDE, next to devenv.exe).</summary>
        private static string InstallerPath()
        {
            try
            {
                string devenv = Process.GetCurrentProcess().MainModule?.FileName;
                string dir = devenv == null ? null : Path.GetDirectoryName(devenv);
                string p = dir == null ? null : Path.Combine(dir, "VSIXInstaller.exe");
                return p != null && File.Exists(p) ? p : null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is NotSupportedException)
            {
                return null;
            }
        }
    }
}
