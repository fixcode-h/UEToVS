using System;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>
    /// "Latest request wins": <see cref="Begin"/> cancels the previous request's token and returns a ticket; a result is
    /// applied only while its ticket is still current. Thread-safe. Used by reference highlighting and the navigation bar
    /// so a slow answer for an old caret never overwrites a newer one.
    /// </summary>
    public sealed class LatestRequestGate : IDisposable
    {
        private readonly object _gate = new object();
        private CancellationTokenSource _cts;
        private long _generation;

        public long Generation => Interlocked.Read(ref _generation);

        /// <summary>Starts a new request: cancels the previous one. Returns its generation and token.</summary>
        public (long Generation, CancellationToken Token) Begin(CancellationToken outer = default)
        {
            lock (_gate)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = outer.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(outer) : new CancellationTokenSource();
                long g = Interlocked.Increment(ref _generation);
                return (g, _cts.Token);
            }
        }

        public bool IsCurrent(long generation) => Interlocked.Read(ref _generation) == generation;

        /// <summary>Cancels the in-flight request (caret left the document, feature turned off).</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                Interlocked.Increment(ref _generation);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    /// <summary>
    /// Trailing-edge debouncer: <see cref="Trigger"/> (re)starts the delay; the action runs once the input was quiet for
    /// <see cref="Delay"/>, on the thread pool, with a token that is canceled by the next trigger. The host marshals to the
    /// UI thread itself. Exceptions from the action are reported through <see cref="Error"/> (never unobserved).
    /// </summary>
    public sealed class Debouncer : IDisposable
    {
        private readonly Func<CancellationToken, Task> _action;
        private readonly LatestRequestGate _gate = new LatestRequestGate();
        private bool _disposed;

        public Debouncer(TimeSpan delay, Func<CancellationToken, Task> action)
        {
            Delay = delay;
            _action = action ?? throw new ArgumentNullException(nameof(action));
        }

        public TimeSpan Delay { get; set; }

        public event Action<Exception> Error;

        /// <summary>Number of times the action actually started (for logs / tests).</summary>
        public int Runs => _runs;

        private int _runs;

        public void Trigger()
        {
            if (_disposed) return;
            var (gen, ct) = _gate.Begin();
            _ = RunAsync(gen, ct);
        }

        public void Cancel() => _gate.Cancel();

        private async Task RunAsync(long gen, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Delay, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || !_gate.IsCurrent(gen)) return;
                Interlocked.Increment(ref _runs);
                await _action(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _gate.Dispose();
        }
    }
}
