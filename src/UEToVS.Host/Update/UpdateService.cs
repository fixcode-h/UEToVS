using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// "检查更新" in the settings window. One state for the whole VS process (<see cref="State"/>, <see cref="Changed"/>):
    /// the button first asks GitHub for the latest release (检查); when a newer one exists it shows the version and size and
    /// becomes "下载并安装", which downloads the VSIX to %TEMP%\UEToVS\Updates with live progress (the button is "取消"
    /// meanwhile) and starts VS's own VSIXInstaller (next to devenv.exe), which asks to close VS and then installs.
    /// A download that receives nothing for <see cref="StallSeconds"/> seconds is stopped instead of hanging; every step and
    /// every failure is logged (update.*). Nothing runs in the background or at startup.
    /// </summary>
    internal static class UpdateService
    {
        private static readonly Logger L = Log.For("Update");
        private const int CheckTimeoutSeconds = 20;
        private const int StallSeconds = 30;
        private static readonly object Sync = new object();
        private static UpdateState _state;
        private static CancellationTokenSource _downloadCts;

        /// <summary>Raised on any thread whenever <see cref="State"/> changes (progress included, at most ~5 times a second).</summary>
        public static event EventHandler Changed;

        public static UpdateState State(string installedText)
        {
            lock (Sync) return _state ?? new UpdateState(UpdatePhase.Idle, installedText);
        }

        /// <summary>Does whatever the button currently means (检查 / 下载并安装 / 取消). Returns the text for the row.</summary>
        public static async Task<string> RunPrimaryAsync(string installedText)
        {
            var s = State(installedText);
            switch (UpdateView.Command(s))
            {
                case UpdateCommand.Check: await CheckAsync(installedText).ConfigureAwait(false); break;
                case UpdateCommand.DownloadAndInstall: await DownloadAndInstallAsync(installedText).ConfigureAwait(false); break;
                case UpdateCommand.Cancel: CancelDownload(); break;
            }
            return UpdateView.Text(State(installedText));
        }

        public static void CancelDownload()
        {
            CancellationTokenSource cts;
            lock (Sync) cts = _downloadCts;
            if (cts == null) return;
            L.Info("update.download.cancel", null);
            cts.Cancel();
        }

        private static void Set(UpdateState s)
        {
            lock (Sync) _state = s;
            var handler = Changed;
            if (handler == null) return;
            foreach (EventHandler h in handler.GetInvocationList())
            {
                try { h(null, EventArgs.Empty); }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { L.Warn("update.changed.handlerFailed", "Update state listener failed", ex); }
            }
        }

        /// <summary>True when the caller now owns the flow (no check or download is running).</summary>
        private static bool TryEnter(UpdateState next)
        {
            lock (Sync)
            {
                if (_state != null && _state.IsBusy) return false;
                _state = next;
            }
            Set(next);
            return true;
        }

        private static HttpClient NewClient()
        {
            // Infinite overall timeout: the check has its own deadline and the download is watched for stalls instead, so a
            // big VSIX on a slow line is not cut off after a fixed time. System proxy settings apply (HttpClientHandler default).
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            // Generic client name only (GitHub requires a User-Agent).
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UEToVS-Updater");
            return http;
        }

        public static async Task CheckAsync(string installedText)
        {
            if (!TryEnter(new UpdateState(UpdatePhase.Checking, installedText))) return;
            var sw = Stopwatch.StartNew();
            var installed = UpdateCheck.ParseVersion(installedText);
            try
            {
                using (var http = NewClient())
                using (var req = new HttpRequestMessage(HttpMethod.Get, UpdateCheck.LatestReleaseApi))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CheckTimeoutSeconds)))
                {
                    req.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using (var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            string remaining = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var values) ? values.FirstOrDefault() : null;
                            L.Warn("update.check.failed", "GitHub latest-release request failed", ("status", (int)resp.StatusCode), ("rateRemaining", remaining),
                                   ("ms", Math.Round(sw.Elapsed.TotalMilliseconds)));
                            Fail(installedText, null, false, SettingsText.UpdateCheckFailed(StatusReason((int)resp.StatusCode, remaining)));
                            return;
                        }
                        var latest = UpdateCheck.Parse(body);
                        L.Info("update.check", null, ("installed", installedText), ("latest", latest?.Version?.ToString()), ("asset", latest?.VsixName),
                               ("size", latest?.VsixSize), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds)));
                        if (latest == null) Fail(installedText, null, false, SettingsText.UpdateCheckFailed(SettingsText.UpdateNoVsix));
                        else if (!UpdateCheck.IsNewer(latest.Version, installed)) Set(new UpdateState(UpdatePhase.UpToDate, installedText, latest));
                        else Set(new UpdateState(UpdatePhase.Available, installedText, latest));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                L.Warn("update.check.timeout", "Update check timed out", ("seconds", CheckTimeoutSeconds));
                Fail(installedText, null, false, SettingsText.UpdateCheckFailed(SettingsText.UpdateConnectTimeout));
            }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                L.Warn("update.check.error", "Update check failed", ex);
                Fail(installedText, null, false, SettingsText.UpdateCheckFailed(SettingsText.UpdateNetwork(Innermost(ex))));
            }
            finally
            {
                LeaveIfStillBusy(installedText, null, false);
            }
        }

        public static async Task DownloadAndInstallAsync(string installedText)
        {
            ReleaseInfo latest;
            lock (Sync) latest = _state?.Latest;
            if (latest?.VsixUrl == null) { await CheckAsync(installedText).ConfigureAwait(false); return; }
            if (!TryEnter(new UpdateState(UpdatePhase.Downloading, installedText, latest, 0, latest.VsixSize))) return;
            var sw = Stopwatch.StartNew();
            var cts = new CancellationTokenSource();
            lock (Sync) _downloadCts = cts;
            try
            {
                string file = await DownloadAsync(installedText, latest, cts.Token).ConfigureAwait(false);
                if (file == null) return; // Fail() already reported why.
                L.Info("update.download", null, ("version", latest.Version.ToString()), ("file", file), ("ms", Math.Round(sw.Elapsed.TotalMilliseconds)));
                StartInstaller(file, latest);
                Set(new UpdateState(UpdatePhase.InstallerStarted, installedText, latest));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                L.Info("update.download.canceled", null, ("ms", Math.Round(sw.Elapsed.TotalMilliseconds)));
                Set(new UpdateState(UpdatePhase.Available, installedText, latest, message: SettingsText.UpdateDownloadCanceled));
            }
            catch (StallException)
            {
                L.Warn("update.download.stalled", "No data received; download stopped", ("seconds", StallSeconds), ("url", latest.VsixUrl),
                       ("ms", Math.Round(sw.Elapsed.TotalMilliseconds)));
                Fail(installedText, latest, true, SettingsText.UpdateDownloadFailedWith(SettingsText.UpdateDownloadStalled(StallSeconds)));
            }
            catch (Exception ex) when (IsNetworkError(ex) || ex is OperationCanceledException)
            {
                L.Warn("update.download.error", "Update download or installer start failed", ex, ("url", latest.VsixUrl));
                Fail(installedText, latest, true, SettingsText.UpdateDownloadFailedWith(Innermost(ex)));
            }
            finally
            {
                lock (Sync) if (ReferenceEquals(_downloadCts, cts)) _downloadCts = null;
                cts.Dispose();
                LeaveIfStillBusy(installedText, latest, true);
            }
        }

        /// <summary>Downloads (or reuses a complete earlier download of) the VSIX; null after reporting a failure.</summary>
        private static async Task<string> DownloadAsync(string installedText, ReleaseInfo latest, CancellationToken ct)
        {
            string dir = Path.Combine(Path.GetTempPath(), "UEToVS", "Updates");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, UpdateCheck.SafeFileName(latest));
            if (File.Exists(file) && latest.VsixSize > 0 && new FileInfo(file).Length == latest.VsixSize)
            {
                L.Info("update.download.cached", null, ("file", file));
                return file;
            }
            L.Info("update.download.start", null, ("version", latest.Version.ToString()), ("url", latest.VsixUrl), ("size", latest.VsixSize));
            string tmp = file + ".part";
            long received = 0, total = latest.VsixSize;
            using (var http = NewClient())
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                stall.CancelAfter(TimeSpan.FromSeconds(StallSeconds));
                HttpResponseMessage resp = null;
                // .NET Framework's response stream ignores the token of a read that is already waiting, so a stalled or
                // canceled download is aborted by disposing the response; the read then throws and is mapped back below.
                using (stall.Token.Register(() => resp?.Dispose()))
                {
                    try
                    {
                        resp = await http.GetAsync(latest.VsixUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            L.Warn("update.download.failed", "VSIX download request failed", ("status", (int)resp.StatusCode));
                            Fail(installedText, latest, true, SettingsText.UpdateDownloadFailedWith(SettingsText.UpdateHttpStatus((int)resp.StatusCode)));
                            return null;
                        }
                        if (total <= 0) total = resp.Content.Headers.ContentLength ?? 0;
                        var lastReport = Stopwatch.StartNew();
                        var buffer = new byte[81920];
                        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        {
                            while (true)
                            {
                                // Each read gets a fresh stall deadline: slow lines are fine, a line that stops is not.
                                stall.CancelAfter(TimeSpan.FromSeconds(StallSeconds));
                                int n = await src.ReadAsync(buffer, 0, buffer.Length, stall.Token).ConfigureAwait(false);
                                if (n == 0) break;
                                await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                                received += n;
                                if (lastReport.ElapsedMilliseconds >= 200)
                                {
                                    lastReport.Restart();
                                    Set(new UpdateState(UpdatePhase.Downloading, installedText, latest, received, total));
                                }
                            }
                        }
                    }
                    catch (Exception) when (stall.IsCancellationRequested)
                    {
                        TryDelete(tmp);
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        throw new StallException();
                    }
                    catch
                    {
                        TryDelete(tmp);
                        throw;
                    }
                    finally
                    {
                        resp?.Dispose();
                    }
                }
            }
            Set(new UpdateState(UpdatePhase.Downloading, installedText, latest, received, total));
            if (latest.VsixSize > 0 && received != latest.VsixSize)
            {
                TryDelete(tmp);
                L.Warn("update.download.sizeMismatch", "Downloaded VSIX has the wrong size", ("expected", latest.VsixSize), ("actual", received));
                Fail(installedText, latest, true, SettingsText.UpdateDownloadFailedWith(SettingsText.UpdateDownloadIncomplete));
                return null;
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            return file;
        }

        private static void StartInstaller(string file, ReleaseInfo latest)
        {
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
        }

        /// <summary>Opens the release page of the found version (or the releases list) in the browser.</summary>
        public static void OpenReleasePage(string installedText)
        {
            string url = State(installedText).Latest?.PageUrl ?? UpdateCheck.ReleasesPage;
            L.Info("update.releasePage", null, ("url", url));
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        /// <summary>
        /// An unexpected exception (logged by the caller's Safe wrapper) must not leave the row on "正在下载…" for good:
        /// that is exactly how the old one-shot flow looked dead.
        /// </summary>
        private static void LeaveIfStillBusy(string installedText, ReleaseInfo latest, bool whileDownloading)
        {
            bool busy;
            lock (Sync) busy = _state != null && _state.IsBusy;
            if (busy) Fail(installedText, latest, whileDownloading, SettingsText.Failed);
        }

        private static void Fail(string installedText, ReleaseInfo latest, bool whileDownloading, string message) =>
            Set(new UpdateState(UpdatePhase.Failed, installedText, latest, message: message, failedWhileDownloading: whileDownloading));

        private static string StatusReason(int status, string rateRemaining)
        {
            if ((status == 403 || status == 429) && rateRemaining == "0") return SettingsText.UpdateRateLimited;
            if (status == 404) return SettingsText.UpdateNoRelease;
            return SettingsText.UpdateHttpStatus(status);
        }

        private static bool IsNetworkError(Exception ex) =>
            ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is WebException
            || ex is InvalidOperationException;

        /// <summary>The innermost message ("无法解析此远程名称" beats "发送请求时出错").</summary>
        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message.Trim().TrimEnd('。', '.');
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { L.Warn("update.download.cleanupFailed", null, ex, ("file", path)); }
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

        private sealed class StallException : Exception { }
    }
}
