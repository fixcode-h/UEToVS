using UEToVS.Contracts;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// When the editors that were opened before the engine could answer (documents VS restores at startup) must ask again.
    /// Their taggers are created as soon as VS shows the document, usually before the UE solution is detected and the engine
    /// is connected; the first request is then dropped and nothing re-triggers it until the user scrolls or types. So the
    /// host broadcasts a refresh when the engine connects and when the index reaches a phase where answers get better
    /// (cache loaded → Parsing, full index → Ready). Pure rules, testable without VS.
    /// </summary>
    public static class EditorRefreshPolicy
    {
        /// <summary>Whether an index phase change should refresh the open editors (only on entering Parsing or Ready).</summary>
        public static bool OnPhaseChanged(IndexPhase? previous, IndexPhase now) =>
            previous != now && (now == IndexPhase.Parsing || now == IndexPhase.Ready);
    }
}
