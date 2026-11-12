using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Diagnostics;
using UEToVS.Host.Logic.Performance;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class DiagnosticsPackageTests
    {
        [Fact]
        public void PackageContainsLogsSettingsManifestAndExtras()
        {
            using (var t = new TempDir())
            {
                string root = t.Sub(".vs", "UEToVS");
                t.File(Path.Combine(".vs", "UEToVS", "Logs", "host-1.log"), "{\"evt\":\"session.start\"}\n");
                string engineLog = t.File(Path.Combine(".vs", "UEToVS", "Logs", "engine-1.log"), "{}\n");
                t.File(Path.Combine(".vs", "UEToVS", "Index", "base", "symbols.seg"), "xxxx");

                // The engine log is still open for writing (as in production).
                using (new FileStream(engineLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    var r = DiagnosticsPackageBuilder.Create(root, Path.Combine(root, "Diagnostics"), "{\"a\":1}",
                        new Dictionary<string, string> { ["engine.json"] = "{}" }, new DateTime(2026, 11, 12, 10, 0, 0));
                    Assert.True(File.Exists(r.ZipPath));
                    Assert.Equal(2, r.LogFiles);
                    Assert.True(r.IndexManifestIncluded);
                    using (var zip = ZipFile.OpenRead(r.ZipPath))
                    {
                        var names = zip.Entries.Select(e => e.FullName).ToList();
                        Assert.Contains("Logs/host-1.log", names);
                        Assert.Contains("Logs/engine-1.log", names);
                        Assert.Contains("Index/listing.tsv", names);
                        Assert.Contains("settings.json", names);
                        Assert.Contains("engine.json", names);
                        Assert.Contains("README.txt", names);
                    }
                    var r2 = DiagnosticsPackageBuilder.Create(root, Path.Combine(root, "Diagnostics"), null, null, new DateTime(2026, 11, 12, 10, 0, 0));
                    Assert.NotEqual(r.ZipPath, r2.ZipPath); // same second → unique name
                }
            }
        }

        [Fact]
        public void EngineManifestPreferredOverListing()
        {
            using (var t = new TempDir())
            {
                string root = t.Sub("UEToVS");
                t.File(Path.Combine("UEToVS", "Index", "manifest.json"), "{\"segments\":[]}");
                var r = DiagnosticsPackageBuilder.Create(root, t.Sub("out"), null, null);
                using (var zip = ZipFile.OpenRead(r.ZipPath))
                    Assert.Contains(zip.Entries, e => e.FullName == "Index/manifest.json");
            }
        }

        [Fact]
        public void SanitizerReplacesProfileAndUserName()
        {
            string text = "{\"path\":\"C:\\\\Users\\\\alice\\\\Projects\",\"p2\":\"C:/Users/alice/x\",\"who\":\"alice\",\"other\":\"malice\"}";
            string s = Sanitizer.Sanitize(text, "alice", @"C:\Users\alice");
            Assert.DoesNotContain("Users\\\\alice", s);
            Assert.DoesNotContain("Users/alice", s);
            Assert.Contains("%USERPROFILE%", s);
            Assert.Contains("\"who\":\"<user>\"", s);
            Assert.Contains("malice", s); // only whole-word user names

            var masked = Sanitizer.MaskSecrets(new Dictionary<string, object> { ["apiToken"] = "abc", ["level"] = "Info", ["password"] = "" });
            Assert.Equal("***", masked["apiToken"]);
            Assert.Equal("Info", masked["level"]);
            Assert.Equal("", masked["password"]);
        }

        [Fact]
        public void RetentionDeletesOldAndOversizedButNeverActiveFiles()
        {
            using (var t = new TempDir())
            {
                var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
                string old = t.File("old.log", "x");
                File.SetLastWriteTimeUtc(old, now.AddDays(-10));
                string big1 = t.File("big1.log", new string('x', 600));
                File.SetLastWriteTimeUtc(big1, now.AddHours(-2));
                string big2 = t.File("big2.log", new string('x', 600));
                File.SetLastWriteTimeUtc(big2, now.AddHours(-3));
                string active = t.File("active.log", new string('x', 2000));
                File.SetLastWriteTimeUtc(active, now.AddMinutes(-1));
                string kept = t.File("current.log", "x");
                File.SetLastWriteTimeUtc(kept, now.AddDays(-30));

                var deleted = LogRetention.Apply(t.Path, 7, 2700, new[] { kept }, now);
                Assert.Contains("old.log", deleted);
                Assert.Contains("big2.log", deleted); // total exceeded (2000+600+600 > 2700)
                Assert.DoesNotContain("big1.log", deleted);
                Assert.True(File.Exists(active)); // recently written: may belong to a running process
                Assert.True(File.Exists(kept));
            }
        }
    }

    public class PerformanceDataTests
    {
        [Fact]
        public void HistoryIsABoundedRing()
        {
            var h = new SampleHistory<int>(3);
            for (int i = 1; i <= 5; i++) h.Add(i);
            Assert.Equal(new[] { 3, 4, 5 }, h.ToArray());
            Assert.Equal(3, h.Count);
            h.Clear();
            Assert.Empty(h.ToArray());
        }

        [Fact]
        public void LatencySummariesMapToOperationStatsWithBudgets()
        {
            var stats = new LatencyStats();
            for (int i = 0; i < 10; i++) stats.Record("Commands/ui.command.invoke", 20);
            stats.Record("Engine/rpc.search", 5);
            var ops = PerformanceDataBuilder.FromLatency(stats.Snapshot());
            var ui = ops.Single(o => o.Name == "Commands/ui.command.invoke");
            Assert.Equal(10, ui.Count);
            Assert.Equal(PerformanceDataBuilder.UiThreadBudgetMs, ui.BudgetMs);
            Assert.True(PerformanceDataBuilder.IsOverBudget(ui));
            var rpc = ops.Single(o => o.Name == "Engine/rpc.search");
            Assert.Equal(100, rpc.BudgetMs);
            Assert.False(PerformanceDataBuilder.IsOverBudget(rpc));
            Assert.Equal("Commands/ui.command.invoke", ops[0].Name); // most frequent first
            Assert.Equal(0, PerformanceDataBuilder.BudgetFor("Other/thing"));
        }

        [Fact]
        public void HostSampleDelta()
        {
            var s = new HostResourceSample { PrivateBytes = 300, PrivateBytesAtLoad = 100 };
            Assert.Equal(200, s.PrivateBytesDelta);
        }
    }
}
