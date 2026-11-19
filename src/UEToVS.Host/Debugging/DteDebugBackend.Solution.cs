using System;
using System.IO;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using UEToVS.Host.Logic.Debugging;

namespace UEToVS.Host.Debugging
{
    internal sealed partial class DteDebugBackend
    {
        /// <summary>
        /// solution.open（v0.9.5）：Agent 面板「用 VS 打开这个项目」选了「在这个 VS 里打开」时，让本 VS 换成另一个解决方案。
        /// 换解决方案会关掉当前的解决方案，连带关掉这个管道，所以先回应答、再在 UI 线程上切换：
        /// 关解决方案走 VS 自己的 File.CloseSolution 命令，有没保存的文件照常弹 VS 的保存提示，用户点取消就不换。
        /// 新解决方案打开后由它自己的 UEToVS 写发现文件，调用方按新解决方案的发现文件重连。
        /// </summary>
        private JObject OpenSolution(DebugArgs a)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string path = a.RequireStr("path");
            if (!path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                throw new DebugControlException($"path 要是解决方案文件（.sln）：{path}");
            if (!File.Exists(path)) throw new DebugControlException($"解决方案不存在：{path}");
            string current = _dte.Solution?.FullName;
            if (!string.IsNullOrEmpty(current) && string.Equals(Path.GetFullPath(current), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                return new JObject { ["already"] = true, ["solution"] = current };
            var mode = CurrentMode;
            if (mode != DebugMode.Design)
                throw new DebugControlException($"这个 VS 正在调试（{ModeText(mode)}）：先在 VS 里停止调试，或者新开一个 VS 打开");
            if (Tracker.Building) throw new DebugControlException("这个 VS 正在编译：等编译完再换解决方案，或者新开一个 VS 打开");

            var dte = _dte;
            var jtf = _package.JoinableTaskFactory;
            _ = jtf.RunAsync(async () =>
            {
                // 让应答先写回管道：切换会关掉这个管道
                await System.Threading.Tasks.Task.Delay(300);
                await jtf.SwitchToMainThreadAsync();
                try
                {
                    try { dte.MainWindow?.Activate(); } catch (Exception) { /* 拿不到主窗口时照样切换 */ }
                    if (dte.Solution.IsOpen)
                    {
                        dte.ExecuteCommand("File.CloseSolution");
                        if (dte.Solution.IsOpen)
                        {
                            L.Info("debug.solution.open.cancelled", null, ("path", path));
                            return;
                        }
                    }
                    dte.Solution.Open(path);
                    L.Info("debug.solution.open", null, ("path", path));
                }
                catch (Exception ex)
                {
                    L.Warn("debug.solution.open.failed", null, ex, ("path", path));
                }
            });
            return new JObject
            {
                ["switching"] = true,
                ["from"] = current,
                ["solution"] = path,
                ["hint"] = "VS 正在关掉当前解决方案并打开新的：有没保存的文件时 VS 会弹保存提示；新解决方案加载完后它的发现文件才出现，按新解决方案重连",
            };
        }
    }
}
