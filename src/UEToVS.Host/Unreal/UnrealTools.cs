using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Build;
using UEToVS.Host.Link;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Shell;

namespace UEToVS.Host.Unreal
{
    /// <summary>Generate project files (UBT) and installing the UEToVSLink editor plugin into the project.</summary>
    internal static class UnrealTools
    {
        private static readonly Logger L = Log.For("Unreal");
        private static readonly Logger Ubt = Log.For("UBT");

        /// <summary>Engine root: detected from the solution layout, else reported by the engine, else the settings override.</summary>
        public static string EngineRoot(HostPackage package)
        {
            string root = package.Unreal?.EngineRoot;
            if (string.IsNullOrEmpty(root)) root = package.Engine?.LastStatus?.EngineRoot;
            if (string.IsNullOrEmpty(root)) root = package.Settings?.Effective?.UnrealEngineRootOverride;
            return string.IsNullOrEmpty(root) ? null : root;
        }

        /// <summary>(exe, args) to regenerate project files, or null when UBT is not found.</summary>
        public static (string Exe, string Args)? UbtCommand(string engineRoot, string uproject)
        {
            string args = $"-projectfiles -project=\"{uproject}\" -game -engine -progress";
            string[] candidates =
            {
                Path.Combine(engineRoot, "Engine", "Binaries", "DotNET", "UnrealBuildTool", "UnrealBuildTool.exe"),
                Path.Combine(engineRoot, "Engine", "Binaries", "DotNET", "UnrealBuildTool.exe"),
                Path.Combine(engineRoot, "Engine", "Build", "BatchFiles", "Build.bat"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return (c, args);
            return null;
        }

        // ---- v0.4.5 生成 Visual Studio 项目文件: one run at a time, live output in Output > UEToVS, status bar animation.
        private static int _generating;

        /// <summary>True while UBT regenerates the project files (the command, toolbar button and context menus are disabled).</summary>
        public static bool IsGeneratingProjectFiles => Volatile.Read(ref _generating) != 0;

        public static async Task GenerateProjectFilesAsync(HostPackage package)
        {
            string uproject = package.Unreal?.ProjectFile;
            string engine = EngineRoot(package);
            if (uproject == null || engine == null)
            {
                L.Info("ubt.projectFiles.noProject", null, ("uproject", uproject), ("engineRoot", engine));
                VsStatusBar.SetText(FeatureStrings.NoProjectOrEngine);
                return;
            }
            var cmd = UbtCommand(engine, uproject);
            if (cmd == null)
            {
                L.Warn("ubt.notFound", "UnrealBuildTool not found", ("engineRoot", engine));
                VsStatusBar.SetText(FeatureStrings.NoUbt);
                return;
            }
            if (Interlocked.CompareExchange(ref _generating, 1, 0) != 0)
            {
                VsStatusBar.SetText(BuildStrings.UbtAlreadyRunning);
                return;
            }
            object icon = (short)Constants.SBAI_Build;
            var pane = HostLog.Pane;
            var tail = new UbtOutputTail();
            var sw = Stopwatch.StartNew();
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RefreshCommands();
                VsEditor_SaveAll();
                Safe.Execute(L, "ubt.pane.activate", () => pane?.Activate());
                pane?.WriteRaw(BuildStrings.UbtPaneHeader(uproject));
                pane?.WriteRaw("> \"" + cmd.Value.Exe + "\" " + cmd.Value.Args);
                Animate(true, ref icon);
                VsStatusBar.SetText(BuildStrings.UbtProgress);
                L.Info("ubt.projectFiles.start", null, ("exe", cmd.Value.Exe), ("args", cmd.Value.Args));
                using (var t = L.Time("ubt.projectFiles"))
                {
                    int code = await RunAsync(cmd.Value.Exe, cmd.Value.Args, Path.GetDirectoryName(uproject), (line, err) =>
                    {
                        tail.Add(line, err);
                        pane?.WriteRaw(line);
                    }).ConfigureAwait(false);
                    if (code != 0) t.Fail();
                    L.Info("ubt.projectFiles.done", null, ("exitCode", code), ("lines", tail.Lines), ("sec", Math.Round(sw.Elapsed.TotalSeconds, 1)));
                    if (code == 0) pane?.WriteRaw(BuildStrings.UbtPaneDone(sw.Elapsed.TotalSeconds));
                    else
                    {
                        pane?.WriteRaw(BuildStrings.UbtPaneFailed(code));
                        var last = tail.FailureLines();
                        if (last.Count > 0)
                        {
                            pane?.WriteRaw(BuildStrings.UbtPaneLastErrors);
                            foreach (var line in last) pane?.WriteRaw("  " + line);
                        }
                        L.Warn("ubt.projectFiles.failed", "Generating project files failed", ("exitCode", code), ("lastLines", last));
                    }
                    VsStatusBar.SetText(code == 0 ? FeatureStrings.ProjectFilesGenerated : FeatureStrings.ProjectFilesFailed);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _generating, 0);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Animate(false, ref icon);
                RefreshCommands();
            }
        }

        private static void Animate(bool on, ref object icon)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar bar) bar.Animation(on ? 1 : 0, ref icon);
            }
            catch (Exception ex)
            {
                L.Debug("ubt.statusbar.animation.failed", ex.Message);
            }
        }

        /// <summary>Re-query command state now (enabled / disabled while UBT runs).</summary>
        private static void RefreshCommands()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell) shell.UpdateCommandUI(0);
        }

        private static void VsEditor_SaveAll() => Navigation.VsEditor.TryExecute("File.SaveAll");

        private static Task<int> RunAsync(string exe, string args, string workDir, Action<string, bool> onLine = null)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, args)
                    {
                        WorkingDirectory = workDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                    EnableRaisingEvents = true,
                };
                // UBT output goes to the log file (ubt.out / ubt.err) and, verbatim, to the UEToVS output pane (onLine).
                p.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    Ubt.Info("ubt.out", e.Data);
                    onLine?.Invoke(e.Data, false);
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    Ubt.Warn("ubt.err", e.Data);
                    onLine?.Invoke(e.Data, true);
                };
                p.Exited += (s, e) =>
                {
                    int code;
                    try { p.WaitForExit(); code = p.ExitCode; } catch (Exception) { code = -1; }
                    tcs.TrySetResult(code);
                    p.Dispose();
                };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                L.Error("ubt.start.failed", "Starting UnrealBuildTool failed", ex, ("exe", exe));
                onLine?.Invoke(ex.Message, true);
                tcs.TrySetResult(-1);
            }
            return tcs.Task;
        }

        /// <summary>Copies the bundled UEToVSLink plugin (&lt;extension&gt;/EditorPlugin/UEToVSLink) into &lt;Project&gt;/Plugins.</summary>
        public static async Task InstallEditorPluginAsync(HostPackage package)
        {
            // v0.4.5: same code path as the settings card (update in place, lock check, link.plugin.install log).
            string text = await Link.LinkPluginManager.InstallAsync(package, "command");
            if (text != null) VsStatusBar.SetText(text);
        }
    }
}
