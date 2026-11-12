using System;
using System.Globalization;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Update
{
    /// <summary>Where "检查更新" is: one state for the whole VS process, so a reopened settings window shows the same thing.</summary>
    public enum UpdatePhase
    {
        Idle,
        Checking,
        UpToDate,
        /// <summary>A newer release was found; nothing downloaded yet (the button turns into "下载并安装").</summary>
        Available,
        Downloading,
        InstallerStarted,
        Failed,
    }

    /// <summary>Immutable snapshot of the update flow (what the settings row renders).</summary>
    public sealed class UpdateState
    {
        public UpdatePhase Phase { get; }
        /// <summary>Installed version as reported by the package ("0.4.5+hash").</summary>
        public string InstalledText { get; }
        public ReleaseInfo Latest { get; }
        public long Received { get; }
        public long Total { get; }
        /// <summary>Failed: what went wrong (already user-readable). Available: an optional note ("已取消下载").</summary>
        public string Message { get; }
        /// <summary>The failure happened while downloading (retry downloads again instead of checking).</summary>
        public bool FailedWhileDownloading { get; }

        public UpdateState(UpdatePhase phase, string installedText, ReleaseInfo latest = null, long received = 0, long total = 0, string message = null,
                           bool failedWhileDownloading = false)
        {
            Phase = phase;
            InstalledText = installedText;
            Latest = latest;
            Received = received;
            Total = total;
            Message = message;
            FailedWhileDownloading = failedWhileDownloading;
        }

        public bool IsBusy => Phase == UpdatePhase.Checking || Phase == UpdatePhase.Downloading;

        /// <summary>"v0.4.5" (build metadata dropped); the raw text when it does not parse.</summary>
        public string InstalledDisplay => UpdateView.Display(UpdateCheck.ParseVersion(InstalledText), InstalledText);
    }

    /// <summary>What the button does next for a given state.</summary>
    public enum UpdateCommand
    {
        Check,
        DownloadAndInstall,
        Cancel,
        None,
    }

    /// <summary>Pure mapping of <see cref="UpdateState"/> to the row's text and button (tested without WPF).</summary>
    public static class UpdateView
    {
        public static string Display(Version v, string fallback) => v != null ? "v" + v.ToString(3) : (string.IsNullOrEmpty(fallback) ? "?" : fallback);

        public static UpdateCommand Command(UpdateState s)
        {
            switch (s?.Phase ?? UpdatePhase.Idle)
            {
                case UpdatePhase.Checking: return UpdateCommand.None;
                case UpdatePhase.Downloading: return UpdateCommand.Cancel;
                case UpdatePhase.Available: return UpdateCommand.DownloadAndInstall;
                case UpdatePhase.Failed: return s.FailedWhileDownloading && s.Latest != null ? UpdateCommand.DownloadAndInstall : UpdateCommand.Check;
                default: return UpdateCommand.Check;
            }
        }

        public static string ButtonLabel(UpdateState s)
        {
            switch (s?.Phase ?? UpdatePhase.Idle)
            {
                case UpdatePhase.Checking: return SettingsText.UpdateButtonChecking;
                case UpdatePhase.Downloading: return SettingsText.Cancel;
                case UpdatePhase.Available: return SettingsText.UpdateButtonInstall;
                case UpdatePhase.Failed: return SettingsText.UpdateButtonRetry;
                default: return SettingsText.UpdateButtonCheck;
            }
        }

        public static string Text(UpdateState s)
        {
            if (s == null) return string.Empty;
            string installed = s.InstalledDisplay;
            string latest = s.Latest == null ? null : Display(s.Latest.Version, s.Latest.Tag);
            switch (s.Phase)
            {
                case UpdatePhase.Checking: return SettingsText.UpdateChecking;
                case UpdatePhase.UpToDate: return SettingsText.UpdateUpToDate(installed);
                case UpdatePhase.Available:
                {
                    string text = SettingsText.UpdateAvailable(latest, installed, Size(s.Latest?.VsixSize ?? 0));
                    return string.IsNullOrEmpty(s.Message) ? text : s.Message + "\n" + text;
                }
                case UpdatePhase.Downloading: return SettingsText.UpdateDownloading(latest, Progress(s.Received, s.Total));
                case UpdatePhase.InstallerStarted: return SettingsText.UpdateInstallerStarted(latest);
                case UpdatePhase.Failed: return s.Message ?? SettingsText.Failed;
                default: return SettingsText.UpdateCurrentVersion(installed);
            }
        }

        /// <summary>"3.2 / 12.5 MB（26%）"; just "3.2 MB" when the size is unknown.</summary>
        public static string Progress(long received, long total)
        {
            if (total <= 0) return Size(received);
            int pct = (int)Math.Min(100, Math.Floor(received * 100.0 / total));
            return string.Format(CultureInfo.InvariantCulture, "{0} / {1} ({2}%)", Mb(received), Size(total), pct);
        }

        public static string Size(long bytes) => bytes <= 0 ? "?" : Mb(bytes) + " MB";

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
    }
}
