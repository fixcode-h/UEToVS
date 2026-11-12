using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Engine
{
    /// <summary>Status bar presentation of an engine status push (OnStatusChangedAsync). Pure.</summary>
    public struct EngineStatusDisplay
    {
        public string Text;

        /// <summary>Progress (Done/Total); Total == 0 means "no progress bar".</summary>
        public int Done;

        public int Total;

        public bool IsError;
    }

    public static class EngineStatusText
    {
        public static EngineStatusDisplay For(EngineStatus s)
        {
            if (s == null) return new EngineStatusDisplay { Text = Strings.StatusEngineStarting };
            switch (s.Phase)
            {
                case IndexPhase.Ready:
                    return new EngineStatusDisplay { Text = Strings.StatusIndexReady(s.SymbolCount, s.FileCount) };
                case IndexPhase.Failed:
                    return new EngineStatusDisplay { Text = Strings.StatusIndexFailed, IsError = true };
                case IndexPhase.Parsing when s.FilesToParse > 0:
                    return new EngineStatusDisplay
                    {
                        Text = Strings.StatusIndexing(s.FilesParsed, s.FilesToParse),
                        Done = s.FilesParsed < 0 ? 0 : s.FilesParsed > s.FilesToParse ? s.FilesToParse : s.FilesParsed,
                        Total = s.FilesToParse,
                    };
                default:
                    return new EngineStatusDisplay { Text = Strings.StatusIndexPhase(Strings.PhaseName((int)s.Phase)) };
            }
        }
    }
}
