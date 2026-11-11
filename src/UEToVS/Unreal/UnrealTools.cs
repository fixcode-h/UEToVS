using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Protocol;
using UEToVS.UI;

namespace UEToVS.Unreal
{
    /// <summary>Generate project files, Live Coding and asset opening.</summary>
    internal static class UnrealTools
    {
        public static void GenerateProjectFiles(UEToVSPackage pkg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string uproject = pkg.ProjectFile;
            string engine = pkg.EngineRoot;
            if (uproject == null || engine == null)
            {
                OutputLog.Status("UEToVS: 没有找到 .uproject 或引擎目录");
                return;
            }
            string ubt = Path.Combine(engine, "Engine", "Binaries", "DotNET", "UnrealBuildTool", "UnrealBuildTool.exe");
            string args;
            string exe;
            if (File.Exists(ubt))
            {
                exe = ubt;
                args = $"-projectfiles -project=\"{uproject}\" -game -engine -progress";
            }
            else
            {
                exe = Path.Combine(engine, "Engine", "Build", "BatchFiles", "Build.bat");
                args = $"-projectfiles -project=\"{uproject}\" -game -engine -progress";
            }
            if (!File.Exists(exe))
            {
                OutputLog.Status("UEToVS: 没有找到 UnrealBuildTool");
                return;
            }
            OutputLog.Activate();
            OutputLog.Write("> " + exe + " " + args);
            OutputLog.Status("UEToVS: 正在生成工程文件…");
            _ = pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                int code = await RunAsync(exe, args, Path.GetDirectoryName(uproject));
                await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                OutputLog.Status(code == 0 ? "UEToVS: 工程文件已生成，VS 会提示重新加载解决方案" : "UEToVS: 生成工程文件失败，详见输出窗口");
            });
        }

        private static System.Threading.Tasks.Task<int> RunAsync(string exe, string args, string workDir)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<int>();
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
                p.OutputDataReceived += (s, e) => { if (e.Data != null) OutputLog.Write(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) OutputLog.Write(e.Data); };
                p.Exited += (s, e) => { tcs.TrySetResult(p.ExitCode); p.Dispose(); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                OutputLog.Write("Starting " + exe + " failed: " + ex.Message);
                tcs.TrySetResult(-1);
            }
            return tcs.Task;
        }

        public static void LiveCoding(UEToVSPackage pkg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsHelpers.TryExecute("File.SaveAll");
            OutputLog.Status("UEToVS: 正在请求编辑器执行 Live Coding…");
            _ = pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                var (ok, message) = await pkg.EditorLink.LiveCodingAsync();
                await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ok) OutputLog.Status("UEToVS: Live Coding 已开始" + (string.IsNullOrEmpty(message) ? "" : "：" + message));
                else if (message != null) OutputLog.Status("UEToVS: Live Coding 失败：" + message);
                else OutputLog.Status("UEToVS: 连不上编辑器。请先打开 Unreal Editor 并启用 UEToVSLink 插件（见 README）");
            });
        }

        public static void OpenAsset(UEToVSPackage pkg, AssetDto asset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                var (ok, message) = await pkg.EditorLink.OpenAssetAsync(asset.ObjectPath);
                await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ok)
                {
                    OutputLog.Status("UEToVS: 已在编辑器中打开 " + asset.Name);
                    return;
                }
                // No editor: copy the object path (paste into the Content Browser) and show the file.
                try
                {
                    Clipboard.SetText(asset.ObjectPath ?? asset.FilePath);
                }
                catch (Exception)
                {
                }
                if (File.Exists(asset.FilePath))
                {
                    try
                    {
                        Process.Start("explorer.exe", "/select,\"" + asset.FilePath + "\"");
                    }
                    catch (Exception)
                    {
                    }
                }
                OutputLog.Status(message != null
                    ? "UEToVS: 编辑器无法打开资产：" + message
                    : "UEToVS: 编辑器未连接，已复制资产路径 " + asset.ObjectPath);
            });
        }
    }
}
