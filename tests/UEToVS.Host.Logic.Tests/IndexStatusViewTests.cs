using System.Linq;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class IndexStatusViewTests
    {
        public IndexStatusViewTests() => Loc.InitializeFromLcid(2052);

        private static IndexStatusInput Status(int phase, int parsed = 0, int toParse = 0, string error = null) => new IndexStatusInput
        {
            HasEngine = true, EngineState = "Connected", HasStatus = true, Phase = phase, FilesParsed = parsed, FilesToParse = toParse,
            SymbolCount = 0, AssetCount = 8811, LastError = error, CppThreads = 2, BlueprintThreads = 2, Cores = 8,
        };

        [Fact]
        public void FailedPhaseIsAnErrorCardWithReasonAndActions()
        {
            var vm = IndexStatusView.From(Status(IndexStatusView.PhaseFailed, error: "index.failed:Parsing: OutOfMemoryException: Insufficient memory"));
            Assert.Equal(IndexStatusTone.Error, vm.Tone);
            Assert.Equal("索引失败", vm.Title);
            Assert.Equal("OutOfMemoryException: Insufficient memory", vm.Reason);
            Assert.True(vm.ShowViewLog);
            Assert.True(vm.ShowRebuild);
            Assert.False(vm.ShowProgress);
            Assert.Contains("8,811 资产", vm.Counts);
        }

        [Fact]
        public void FailedWithoutErrorStillExplains()
        {
            var vm = IndexStatusView.From(Status(IndexStatusView.PhaseFailed));
            Assert.Equal(IndexStatusTone.Error, vm.Tone);
            Assert.False(string.IsNullOrEmpty(vm.Reason));
        }

        [Fact]
        public void ParsingShowsDeterminateProgress()
        {
            var vm = IndexStatusView.From(Status(IndexStatusView.PhaseParsing, 250, 1000));
            Assert.Equal(IndexStatusTone.Progress, vm.Tone);
            Assert.True(vm.ShowProgress);
            Assert.Equal(0.25, vm.Fraction, 3);
            Assert.EndsWith("25%", vm.Title);
            Assert.False(vm.ShowRebuild);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        public void OtherBusyPhasesAreIndeterminate(int phase)
        {
            var vm = IndexStatusView.From(Status(phase, 10, 100));
            Assert.Equal(IndexStatusTone.Progress, vm.Tone);
            Assert.True(vm.ShowProgress);
            Assert.Equal(-1, vm.Fraction);
        }

        [Fact]
        public void ReadyIsSuccessUnlessTheEngineReportedAnError()
        {
            Assert.Equal(IndexStatusTone.Success, IndexStatusView.From(Status(IndexStatusView.PhaseReady, 10, 10)).Tone);
            var warn = IndexStatusView.From(Status(IndexStatusView.PhaseReady, 10, 10, "segment write retried"));
            Assert.Equal(IndexStatusTone.Warning, warn.Tone);
            Assert.True(warn.ShowViewLog);
        }

        [Fact]
        public void NoSolutionAndEngineStates()
        {
            Assert.Equal(IndexStatusTone.Neutral, IndexStatusView.From(new IndexStatusInput()).Tone);
            Assert.Equal(IndexStatusTone.Neutral, IndexStatusView.From(null).Tone);
            var down = IndexStatusView.From(new IndexStatusInput { HasEngine = true, EngineState = "Unavailable", EngineError = ".NET 8 runtime missing" });
            Assert.Equal(IndexStatusTone.Error, down.Tone);
            Assert.Equal(".NET 8 runtime missing", down.Reason);
            Assert.True(down.ShowViewLog);
            var starting = IndexStatusView.From(new IndexStatusInput { HasEngine = true, EngineState = "Starting" });
            Assert.Equal(IndexStatusTone.Progress, starting.Tone);
            Assert.Equal(-1, starting.Fraction);
        }

        [Fact]
        public void ThreadsLineAlwaysPresent()
        {
            Assert.Contains("C++ 2", IndexStatusView.From(Status(IndexStatusView.PhaseReady)).Threads);
            Assert.False(string.IsNullOrEmpty(IndexStatusView.From(new IndexStatusInput { Cores = 8, CppThreads = 2, BlueprintThreads = 2 }).Threads));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("   ", null)]
        [InlineData("index.failed:Merging: IOException: disk full", "IOException: disk full")]
        [InlineData("index.failed:", null)]
        [InlineData("plain\n  multi\tline", "plain multi line")]
        public void CleanReason(string raw, string expected) => Assert.Equal(expected, IndexStatusView.CleanReason(raw));

        [Fact]
        public void LongReasonsAreCapped()
        {
            string r = IndexStatusView.CleanReason(new string('x', 2000));
            Assert.True(r.Length <= 400);
            Assert.EndsWith("…", r);
        }

        [Fact]
        public void EnglishStrings()
        {
            try
            {
                Loc.InitializeFromLcid(1033);
                var vm = IndexStatusView.From(Status(IndexStatusView.PhaseFailed));
                Assert.Equal("Indexing failed", vm.Title);
                Assert.Contains("8,811 assets", vm.Counts);
            }
            finally
            {
                Loc.InitializeFromLcid(2052);
            }
        }

        [Fact]
        public void ThreadDescriptorsCarryTheirAutoValue()
        {
            var schema = HostSettingsSchema.Build(16);
            var cpp = schema.Single(d => d.Key == "indexing.cppThreads");
            var bp = schema.Single(d => d.Key == "indexing.blueprintThreads");
            Assert.Equal(4, cpp.AutoValue);
            Assert.Equal(4, bp.AutoValue);
            Assert.Equal("0", cpp.Default); // stored semantics unchanged: 0 = auto
            var port = schema.Single(d => d.Key == "unreal.editorLinkPort");
            Assert.Null(port.AutoValue);
            Assert.False(string.IsNullOrEmpty(port.AutoLabelZh));
        }
    }
}
