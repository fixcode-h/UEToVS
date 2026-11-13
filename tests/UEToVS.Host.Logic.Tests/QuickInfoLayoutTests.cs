using UEToVS.Host.Logic.IntelliSense;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class QuickInfoLayoutTests
    {
        [Fact]
        public void CodeBlockIsCappedToPartOfTheScreen()
        {
            Assert.Equal(1040 * QuickInfoLayout.CodeFraction, QuickInfoLayout.CodeMaxHeight(1040), 3);
            Assert.Equal(QuickInfoLayout.CodeMaxHeightCap, QuickInfoLayout.CodeMaxHeight(2100));
            Assert.Equal(QuickInfoLayout.CodeMinHeight, QuickInfoLayout.CodeMaxHeight(200));
            Assert.Equal(1040 * QuickInfoLayout.CodeFraction, QuickInfoLayout.CodeMaxHeight(0), 3);
        }

        [Fact]
        public void PopupNeverExceedsTheWorkArea()
        {
            Assert.Equal(780, QuickInfoLayout.PopupMaxHeight(1040), 3);
            Assert.Equal(200, QuickInfoLayout.PopupMaxHeight(200));
            Assert.Equal(QuickInfoLayout.PopupMinHeight, QuickInfoLayout.PopupMaxHeight(300));
            Assert.True(QuickInfoLayout.PopupMaxHeight(1040) > QuickInfoLayout.CodeMaxHeight(1040));
        }

        [Theory]
        [InlineData(120, 0, 100, true)]     // 向上滚，已在顶部 → 交给外层
        [InlineData(120, 30, 100, false)]   // 向上滚，还能滚
        [InlineData(-120, 100, 100, true)]  // 向下滚，已在底部
        [InlineData(-120, 50, 100, false)]
        [InlineData(-120, 0, 0, true)]      // 内容没超出，不需要内层滚动
        public void WheelChainsOnlyAtTheEdge(int delta, double offset, double scrollable, bool chain) =>
            Assert.Equal(chain, QuickInfoLayout.ShouldChainWheel(delta, offset, scrollable));
    }
}
