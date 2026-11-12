using System;
using System.Diagnostics;
using System.Windows.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Commands;

namespace UEToVS.Host.Shell
{
    /// <summary>
    /// Double Shift → Search Everywhere (Rider/IntelliJ style). VS key bindings can't express "Shift, Shift", so the UI
    /// thread's message pump is observed through <see cref="ComponentDispatcher.ThreadPreprocessMessage"/> (never
    /// handled/swallowed: other keys behave exactly as before) and fed into the pure <see cref="DoubleShiftDetector"/>.
    /// Active only while the setting 常规 > 键盘 > 双击 Shift is on and the Search Everywhere action is registered.
    /// The hook body is a few integer compares, well under the 16 ms UI budget.
    /// </summary>
    internal sealed class DoubleShiftMonitor : IDisposable
    {
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, VK_SHIFT = 0x10;
        private static readonly Logger L = Log.For("Keyboard");
        private readonly DoubleShiftDetector _detector = new DoubleShiftDetector();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Action _fire;
        private bool _hooked;

        public DoubleShiftMonitor(Action fire)
        {
            _fire = fire;
        }

        /// <summary>Hooks / unhooks the message filter (UI thread).</summary>
        public void SetEnabled(bool enabled)
        {
            if (enabled == _hooked) return;
            if (enabled) ComponentDispatcher.ThreadPreprocessMessage += OnMessage;
            else ComponentDispatcher.ThreadPreprocessMessage -= OnMessage;
            _hooked = enabled;
            _detector.Reset();
            L.Info("doubleShift.enabled", null, ("enabled", enabled));
        }

        private void OnMessage(ref MSG msg, ref bool handled)
        {
            int m = msg.message;
            if (m != WM_KEYDOWN && m != WM_KEYUP && m != WM_SYSKEYDOWN && m != WM_SYSKEYUP) return;
            try
            {
                bool isShift = (int)msg.wParam == VK_SHIFT;
                long now = _clock.ElapsedMilliseconds;
                if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
                {
                    bool repeat = ((long)msg.lParam & (1L << 30)) != 0; // previous key state: auto-repeat
                    if (_detector.OnKeyDown(isShift, repeat, now))
                    {
                        L.Info("doubleShift.detected", null);
                        _fire();
                    }
                }
                else _detector.OnKeyUp(isShift, now);
            }
            catch (Exception ex)
            {
                L.Error("doubleShift.failed", "Double Shift detection failed", ex);
            }
        }

        public void Dispose() => SetEnabled(false);
    }
}
