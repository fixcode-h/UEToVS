namespace UEToVS.Host.Logic.Commands
{
    /// <summary>
    /// Rider-style "press Shift twice" detector (Search Everywhere). Feed it raw key events with a monotonic clock:
    /// fires on the second Shift press when both presses were short taps of Shift alone and the second came within
    /// <see cref="MaxGapMs"/> of the first release. Any other key (or a held Shift, or auto-repeat) resets it, so
    /// Shift+letter typing never triggers.
    /// </summary>
    public sealed class DoubleShiftDetector
    {
        public int MaxGapMs { get; set; } = 400;
        public int MaxTapMs { get; set; } = 350;

        private enum State { Idle, FirstDown, FirstUp }

        private State _state;
        private long _t;

        /// <summary>Returns true when this event completes a double Shift.</summary>
        public bool OnKeyDown(bool isShift, bool isRepeat, long nowMs)
        {
            if (!isShift) { _state = State.Idle; return false; }
            if (isRepeat) return false;
            switch (_state)
            {
                case State.FirstUp:
                    if (nowMs - _t <= MaxGapMs)
                    {
                        _state = State.Idle;
                        return true;
                    }
                    _state = State.FirstDown;
                    _t = nowMs;
                    return false;
                default:
                    _state = State.FirstDown;
                    _t = nowMs;
                    return false;
            }
        }

        public void OnKeyUp(bool isShift, long nowMs)
        {
            if (!isShift) { _state = State.Idle; return; }
            if (_state == State.FirstDown && nowMs - _t <= MaxTapMs)
            {
                _state = State.FirstUp;
                _t = nowMs;
            }
            else _state = State.Idle;
        }

        public void Reset() => _state = State.Idle;
    }
}
