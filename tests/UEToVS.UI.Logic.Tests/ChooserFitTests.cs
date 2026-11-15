using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    /// <summary>v0.7.7: list popups fitted to their content (Code Vision Blueprint list).</summary>
    public class ChooserFitTests
    {
        [Fact]
        public void WidthFollowsTheWidestRowWithinTheLimits()
        {
            Assert.Equal(620, ChooserFit.Width(600, 200, 320, 960));          // row + slack
            Assert.Equal(320, ChooserFit.Width(100, 80, 320, 960));           // never narrower than the minimum
            Assert.Equal(960, ChooserFit.Width(2400, 200, 320, 960));         // long paths: capped, trimmed in the middle
            Assert.Equal(500, ChooserFit.Width(100, 500, 320, 960));          // the title fits too
            Assert.Equal(321, ChooserFit.Width(300.2, 0, 0, 960));            // whole DIPs
            Assert.Equal(400, ChooserFit.Width(double.NaN, 0, 400, 960));
            Assert.Equal(300, ChooserFit.Width(800, 0, 400, 300));            // min above max: max wins
            Assert.Equal(1220, ChooserFit.Width(1200, 0, 320, 0));            // no maximum
        }

        [Fact]
        public void HeightFollowsTheRowCountUpToTheLimit()
        {
            Assert.Equal(3 * 24 + 36, ChooserFit.Height(3, 24, 36));
            Assert.Equal(1 * 24 + 36, ChooserFit.Height(0, 24, 36));
            Assert.Equal(ChooserFit.MaxRows * 24 + 36, ChooserFit.Height(500, 24, 36));
        }
    }
}
