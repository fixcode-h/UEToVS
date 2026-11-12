using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
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
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsEditor_SaveAll();
            VsStatusBar.SetText(FeatureStrings.GeneratingProjectFiles);
            L.Info("ubt.projectFiles.start", null, ("exe", cmd.Value.Exe), ("args", cmd.Value.Args));
            using (var t = L.Time("ubt.projectFiles"))
            {
                int code = await RunAsync(cmd.Value.Exe, cmd.Value.Args, Path.GetDirectoryName(uproject)).ConfigureAwait(false);
                if (code != 0) t.Fail();
                L.Info("ubt.projectFiles.done", null, ("exitCode", code));
                VsStatusBar.SetText(code == 0 ? FeatureStrings.ProjectFilesGenerated : FeatureStrings.ProjectFilesFailed);
            }
        }

        private static void VsEditor_SaveAll() => Navigation.VsEditor.TryExecute("File.SaveAll");

        private static Task<int> RunAsync(string exe, string args, string workDir)
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
                // UBT output goes to the log (Info → also the UEToVS output pane).
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Ubt.Info("ubt.out", e.Data); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Ubt.Warn("ubt.err", e.Data); };
                p.Exited += (s, e) =>
                {
                    int code;
                    try { code = p.ExitCode; } catch (Exception) { code = -1; }
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
