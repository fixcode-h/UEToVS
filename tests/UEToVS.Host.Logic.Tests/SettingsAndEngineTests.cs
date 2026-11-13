using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Engine;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class SettingsMergerTests
    {
        [Fact]
        public void NoSolutionFileKeepsUserSettings()
        {
            var user = new HostSettings { LogLevel = LogLevel.Debug };
            var r = SettingsMerger.Merge(user, null);
            Assert.Equal(LogLevel.Debug, r.Effective.LogLevel);
            Assert.NotSame(user, r.Effective);
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public void NestedAndDottedKeysOverride()
        {
            const string json = @"{
              // comments are allowed
              ""log"": { ""level"": ""trace"", ""retentionDays"": 3 },
              ""intelliSense.mode"": ""UnrealSolutionOnly"",
              ""intelliSense"": { ""takeover"": false },
              ""engine"": { ""unrealEngineRoot"": ""D:/UE_5.4"" },
              ""naming"": { ""prefix"": ""A"" }
            }";
            var user = new HostSettings();
            var r = SettingsMerger.Merge(user, json);
            Assert.Equal(LogLevel.Trace, r.Effective.LogLevel);
            Assert.Equal(3, r.Effective.LogRetentionDays);
            Assert.Equal(IntelliSenseTakeoverMode.Off, r.Effective.IntelliSenseTakeoverMode); // legacy takeover=false → scope Off
            Assert.False(r.Effective.DisableDatabase);
            Assert.Equal("D:/UE_5.4", r.Effective.UnrealEngineRootOverride);
            Assert.Contains("naming.prefix", r.ForeignKeys);
            Assert.Empty(r.Warnings);
            Assert.Equal(5, r.SolutionOverrides.Count);
            Assert.Equal(LogLevel.Info, user.LogLevel); // user layer untouched
        }

        [Fact]
        public void VsBackgroundFeaturesFollowTheTakeoverScope()
        {
            // v0.5.1: no switches of their own; on by default (UE solutions), all restored with scope Off
            var d = new HostSettings();
            Assert.Equal(IntelliSenseTakeoverMode.UnrealSolutionOnly, d.IntelliSenseTakeoverMode);
            Assert.True(d.IntelliSenseTakeover);
            Assert.True(d.DisableDatabase);
            Assert.True(d.DisableBackgroundCodeAnalysis);
            Assert.False(d.VsBlueprintSupport);
            var off = new HostSettings { IntelliSenseTakeoverMode = IntelliSenseTakeoverMode.Off };
            Assert.False(off.IntelliSenseTakeover);
            Assert.False(off.DisableDatabase);
            Assert.False(off.DisableBackgroundCodeAnalysis);
            Assert.True(off.VsBlueprintSupport);
            off.IntelliSenseTakeover = true;
            Assert.Equal(IntelliSenseTakeoverMode.UnrealSolutionOnly, off.IntelliSenseTakeoverMode);
            var global = new HostSettings { IntelliSenseTakeoverMode = IntelliSenseTakeoverMode.Global };
            global.IntelliSenseTakeover = true;
            Assert.Equal(IntelliSenseTakeoverMode.Global, global.IntelliSenseTakeoverMode);
            var r = SettingsMerger.Merge(new HostSettings(), @"{ ""intelliSense"": { ""mode"": ""Off"" } }");
            Assert.False(r.Effective.DisableBackgroundCodeAnalysis);
            Assert.Empty(r.Warnings);
            // the retired switches are no longer settings
            var schema = HostSettingsSchema.Build(8);
            foreach (var key in SettingsUpgrade.RetiredKeys) Assert.DoesNotContain(schema, x => x.Key == key);
            Assert.Contains(schema.Single(x => x.Key == "intelliSense.mode").Options, o => o.Value == nameof(IntelliSenseTakeoverMode.Off));
        }

        [Fact]
        public void LiveCodingIsOffByDefault()
        {
            // v0.5.1: LiveCodingConsole can hold several GB, so UEToVS does not use Live Coding unless asked to
            var d = new HostSettings();
            Assert.False(d.LinkLiveCodingEnabled);
            Assert.False(d.LiveCodingErrorsActive);
            Assert.True(new HostSettings { LinkLiveCodingEnabled = true }.LiveCodingErrorsActive);
            var schema = HostSettingsSchema.Build(8);
            Assert.Equal("false", schema.Single(x => x.Key == "link.liveCoding").Default);
            Assert.Equal("link.liveCoding", schema.Single(x => x.Key == "link.liveCodingErrors").EnabledWhenKey);
            var back = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string> { ["link.liveCoding"] = "true" }, schema, new HostSettings());
            Assert.True(back.LinkLiveCodingEnabled);
        }

        [Fact]
        public void RetiredTakeoverSwitchMigratesToScopeOff()
        {
            Assert.Equal(nameof(IntelliSenseTakeoverMode.Off),
                SettingsUpgrade.MigrateRetired(new Dictionary<string, string> { ["intelliSense.takeover"] = "false" })["intelliSense.mode"]);
            Assert.Empty(SettingsUpgrade.MigrateRetired(new Dictionary<string, string> { ["intelliSense.takeover"] = "true", ["vs.blueprintSupport"] = "true" }));
            Assert.Empty(SettingsUpgrade.MigrateRetired(null));
        }

        [Fact]
        public void EnginePathIsRefusedFromSolutionFiles()
        {
            var r = SettingsMerger.Merge(new HostSettings(), @"{ ""engine"": { ""path"": ""C:/evil.exe"" } }");
            Assert.Equal(string.Empty, r.Effective.EnginePathOverride);
            Assert.Single(r.Warnings);
        }

        [Fact]
        public void BadValuesAndUnknownKeysWarn()
        {
            var r = SettingsMerger.Merge(new HostSettings(), @"{ ""log"": { ""level"": ""loud"", ""retentionDays"": -1, ""colour"": 1 }, ""intelliSense"": { ""takeover"": ""maybe"" } }");
            Assert.Equal(4, r.Warnings.Count);
            Assert.Equal(LogLevel.Info, r.Effective.LogLevel);
            Assert.Equal(7, r.Effective.LogRetentionDays);
            Assert.True(r.Effective.IntelliSenseTakeover); // unchanged default (on since M3, ADR-08)
        }

        [Fact]
        public void InvalidJsonWarnsAndKeepsUser()
        {
            var r = SettingsMerger.Merge(new HostSettings { LogMaxTotalMB = 50 }, "{ nope");
            Assert.Single(r.Warnings);
            Assert.Equal(50, r.Effective.LogMaxTotalMB);
        }

        [Fact]
        public void ReadsFromSolutionFolder()
        {
            using (var t = new TempDir())
            {
                t.File(Path.Combine(".uetovs", "settings.json"), @"{ ""log.level"": ""Warn"" }");
                var r = SettingsMerger.MergeFromSolution(new HostSettings(), t.Path);
                Assert.Equal(LogLevel.Warn, r.Effective.LogLevel);
                Assert.EndsWith("settings.json", r.SolutionFile);
                var none = SettingsMerger.MergeFromSolution(new HostSettings(), Path.Combine(t.Path, "other"));
                Assert.Null(none.SolutionFile);
            }
        }
    }

    public class RestartPolicyTests
    {
        [Fact]
        public void ExponentialBackoffAndBudget()
        {
            var p = new RestartPolicy(3, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(p.TryScheduleRestart(t0, out var d1, out int a1));
            Assert.True(p.TryScheduleRestart(t0.AddSeconds(5), out var d2, out _));
            Assert.True(p.TryScheduleRestart(t0.AddSeconds(10), out var d3, out int a3));
            Assert.Equal(TimeSpan.FromSeconds(1), d1);
            Assert.Equal(TimeSpan.FromSeconds(2), d2);
            Assert.Equal(TimeSpan.FromSeconds(4), d3);
            Assert.Equal(1, a1);
            Assert.Equal(3, a3);
            Assert.False(p.TryScheduleRestart(t0.AddMinutes(5), out _, out int a4));
            Assert.Equal(4, a4);

            // The window slides: the first restart drops out after 10 minutes.
            Assert.True(p.TryScheduleRestart(t0.AddMinutes(10).AddSeconds(1), out var d5, out _));
            Assert.Equal(TimeSpan.FromSeconds(4), d5);
        }

        [Fact]
        public void ManualResetRestoresBudgetAndDelayIsCapped()
        {
            var p = new RestartPolicy(3);
            var now = DateTime.UtcNow;
            for (int i = 0; i < 3; i++) Assert.True(p.TryScheduleRestart(now, out _, out _));
            Assert.False(p.TryScheduleRestart(now, out _, out _));
            p.Reset();
            Assert.True(p.TryScheduleRestart(now, out _, out _));
            Assert.Equal(TimeSpan.FromSeconds(30), p.DelayFor(50));
        }
    }

    public class EngineLaunchTests
    {
        [Fact]
        public void ArgumentsMatchTheEngineContract()
        {
            string args = EngineLaunch.BuildArguments("uetovs-1-abc", @"C:\My Projects\Game", 1234, "Info");
            Assert.Equal("--pipe uetovs-1-abc --solution-dir \"C:\\My Projects\\Game\" --parent 1234 --log-level info", args);
            string full = EngineLaunch.BuildArguments("p", "D", 1, "Debug", @"D\G.uproject", @"E:\UE 5.4");
            Assert.Equal("--pipe p --solution-dir D --project D\\G.uproject --engine \"E:\\UE 5.4\" --parent 1 --log-level debug", full);
        }

        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("", "\"\"")]
        [InlineData("a b", "\"a b\"")]
        [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        public void QuotesLikeCommandLineToArgv(string input, string expected)
        {
            Assert.Equal(expected, EngineLaunch.Quote(input));
        }

        [Fact]
        public void ResolvesBundledOrOverride()
        {
            using (var t = new TempDir())
            {
                string bundled = EngineLaunch.ResolveExecutable(t.Path, "", out string s1);
                Assert.Equal("bundled", s1);
                Assert.Equal(Path.Combine(t.Path, "Engine", EngineLaunch.ExeName), bundled);

                string folder = t.Sub("mybuild");
                Assert.Equal(Path.Combine(folder, EngineLaunch.ExeName), EngineLaunch.ResolveExecutable(t.Path, "\"" + folder + "\"", out string s2));
                Assert.Equal("override", s2);
                string exe = Path.Combine(folder, "Custom.exe");
                Assert.Equal(exe, EngineLaunch.ResolveExecutable(t.Path, exe, out _));
            }
        }

        [Fact]
        public void MissingRuntimeExitCodes()
        {
            Assert.True(EngineLaunch.IsMissingRuntimeExitCode(unchecked((int)0x80008096)));
            Assert.False(EngineLaunch.IsMissingRuntimeExitCode(1));
        }

        [Fact]
        public void PipeNameIsSharedWithTheEngine()
        {
            Assert.Equal(ProtocolInfo.PipeName(42, @"C:\Game"), ProtocolInfo.PipeName(42, @"c:\game"));
            Assert.StartsWith("uetovs-42-", ProtocolInfo.PipeName(42, @"C:\Game"));
        }
    }
}
