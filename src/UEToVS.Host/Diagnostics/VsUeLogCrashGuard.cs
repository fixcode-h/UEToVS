using System;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Diagnostics
{
    /// <summary>
    /// v0.9.6：VS 自带的「Unreal Engine 日志」窗口（Microsoft.VisualStudio.VC.UnrealEngineTools 的 UELog）在 F5 启动 UE、
    /// 窗口第一次显示时读「字体和颜色」里自己的字体，拿到空的字体名就 <c>new Typeface(null)</c> 抛 ArgumentNullException，
    /// 异常没人接，整个 VS 退出（用户 2026-11-19 的日志：每次编译完开始调试就崩）。这是 VS 的代码，UEToVS 改不了它，
    /// 只在 UI 线程的未处理异常里认出这一种（异常类型 + 栈里有 UELogFontAndColorsUpdateHandler），标成已处理，
    /// 让 VS 活下来：代价是那个日志窗口这次是空的。别的异常一律不碰，照常交给 VS。
    /// </summary>
    internal static class VsUeLogCrashGuard
    {
        private static readonly Logger L = Log.For("Diagnostics");
        private const string Signature = "UELogFontAndColorsUpdateHandler";
        private static bool _installed;
        private static int _caught;

        public static void Install()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_installed) return;
            _installed = true;
            Dispatcher.CurrentDispatcher.UnhandledException += OnUnhandled;
        }

        /// <summary>只认 VS UE 日志窗口取字体失败这一种（单测用）。</summary>
        internal static bool IsKnown(Exception ex) =>
            ex is ArgumentNullException && (ex.StackTrace ?? "").IndexOf(Signature, StringComparison.Ordinal) >= 0;

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.Handled || !IsKnown(e.Exception)) return;
                e.Handled = true;
                if (++_caught <= 3)
                    L.Warn("vs.ueLog.fontCrash.suppressed",
                           "VS 的 Unreal Engine 日志窗口读不到字体名（字体和颜色里它的字体是空的），UEToVS 拦下了这个会让 VS 退出的异常；" +
                           "到 工具 > 选项 > 环境 > 字体和颜色 里给它选一个字体（或点“使用默认值”）就不会再出现",
                           e.Exception, ("count", _caught));
            }
            catch (Exception) { /* 这里再抛出去就真的带崩 VS */ }
        }
    }
}
