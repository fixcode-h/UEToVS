using System;

namespace UEToVS.Core.Input
{
    public enum ShiftSide
    {
        Left,
        Right,
    }

    /// <summary>
    /// Detects a "double Shift" gesture: Shift tapped twice with nothing else in between.
    /// Feed it raw key events from the UI thread's message loop. Pure logic, no Win32 dependency.
    /// </summary>
    public sealed class DoubleShiftDetector
    {
        private bool _shiftDown;
        private ShiftSide _downSide;
        private long _downTime;
        private bool _dirty;           // another key/mouse happened while Shift was down
        private long? _firstTapTime;   // key-down time of the first clean tap

        /// <summary>Maximum time between the two key presses, in ms.</summary>
        public int IntervalMs { get; set; } = 300;

        /// <summary>A tap held longer than this is not a tap.</summary>
        public int MaxHoldMs { get; set; } = 400;

        public bool LeftShiftOnly { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>Set by the host while an IME composition is in progress.</summary>
        public Func<bool> IsImeComposing { get; set; }

        public event EventHandler Triggered;

        public void OnShiftDown(ShiftSide side, long timeMs, bool isRepeat)
        {
            if (isRepeat || _shiftDown) return;
            if (LeftShiftOnly && side != ShiftSide.Left)
            {
                Reset();
                return;
            }
            _shiftDown = true;
            _downSide = side;
            _downTime = timeMs;
            _dirty = false;
            if (_firstTapTime.HasValue && timeMs - _firstTapTime.Value > IntervalMs) _firstTapTime = null;
        }

        /// <summary>Returns true when this release completes a double Shift.</summary>
        public bool OnShiftUp(ShiftSide side, long timeMs)
        {
            if (!_shiftDown) return false;
            _shiftDown = false;
            bool clean = !_dirty && timeMs - _downTime <= MaxHoldMs && side == _downSide;
            if (!clean || !Enabled || (IsImeComposing?.Invoke() ?? false))
            {
                _firstTapTime = null;
                return false;
            }

            if (_firstTapTime.HasValue && _downTime - _firstTapTime.Value <= IntervalMs)
            {
                _firstTapTime = null;
                Triggered?.Invoke(this, EventArgs.Empty);
                return true;
            }

            _firstTapTime = _downTime;
            return false;
        }

        /// <summary>Any non-Shift key press or mouse button breaks the gesture.</summary>
        public void OnOtherInput()
        {
            if (_shiftDown) _dirty = true;
            _firstTapTime = null;
        }

        public void Reset()
        {
            _shiftDown = false;
            _dirty = false;
            _firstTapTime = null;
        }
    }
}
