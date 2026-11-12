using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Performance;
using UEToVS.Host.Shell;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Performance
{
    /// <summary>
    /// Floating window "UEToVS 性能" (§9.8; v0.3.0: a <see cref="UtilityWindow"/> that remembers position and size instead
    /// of a docked tool window). Polls through <see cref="PerformanceMonitor"/> only while open; logs perf.panel.open /
    /// perf.panel.close. Content: <see cref="HostIntegration.PerformancePanel"/> (UEToVS.UI's PerformancePanel); if the
    /// slot is empty or creating it fails, a plain-text placeholder that still shows the numbers.
    /// </summary>
    internal sealed class PerformanceWindow : UtilityWindow
    {
        private static readonly Logger L = Log.For("Performance");
        private readonly PerformanceMonitor _monitor;
        private IPerformancePanelContent _content;
        private bool _open;
        private DateTime _openedUtc;

        public PerformanceWindow(PerformanceMonitor monitor)
            : base("performance", () => SettingsText.PerformanceWindowTitle, new Size(980, 680))
        {
            _monitor = monitor;
            EnsureContent();
            Loaded += (s, e) => Safe.Execute(L, "perf.panel.open", Open);
            Closed += (s, e) => Safe.Execute(L, "perf.panel.close", CloseMonitor);
        }

        private void EnsureContent()
        {
            var commands = new PerformancePanelCommands
            {
                OpenLog = () => HostPackage.Instance?.ExecuteAction(ActionIds.OpenCurrentLog),
                CreateDiagnosticsPackage = () => HostPackage.Instance?.ExecuteAction(ActionIds.CreateDiagnostics),
                SetBackgroundPaused = paused => HostPackage.Instance?.SetEngineBackgroundPaused(paused),
            };
            var provider = HostIntegration.PerformancePanel;
            _content = provider != null
                ? Safe.Execute(L, "perf.panel.create", () => provider.Create(commands), null)
                : null;
            if (_content == null) _content = new PlaceholderContent();
            WindowContent = _content.Element;
        }

        private void Open()
        {
            if (_open) return;
            _open = true;
            _openedUtc = DateTime.UtcNow;
            L.Info("perf.panel.open", null, ("content", _content?.GetType().FullName), ("intervalMs", _monitor?.Interval.TotalMilliseconds));
            if (_monitor == null) return;
            _monitor.Updated += OnUpdated;
            _monitor.Start();
            if (_monitor.Last != null) _content.Update(_monitor.Last);
        }

        private void CloseMonitor()
        {
            if (!_open) return;
            _open = false;
            if (_monitor != null)
            {
                _monitor.Updated -= OnUpdated;
                _monitor.Stop();
            }
            L.Info("perf.panel.close", null, ("visibleSec", Math.Round((DateTime.UtcNow - _openedUtc).TotalSeconds, 1)));
        }

        private void OnUpdated(object sender, PerformanceData data)
        {
            Safe.RunAsync(L, "perf.panel.update", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_open || _content == null) return;
                using (UiThread.Measure(L, "perf.panel.render")) _content.Update(data);
            });
        }

        /// <summary>Text-only fallback when UEToVS.UI's PerformancePanel is unavailable.</summary>
        private sealed class PlaceholderContent : IPerformancePanelContent
        {
            private readonly TextBlock _text = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.NoWrap,
            };

            public PlaceholderContent()
            {
                _text.SetResourceReference(TextBlock.ForegroundProperty, UEToVS.UI.Theming.Tokens.TextPrimary);
                Element = new ScrollViewer { Content = _text, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
                _text.Text = Strings.PerfSampling;
            }

            public UIElement Element { get; }

            public void Update(PerformanceData d)
            {
                var sb = new StringBuilder();
                var ci = CultureInfo.InvariantCulture;
                string Mb(long b) => (b / 1048576.0).ToString("F1", ci) + " MB";
                sb.AppendLine(Strings.PerfPlaceholderTitle);
                sb.AppendLine();
                sb.AppendLine(Strings.PerfHostHeader);
                if (d.Host != null)
                {
                    sb.AppendLine(Strings.PerfHostMemory(Mb(d.Host.PrivateBytes), (d.Host.PrivateBytesDelta >= 0 ? "+" : "") + Mb(d.Host.PrivateBytesDelta), Mb(d.Host.WorkingSetBytes)));
                    sb.AppendLine(Strings.PerfHostHeap(Mb(d.Host.ManagedHeapBytes), $"{d.Host.Gen0Collections}/{d.Host.Gen1Collections}/{d.Host.Gen2Collections}", d.Host.ThreadCount));
                }
                sb.AppendLine();
                sb.AppendLine(Strings.PerfEngineHeader(d.EngineState, d.EngineError));
                var e = d.Engine;
                if (e != null)
                {
                    sb.AppendLine(Strings.PerfEngineProcess(e.ProcessId, e.UptimeSeconds, e.CpuPercent, d.EngineRoundTripMs));
                    if (e.Resources != null)
                        sb.AppendLine(Strings.PerfEngineMemory(Mb(e.Resources.PrivateBytes), Mb(e.Resources.WorkingSetBytes), Mb(e.Resources.ManagedHeapBytes), Mb(e.Resources.IndexBytesOnDisk)));
                    if (e.Status?.LastError != null) sb.AppendLine("  " + EngineCodes.Error(e.Status.LastError));
                    if (e.Status != null)
                        sb.AppendLine(Strings.PerfIndex(Strings.PhaseName((int)e.Status.Phase), e.Status.FilesParsed, e.Status.FilesToParse, e.Status.SymbolCount));
                    sb.AppendLine(Strings.PerfQueues(e.ScheduledInteractive, e.ScheduledBackground, e.BackgroundPaused));
                    foreach (var a in e.Active ?? Enumerable.Empty<UEToVS.Contracts.ActiveWork>())
                        sb.AppendLine($"  > {EngineCodes.WorkName(a.Name)} [{a.Lane}] {a.Done}/{a.Total} {a.ElapsedMs:F0} ms");
                    if (e.Phases?.Count > 0)
                    {
                        sb.AppendLine(Strings.PerfPhasesHeader);
                        foreach (var p in e.Phases)
                            sb.AppendLine($"    {Strings.PhaseName((int)p.Phase),-18} {p.DurationMs,10:F0} ms  CPU {p.CpuMs,10:F0} ms  {p.Items,8}{(p.Running ? "  *" : "")}  {EngineCodes.Detail(p.Detail)}");
                    }
                    if (e.IndexParts?.Count > 0)
                    {
                        sb.AppendLine(Strings.PerfIndexPartsHeader);
                        foreach (var m in e.IndexParts)
                            sb.AppendLine($"    {m.Name,-18} managed {Mb(m.ManagedBytes),10}  mapped {Mb(m.MappedBytes),10}  disk {Mb(m.DiskBytes),10}");
                    }
                    AppendOps(sb, Strings.PerfEngineOps, e.Operations);
                }
                AppendOps(sb, Strings.PerfHostOps, d.HostOperations);
                _text.Text = sb.ToString();
            }

            private static void AppendOps(StringBuilder sb, string title, System.Collections.Generic.List<UEToVS.Contracts.OperationStats> ops)
            {
                if (ops == null || ops.Count == 0) return;
                sb.AppendLine();
                sb.AppendLine("[" + title + "]  " + Strings.PerfOpsColumns);
                foreach (var o in ops.Take(40))
                    sb.AppendLine($"  {(PerformanceDataBuilder.IsOverBudget(o) ? "!" : " ")} {o.Name,-48} {o.Count,7} {o.Errors,5} {o.P50Ms,8:F1} {o.P95Ms,8:F1} {o.MaxMs,8:F1}{(o.BudgetMs > 0 ? "  " + Strings.PerfBudget(o.BudgetMs) : "")}");
            }
        }
    }
}
