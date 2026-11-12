using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Input;

namespace UEToVS.Input
{
    /// <summary>
    /// Thread-local Win32 keyboard and mouse hooks on the VS UI thread. Feeds the double-Shift detector and
    /// turns mouse XButton1/XButton2 into Back/Forward. VS's command system cannot bind a modifier-only gesture,
    /// so this is the only way to get Rider's double Shift.
    /// </summary>
    internal sealed class KeyboardMonitor : IDisposable
    {
        private const int WH_KEYBOARD = 2;
        private const int WH_MOUSE = 7;
        private const int HC_ACTION = 0;
        private const int VK_SHIFT = 0x10;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;
        private const int WM_NCXBUTTONDOWN = 0x00AB;
        private const int WM_NCXBUTTONUP = 0x00AC;
        private const int GCS_COMPSTR = 0x0008;
        private const int RightShiftScanCode = 0x36;

        private readonly DoubleShiftDetector _detector;
        private readonly HookProc _keyboardProc;
        private readonly HookProc _mouseProc;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;

        public KeyboardMonitor(DoubleShiftDetector detector)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _detector = detector;
            _detector.IsImeComposing = IsImeComposing;
            _keyboardProc = KeyboardHook;
            _mouseProc = MouseHook;
            uint thread = GetCurrentThreadId();
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD, _keyboardProc, IntPtr.Zero, thread);
            _mouseHook = SetWindowsHookEx(WH_MOUSE, _mouseProc, IntPtr.Zero, thread);
        }

        /// <summary>Raised for mouse XButton1 (back = true) / XButton2 (back = false). Return true to swallow.</summary>
        public Func<bool, bool> MouseNavigate { get; set; }

        public bool MouseButtonsEnabled { get; set; } = true;

        private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == HC_ACTION)
            {
                try
                {
                    long flags = lParam.ToInt64();
                    bool isUp = (flags & 0x80000000L) != 0;
                    bool wasDown = (flags & 0x40000000L) != 0;
                    int scan = (int)((flags >> 16) & 0xFF);
                    int vk = wParam.ToInt32();
                    long now = _clock.ElapsedMilliseconds;
                    if (vk == VK_SHIFT || vk == 0xA0 || vk == 0xA1)
                    {
                        var side = vk == 0xA1 || scan == RightShiftScanCode ? ShiftSide.Right : ShiftSide.Left;
                        if (isUp) _detector.OnShiftUp(side, now);
                        else _detector.OnShiftDown(side, now, wasDown);
                    }
                    else if (!isUp)
                    {
                        _detector.OnOtherInput();
                    }
                }
                catch (Exception)
                {
                    // never let an exception escape a hook
                }
            }
            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == HC_ACTION)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    switch (msg)
                    {
                        case WM_LBUTTONDOWN:
                        case WM_RBUTTONDOWN:
                        case WM_MBUTTONDOWN:
                            _detector.OnOtherInput();
                            break;
                        case WM_XBUTTONDOWN:
                        case WM_NCXBUTTONDOWN:
                        case WM_XBUTTONUP:
                        case WM_NCXBUTTONUP:
                            _detector.OnOtherInput();
                            if (MouseButtonsEnabled && MouseNavigate != null)
                            {
                                var info = Marshal.PtrToStructure<MOUSEHOOKSTRUCTEX>(lParam);
                                int button = (int)((info.mouseData >> 16) & 0xFFFF);
                                bool up = msg == WM_XBUTTONUP || msg == WM_NCXBUTTONUP;
                                if (button == 1 || button == 2)
                                {
                                    // act on release, swallow both so VS does not also navigate (WM_APPCOMMAND)
                                    if (up) MouseNavigate(button == 1);
                                    return new IntPtr(1);
                                }
                            }
                            break;
                    }
                }
                catch (Exception)
                {
                }
            }
            return CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        private static bool IsImeComposing()
        {
            IntPtr hwnd = GetFocus();
            if (hwnd == IntPtr.Zero) return false;
            IntPtr ctx = ImmGetContext(hwnd);
            if (ctx == IntPtr.Zero) return false;
            try
            {
                return ImmGetCompositionStringW(ctx, GCS_COMPSTR, IntPtr.Zero, 0) > 0;
            }
            finally
            {
                ImmReleaseContext(hwnd, ctx);
            }
        }

        public void Dispose()
        {
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = _mouseHook = IntPtr.Zero;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEHOOKSTRUCTEX
        {
            public POINT pt;
            public IntPtr hwnd;
            public uint wHitTestCode;
            public IntPtr dwExtraInfo;
            public uint mouseData;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        private static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex, IntPtr lpBuf, int dwBufLen);
    }
}
