namespace UEToVS.Host.Logic.Link
{
    /// <summary>
    /// Receiver of compiler output from a build that UEToVS observes (v0.4.5: Live Coding through UEToVSLink). The
    /// producer calls <see cref="BuildStarted"/>, then <see cref="OutputLine"/> for every raw output line in order, then
    /// <see cref="BuildFinished"/> exactly once per started build. Lines are raw text (Live Coding console / editor log
    /// lines, possibly with a UE log prefix); parsing them into diagnostics is the sink's job (the generic UBT/MSVC
    /// parser), <see cref="MsvcErrorLine"/> is only a minimal fallback.
    /// Calls arrive on a background thread, in order, never concurrently for the same source; implementations must not
    /// block (queue the work) and must not throw.
    /// </summary>
    public interface IBuildOutputSink
    {
        /// <param name="source">Who builds: "liveCoding" (more may follow, e.g. "ubt").</param>
        /// <param name="projectRoot">Folder of the .uproject when known (to resolve relative paths), else null.</param>
        void BuildStarted(string source, string projectRoot);

        void OutputLine(string source, string line);

        /// <param name="succeeded">true = success / nothing to compile; false = failed or cancelled; null = unknown.</param>
        /// <param name="result">Raw result word from the producer ("success", "failed", "noChanges", "cancelled", "unknown").</param>
        void BuildFinished(string source, bool? succeeded, string result);
    }
}
