using System;
using System.Runtime.InteropServices;
using UEToVS.UI.Logic;

namespace UEToVS.UI.Shells
{
    /// <summary>Win32 interop for popup placement (device pixels, per-monitor DPI) and native chrome.</summary>
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public RectD ToRectD() => new RectD(Left, Top, Right - Left, Bottom - Top);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        public const int MONITOR_DEFAULTTONEAREST = 2;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;
        public const int GCL_STYLE = -26;
        public const int CS_DROPSHADOW = 0x00020000;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
        private static extern int GetClassLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
        private static extern IntPtr SetClassLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
        private static extern int SetClassLong32(IntPtr hwnd, int index, int value);

        public static long GetClassStyle(IntPtr hwnd) =>
            IntPtr.Size == 8 ? GetClassLongPtr64(hwnd, GCL_STYLE).ToInt64() : GetClassLong32(hwnd, GCL_STYLE);

        public static void SetClassStyle(IntPtr hwnd, long style)
        {
            if (IntPtr.Size == 8) SetClassLongPtr64(hwnd, GCL_STYLE, new IntPtr(style));
            else SetClassLong32(hwnd, GCL_STYLE, (int)style);
        }

        /// <summary>Work area (device px) of the monitor nearest to a point; null on failure.</summary>
        public static RectD? WorkAreaAt(double x, double y, out IntPtr monitor)
        {
            monitor = MonitorFromPoint(new POINT { X = (int)x, Y = (int)y }, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return null;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(monitor, ref mi)) return null;
            return mi.rcWork.ToRectD();
        }

        /// <summary>DPI scale of a monitor (1.0 = 96 DPI); null when unavailable (pre-8.1).</summary>
        public static double? MonitorScale(IntPtr monitor)
        {
            try
            {
                if (GetDpiForMonitor(monitor, 0, out uint dx, out _) == 0 && dx > 0) return dx / 96.0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return null;
        }
    }
}
