using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Engine
{
    /// <summary>
    /// Automatic engine restart policy (§10.2): exponential backoff, at most <see cref="MaxRestarts"/> automatic restarts
    /// within a sliding <see cref="Window"/>; after that the host stops restarting and tells the user (banner).
    /// A manual restart (command) resets the budget. Thread-safe.
    /// </summary>
    public sealed class RestartPolicy
    {
        private readonly object _gate = new object();
        private readonly Queue<DateTime> _restarts = new Queue<DateTime>();

        public RestartPolicy(int maxRestarts = 3, TimeSpan? window = null, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
        {
            if (maxRestarts < 0) throw new ArgumentOutOfRangeException(nameof(maxRestarts));
            MaxRestarts = maxRestarts;
            Window = window ?? TimeSpan.FromMinutes(10);
            BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
            MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
        }

        public int MaxRestarts { get; }
        public TimeSpan Window { get; }
        public TimeSpan BaseDelay { get; }
        public TimeSpan MaxDelay { get; }

        /// <summary>Restarts counted in the current window (as of the last call).</summary>
        public int RecentRestarts
        {
            get { lock (_gate) return _restarts.Count; }
        }

        /// <summary>
        /// Called after an unexpected engine exit/disconnect. Returns true with the delay to wait before restarting, or
        /// false when the budget is exhausted (the caller must give up and inform the user).
        /// </summary>
        public bool TryScheduleRestart(DateTime nowUtc, out TimeSpan delay, out int attempt)
        {
            lock (_gate)
            {
                Prune(nowUtc);
                attempt = _restarts.Count + 1;
                if (_restarts.Count >= MaxRestarts)
                {
                    delay = TimeSpan.Zero;
                    return false;
                }
                _restarts.Enqueue(nowUtc);
                delay = DelayFor(attempt);
                return true;
            }
        }

        /// <summary>Backoff for the n-th restart in the window (1-based): base * 2^(n-1), capped.</summary>
        public TimeSpan DelayFor(int attempt)
        {
            if (attempt < 1) attempt = 1;
            double ms = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 20));
            return TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelay.TotalMilliseconds));
        }

        /// <summary>Manual restart by the user: forget automatic restarts.</summary>
        public void Reset()
        {
            lock (_gate) _restarts.Clear();
        }

        private void Prune(DateTime now)
        {
            while (_restarts.Count > 0 && now - _restarts.Peek() >= Window) _restarts.Dequeue();
        }
    }
}
