using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Performance;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class PerformanceViewMapperTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public PerformanceViewMapperTests() => Loc.Language = UiLanguage.English;

        public void Dispose() => Loc.Language = _saved;

        private const long MB = 1048576;

        private static PerformanceData Sample() => new PerformanceData
        {
            Host = new HostResourceSample { PrivateBytes = 900 * MB, PrivateBytesAtLoad = 880 * MB, ManagedHeapBytes = 120 * MB },
            HostHistory = new[] { new HostResourceSample { PrivateBytes = 1 * MB }, new HostResourceSample { PrivateBytes = 2 * MB } },
            HostOperations = new List<OperationStats>
            {
                new OperationStats { Name = "Commands/ui.command.invoke", Count = 3, P50Ms = 2, P95Ms = 20, MaxMs = 30, BudgetMs = 16 },
            },
            EngineState = "Connected",
            Engine = new PerformanceSnapshot
            {
                CpuPercent = 12.5,
                Resources = new ResourceSnapshot { PrivateBytes = 300 * MB, WorkingSetBytes = 250 * MB, ManagedHeapBytes = 100 * MB },
                History = new List<ResourceSample>
                {
                    new ResourceSample { CpuPercent = 10, PrivateBytes = 100 },
                    new ResourceSample { CpuPercent = 20, PrivateBytes = 200 },
                },
                Status = new EngineStatus { Phase = IndexPhase.Parsing, FilesParsed = 25, FilesToParse = 100, SymbolCount = 4000 },
                Phases = new List<PhaseTiming> { new PhaseTiming { Phase = IndexPhase.Parsing, DurationMs = 1000, CpuMs = 3000, Items = 25, Running = true, Detail = "project files=100" } },
                IndexParts = new List<MemoryPart> { new MemoryPart { Name = "symbols", ManagedBytes = 1, MappedBytes = 2, DiskBytes = 3 } },
                Operations = new List<OperationStats>
                {
                    new OperationStats { Name = "rpc.search", Count = 10, P95Ms = 50, BudgetMs = 100 },
                },
                Active = new List<ActiveWork> { new ActiveWork { Name = "index.parse.project", Lane = "background", Done = 25, Total = 100, ElapsedMs = 900 } },
                ScheduledInteractive = 1,
                ScheduledBackground = 7,
                BackgroundPaused = true,
            },
        };

        [Fact]
        public void MapsEngineHostIndexAndTrends()
        {
            var v = PerformanceViewMapper.Map(Sample());

            Assert.Equal(300.0 * MB, v.EngineMemoryBytes);
            Assert.Contains("250.0 MB", v.EngineMemoryDetail);
            Assert.Contains("100.0 MB", v.EngineMemoryDetail);
            Assert.Equal(12.5, v.CpuPercent);

            Assert.Equal(900.0 * MB, v.HostMemoryBytes);
            Assert.Contains("+20.0 MB", v.HostMemoryDetail);
            Assert.Contains("120.0 MB", v.HostMemoryDetail);

            Assert.Equal("Parsing", v.IndexState);
            Assert.Equal(0.25, v.IndexProgress);
            Assert.Equal(Severity.Info, v.IndexSeverity);
            Assert.Contains("25/100", v.IndexDetail);

            Assert.Equal(new double[] { 10, 20 }, v.CpuHistory);
            Assert.Equal(new double[] { 100, 200 }, v.MemoryHistory);
            Assert.Equal(new double[] { 1 * MB, 2 * MB }, v.HostMemoryHistory);
            Assert.Null(v.Notice);
        }

        [Fact]
        public void LocalizesEngineCodes()
        {
            var v = PerformanceViewMapper.Map(Sample());
            var a = Assert.Single(v.Active);
            Assert.Equal("Parsing project files", a.Name);
            Assert.Equal(25, a.Done);
            Assert.Equal(100, a.Total);
            Assert.True(v.BackgroundPaused);
            Assert.Contains("7", v.ActiveDetail);
            Assert.Contains("paused", v.ActiveDetail);

            var p = Assert.Single(v.Phases);
            Assert.Equal("Parsing", p.Name);
            Assert.Equal("project files=100", p.Detail);
            Assert.True(p.Running);
            Assert.Equal("symbols", Assert.Single(v.MemoryParts).Name);
        }

        [Fact]
        public void LastErrorIsLocalizedAndMarksTheIndexAsError()
        {
            var d = Sample();
            d.Engine.Status.Phase = IndexPhase.Failed;
            d.Engine.Status.LastError = "index.failed:Parsing: IOException: disk";
            var v = PerformanceViewMapper.Map(d);
            Assert.Equal(Severity.Error, v.IndexSeverity);
            Assert.Equal("Indexing failed (Parsing): IOException: disk", v.IndexDetail);
            Assert.Null(v.IndexProgress);
        }

        [Fact]
        public void ReadyIndexIsSuccessWithoutProgress()
        {
            var d = Sample();
            d.Engine.Status = new EngineStatus { Phase = IndexPhase.Ready, SymbolCount = 5, FileCount = 2 };
            var v = PerformanceViewMapper.Map(d);
            Assert.Equal(Severity.Success, v.IndexSeverity);
            Assert.Null(v.IndexProgress);
            Assert.Equal("Ready", v.IndexState);
        }

        [Fact]
        public void OperationsMergeEngineAndHostWithHostBudgetRule()
        {
            var v = PerformanceViewMapper.Map(Sample());
            Assert.Equal(2, v.Operations.Count);
            var engineOp = v.Operations.Single(o => o.Name.Contains("rpc.search"));
            var hostOp = v.Operations.Single(o => o.Name.Contains("ui.command.invoke"));
            Assert.StartsWith("Engine", engineOp.Name);
            Assert.StartsWith("VS", hostOp.Name);
            Assert.False(engineOp.IsOverBudget);
            Assert.True(hostOp.IsOverBudget);
            Assert.Equal(PerformanceDataBuilder.IsOverBudget(Sample().HostOperations[0]), hostOp.OverBudget);
        }

        [Fact]
        public void EngineUnavailableShowsStateAndOnlyHostData()
        {
            var d = Sample();
            d.Engine = null;
            d.EngineState = "Unavailable";
            d.EngineError = "crashed 3 times";
            var v = PerformanceViewMapper.Map(d);

            Assert.True(double.IsNaN(v.EngineMemoryBytes));
            Assert.True(double.IsNaN(v.CpuPercent));
            Assert.Equal("Unavailable", v.IndexState);
            Assert.Equal(Severity.Error, v.IndexSeverity);
            Assert.Equal("crashed 3 times", v.IndexDetail);
            Assert.Contains("crashed 3 times", v.Notice);
            Assert.Equal(Severity.Error, v.NoticeSeverity);
            Assert.Empty(v.CpuHistory);
            Assert.Empty(v.Active);
            Assert.Empty(v.Phases);
            Assert.Single(v.Operations);
            Assert.Equal(900.0 * MB, v.HostMemoryBytes);
        }

        [Fact]
        public void StartingEngineIsAWarningAndNullDataIsSafe()
        {
            var v = PerformanceViewMapper.Map(new PerformanceData { EngineState = "Starting" });
            Assert.Equal(Severity.Warning, v.IndexSeverity);
            Assert.Equal(Severity.Warning, v.NoticeSeverity);
            Assert.Empty(v.HostMemoryHistory);
            Assert.NotNull(PerformanceViewMapper.Map(null));
        }

        [Fact]
        public void EngineStateNamesAreLocalized()
        {
            Loc.Language = UiLanguage.Chinese;
            Assert.Equal("不可用", Localization.Strings.EngineStateName("Unavailable"));
            Assert.Equal("Weird", Localization.Strings.EngineStateName("Weird"));
            Loc.Language = UiLanguage.English;
            Assert.Equal("Not started", Localization.Strings.EngineStateName("None"));
        }
    }
}
