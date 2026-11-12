using System.Globalization;
using System.Threading;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class PopupStateTextTests
    {
        [Fact]
        public void RoundTripsAllFields()
        {
            string text = PopupStateText.Format(760.5, 500, 0.42, true);
            Assert.Equal("w=760.5;h=500;split=0.42;preview=1", text);
            Assert.True(PopupStateText.TryParse(text, out double w, out double h, out double split, out bool? preview));
            Assert.Equal(760.5, w);
            Assert.Equal(500, h);
            Assert.Equal(0.42, split);
            Assert.True(preview);
        }

        [Fact]
        public void OptionalFieldsAreOmittedAndDefaulted()
        {
            string text = PopupStateText.Format(640, 420, double.NaN, null);
            Assert.Equal("w=640;h=420", text);
            Assert.True(PopupStateText.TryParse(text, out _, out _, out double split, out bool? preview));
            Assert.True(double.IsNaN(split));
            Assert.Null(preview);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("w=abc;h=10")]
        [InlineData("w=0;h=10")]
        [InlineData("h=10")]
        public void RejectsInvalidText(string text)
        {
            Assert.False(PopupStateText.TryParse(text, out _, out _, out _, out _));
        }

        [Fact]
        public void OutOfRangeSplitIsDropped()
        {
            Assert.True(PopupStateText.TryParse("w=1;h=1;split=1.5;preview=x", out _, out _, out double split, out bool? preview));
            Assert.True(double.IsNaN(split));
            Assert.Null(preview);
        }

        [Fact]
        public void IsCultureInvariant()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string text = PopupStateText.Format(10.5, 20.25, 0.5, false);
                Assert.Equal("w=10.5;h=20.25;split=0.5;preview=0", text);
                Assert.True(PopupStateText.TryParse(text, out double w, out _, out _, out _));
                Assert.Equal(10.5, w);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
