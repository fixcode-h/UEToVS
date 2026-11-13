using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class WindowResizeTests
    {
        private static readonly RectD R = new RectD(100, 100, 800, 500);

        [Fact]
        public void RightBottomGrowKeepsOrigin()
        {
            var r = WindowResize.Apply(R, 1, 1, 40, 30, 360, 160);
            Assert.Equal(100, r.X);
            Assert.Equal(100, r.Y);
            Assert.Equal(840, r.Width);
            Assert.Equal(530, r.Height);
        }

        [Fact]
        public void LeftTopGrowMovesOriginAndKeepsOppositeEdge()
        {
            var r = WindowResize.Apply(R, -1, -1, -50, -20, 360, 160);
            Assert.Equal(50, r.X);
            Assert.Equal(80, r.Y);
            Assert.Equal(850, r.Width);
            Assert.Equal(520, r.Height);
            Assert.Equal(R.Right, r.Right);
            Assert.Equal(R.Bottom, r.Bottom);
        }

        [Fact]
        public void LeftShrinkStopsAtMinimumWithoutWalking()
        {
            var r = WindowResize.Apply(R, -1, 0, 1000, 0, 360, 160);
            Assert.Equal(360, r.Width);
            Assert.Equal(R.Right, r.Right);
            Assert.Equal(500, r.Height);
        }

        [Fact]
        public void UnusedAxisIsUntouched()
        {
            var r = WindowResize.Apply(R, 0, 1, 99, -10, 360, 160);
            Assert.Equal(800, r.Width);
            Assert.Equal(100, r.X);
            Assert.Equal(490, r.Height);
        }
    }
}
