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
        public static Task InstallEditorPluginAsync(HostPackage package)
        {
            string projectRoot = EditorLinkService.Instance?.ProjectRoot;
            if (projectRoot == null)
            {
                VsStatusBar.SetText(FeatureStrings.NoUProject);
                return Task.CompletedTask;
            }
            string source = Path.Combine(HostPackage.ExtensionDirectory, "EditorPlugin", "UEToVSLink");
            string target = Path.Combine(projectRoot, "Plugins", "UEToVSLink");
            if (!Directory.Exists(source))
            {
                L.Error("link.plugin.sourceMissing", "Plugin source missing in the extension", null, ("source", source));
                VsStatusBar.SetText(FeatureStrings.PluginSourceMissing(source));
                return Task.CompletedTask;
            }
            return Task.Run(() =>
            {
                try
                {
                    int n = 0;
                    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                    {
                        string dest = Path.Combine(target, file.Substring(source.Length).TrimStart('\\', '/'));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(file, dest, true);
                        n++;
                    }
                    L.Info("link.plugin.installed", null, ("target", target), ("files", n));
                    VsStatusBar.SetText(FeatureStrings.PluginInstalled);
                }
                catch (Exception ex)
                {
                    L.Error("link.plugin.install.failed", "Installing UEToVSLink failed", ex, ("target", target));
                    VsStatusBar.SetText(FeatureStrings.PluginInstallFailed);
                }
            });
        }
    }
}
