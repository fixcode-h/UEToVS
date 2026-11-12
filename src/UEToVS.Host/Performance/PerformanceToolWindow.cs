using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
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

namespace UEToVS.Host.Performance
{
    /// <summary>
    /// Tool window "UEToVS 性能" (§9.8). Polls through <see cref="PerformanceMonitor"/> only while visible
    /// (WPF IsVisibleChanged follows tab switches, auto-hide and close); logs perf.panel.open / perf.panel.close.
    /// Content: <see cref="HostIntegration.PerformancePanel"/> (UEToVS.UI's PerformancePanel at integration), otherwise a
    /// plain-text placeholder that still shows the numbers.
    /// </summary>
    [Guid(PackageGuids.PerformanceToolWindowString)]
    public sealed class PerformanceToolWindow : ToolWindowPane
    {
        private static readonly Logger L = Log.For("Performance");
        private readonly Border _root;
        private IPerformancePanelContent _content;
        private PerformanceMonitor _monitor;
        private bool _open;
        private DateTime _openedUtc;

        public PerformanceToolWindow() : base(null)
        {
            Caption = Strings.PerformanceWindowCaption;
            _root = new Border();
            _root.IsVisibleChanged += (s, e) => Safe.Execute(L, "perf.panel.visibility", () => OnVisibilityChanged((bool)e.NewValue));
            Content = _root;
        }

        private void EnsureContent()
        {
            if (_content != null) return;
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
            if (_content == null) _content = new PlaceholderContent(); // TODO(integration/UI): PerformancePanel
            _root.Child = _content.Element;
        }

        private void OnVisibilityChanged(bool visible)
        {
            _monitor = _monitor ?? HostPackage.Instance?.Performance;
            if (visible && !_open)
            {
                EnsureContent();
                _open = true;
                _openedUtc = DateTime.UtcNow;
                L.Info("perf.panel.open", null, ("content", _content?.GetType().FullName));
                if (_monitor != null)
                {
                    _monitor.Updated += OnUpdated;
                    _monitor.Start();
                    if (_monitor.Last != null) _content.Update(_monitor.Last);
                }
            }
            else if (!visible && _open)
            {
                _open = false;
                if (_monitor != null)
                {
                    _monitor.Updated -= OnUpdated;
                    _monitor.Stop();
                }
                L.Info("perf.panel.close", null, ("visibleSec", Math.Round((DateTime.UtcNow - _openedUtc).TotalSeconds, 1)));
            }
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

        protected override void Dispose(bool disposing)
        {
            if (disposing && _open)
            {
                _open = false;
                if (_monitor != null)
                {
                    _monitor.Updated -= OnUpdated;
                    _monitor.Stop();
                }
                L.Info("perf.panel.close", null, ("reason", "dispose"));
            }
            base.Dispose(disposing);
        }

        /// <summary>Text-only stand-in until UEToVS.UI's PerformancePanel is plugged in.</summary>
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
                _text.SetResourceReference(TextBlock.ForegroundProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);
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
