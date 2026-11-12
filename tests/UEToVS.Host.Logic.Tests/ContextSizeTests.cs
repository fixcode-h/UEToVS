using UEToVS.Host.Logic.Agent;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class ContextSizeTests
    {
        [Theory]
        [InlineData(128000, "128K")]
        [InlineData(999000, "999K")]
        [InlineData(1000000, "1M")]
        [InlineData(1500000, "1.5M")]
        [InlineData(2000000, "2M")]
        public void FormatsKBelowOneMillionAndMFromThere(int tokens, string text) => Assert.Equal(text, ContextSize.Format(tokens));

        [Theory]
        [InlineData("1M", 1000000)]
        [InlineData("1000K", 1000000)]
        [InlineData("1000k", 1000000)]
        [InlineData(" 1.5m ", 1500000)]
        [InlineData("128", 128000)]
        [InlineData("64K", 64000)]
        public void ParsesKAndM(string text, int tokens)
        {
            Assert.True(ContextSize.TryParse(text, out int v));
            Assert.Equal(tokens, v);
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("0")]
        [InlineData("-5K")]
        [InlineData("1G")]
        [InlineData("1000M")]
        public void RejectsNonsense(string text) => Assert.False(ContextSize.TryParse(text, out _));
    }
}
