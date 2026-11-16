using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Host.Logic.Debugging
{
    public enum DebugMode { Design, Run, Break }

    /// <summary>What a wait ended on.</summary>
    public static class WaitOutcome
    {
        /// <summary>The debuggee stopped (breakpoint, step done, exception, pause).</summary>
        public const string Break = "break";
        /// <summary>Debugging ended (process exited or was stopped).</summary>
        public const string Exited = "exited";
        /// <summary>A build without launch finished successfully.</summary>
        public const string BuildDone = "buildDone";
        /// <summary>The build failed (for F5 too: nothing was launched).</summary>
        public const string BuildFailed = "buildFailed";
        /// <summary>Nothing happened within the timeout (still running / building).</summary>
        public const string Timeout = "timeout";
        /// <summary>Nothing to wait for: not debugging, not building, no launch pending.</summary>
        public const string Idle = "idle";
    }

    /// <summary>
    /// Debugger state as seen through VS's debugger and build events (fed by the host on the UI thread), plus the waiters
    /// of <c>wait</c>. A wait never blocks the UI thread: it is a task completed by the next relevant event.
    /// <para>"Launch pending" covers the gap after F5 while VS builds and starts the process: the mode is still Design then,
    /// and a wait must keep waiting instead of answering "idle".</para>
    /// </summary>
    public sealed class BreakTracker
    {
        private readonly object _gate = new object();
        private readonly List<TaskCompletionSource<string>> _waiters = new List<TaskCompletionSource<string>>();

        /// <summary>Raised after every state change (outside the lock; on the caller's thread, the UI thread in VS).</summary>
        public event EventHandler Changed;

        public DebugMode Mode { get; private set; } = DebugMode.Design;
        public bool Building { get; private set; }
        public bool LaunchPending { get; private set; }
        public bool? LastBuildSucceeded { get; private set; }

        /// <summary>Why the debuggee last stopped / ended ("breakpoint", "step", "exception", "userBreak", "exited"…).</summary>
        public string LastReason { get; private set; }

        /// <summary>Counts every break; a stack read is tagged with it so callers can tell a stale answer.</summary>
        public long BreakCount { get; private set; }

        /// <summary>Seeds the mode when the server starts while VS is already debugging.</summary>
        public void Reset(DebugMode mode)
        {
            lock (_gate) Mode = mode;
        }

        public void OnLaunchRequested()
        {
            lock (_gate) LaunchPending = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>stop: whatever launch was pending is off (also clears one that never started, e.g. a launch error dialog).</summary>
        public void OnStopRequested()
        {
            lock (_gate)
            {
                LaunchPending = false;
                if (Mode == DebugMode.Design && !Building) CompleteAll(WaitOutcome.Exited);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void OnBuildBegin()
        {
            lock (_gate)
            {
                Building = true;
                LastBuildSucceeded = null;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void OnBuildDone(bool success)
        {
            lock (_gate)
            {
                Building = false;
                LastBuildSucceeded = success;
                if (!success)
                {
                    // F5 does not launch after a failed build.
                    LaunchPending = false;
                    CompleteAll(WaitOutcome.BuildFailed);
                }
                else if (!LaunchPending && Mode == DebugMode.Design)
                {
                    CompleteAll(WaitOutcome.BuildDone);
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void OnRun()
        {
            lock (_gate)
            {
                Mode = DebugMode.Run;
                LaunchPending = false;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void OnBreak(string reason)
        {
            lock (_gate)
            {
                Mode = DebugMode.Break;
                LaunchPending = false;
                LastReason = reason;
                BreakCount++;
                CompleteAll(WaitOutcome.Break);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void OnDesign(string reason)
        {
            lock (_gate)
            {
                bool wasDebugging = Mode != DebugMode.Design;
                Mode = DebugMode.Design;
                if (wasDebugging) LastReason = reason ?? "exited";
                // Restart ends the old session first: a launch still pending means the new one is on its way.
                if (!Building && !LaunchPending) CompleteAll(WaitOutcome.Exited);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Waits for the next break / end of debugging / end of a build. Answers at once when already stopped (a breakpoint
        /// hit between two calls must not be missed) or when there is nothing to wait for.
        /// </summary>
        public async Task<string> WaitAsync(int timeoutMs, CancellationToken ct)
        {
            TaskCompletionSource<string> tcs;
            lock (_gate)
            {
                if (Mode == DebugMode.Break) return WaitOutcome.Break;
                if (Mode == DebugMode.Design && !Building && !LaunchPending) return WaitOutcome.Idle;
                tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(tcs);
            }
            try
            {
                using (var timeout = new CancellationTokenSource(Math.Max(0, timeoutMs)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct))
                using (linked.Token.Register(() => tcs.TrySetResult(WaitOutcome.Timeout)))
                {
                    string outcome = await tcs.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return outcome;
                }
            }
            finally
            {
                lock (_gate) _waiters.Remove(tcs);
            }
        }

        public int WaiterCount
        {
            get { lock (_gate) return _waiters.Count; }
        }

        private void CompleteAll(string outcome)
        {
            foreach (var w in _waiters) w.TrySetResult(outcome);
            _waiters.Clear();
        }
    }
}
