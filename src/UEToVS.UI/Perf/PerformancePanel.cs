using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Shells;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Perf
{
    // View data (PerformanceViewData, ActiveWorkView, PhaseView, MemoryPartView, OperationView): Logic/PerformanceViewData.cs.

    // ------------------------------------------------------------------ panel

    /// <summary>
    /// Plugin performance tool window content (ToolWindowShell): resource tiles, CPU+memory chart, active work,
    /// index phases, index memory breakdown, operation latencies vs budget, and actions. Built once;
    /// <see cref="Update"/> (UI thread, ~1/s) only changes texts / chart data / table rows — no tree rebuild.
    /// </summary>
    public class PerformancePanel
    {
        private const int ChartCapacity = 600;

        private readonly StatTile _engineMem = new StatTile(() => Strings.PerfEngineMemory);
        private readonly StatTile _hostMem = new StatTile(() => Strings.PerfHostMemory);
        private readonly StatTile _cpu = new StatTile(() => Strings.PerfCpu);
        private readonly StatTile _index = new StatTile(() => Strings.PerfIndex);
        private readonly UniformGrid _tiles = new UniformGrid { Columns = 4 };
        private readonly LineChart _chart = new LineChart { Height = 170 };
        private readonly LineChart _hostChart = new LineChart { Height = 120 };
        private readonly FrameworkElement _hostChartSection;
        private readonly UiText _activeDetail = new UiText((string)null, TextVariant.Small) { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Banner _notice = new Banner(Severity.Warning) { CanClose = false, Visibility = Visibility.Collapsed };
        private readonly StackPanel _activeList = new StackPanel();
        private readonly List<ProgressRow> _activeRows = new List<ProgressRow>();
        private readonly UiText _activeEmpty = new UiText(() => Strings.PerfActiveNone, TextVariant.Secondary) { Margin = new Thickness(0, 4, 0, 4) };
        private readonly DataTable<PhaseView> _phases = new DataTable<PhaseView>();
        private readonly DataTable<OperationView> _ops = new DataTable<OperationView> { MaxVisibleRows = 16 };
        private readonly DataTable<HostMemoryRowView> _hostBreakdown = new DataTable<HostMemoryRowView> { MaxVisibleRows = 24 };
        private readonly UiText _hostBreakdownNote = new UiText((string)null, TextVariant.Small) { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        private readonly FrameworkElement _hostBreakdownSection;
        private readonly StackedBar _memManaged = new StackedBar();
        private readonly StackedBar _memMapped = new StackedBar();
        private readonly StackedBar _memDisk = new StackedBar();
        private readonly UiText _memManagedTotal = new UiText((string)null, TextVariant.Small) { MinWidth = 70, TextAlignment = TextAlignment.Right };
        private readonly UiText _memMappedTotal = new UiText((string)null, TextVariant.Small) { MinWidth = 70, TextAlignment = TextAlignment.Right };
        private readonly UiText _memDiskTotal = new UiText((string)null, TextVariant.Small) { MinWidth = 70, TextAlignment = TextAlignment.Right };
        private readonly BarLegend _legend = new BarLegend { Margin = new Thickness(0, 6, 0, 0) };
        private readonly UiButton _pause;
        private bool _paused;

        public PerformancePanel()
        {
            Root = new ToolWindowShell();
            var openLog = new UiButton(() => Strings.PerfOpenLog, ButtonKind.Secondary);
            var diag = new UiButton(() => Strings.PerfDiagnostics, ButtonKind.Secondary) { Margin = new Thickness(6, 0, 0, 0) };
            _pause = new UiButton(() => _paused ? Strings.PerfResume : Strings.PerfPause, ButtonKind.Secondary) { Margin = new Thickness(6, 0, 0, 0) };
            openLog.Click += (s, e) => Ui.Safe("ui.perf.openlog", () => OpenLogRequested?.Invoke(this, EventArgs.Empty));
            diag.Click += (s, e) => Ui.Safe("ui.perf.diag", () => DiagnosticsRequested?.Invoke(this, EventArgs.Empty));
            _pause.Click += (s, e) => Ui.Safe("ui.perf.pause", () => PauseToggled?.Invoke(this, !_paused));
            Root.AddToolbarItem(openLog);
            Root.AddToolbarItem(diag);
            Root.AddToolbarItem(_pause);
            Root.ShowFooter = false;

            foreach (var t in new[] { _engineMem, _hostMem, _cpu, _index })
            {
                t.Margin = new Thickness(0, 0, 8, 8);
                _tiles.Children.Add(t);
            }

            _chart.AddSeries(new ChartSeries(() => Strings.PerfSeriesCpu, ChartCapacity, Tokens.Accent, v => v.ToString("0") + "%") { FixedMin = 0, FixedMax = 100 });
            _chart.AddSeries(new ChartSeries(() => Strings.PerfSeriesMemory, ChartCapacity, Tokens.BadgeSolid(BadgeKind.UFunction), PerfMath.FormatBytes) { FixedMin = 0 });
            _hostChart.AddSeries(new ChartSeries(() => Strings.PerfSeriesHostMemory, ChartCapacity, Tokens.BadgeSolid(BadgeKind.UProperty), PerfMath.FormatBytes) { FixedMin = 0 });

            BuildTables();

            var content = new StackPanel { Margin = new Thickness(12, 10, 4, 12) };
            content.Children.Add(_tiles);
            content.Children.Add(Section(() => Strings.PerfChart, _chart));
            _hostChartSection = Section(() => Strings.PerfHostChart, _hostChart);
            _hostChartSection.Visibility = Visibility.Collapsed;
            content.Children.Add(_hostChartSection);
            var breakdown = new StackPanel();
            breakdown.Children.Add(_hostBreakdown);
            breakdown.Children.Add(_hostBreakdownNote);
            _hostBreakdownSection = Section(() => Strings.PerfHostBreakdown, breakdown);
            _hostBreakdownSection.Visibility = Visibility.Collapsed;
            content.Children.Add(_hostBreakdownSection);
            _activeList.Children.Add(_activeEmpty);
            _activeList.Children.Add(_activeDetail);
            content.Children.Add(Section(() => Strings.PerfActive, _activeList));
            content.Children.Add(Section(() => Strings.PerfPhases, _phases));
            content.Children.Add(Section(() => Strings.PerfMemory, BuildMemory()));
            content.Children.Add(Section(() => Strings.PerfOps, _ops));
            var scroll = new UiScrollViewer { Content = content };
            Root.Body = scroll;
            Root.ShowBanner(_notice);
            Root.SizeChanged += (s, e) => Ui.Safe("ui.perf.size", () => _tiles.Columns = e.NewSize.Width < 560 ? 2 : 4);
        }

        public ToolWindowShell Root { get; }

        public event EventHandler OpenLogRequested;
        public event EventHandler DiagnosticsRequested;

        /// <summary>true = user wants background indexing paused.</summary>
        public event EventHandler<bool> PauseToggled;

        private static FrameworkElement Section(Func<string> title, UIElement body)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 10, 8, 0) };
            var t = new UiText(title, TextVariant.Strong) { Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(t);
            var card = new Border { Child = body, BorderThickness = new Thickness(1), Padding = new Thickness(8) };
            card.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
            card.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
            card.Bind(Border.BackgroundProperty, Tokens.SurfacePopup);
            sp.Children.Add(card);
            return sp;
        }

        private void BuildTables()
        {
            _phases.AddColumn(new TableColumn<PhaseView>(() => Strings.PerfColPhase, p => p.Running ? p.Name + " …" : p.Name) { Star = 1.4, Sort = (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture) });
            _phases.AddColumn(new TableColumn<PhaseView>(() => Strings.PerfColDuration, p => PerfMath.FormatDuration(p.DurationMs)) { Numeric = true, FixedWidth = 84, Sort = (a, b) => a.DurationMs.CompareTo(b.DurationMs) });
            _phases.AddColumn(new TableColumn<PhaseView>(() => Strings.PerfColCpu, p => PerfMath.FormatDuration(p.CpuMs)) { Numeric = true, FixedWidth = 84, Sort = (a, b) => a.CpuMs.CompareTo(b.CpuMs) });
            _phases.AddColumn(new TableColumn<PhaseView>(() => Strings.PerfColItems, p => PerfMath.FormatCount(p.Items)) { Numeric = true, FixedWidth = 80, Sort = (a, b) => a.Items.CompareTo(b.Items) });
            _phases.AddColumn(new TableColumn<PhaseView>(() => Strings.PerfColDetail, p => p.Detail) { Star = 2 });

            _ops.AddColumn(new TableColumn<OperationView>(() => Strings.PerfColOp, o => o.Name) { Star = 1.5, Sort = (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal) });
            _ops.AddColumn(new TableColumn<OperationView>(() => Strings.PerfColCount, o => PerfMath.FormatCount(o.Count)) { Numeric = true, FixedWidth = 70, Sort = (a, b) => a.Count.CompareTo(b.Count) });
            _ops.AddColumn(new TableColumn<OperationView>(() => Strings.PerfColErrors, o => PerfMath.FormatCount(o.Errors)) { Numeric = true, FixedWidth = 60, Sort = (a, b) => a.Errors.CompareTo(b.Errors), CellState = o => o.Errors > 0 ? Severity.Warning : (Severity?)null });
            _ops.AddColumn(new TableColumn<OperationView>(() => Strings.PerfColP50, o => PerfMath.FormatDuration(o.P50Ms)) { Numeric = true, FixedWidth = 72, Sort = (a, b) => a.P50Ms.CompareTo(b.P50Ms) });
            _ops.AddColumn(new TableColumn<OperationView>(() => Strings.PerfColP95, o => PerfMath.FormatDuration(o.P95Ms)) { Numeric = true, FixedWidth = 72, Sort = (a, b) => a.P95Ms.CompareTo(b.P95Ms), CellState = o => o.IsOverBudget ? Severity.Error : (Severity?)null });
            _ops.AddColumn(new TableColumn<OperationView>(() => Strings.PerfColMax, o => PerfMath.FormatDuration(o.MaxMs)) { Numeric = true, FixedWidth = 72, Sort = (a, b) => a.MaxMs.CompareTo(b.MaxMs) });
            _ops.AddColumn(new TableColumn<OperationView>(() => Strings.PerfColBudget, o => o.BudgetMs > 0 ? PerfMath.FormatDuration(o.BudgetMs) : "—") { Numeric = true, FixedWidth = 72, Sort = (a, b) => a.BudgetMs.CompareTo(b.BudgetMs) });
            _hostBreakdown.AddColumn(new TableColumn<HostMemoryRowView>(() => Strings.PerfColItem, r => r.IsChild ? "    " + r.Name : r.Name) { Star = 1.4 });
            _hostBreakdown.AddColumn(new TableColumn<HostMemoryRowView>(() => Strings.PerfColSize, r => PerfMath.FormatBytes(r.Bytes)) { Numeric = true, FixedWidth = 84 });
            _hostBreakdown.AddColumn(new TableColumn<HostMemoryRowView>(() => Strings.PerfColShare, r => PerfMath.FormatPercent(r.Share)) { Numeric = true, FixedWidth = 64 });
            _hostBreakdown.AddColumn(new TableColumn<HostMemoryRowView>(() => Strings.PerfColDetail, r => r.Detail) { Star = 2.2 });
            _hostBreakdown.RowState = r => !r.IsChild && r.Share >= 0.5 ? Severity.Warning : (Severity?)null;

            _ops.RowState = o => o.IsOverBudget ? Severity.Error : (Severity?)null;
            _ops.SortBy(4, descending: true); // worst p95 first
        }

        private FrameworkElement BuildMemory()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            void Row(int r, Func<string> label, StackedBar bar, UiText total)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
                var l = new UiText(label, TextVariant.Small) { Margin = new Thickness(0, 0, 10, 0), MinWidth = 48 };
                bar.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(l, r);
                Grid.SetRow(bar, r);
                Grid.SetColumn(bar, 1);
                Grid.SetRow(total, r);
                Grid.SetColumn(total, 2);
                grid.Children.Add(l);
                grid.Children.Add(bar);
                grid.Children.Add(total);
            }
            Row(0, () => Strings.PerfMemManaged, _memManaged, _memManagedTotal);
            Row(1, () => Strings.PerfMemMapped, _memMapped, _memMappedTotal);
            Row(2, () => Strings.PerfMemDisk, _memDisk, _memDiskTotal);
            var sp = new StackPanel();
            sp.Children.Add(grid);
            sp.Children.Add(_legend);
            return sp;
        }

        /// <summary>Applies a new snapshot. UI thread; cheap (texts, chart points, visible table rows).</summary>
        public void Update(PerformanceViewData d)
        {
            if (d == null) return;
            using (Ui.Logger.Time("ui.perf.update", 8))
            {
                try
                {
                    UpdateCore(d);
                }
                catch (Exception ex)
                {
                    Ui.Logger.Error("ui.perf.update_failed", "PerformancePanel.Update failed", ex);
                }
            }
        }

        private static string Join(string a, string b) =>
            string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + " · " + b;

        private static Severity? BudgetState(double value, double budget)
        {
            if (budget <= 0 || double.IsNaN(value)) return null;
            if (value > budget) return Severity.Error;
            if (value > budget * 0.85) return Severity.Warning;
            return Severity.Success;
        }

        private void UpdateCore(PerformanceViewData d)
        {
            _engineMem.Set(double.IsNaN(d.EngineMemoryBytes) ? "—" : PerfMath.FormatBytes(d.EngineMemoryBytes),
                Join(d.EngineMemoryDetail, d.EngineMemoryBudgetBytes > 0 ? Strings.PerfBudgetOf(PerfMath.FormatBytes(d.EngineMemoryBudgetBytes)) : null),
                BudgetState(d.EngineMemoryBytes, d.EngineMemoryBudgetBytes));
            _hostMem.Set(double.IsNaN(d.HostMemoryBytes) ? "—" : PerfMath.FormatBytes(d.HostMemoryBytes),
                Join(d.HostMemoryDetail, d.HostMemoryBudgetBytes > 0 ? Strings.PerfBudgetOf(PerfMath.FormatBytes(d.HostMemoryBudgetBytes)) : null),
                BudgetState(d.HostMemoryBytes, d.HostMemoryBudgetBytes));
            _cpu.Set(double.IsNaN(d.CpuPercent) ? "—" : d.CpuPercent.ToString("0") + "%", null,
                double.IsNaN(d.CpuPercent) ? (Severity?)null : d.CpuPercent > 80 ? Severity.Warning : (Severity?)null);
            string idxValue = d.IndexProgress.HasValue ? PerfMath.FormatPercent(d.IndexProgress.Value) : (d.IndexState ?? "—");
            string idxSub = d.IndexProgress.HasValue ? (d.IndexState + (string.IsNullOrEmpty(d.IndexDetail) ? "" : " · " + d.IndexDetail)) : d.IndexDetail;
            _index.Set(idxValue, idxSub, d.IndexSeverity == Severity.Info ? (Severity?)null : d.IndexSeverity, d.IndexProgress);

            _chart.SampleIntervalSeconds = d.SampleIntervalSeconds > 0 ? d.SampleIntervalSeconds : 1;
            if (d.CpuHistory != null) _chart.SetData(0, d.CpuHistory);
            if (d.MemoryHistory != null) _chart.SetData(1, d.MemoryHistory);
            _hostChartSection.Visibility = d.HostMemoryHistory != null ? Visibility.Visible : Visibility.Collapsed;
            if (d.HostMemoryHistory != null)
            {
                _hostChart.SampleIntervalSeconds = _chart.SampleIntervalSeconds;
                _hostChart.SetData(0, d.HostMemoryHistory);
            }

            _hostBreakdownSection.Visibility = d.HostMemoryRows != null ? Visibility.Visible : Visibility.Collapsed;
            if (d.HostMemoryRows != null) _hostBreakdown.SetItems(d.HostMemoryRows);
            _hostBreakdownNote.Text = d.HostMemoryNote ?? string.Empty;
            _hostBreakdownNote.Visibility = string.IsNullOrEmpty(d.HostMemoryNote) ? Visibility.Collapsed : Visibility.Visible;

            _notice.Visibility = string.IsNullOrEmpty(d.Notice) ? Visibility.Collapsed : Visibility.Visible;
            if (!string.IsNullOrEmpty(d.Notice))
            {
                if (_notice.Severity != d.NoticeSeverity) _notice.Severity = d.NoticeSeverity;
                if (_notice.Message != d.Notice) _notice.Message = d.Notice;
            }

            // Active work: reuse rows by position.
            var active = d.Active ?? Array.Empty<ActiveWorkView>();
            while (_activeRows.Count < active.Count)
            {
                var r = new ProgressRow();
                _activeRows.Add(r);
                _activeList.Children.Add(r);
            }
            for (int i = 0; i < _activeRows.Count; i++)
            {
                if (i < active.Count)
                {
                    var a = active[i];
                    _activeRows[i].Visibility = Visibility.Visible;
                    _activeRows[i].Update(a.Name, a.Lane, a.Done, a.Total, a.ElapsedMs);
                }
                else _activeRows[i].Visibility = Visibility.Collapsed;
            }
            _activeEmpty.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _activeDetail.Text = d.ActiveDetail ?? string.Empty;
            _activeDetail.Visibility = string.IsNullOrEmpty(d.ActiveDetail) ? Visibility.Collapsed : Visibility.Visible;

            _phases.SetItems(d.Phases);
            _ops.SetItems(d.Operations);

            var parts = d.MemoryParts ?? Array.Empty<MemoryPartView>();
            var managed = new List<BarSegment>(parts.Count);
            var mapped = new List<BarSegment>(parts.Count);
            var disk = new List<BarSegment>(parts.Count);
            var names = new List<string>(parts.Count);
            double tm = 0, tp = 0, td = 0;
            foreach (var p in parts)
            {
                managed.Add(new BarSegment(p.Name, p.ManagedBytes));
                mapped.Add(new BarSegment(p.Name, p.MappedBytes));
                disk.Add(new BarSegment(p.Name, p.DiskBytes));
                names.Add(p.Name);
                tm += p.ManagedBytes;
                tp += p.MappedBytes;
                td += p.DiskBytes;
            }
            double max = Math.Max(tm, Math.Max(tp, td));
            foreach (var b in new[] { _memManaged, _memMapped, _memDisk }) b.ScaleMax = max;
            _memManaged.SetSegments(managed);
            _memMapped.SetSegments(mapped);
            _memDisk.SetSegments(disk);
            _memManagedTotal.Text = PerfMath.FormatBytes(tm);
            _memMappedTotal.Text = PerfMath.FormatBytes(tp);
            _memDiskTotal.Text = PerfMath.FormatBytes(td);
            _legend.SetNames(names);

            if (_paused != d.BackgroundPaused)
            {
                _paused = d.BackgroundPaused;
                _pause.Content = _paused ? Strings.PerfResume : Strings.PerfPause;
            }
        }
    }
}
