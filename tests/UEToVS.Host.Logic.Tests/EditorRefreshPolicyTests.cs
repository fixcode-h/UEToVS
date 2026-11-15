using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Navigation;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class EditorRefreshPolicyTests
    {
        [Theory]
        [InlineData(null, IndexPhase.Ready, true)] // first status already Ready (cache hit)
        [InlineData(IndexPhase.LoadingCache, IndexPhase.Parsing, true)]
        [InlineData(IndexPhase.Merging, IndexPhase.Ready, true)]
        [InlineData(IndexPhase.Ready, IndexPhase.Ready, false)] // repeated status: no refresh storm
        [InlineData(IndexPhase.Parsing, IndexPhase.Parsing, false)]
        [InlineData(IndexPhase.ListingFiles, IndexPhase.LoadingCache, false)]
        [InlineData(IndexPhase.Parsing, IndexPhase.Failed, false)]
        public void RefreshesOnlyWhenEnteringParsingOrReady(IndexPhase? previous, IndexPhase now, bool expected) =>
            Assert.Equal(expected, EditorRefreshPolicy.OnPhaseChanged(previous, now));

        [Fact]
        public void StartupRevealRetriesAreIncreasingAndBounded()
        {
            var d = SolutionExplorerSync.RevealRetryDelays;
            Assert.NotEmpty(d);
            Assert.True(d.Zip(d.Skip(1), (a, b) => b > a).All(x => x));
            Assert.True(d.Sum(x => x.TotalSeconds) < 30); // gives up within half a minute
        }
    }
}
