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
    /// 只在 UI 线程的未处理异常里认出这一种（异常类型 + 栈里有 UELogFontAndColorsUpdateHandler），记一条说明。
    /// v0.9.7 起不再标成已处理：0.9.6 拦下第一次后，窗口的 Lazy 初始化把同一个异常缓存住，下一次显示又抛出来且不经过这里，
    /// VS 照样退出（用户 2026-11-19 的日志），拦了反而让人以为修好了。根治靠 FontService 给这个类别写上字体（VsFontStorage.Set）。
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
                if (++_caught <= 3)
                    L.Warn("vs.ueLog.fontCrash",
                           "VS 的 Unreal Engine 日志窗口读不到字体名，VS 会因此退出；UEToVS 启动时会给它写字体（设置 > 外观 > 字体 > Unreal Engine 日志），" +
                           "看本日志里 font.vs.* 的记录有没有写成功；也可以在 工具 > 选项 > 环境 > 字体和颜色 里给它选一个字体",
                           e.Exception, ("count", _caught));
            }
            catch (Exception) { /* 这里再抛出去就真的带崩 VS */ }
        }
    }
}
