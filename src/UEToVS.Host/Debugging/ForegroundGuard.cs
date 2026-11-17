using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Debugging;

namespace UEToVS.Host.Debugging
{
    /// <summary>
    /// Agent 调试时不让 VS 抢前台（设置“Agent 调试时不把 VS 拉到前台”，默认开）：VS 调试器一停下就把自己拉到前台，
    /// 这里在它刚被拉起时把前台还给之前的窗口，原来最小化的再最小化回去。什么时候还由 <see cref="FocusPolicy"/> 定。
    /// <para>只能“事后还回去”：被调试进程在前台时 Windows 允许调试器抢前台，LockSetForegroundWindow 也只有前台进程能调，
    /// 所以拦不住，只能在 VS 拿到前台后（这时 VS 有权设前台）马上交回去，会闪一下。</para>
    /// <para>WinEvent 钩子是进程外回调（WINEVENT_OUTOFCONTEXT），在装钩子的 UI 线程的消息循环里调，不会卡别的程序。</para>
    /// </summary>
    internal sealed class ForegroundGuard : IDisposable
    {
        private static readonly Logger L = Log.For("Debug");

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int OBJID_WINDOW = 0;
        private const int SW_SHOWMINNOACTIVATE = 7;

        /// <summary>VS 激活主窗口后通常还要激活源码文档，稍等一下再还，免得刚还回去又被抢走。</summary>
        private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(120);

        private readonly Func<bool> _enabled;
        private readonly uint _pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        private readonly WinEventProc _onEvent; // 委托必须一直被引用，否则被回收后钩子回调会崩
        private IntPtr _foregroundHook;
        private IntPtr _minimizeHook;
        private IntPtr _lastExternal;
        private DispatcherTimer _pending;

        public FocusPolicy Policy { get; } = new FocusPolicy();

        public ForegroundGuard(Func<bool> enabled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _enabled = enabled;
            _onEvent = OnWinEvent;
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onEvent, 0, 0, WINEVENT_OUTOFCONTEXT);
            _minimizeHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _onEvent, _pid, 0, WINEVENT_OUTOFCONTEXT);
            if (_foregroundHook == IntPtr.Zero)
                L.Warn("debug.focus.hook_failed", "Cannot watch the foreground window; VS will come to the front on every break", ("error", Marshal.GetLastWin32Error()));
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && !IsOwn(fg)) _lastExternal = fg;
        }

        /// <summary>调试器进中断模式（DTE 的 OnEnterBreakMode，UI 线程）。</summary>
        public void OnBreak()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_foregroundHook == IntPtr.Zero) return;
            bool enabled = false;
            try { enabled = _enabled(); } catch (Exception) { } // 读不到设置时按 VS 原样（跳到前台）
            var fg = GetForegroundWindow();
            if (Policy.OnBreak(enabled, fg != IntPtr.Zero && IsOwn(fg), DateTime.UtcNow)) ScheduleRestore();
        }

        private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
                var now = DateTime.UtcNow;
                if (evt == EVENT_SYSTEM_MINIMIZEEND)
                {
                    if (hwnd == MainWindow()) Policy.OnUnminimized(now);
                    return;
                }
                bool own = IsOwn(hwnd);
                if (!own) _lastExternal = hwnd;
                if (Policy.OnForeground(own, now)) ScheduleRestore();
            }
            catch (Exception ex)
            {
                L.Warn("debug.focus.event_failed", "Foreground hook callback failed", ex); // 钩子回调里抛出去会带崩 VS
            }
        }

        private void ScheduleRestore()
        {
            if (_pending != null) return;
            _pending = new DispatcherTimer(DispatcherPriority.Background) { Interval = RestoreDelay };
            _pending.Tick += (s, e) =>
            {
                _pending.Stop();
                _pending = null;
                Restore();
            };
            _pending.Start();
        }

        private void Restore()
        {
            try
            {
                var target = _lastExternal;
                if (target == IntPtr.Zero || !IsWindow(target) || IsOwn(target)) return; // 之前的窗口没了：留在 VS
                bool minimized = false;
                var main = MainWindow();
                if (main != IntPtr.Zero && Policy.ShouldMinimize(DateTime.UtcNow) && !IsIconic(main))
                {
                    ShowWindow(main, SW_SHOWMINNOACTIVATE);
                    minimized = true;
                }
                bool ok = SetForegroundWindow(target);
                L.Info("debug.focus.restored", null, ("ok", ok), ("minimized", minimized));
            }
            catch (Exception ex)
            {
                L.Warn("debug.focus.restore_failed", "Could not give the foreground back", ex);
            }
        }

        private static IntPtr MainWindow()
        {
            try { return VsMainWindow.Handle; }
            catch (Exception) { return IntPtr.Zero; } // 不在 UI 线程或 VS 正在关：不做最小化
        }

        private bool IsOwn(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == _pid;
        }

        public void Dispose()
        {
            _pending?.Stop();
            _pending = null;
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            if (_minimizeHook != IntPtr.Zero) UnhookWinEvent(_minimizeHook);
            _foregroundHook = _minimizeHook = IntPtr.Zero;
        }

        private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
