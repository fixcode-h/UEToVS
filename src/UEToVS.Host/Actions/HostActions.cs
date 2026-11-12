using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Diagnostics;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Options;
using UEToVS.Host.Shell;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Actions
{
    /// <summary>
    /// The host's own actions (§7.4). Each is registered once in the <see cref="ActionRegistry"/>; the VS commands
    /// (Extensions > UEToVS) only dispatch to them, so Find Action / Alt+Enter list exactly the same set.
    /// </summary>
    internal static class HostActions
    {
        private static readonly Logger L = Log.For("HostActions");

        public static void Register(HostPackage package, ActionRegistry registry)
        {
            registry.Register(new DelegateAction(ActionIds.OpenLogFolder, Strings.CmdOpenLogFolder, ActionCategory.Diagnostics,
                ctx => OpenLogFolderAsync(), ctx => HostLog.LogDirectory != null, null, Strings.KwLogs));
            registry.Register(new DelegateAction(ActionIds.OpenCurrentLog, Strings.CmdOpenCurrentLog, ActionCategory.Diagnostics,
                ctx => OpenCurrentLogAsync(package), ctx => HostLog.CurrentFile != null, null, Strings.KwLogs));
            registry.Register(new DelegateAction(ActionIds.CreateDiagnostics, Strings.CmdCreateDiagnostics, ActionCategory.Diagnostics,
                ctx => CreateDiagnosticsPackageAsync(package), ctx => HostLog.Paths != null, null, Strings.KwDiagnostics));
            registry.Register(new DelegateAction(ActionIds.ShowPerformancePanel, Strings.CmdPerformancePanel, ActionCategory.Diagnostics,
                ctx => package.ShowPerformanceWindowAsync(), null, null, Strings.KwPerformance));
            registry.Register(new DelegateAction(ActionIds.RestartEngine, Strings.CmdRestartEngine, ActionCategory.Unreal,
                ctx => package.RestartEngineAsync(), ctx => ctx.IsUnrealSolution,
                ctx => ctx.EngineAvailable ? 0 : 10, Strings.KwEngine));
            registry.Register(new DelegateAction(ActionIds.TakeOverIntelliSense, Strings.CmdTakeOverIntelliSense, ActionCategory.Settings,
                ctx => package.IntelliSense.TakeOverAsync("command"), null, null, Strings.KwIntelliSense));
            registry.Register(new DelegateAction(ActionIds.RestoreIntelliSense, Strings.CmdRestoreIntelliSense, ActionCategory.Settings,
                ctx => package.IntelliSense.RestoreAsync("command"), null,
                ctx => package.IntelliSense.BackupExists ? 5 : 0, Strings.KwIntelliSense));
            registry.Register(new DelegateAction(ActionIds.ShowComponentGallery, Strings.CmdComponentGallery, ActionCategory.Diagnostics,
                ctx => ShowGalleryAsync(), null, null, Strings.KwGallery));
            registry.Register(new DelegateAction(ActionIds.Settings, Strings.CmdSettings, ActionCategory.Settings,
                ctx => package.ShowSettingsWindowAsync(), null, null, Strings.KwSettings));
            registry.Register(new DelegateAction(ActionIds.FindAction, Strings.CmdFindAction, ActionCategory.Navigation,
                ctx => ShowFindActionAsync(package, registry), null, null, Strings.KwFindAction));
            Navigation.NavigationActions.Register(registry);
            registry.Register(new DelegateAction(ActionIds.WriteMcpConfig, McpStrings.CmdWriteMcpConfig, ActionCategory.Settings,
                ctx => Mcp.McpRegistration.WriteSolutionConfigAsync(ctx.SolutionDirectory), ctx => ctx.IsUnrealSolution, null, McpStrings.KwMcp));
            Generate.GenerateActions.Register(registry, package);
            Editing.SmartSelectActions.Register(registry);
            Translation.TranslationActions.Register(registry);
            // ---- v0.4.5 构建结果 / 错误列表 / 新建 UE 类
            Build.BuildFeature.Register(package, registry);
            Editing.EditorFeatureActions.Register(registry); // v0.4.5 编辑体验
            L.Info("actions.registered", null, ("count", registry.Count));
        }

        private static Task OpenLogFolderAsync()
        {
            string dir = HostLog.LogDirectory;
            if (dir == null) return Task.CompletedTask;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            L.Info("logs.folder.opened", null, ("dir", dir));
            return Task.CompletedTask;
        }

        private static async Task OpenCurrentLogAsync(HostPackage package)
        {
            string file = HostLog.CurrentFile;
            if (file == null || !File.Exists(file))
            {
                VsStatusBar.SetText(Strings.NoLogYet);
                return;
            }
            Log.Flush();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            using (UiThread.Measure(L, "logs.open"))
                VsShellUtilities.OpenDocument(package, file);
            L.Info("logs.file.opened", null, ("file", file));
        }

        /// <summary>Find Action palette (§7.4); the palette lists every other available action.</summary>
        private static async Task ShowFindActionAsync(HostPackage package, ActionRegistry registry)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var palette = HostIntegration.CommandPalette;
            if (palette == null)
            {
                L.Warn("findAction.unavailable", "Command palette slot is empty");
                return;
            }
            using (UiThread.Measure(L, "findAction.show"))
                palette.Show(registry, package.CreateActionContext(ActionSource.FindAction), null);
        }

        private static async Task ShowGalleryAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var gallery = HostIntegration.ComponentGallery;
            if (gallery == null)
            {
                L.Info("gallery.unavailable", "Component gallery not integrated yet");
                Banners.Show("ui.gallery", Strings.GalleryNotAvailable, BannerKind.Info);
                return;
            }
            gallery.Show();
        }

        /// <summary>Zip of Logs + sanitized settings + index manifest + environment/engine/IntelliSense/performance state.</summary>
        private static async Task CreateDiagnosticsPackageAsync(HostPackage package)
        {
            var paths = HostLog.Paths;
            if (paths == null) return;
            L.Info("diagnostics.package.start", null, ("root", paths.Root));
            Log.Flush();

            string settingsJson = package.Settings.ToSanitizedJson();
            var extras = new Dictionary<string, string>();
            string user = Environment.UserName, profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string San(object o) => Sanitizer.Sanitize(JsonConvert.SerializeObject(o, Formatting.Indented), user, profile);

            var engine = package.Engine;
            extras["environment.json"] = San(new
            {
                hostVersion = HostPackage.HostVersion,
                vsVersion = package.VsVersion,
                uiLanguage = Loc.Language.ToString(),
                os = Environment.OSVersion.ToString(),
                is64 = Environment.Is64BitProcess,
                cpus = Environment.ProcessorCount,
                clr = Environment.Version.ToString(),
                solutionDir = package.SolutionDirectory,
                unreal = package.Unreal,
                extensionDir = HostPackage.ExtensionDirectory,
            });
            extras["engine.json"] = San(new
            {
                state = engine?.State.ToString() ?? "None",
                pid = engine?.ProcessId,
                exe = engine?.ExecutablePath,
                starts = engine?.TotalStarts,
                lastError = engine?.LastError,
                handshake = engine?.Handshake,
                lastStatus = engine?.LastStatus,
            });
            string backupText = null;
            try
            {
                if (File.Exists(package.IntelliSense.BackupPath)) backupText = File.ReadAllText(package.IntelliSense.BackupPath);
            }
            catch (Exception ex)
            {
                backupText = "unreadable: " + ex.Message;
            }
            extras["intellisense.json"] = San(new
            {
                state = package.IntelliSense.LastState.ToString(),
                backupPath = package.IntelliSense.BackupPath,
                backup = backupText,
                visualAssist = package.IntelliSense.VisualAssistEvidence,
                table = CppIntelliSenseSettings.All,
            });
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    var perf = await package.Performance.SampleAsync(cts.Token).ConfigureAwait(false);
                    extras["performance.json"] = San(perf);
                }
            }
            catch (Exception ex)
            {
                L.Warn("diagnostics.package.perf.failed", "Performance sample for the package failed", ex);
            }

            var result = await Task.Run(() => DiagnosticsPackageBuilder.Create(paths.Root, Path.Combine(paths.Root, "Diagnostics"), settingsJson, extras,
                                                                           sanitizeLine: Sanitizer.CreateLineSanitizer(user, profile)))
                                   .ConfigureAwait(false);
            L.Info("diagnostics.package.created", null, ("zip", result.ZipPath), ("logFiles", result.LogFiles), ("logMB", Math.Round(result.LogBytes / 1048576.0, 1)),
                   ("indexManifest", result.IndexManifestIncluded), ("skipped", result.Skipped));
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + result.ZipPath + "\"") { UseShellExecute = true });
            VsStatusBar.SetText(Strings.DiagnosticsCreated(result.ZipPath));
        }
    }
}
