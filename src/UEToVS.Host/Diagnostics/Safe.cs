using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Diagnostics
{
    /// <summary>
    /// Wrappers for every host entry point (command handlers, MEF entry points, event handlers, async tasks) so that no
    /// failure is silent (§10.5): exceptions are logged with module, event and context, then swallowed (an extension must
    /// never take VS down). UI-thread entry points are additionally timed against the 16 ms budget (<see cref="UiThread"/>).
    /// </summary>
    internal static class Safe
    {
        /// <summary>Runs a synchronous entry point; logs and swallows exceptions.</summary>
        public static void Execute(Logger log, string evt, Action action, params (string Key, object Value)[] data)
        {
            using (UiThread.Measure(log, evt, data))
            {
                try
                {
                    action();
                }
                catch (Exception ex) when (!IsCritical(ex))
                {
                    log.Error(evt + ".failed", "Unhandled exception in " + evt, ex, data);
                }
            }
        }

        /// <summary>Runs a synchronous entry point returning a value; logs and returns <paramref name="fallback"/> on failure.</summary>
        public static T Execute<T>(Logger log, string evt, Func<T> func, T fallback, params (string Key, object Value)[] data)
        {
            using (UiThread.Measure(log, evt, data))
            {
                try
                {
                    return func();
                }
                catch (Exception ex) when (!IsCritical(ex))
                {
                    log.Error(evt + ".failed", "Unhandled exception in " + evt, ex, data);
                    return fallback;
                }
            }
        }

        /// <summary>
        /// Starts async work through the JoinableTaskFactory (never blocks the caller). Exceptions are logged
        /// (<c>evt.failed</c>), cancellation is logged at Debug. The returned task never faults.
        /// </summary>
        public static JoinableTask RunAsync(Logger log, string evt, Func<Task> work, params (string Key, object Value)[] data)
        {
            var jtf = ThreadHelper.JoinableTaskFactory;
            return jtf.RunAsync(async () =>
            {
                try
                {
                    await work().ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    log.Debug(evt + ".canceled", null, data);
                }
                catch (Exception ex) when (!IsCritical(ex))
                {
                    log.Error(evt + ".failed", "Unhandled exception in " + evt, ex, data);
                }
            });
        }

        /// <summary>Fire-and-forget on a background thread (Task.Run) with logging. Never touches the UI thread.</summary>
        public static Task RunInBackgroundAsync(Logger log, string evt, Func<Task> work, params (string Key, object Value)[] data)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    log.Debug(evt + ".canceled", null, data);
                }
                catch (Exception ex) when (!IsCritical(ex))
                {
                    log.Error(evt + ".failed", "Unhandled exception in " + evt, ex, data);
                }
            });
        }

        /// <summary>Wraps an event handler.</summary>
        public static EventHandler Handler(Logger log, string evt, Action handler) =>
            (s, e) => Execute(log, evt, handler);

        private static bool IsCritical(Exception ex) =>
            ex is OutOfMemoryException || ex is StackOverflowException || ex is AccessViolationException;
    }

    /// <summary>
    /// UI-thread watchdog for code we own (§9.1: hard limit 16 ms per UI-thread operation). Every UI-thread entry point is
    /// wrapped in a PerfScope named "ui.&lt;evt&gt;" with a 16 ms budget: over budget it is logged as Warn
    /// (ui.&lt;evt&gt;.slow, with elapsed ms), and all of them feed perf.summary and the performance panel.
    /// Off the UI thread it returns null (no cost).
    /// </summary>
    internal static class UiThread
    {
        public const double BudgetMs = 16;

        public static IDisposable Measure(Logger log, string evt, params (string Key, object Value)[] data)
        {
            if (!ThreadHelper.CheckAccess()) return null;
            return log.Time("ui." + evt, BudgetMs, data);
        }
    }
}
