using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Storage;
using Xunit;

namespace UEToVS.Core.Tests
{
    public class DiagnosticsTests
    {
        [Fact]
        public void JsonLine_IsValidJsonWithFixedFields()
        {
            var e = new LogEvent
            {
                Timestamp = new DateTime(2026, 11, 12, 3, 4, 5, 678, DateTimeKind.Utc),
                Level = LogLevel.Error,
                Process = "engine",
                Module = "Index",
                Event = "index.parse.failed",
                Message = "bad \"file\"\n",
                RequestId = "r1",
                ElapsedMs = 12.345,
                Data = new[] { new System.Collections.Generic.KeyValuePair<string, object>("files", 3), new System.Collections.Generic.KeyValuePair<string, object>("path", "C:\\a.h") },
                Exception = new InvalidOperationException("outer", new ArgumentException("inner")),
            };
            using var doc = JsonDocument.Parse(JsonLineFormatter.Format(e));
            var r = doc.RootElement;
            Assert.Equal("2026-11-12T03:04:05.678Z", r.GetProperty("ts").GetString());
            Assert.Equal("error", r.GetProperty("lvl").GetString());
            Assert.Equal("index.parse.failed", r.GetProperty("evt").GetString());
            Assert.Equal("bad \"file\"\n", r.GetProperty("msg").GetString());
            Assert.Equal(3, r.GetProperty("data").GetProperty("files").GetInt32());
            Assert.Equal("C:\\a.h", r.GetProperty("data").GetProperty("path").GetString());
            Assert.Equal("System.ArgumentException", r.GetProperty("ex").GetProperty("inner").GetProperty("type").GetString());
        }

        [Fact]
        public void FileSink_WritesLinesUnderLogsFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = StoragePaths.ForSolution(dir);
                Assert.Equal(Path.Combine(Path.GetFullPath(dir), ".vs", "UEToVS", "Logs"), paths.Logs);
                using (var sink = new JsonLinesFileSink(paths.EnsureCreated().Logs, "engine"))
                {
                    Log.AddSink(sink);
                    Log.For("Test").Info("test.hello", "hi", ("n", 1));
                    Log.For("Test").Error("test.fail", "boom", new Exception("x"));
                    Log.Flush();
                    Log.RemoveSink(sink);
                }
                var file = Directory.GetFiles(paths.Logs, "engine-*.log").Single();
                var lines = File.ReadAllLines(file);
                Assert.Contains(lines, l => l.Contains("\"evt\":\"test.hello\""));
                Assert.Contains(lines, l => l.Contains("\"evt\":\"test.fail\"") && l.Contains("\"ex\""));
                foreach (var l in lines) JsonDocument.Parse(l).Dispose();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void LatencyStats_ComputesPercentiles()
        {
            var s = new LatencyStats();
            for (int i = 1; i <= 100; i++) s.Record("op", i, failed: i == 100);
            var row = s.SnapshotAndReset().Single();
            Assert.Equal(100, row.Count);
            Assert.Equal(1, row.Errors);
            Assert.Equal(50, row.P50);
            Assert.Equal(95, row.P95);
            Assert.Equal(100, row.Max);
            Assert.Empty(s.SnapshotAndReset());
        }
    }
}
