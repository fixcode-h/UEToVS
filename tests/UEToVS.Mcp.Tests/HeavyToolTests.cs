using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>v0.4.5 run_project_inspection: asynchronous start / status / results through a running engine; refused when switched off.</summary>
    public sealed class HeavyToolTests : IClassFixture<LiveFixture>
    {
        private readonly LiveFixture _f;

        public HeavyToolTests(LiveFixture f) => _f = f;

        [Fact]
        public async Task ProjectInspection_StartPollResults_ThenDisabled()
        {
            var start = await _f.Call("run_project_inspection", new { action = "start" });
            Assert.Equal("engine", start.Str("source"));
            Assert.Contains(start.Str("state"), new[] { "Running", "Completed" });
            Assert.EndsWith(".jsonl", start.Str("resultsFile"));
            Assert.True(_f.Host.Inspection.Wait(TimeSpan.FromSeconds(60)));

            var status = await _f.Call("run_project_inspection", new { action = "status" });
            Assert.Equal("Completed", status.Str("state"));
            Assert.Equal(status.Int("filesTotal"), status.Int("filesDone"));
            Assert.True(status.Int("filesTotal") > 0);

            var results = await _f.Call("run_project_inspection", new { action = "results", limit = 5 });
            Assert.Equal(status.Int("findings"), results.Int("matched"));
            Assert.True(results.Items().Count() <= 5);
            Assert.All(results.Items(), i => Assert.StartsWith("UE", i.Str("rule")));

            await _f.Call("run_project_inspection", new { action = "nope" }, expectError: true);

            _f.Host.Inspection.Enabled = false;
            try
            {
                var off = await _f.Call("run_project_inspection", new { action = "start" }, expectError: true);
                Assert.Equal("feature_disabled", off.Str("error"));
            }
            finally
            {
                _f.Host.Inspection.Enabled = true;
            }
        }
    }
}
