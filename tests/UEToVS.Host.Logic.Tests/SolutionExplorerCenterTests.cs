using UEToVS.Host.Logic.Navigation;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class SolutionExplorerCenterTests
    {
        [Fact]
        public void LogicalScrollingMovesByItems()
        {
            // 900 px viewport, 24 px rows, selected row at the bottom edge → scroll so it sits in the middle
            var t = SolutionExplorerSync.CenteredOffset(100, 870, 24, 900, logicalScrolling: true, maxOffset: 5000);
            Assert.Equal(100 + System.Math.Round((882 - 450) / 24.0), t);
        }

        [Fact]
        public void PhysicalScrollingMovesByPixels()
        {
            var t = SolutionExplorerSync.CenteredOffset(1000, 20, 24, 900, logicalScrolling: false, maxOffset: 50000);
            Assert.Equal(1000 + 32 - 450, t);
        }

        [Fact]
        public void AlreadyNearTheMiddleOrClampedStaysPut()
        {
            Assert.Null(SolutionExplorerSync.CenteredOffset(100, 440, 24, 900, true, 5000));
            Assert.Equal(0, SolutionExplorerSync.CenteredOffset(3, 10, 24, 900, true, 5000));
            Assert.Null(SolutionExplorerSync.CenteredOffset(0, 10, 24, 900, true, 5000)); // top of the tree: nothing to scroll
            Assert.Null(SolutionExplorerSync.CenteredOffset(0, 10, 0, 900, true, 5000));
        }
    }
}
