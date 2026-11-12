using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Inspection;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.4.5 heavy features, host side: settings switches, commands, grouping, batch fix / replace plans.</summary>
    public class HeavyFeatureLogicTests
    {
        private static InspectionFinding F(string code, string file, int line, int col = 0, params FileTextEdit[] fix) => new InspectionFinding
        {
            Code = code, FilePath = file, Line = line, Column = col, Length = 3, Message = code, Args = new List<string> { "Arg" + line },
            Fix = fix.Length == 0 ? null : fix.ToList(),
        };

        private static FileTextEdit E(string file, int line, int col, int len, string oldText, string newText) =>
            new FileTextEdit { FilePath = file, Line = line, Column = col, Length = len, OldText = oldText, NewText = newText };

        [Fact]
        public void Switches_AreOnByDefault_SayTheyAreHeavy_AndReachTheEngine()
        {
            var s = new HostSettings();
            Assert.True(s.ProjectInspectionEnabled);
            Assert.True(s.UpgradeAssistantEnabled);
            Assert.True(s.TemplateInstantiationEnabled);
            Assert.False(s.InspectionIncludePlugins);
            Assert.Equal(2048, s.TemplateCacheCapacity);

            var schema = HostSettingsSchema.Build(8);
            foreach (var key in new[] { "inspection.enabled", "upgrade.enabled", "templates.enabled" })
            {
                var d = schema.Single(x => x.Key == key);
                Assert.Equal("true", d.Default);
                Assert.Contains("可能占用较多 CPU/内存", d.DescriptionZh);
                Assert.Contains("CPU/memory", d.DescriptionEn);
            }
            Assert.Equal(HostSettingsSchema.IntelliSense, schema.Single(x => x.Key == "templates.enabled").Category);
            Assert.Equal(HostSettingsSchema.Inspection, schema.Single(x => x.Key == "inspection.enabled").Category);
            Assert.Contains(HostSettingsSchema.Categories, c => c.Id == HostSettingsSchema.Inspection);
            Assert.Equal("uetovs.inspection.run", schema.Single(x => x.Key == "inspection.run").ActionId);
            Assert.Equal("uetovs.upgrade.run", schema.Single(x => x.Key == "upgrade.run").ActionId);

            // round trip through the persisted values and into the engine settings
            s.ProjectInspectionEnabled = false;
            s.UpgradeAssistantEnabled = false;
            s.TemplateInstantiationEnabled = false;
            s.TemplateCacheCapacity = 4096;
            s.InspectionIncludePlugins = true;
            var values = HostSettingsSchema.Codec.ToValues(s);
            Assert.Equal("false", values["inspection.enabled"]);
            Assert.Equal("4096", values["templates.cacheCapacity"]);
            var back = HostSettingsSchema.Codec.FromValues(values, schema, new HostSettings());
            Assert.False(back.ProjectInspectionEnabled);
            Assert.False(back.UpgradeAssistantEnabled);
            Assert.False(back.TemplateInstantiationEnabled);
            Assert.True(back.InspectionIncludePlugins);
            Assert.Equal(4096, back.TemplateCacheCapacity);
            var idx = HostSettingsSchema.Codec.ToIndexing(back);
            Assert.False(idx.ProjectInspectionEnabled);
            Assert.False(idx.UpgradeAssistantEnabled);
            Assert.False(idx.TemplateInstantiationEnabled);
            Assert.Equal(4096, idx.TemplateCacheCapacity);
            Assert.True(new IndexingSettings().ProjectInspectionEnabled); // engine default = on as well
        }

        [Fact]
        public void Commands_UseTheReservedRangeAndDefaultKeys()
        {
            var expected = new Dictionary<int, string>
            {
                [CommandCatalog.RunProjectInspection] = "Ctrl+Shift+Alt+U, 6",
                [CommandCatalog.ShowInspectionResults] = "Ctrl+Shift+Alt+U, 7",
                [CommandCatalog.RunUpgradeAssistant] = "Ctrl+Shift+Alt+U, 8",
                [CommandCatalog.ShowUpgradeResults] = "Ctrl+Shift+Alt+U, 9",
                [CommandCatalog.CancelHeavyJob] = "Ctrl+Shift+Alt+U, U",
            };
            foreach (var kv in expected)
            {
                var c = CommandCatalog.ById(kv.Key);
                Assert.NotNull(c);
                Assert.InRange(c.Id, 0x01A0, 0x01AF);
                Assert.Equal(CommandOwner.Host, c.Owner);
                Assert.Equal(kv.Value, c.VaKeys);
                Assert.StartsWith("uetovs.", c.ActionId);
            }
        }

        [Fact]
        public void Group_ByRuleSortsByCount_ByFileByPath()
        {
            var items = new[]
            {
                F("UE0001", "C:/p/B.cpp", 5), F("UE0004", "C:/p/A.cpp", 2), F("UE0001", "C:/p/A.cpp", 9), F("UE0001", "C:/p/A.cpp", 1),
            };
            var byRule = InspectionView.Group(items, InspectionGrouping.Rule);
            Assert.Equal(new[] { "UE0001", "UE0004" }, byRule.Select(g => g.Key).ToArray());
            Assert.Equal(new[] { 1, 9, 5 }, byRule[0].Items.Select(i => i.Line).ToArray()); // file, then line
            Assert.Contains("(3)", byRule[0].Title.Replace("（", "(").Replace("）", ")"));
            var byFile = InspectionView.Group(items, InspectionGrouping.File);
            Assert.Equal(new[] { "C:/p/A.cpp", "C:/p/B.cpp" }, byFile.Select(g => g.Key).ToArray());
            Assert.StartsWith("A.cpp", byFile[0].Title);
        }

        [Fact]
        public void MatchesText_LooksAtFileRuleArgsAndLine()
        {
            var f = F("UE0004", "C:/p/Gadget.cpp", 3);
            f.LineText = "void AGadget::ServerUse()";
            Assert.True(InspectionView.MatchesText(f, null));
            Assert.True(InspectionView.MatchesText(f, "gadget.cpp"));
            Assert.True(InspectionView.MatchesText(f, "ue0004"));
            Assert.True(InspectionView.MatchesText(f, "ServerUse"));
            Assert.True(InspectionView.MatchesText(f, "Arg3"));
            Assert.False(InspectionView.MatchesText(f, "Health"));
        }

        [Fact]
        public void PlanFixes_DedupesAndDropsOverlaps()
        {
            var a = F("UE0004", "C:/p/A.cpp", 2, 0, E("C:/p/A.cpp", 2, 14, 9, "ServerUse", "ServerUse_Implementation"));
            var dup = F("UE0004", "C:/p/A.cpp", 2, 0, E("c:/P/a.cpp", 2, 14, 9, "ServerUse", "ServerUse_Implementation"));
            var overlap = F("UE0004", "C:/p/A.cpp", 2, 0, E("C:/p/A.cpp", 2, 15, 2, "rv", "X"));
            var other = F("UE0003", "C:/p/B.h", 4, 0, E("C:/p/B.h", 5, 0, 0, "", "\tGENERATED_BODY()\n"));
            var none = F("UE0001", "C:/p/C.cpp", 1);
            var plan = InspectionView.PlanFixes(new[] { a, dup, overlap, other, none }, out int dropped);
            Assert.Equal(2, plan.Count);
            Assert.Equal(1, dropped);
            Assert.Equal("ServerUse", plan[0].OldText);
            Assert.Equal("\tGENERATED_BODY()\n", plan[1].NewText);

            // applying the plan to the text: one edit per place, OldText guards a changed file
            string text = "x\nx\nvoid AGadget::ServerUse()\n";
            var applied = TextEditApplier.Apply(text, plan.Where(p => p.FilePath == "C:/p/A.cpp"), out int skipped);
            Assert.Equal("x\nx\nvoid AGadget::ServerUse_Implementation()\n", applied);
            Assert.Equal(0, skipped);
        }

        [Fact]
        public void Upgrade_GroupsByVersionNewestFirst_AndRenamesExactUsagesOnly()
        {
            DeprecatedApi Api(string name, string version, string replacement, params (int Line, ResolutionConfidence C)[] usages) => new DeprecatedApi
            {
                Symbol = new SymbolInfo { Name = name, Container = "UThing" }, Version = version, Replacement = replacement,
                Usages = usages.Select(u => new DeprecatedUsage { FilePath = "C:/p/U.cpp", Line = u.Line, Column = 4, Length = name.Length, Confidence = u.C }).ToList(),
            };
            var apis = new[]
            {
                Api("GetOldName", "5.1", "GetNewName", (3, ResolutionConfidence.Exact), (7, ResolutionConfidence.NameOnly)),
                Api("SetValue", "5.10", null, (4, ResolutionConfidence.Exact)),
                Api("Legacy", "", null),
                Api("Older", "4.27", "Newer", (9, ResolutionConfidence.Exact)),
            };
            var groups = UpgradeView.GroupByVersion(apis);
            Assert.Equal(new[] { "5.10", "5.1", "4.27", "" }, groups.Select(g => g.Version).ToArray());
            Assert.Equal(2, UpgradeView.Replaceable(apis));
            var plan = UpgradeView.PlanReplacements(apis);
            Assert.Equal(2, plan.Count);
            Assert.Equal("GetOldName", plan[0].OldText);
            Assert.Equal("GetNewName", plan[0].NewText);
            Assert.Equal(3, plan[0].Line);
            Assert.Equal("Newer", plan[1].NewText);
            Assert.True(UpgradeView.CompareVersions("5.10", "5.9") > 0);
            Assert.True(UpgradeView.CompareVersions("", "4.0") < 0);
        }

        [Fact]
        public void StatusTexts_CoverEveryState()
        {
            Assert.Equal(HeavyFeatureStrings.InspectionDisabled, InspectionView.StatusText(new ProjectInspectionResponse { State = HeavyJobState.Disabled }));
            Assert.Equal(HeavyFeatureStrings.IndexNotReady, InspectionView.StatusText(new ProjectInspectionResponse { State = HeavyJobState.Failed, Error = "index-not-ready" }));
            Assert.Contains("3", InspectionView.StatusText(new ProjectInspectionResponse { State = HeavyJobState.Running, FilesDone = 3, FilesTotal = 10, Findings = 1 }));
            Assert.Equal(HeavyFeatureStrings.EngineUnavailable, InspectionView.StatusText(null));
            Assert.Equal(HeavyFeatureStrings.UpgradeNoIndexData, UpgradeView.StatusText(new DeprecationScanResponse { State = HeavyJobState.Completed, IndexHasDeprecations = false }));
            Assert.Equal(HeavyFeatureStrings.UpgradeDisabled, UpgradeView.StatusText(new DeprecationScanResponse { State = HeavyJobState.Disabled }));
            Assert.True(InspectionView.IsFinished(HeavyJobState.Cancelled));
            Assert.False(InspectionView.IsFinished(HeavyJobState.Running));
            Assert.Equal(500, InspectionView.PollDelayMs(TimeSpan.FromSeconds(1)));
            Assert.Equal(2000, InspectionView.PollDelayMs(TimeSpan.FromMinutes(5)));
            foreach (var code in new[] { "UE0001", "UE0002", "UE0003", "UE0004", "UE0005", "UE0006", "UE0007", "UE0008", "UE0009", "UE0010" })
                Assert.NotEqual(code, HeavyFeatureStrings.RuleName(code));
        }
    }
}
