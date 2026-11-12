using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Settings;
using UEToVS.Host.Options;
using UEToVS.Host.Shell;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>
    /// Takes over VS's own C++ IntelliSense for UE solutions and restores it (§7.5). Lifecycle:
    /// UE solution opened → (setting on) back up originals to %LocalAppData%\UEToVS\intellisense-backup.json, then switch
    /// the C/C++ advanced options off; first time: banner with a 恢复 link. Mode UnrealSolutionOnly: restore when the
    /// solution closes. Restore command: write back exactly the backed-up values. Every step is logged
    /// (intellisense.*), including which property names exist on this VS.
    /// </summary>
    internal sealed class IntelliSenseTakeoverService
    {
        private static readonly Logger L = Log.For("IntelliSense");
        private const string BannerId = "intellisense.takeover";
        private const string VaBannerId = "intellisense.visualassist";

        private readonly AsyncPackage _package;
        private readonly SettingsService _settings;
        private readonly string _backupPath;
        private readonly string _codeAnalysisBackupPath;
        private bool _propertiesLogged;
        private bool _vaChecked;

        public IntelliSenseTakeoverService(AsyncPackage package, SettingsService settings, string backupPath = null, string codeAnalysisBackupPath = null)
        {
            _package = package;
            _settings = settings;
            _backupPath = backupPath ?? IntelliSenseBackupFile.DefaultPath;
            _codeAnalysisBackupPath = codeAnalysisBackupPath ?? IntelliSenseBackupFile.DefaultCodeAnalysisPath;
        }

        /// <summary>Last known state, for the session header and the diagnostics package.</summary>
        public TakeoverState LastState { get; private set; } = TakeoverState.Unknown;

        public bool BackupExists => File.Exists(_backupPath);

        public string BackupPath => _backupPath;

        public bool CodeAnalysisBackupExists => File.Exists(_codeAnalysisBackupPath);

        /// <summary>Visual Assist found (null = not checked yet).</summary>
        public string VisualAssistEvidence { get; private set; }

        private static TakeoverOptions OptionsFrom(HostSettings s) => new TakeoverOptions { DisableDatabase = s.DisableDatabase };

        // ---------------------------------------------------------------- lifecycle hooks (called by the package)

        public async Task OnUnrealSolutionOpenedAsync()
        {
            var s = _settings.Effective;
            L.Info("intellisense.solution.opened", null, ("takeover", s.IntelliSenseTakeover), ("mode", s.IntelliSenseTakeoverMode.ToString()),
                   ("disableDatabase", s.DisableDatabase), ("backupExists", BackupExists),
                   ("disableBackgroundCodeAnalysis", s.DisableBackgroundCodeAnalysis), ("codeAnalysisBackupExists", CodeAnalysisBackupExists));
            if (s.IntelliSenseTakeover) await TakeOverAsync("auto").ConfigureAwait(false);
            else await RefreshStateAsync().ConfigureAwait(false);
            if (s.DisableBackgroundCodeAnalysis) await DisableBackgroundCodeAnalysisAsync("auto").ConfigureAwait(false);
            await CheckVisualAssistAsync().ConfigureAwait(false);
        }

        public async Task OnSolutionClosedAsync(bool wasUnreal)
        {
            var s = _settings.Effective;
            if (wasUnreal && s.IntelliSenseTakeoverMode == IntelliSenseTakeoverMode.UnrealSolutionOnly && BackupExists)
            {
                L.Info("intellisense.solution.closed", "Mode UnrealSolutionOnly: restoring VS IntelliSense");
                await RestoreAsync("solutionClosed").ConfigureAwait(false);
            }
        }

        public async Task OnSettingsChangedAsync(HostSettings old, HostSettings now, bool unrealSolutionOpen)
        {
            if (old.DisableBackgroundCodeAnalysis != now.DisableBackgroundCodeAnalysis)
            {
                L.Info("codeAnalysis.settings.changed", null, ("disableBackgroundCodeAnalysis", now.DisableBackgroundCodeAnalysis), ("ueSolutionOpen", unrealSolutionOpen));
                if (!now.DisableBackgroundCodeAnalysis) await RestoreBackgroundCodeAnalysisAsync("settingOff").ConfigureAwait(false);
                else if (unrealSolutionOpen) await DisableBackgroundCodeAnalysisAsync("settingsChanged").ConfigureAwait(false);
            }
            bool takeoverChanged = old.IntelliSenseTakeover != now.IntelliSenseTakeover;
            bool dbChanged = old.DisableDatabase != now.DisableDatabase;
            bool modeChanged = old.IntelliSenseTakeoverMode != now.IntelliSenseTakeoverMode;
            if (!takeoverChanged && !dbChanged && !modeChanged) return;
            L.Info("intellisense.settings.changed", null, ("takeover", now.IntelliSenseTakeover), ("mode", now.IntelliSenseTakeoverMode.ToString()),
                   ("disableDatabase", now.DisableDatabase), ("ueSolutionOpen", unrealSolutionOpen));
            if (!now.IntelliSenseTakeover)
            {
                if (BackupExists) await RestoreAsync("settingOff").ConfigureAwait(false);
                return;
            }
            if (unrealSolutionOpen || now.IntelliSenseTakeoverMode == IntelliSenseTakeoverMode.Global)
                await TakeOverAsync("settingsChanged").ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- commands

        /// <summary>Applies the takeover. <paramref name="trigger"/>: auto / command / settingsChanged.</summary>
        public async Task<TakeoverReport> TakeOverAsync(string trigger)
        {
            // Backup file IO off the UI thread.
            IntelliSenseBackup backup;
            try
            {
                backup = await Task.Run(() => IntelliSenseBackupFile.Load(_backupPath)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            {
                // Without a trustworthy backup we cannot know the user's originals: do not touch anything.
                L.Error("intellisense.backup.corrupt", "IntelliSense backup unreadable; takeover skipped", ex, ("path", _backupPath));
                Banners.Show(BannerId, Strings.BannerBackupUnreadable(_backupPath), BannerKind.Error);
                return null;
            }
            string vsVersion = null;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            TakeoverReport report;
            using (UiThread.Measure(L, "intellisense.apply", ("trigger", trigger)))
            {
                var store = await CreateStoreAsync();
                if (store == null) return null;
                LogPropertiesOnce(store);
                try { vsVersion = (await _package.GetServiceAsync(typeof(DTE)) as DTE)?.Version; } catch (Exception) { }
                if (backup == null) backup = new IntelliSenseBackup { VsVersion = vsVersion };
                report = IntelliSenseTakeover.Apply(store, backup, OptionsFrom(_settings.Effective),
                    b => IntelliSenseBackupFile.Save(_backupPath, b));
                LastState = IntelliSenseTakeover.GetState(store, OptionsFrom(_settings.Effective));
            }
            LogReport("intellisense.takeover", trigger, report);

            if (report.BackupError != null)
            {
                L.Error("intellisense.backup.failed", "Backup could not be saved; nothing changed", null, ("error", report.BackupError), ("path", _backupPath));
                Banners.Show(BannerId, Strings.BannerBackupWriteFailed, BannerKind.Error);
                return report;
            }
            int changed = report.Count(SettingOutcomeStatus.Changed);
            if (LastState == TakeoverState.Unknown)
            {
                L.Warn("intellisense.unknownVs", "None of the known C/C++ IntelliSense properties exist on this VS; takeover not possible");
                Banners.Show(BannerId, Strings.BannerUnknownVs, BannerKind.Warning,
                    (Strings.OpenLog, () => OpenCurrentLog()));
                return report;
            }
            if (changed > 0 || trigger == "command")
            {
                bool first = !_settings.General.TakeoverNoticeShown;
                if (first || trigger == "command")
                {
                    Banners.Show(BannerId, Strings.BannerTakenOver, BannerKind.Info,
                        (Strings.Restore, () => _ = RestoreAsync("banner")),
                        (Strings.Settings, () => HostPackage.Instance?.ExecuteAction(UEToVS.Host.Shell.ActionIds.Settings)));
                    await MarkNoticeShownAsync();
                }
            }
            return report;
        }

        /// <summary>Restores exactly the backed-up values, then deletes the backup (keeps failed entries).</summary>
        public async Task<TakeoverReport> RestoreAsync(string trigger)
        {
            IntelliSenseBackup backup;
            try
            {
                backup = await Task.Run(() => IntelliSenseBackupFile.Load(_backupPath)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Error("intellisense.backup.corrupt", "IntelliSense backup unreadable; cannot restore", ex, ("path", _backupPath));
                Banners.Show(BannerId, Strings.BannerBackupCorruptRestore, BannerKind.Error);
                return null;
            }
            if (backup == null || backup.IsEmpty)
            {
                L.Info("intellisense.restore.nothing", "No backup: VS IntelliSense settings were not changed by UEToVS", ("trigger", trigger));
                if (trigger == "command" || trigger == "banner")
                    Banners.Show(BannerId, Strings.BannerNothingToRestore, BannerKind.Info);
                return new TakeoverReport();
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            TakeoverReport report;
            using (UiThread.Measure(L, "intellisense.restore", ("trigger", trigger)))
            {
                var store = await CreateStoreAsync();
                if (store == null) return null;
                report = IntelliSenseTakeover.Restore(store, backup);
                LastState = IntelliSenseTakeover.GetState(store, OptionsFrom(_settings.Effective));
            }
            try
            {
                IntelliSenseBackupFile.SaveOrDelete(_backupPath, backup);
            }
            catch (Exception ex)
            {
                L.Error("intellisense.backup.update.failed", "Could not update the backup after restore", ex, ("path", _backupPath));
            }
            LogReport("intellisense.restore", trigger, report);
            int failed = report.Count(SettingOutcomeStatus.Failed);
            if (trigger != "solutionClosed")
            {
                Banners.Show(BannerId, failed == 0 ? Strings.BannerRestored : Strings.BannerRestoredPartially(failed),
                    failed == 0 ? BannerKind.Info : BannerKind.Warning);
            }
            return report;
        }

        public async Task<TakeoverState> RefreshStateAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            using (UiThread.Measure(L, "intellisense.state"))
            {
                var store = await CreateStoreAsync();
                if (store == null) return LastState;
                LogPropertiesOnce(store);
                LastState = IntelliSenseTakeover.GetState(store, OptionsFrom(_settings.Effective));
            }
            L.Info("intellisense.state", null, ("state", LastState.ToString()), ("backupExists", BackupExists));
            return LastState;
        }

        // ---------------------------------------------------------------- background code analysis

        /// <summary>
        /// Turns on VS's "Disable Background Code Analysis" (original value backed up to code-analysis-backup.json first).
        /// Same backup-before-write algorithm as the takeover, with its own table and file. Logged as codeAnalysis.*.
        /// </summary>
        public async Task<TakeoverReport> DisableBackgroundCodeAnalysisAsync(string trigger)
        {
            IntelliSenseBackup backup;
            try
            {
                backup = await Task.Run(() => IntelliSenseBackupFile.Load(_codeAnalysisBackupPath)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Error("codeAnalysis.backup.corrupt", "Code analysis backup unreadable; setting left unchanged", ex, ("path", _codeAnalysisBackupPath));
                return null;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            TakeoverReport report;
            using (UiThread.Measure(L, "codeAnalysis.apply", ("trigger", trigger)))
            {
                var store = await CreateStoreAsync();
                if (store == null) return null;
                LogPropertiesOnce(store);
                if (backup == null) backup = new IntelliSenseBackup();
                report = IntelliSenseTakeover.Apply(store, backup, null, b => IntelliSenseBackupFile.Save(_codeAnalysisBackupPath, b),
                    CppIntelliSenseSettings.BackgroundCodeAnalysis);
            }
            LogReport("codeAnalysis.disable", trigger, report, _codeAnalysisBackupPath);
            if (report.Count(SettingOutcomeStatus.Missing) > 0)
                L.Warn("codeAnalysis.unknownVs", "\"Disable Background Code Analysis\" not found on this VS (see intellisense.properties)");
            return report;
        }

        /// <summary>Writes back the backed-up value of the background code analysis option, then deletes the backup.</summary>
        public async Task<TakeoverReport> RestoreBackgroundCodeAnalysisAsync(string trigger)
        {
            IntelliSenseBackup backup;
            try
            {
                backup = await Task.Run(() => IntelliSenseBackupFile.Load(_codeAnalysisBackupPath)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            {
                L.Error("codeAnalysis.backup.corrupt", "Code analysis backup unreadable; cannot restore", ex, ("path", _codeAnalysisBackupPath));
                return null;
            }
            if (backup == null || backup.IsEmpty)
            {
                L.Info("codeAnalysis.restore.nothing", "Background code analysis was not changed by UEToVS", ("trigger", trigger));
                return new TakeoverReport();
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            TakeoverReport report;
            using (UiThread.Measure(L, "codeAnalysis.restore", ("trigger", trigger)))
            {
                var store = await CreateStoreAsync();
                if (store == null) return null;
                report = IntelliSenseTakeover.Restore(store, backup);
            }
            try
            {
                IntelliSenseBackupFile.SaveOrDelete(_codeAnalysisBackupPath, backup);
            }
            catch (Exception ex)
            {
                L.Error("codeAnalysis.backup.update.failed", "Could not update the code analysis backup after restore", ex, ("path", _codeAnalysisBackupPath));
            }
            LogReport("codeAnalysis.restore", trigger, report, _codeAnalysisBackupPath);
            return report;
        }

        // ---------------------------------------------------------------- Visual Assist

        /// <summary>Detects VA (package GUID + extension folders). Logs and shows a banner suggesting choosing one.</summary>
        public async Task CheckVisualAssistAsync()
        {
            if (_vaChecked) return;
            _vaChecked = true;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string evidence = null;
            bool loaded = false;
            var roots = new List<string>();
            using (UiThread.Measure(L, "intellisense.va.check"))
            {
                try
                {
                    if (await _package.GetServiceAsync(typeof(SVsShell)) is IVsShell shell)
                    {
                        var guid = new Guid(VisualAssistDetection.PackageGuid);
                        if (ErrorHandler.Succeeded(shell.IsPackageInstalled(ref guid, out int installed)) && installed != 0)
                        {
                            evidence = "package " + VisualAssistDetection.PackageGuid;
                            loaded = ErrorHandler.Succeeded(shell.IsPackageLoaded(ref guid, out var pkg)) && pkg != null;
                        }
                    }
                    if (await _package.GetServiceAsync(typeof(SVsSettingsManager)) is IVsSettingsManager sm)
                    {
                        foreach (var folder in new[] { __VsApplicationDataFolder.ApplicationDataFolder_UserExtensions, __VsApplicationDataFolder.ApplicationDataFolder_ApplicationExtensions })
                        {
                            if (ErrorHandler.Succeeded(sm.GetApplicationDataFolder((uint)folder, out string path)) && !string.IsNullOrEmpty(path)) roots.Add(path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    L.Warn("intellisense.va.check.failed", "Visual Assist package check failed", ex);
                }
            }
            if (evidence == null && roots.Count > 0)
            {
                string found = await Task.Run(() => VisualAssistDetection.FindInExtensionDirectories(roots)).ConfigureAwait(false);
                if (found != null) evidence = "files " + found;
            }
            VisualAssistEvidence = evidence ?? string.Empty;
            if (evidence == null)
            {
                L.Info("intellisense.va.notFound", null, ("roots", roots));
                return;
            }
            L.Warn("intellisense.va.detected", "Visual Assist is installed; two C++ code-insight engines will compete", ("evidence", evidence), ("loaded", loaded));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_settings.General.VisualAssistNoticeDismissed) return;
            Banners.Show(VaBannerId, Strings.BannerVisualAssist, BannerKind.Warning,
                (Strings.DontShowAgain, () =>
                {
                    _settings.General.VisualAssistNoticeDismissed = true;
                    _settings.General.SaveSettingsToStorage();
                    L.Info("intellisense.va.dismissed", null);
                }));
        }

        // ---------------------------------------------------------------- helpers

        private async Task<DteCppSettingsStore> CreateStoreAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(await _package.GetServiceAsync(typeof(DTE)) is DTE dte))
            {
                L.Error("intellisense.dte.unavailable", "DTE service unavailable", null);
                return null;
            }
            return new DteCppSettingsStore(dte);
        }

        private void LogPropertiesOnce(DteCppSettingsStore store)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_propertiesLogged) return;
            _propertiesLogged = true;
            foreach (var page in CppIntelliSenseSettings.Pages)
            {
                var names = store.GetPropertyNames(page);
                L.Info("intellisense.properties", "C/C++ option properties on this VS (for calibrating the candidate table)",
                       ("page", page), ("count", names?.Count ?? -1), ("names", names ?? new List<string>()),
                       ("unknownRelated", IntelliSenseTakeover.UnknownInterestingProperties(names)));
            }
            foreach (var kv in store.PageErrors) L.Warn("intellisense.page.error", "Cannot access options page", ("page", kv.Key), ("error", kv.Value));
        }

        private void LogReport(string evt, string trigger, TakeoverReport report, string backupPath = null)
        {
            foreach (var o in report.Outcomes)
            {
                var data = new (string, object)[]
                {
                    ("key", o.Key), ("page", o.Page), ("property", o.Property), ("status", o.Status.ToString()),
                    ("before", o.Before), ("after", o.After), ("error", o.Error), ("triedMissing", o.MissingCandidates),
                };
                switch (o.Status)
                {
                    case SettingOutcomeStatus.Failed:
                        L.Error("intellisense.property.failed", "Property not changed", null, data);
                        break;
                    case SettingOutcomeStatus.Missing:
                    case SettingOutcomeStatus.NotBoolean:
                        L.Warn("intellisense.property." + o.Status.ToString().ToLowerInvariant(), null, data);
                        break;
                    default:
                        L.Info("intellisense.property." + o.Status.ToString().ToLowerInvariant(), null, data);
                        break;
                }
            }
            L.Info(evt + ".done", null, ("trigger", trigger), ("state", LastState.ToString()),
                   ("changed", report.Count(SettingOutcomeStatus.Changed)), ("alreadyAtTarget", report.Count(SettingOutcomeStatus.AlreadyAtTarget)),
                   ("missing", report.Count(SettingOutcomeStatus.Missing)), ("failed", report.Count(SettingOutcomeStatus.Failed)),
                   ("reverted", report.Count(SettingOutcomeStatus.Reverted)), ("skipped", report.Count(SettingOutcomeStatus.Skipped)),
                   ("backupWritten", report.BackupWritten), ("backupPath", backupPath ?? _backupPath));
        }

        private async Task MarkNoticeShownAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _settings.General.TakeoverNoticeShown = true;
            _settings.General.SaveSettingsToStorage();
        }

        private static void OpenCurrentLog()
        {
            string f = HostLog.CurrentFile;
            if (f != null && File.Exists(f)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f) { UseShellExecute = true });
        }
    }
}
