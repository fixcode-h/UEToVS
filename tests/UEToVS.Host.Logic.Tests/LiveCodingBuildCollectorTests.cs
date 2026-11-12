using UEToVS.Host.Logic.Build;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class LiveCodingBuildCollectorTests
    {
        [Theory]
        [InlineData("[2024.01.01-10.00.00:000][  0]LogLiveCoding: Error: C:\\P\\A.cpp(3): error C2065: 'x': undeclared identifier",
                    "C:\\P\\A.cpp(3): error C2065: 'x': undeclared identifier")]
        [InlineData("LogLiveCoding: Display: Building patch", "Building patch")]
        [InlineData("C:\\P\\A.cpp(3): error C2065: x", "C:\\P\\A.cpp(3): error C2065: x")]
        public void StripsUeLogPrefix(string line, string expected) =>
            Assert.Equal(expected, LiveCodingBuildCollector.StripUePrefix(line));

        [Fact]
        public void ParsesErrorsAndReportsFailed()
        {
            var c = new LiveCodingBuildCollector();
            BuildRecord got = null;
            c.Completed += r => got = r;
            c.BuildStarted("liveCoding", "C:\\P");
            c.OutputLine("liveCoding", "[2024.01.01-10.00.00:000][  0]LogLiveCoding: Display: Compiling A.cpp");
            c.OutputLine("liveCoding", "[2024.01.01-10.00.00:000][  0]LogLiveCoding: Error: C:\\P\\Source\\A.cpp(12,5): error C2065: 'Foo': undeclared identifier");
            c.BuildFinished("liveCoding", true, "success"); // the editor said success, but there is an error
            Assert.NotNull(got);
            Assert.Equal("failed", got.Result);
            Assert.Equal(1, got.Errors);
            Assert.Equal(LiveCodingBuildCollector.RecordSource, got.Source);
        }

        [Fact]
        public void FinishWithoutStartIsIgnored()
        {
            var c = new LiveCodingBuildCollector();
            bool raised = false;
            c.Completed += r => raised = true;
            c.BuildFinished("liveCoding", true, "success");
            Assert.False(raised);
        }
    }
}
