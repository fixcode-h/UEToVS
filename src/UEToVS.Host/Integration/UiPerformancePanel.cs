using System.Windows;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Performance;
using UEToVS.UI.Perf;

namespace UEToVS.Host.Integration
{
    /// <summary>"UEToVS 性能" tool window content: UEToVS.UI's <see cref="PerformancePanel"/>.</summary>
    internal sealed class UiPerformancePanelProvider : IPerformancePanelContentProvider
    {
        public IPerformancePanelContent Create(PerformancePanelCommands commands) => new UiPerformancePanelContent(commands);
    }

    /// <summary>
    /// Maps each <see cref="PerformanceData"/> through <see cref="PerformanceViewMapper"/> (pure, unit-tested) and routes
    /// the panel's buttons to <see cref="PerformancePanelCommands"/> through Safe.
    /// </summary>
    internal sealed class UiPerformancePanelContent : IPerformancePanelContent
    {
        private static readonly Logger L = Log.For("Performance");
        private readonly PerformancePanel _panel = new PerformancePanel();

        public UiPerformancePanelContent(PerformancePanelCommands commands)
        {
            commands = commands ?? new PerformancePanelCommands();
            _panel.OpenLogRequested += (s, e) => Safe.Execute(L, "perf.panel.openLog", () => commands.OpenLog?.Invoke());
            _panel.DiagnosticsRequested += (s, e) => Safe.Execute(L, "perf.panel.diagnostics", () => commands.CreateDiagnosticsPackage?.Invoke());
            _panel.PauseToggled += (s, paused) => Safe.Execute(L, "perf.panel.pause", () =>
            {
                L.Info("perf.panel.pause.toggled", null, ("paused", paused));
                commands.SetBackgroundPaused?.Invoke(paused);
            }, ("paused", paused));
        }

        public UIElement Element => _panel.Root;

        public void Update(PerformanceData data)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _panel.Update(PerformanceViewMapper.Map(data));
        }
    }
}
