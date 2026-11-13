using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    // v0.4.5 navigation gaps: call hierarchy model, takeover feature table, GENERATED_BODY Quick Info, strings.
    public class CallHierarchyModelTests
    {
        private static SymbolInfo Fn(string container, string name, string file = @"C:\P\Source\Game\A.cpp", int line = 10) =>
            new SymbolInfo { Name = name, Container = container, Kind = SymbolKind.Method, Location = new SourceLocation { FilePath = file, Line = line, Column = 4 } };

        private static CallSite Site(string file, int line, CallSiteKind kind = CallSiteKind.Call, ResolutionConfidence c = ResolutionConfidence.Exact, string text = "Fire();") =>
            new CallSite { Location = new SourceLocation { FilePath = file, Line = line, Column = 2 }, Length = 4, Kind = kind, Confidence = c, LineText = text, LineTextColumn = 0, BindMethod = kind == CallSiteKind.DelegateBinding ? "AddDynamic" : null };

        private static CallHierarchyResponse Callers(SymbolInfo root, params CallHierarchyItem[] items) =>
            new CallHierarchyResponse { Root = root, Direction = CallHierarchyDirection.Callers, Items = items.ToList(), IndexReady = true, ElapsedMs = 12 };

        [Fact]
        public void BuildsOneLevelWithLoadingPlaceholdersAndSiteRows()
        {
            var fire = Fn("AMyActor", "Fire");
            var tick = new CallHierarchyItem { Function = Fn("AMyActor", "Tick", line: 30), CanExpand = true, Confidence = ResolutionConfidence.Exact, Sites = { Site(@"C:\P\A.cpp", 31), Site(@"C:\P\A.cpp", 35) } };
            var bind = new CallHierarchyItem { Function = Fn("AMyActor", "BeginPlay", line: 50), CanExpand = true, Confidence = ResolutionConfidence.Exact, Sites = { Site(@"C:\P\A.cpp", 52, CallSiteKind.DelegateBinding) } };
            var root = CallHierarchyModel.CreateRoot(Callers(fire, tick, bind));

            Assert.True(root.Loaded);
            Assert.Equal(2, root.Children.Count);
            var t = root.Children[0];
            Assert.Equal(new[] { CallNodeKind.Site, CallNodeKind.Site, CallNodeKind.Loading }, t.Children.Select(c => c.Kind));
            Assert.True(t.CanLoad);
            Assert.Contains("2", CallHierarchyModel.Right(t, CallHierarchyDirection.Callers));
            // callers jump to the call site, not to the caller's declaration
            Assert.Equal(31, CallHierarchyModel.TargetOf(t, CallHierarchyDirection.Callers).Line);
            Assert.Equal(30, CallHierarchyModel.TargetOf(t, CallHierarchyDirection.Callees).Line);
            Assert.Contains("AddDynamic", CallHierarchyModel.Detail(root.Children[1], CallHierarchyDirection.Callers));
            Assert.Contains("1", CallHierarchyModel.Summary(Callers(fire, tick, bind)));

            // expanding Tick: its callers replace "Loading…"
            CallHierarchyModel.ApplyLevel(t, Callers(tick.Function));
            Assert.False(t.CanLoad);
            Assert.Equal(CallNodeKind.Message, t.Children.Last().Kind);
            Assert.DoesNotContain(t.Children, c => c.Kind == CallNodeKind.Loading);
        }

        [Fact]
        public void RecursionAndImplementationSuffixesAreNotExpandedAgain()
        {
            var fire = Fn("AMyActor", "Fire");
            var self = new CallHierarchyItem { Function = Fn("AMyActor", "Fire_Implementation", line: 70), CanExpand = true, Sites = { Site(@"C:\P\A.cpp", 71) } };
            var root = CallHierarchyModel.CreateRoot(Callers(fire, self));
            var n = root.Children.Single();
            Assert.True(n.IsRecursive);
            Assert.False(n.CanLoad);
            Assert.Equal(CallHierarchyModel.FunctionKey(fire), CallHierarchyModel.FunctionKey(self.Function));
            Assert.Contains(NavGapStrings.Recursive, CallHierarchyModel.Detail(n, CallHierarchyDirection.Callers));
        }

        [Fact]
        public void FiltersByWordsAndHidesUncertainCallsUnlessAsked()
        {
            var fire = Fn("AMyActor", "Fire");
            var exact = new CallHierarchyItem { Function = Fn("AMyActor", "Tick"), CanExpand = true, Confidence = ResolutionConfidence.Exact, Sites = { Site(@"C:\P\A.cpp", 31) } };
            var guess = new CallHierarchyItem { Function = Fn("UOther", "Run"), CanExpand = true, Confidence = ResolutionConfidence.NameOnly, Sites = { Site(@"C:\P\B.cpp", 5, c: ResolutionConfidence.NameOnly) } };
            var root = CallHierarchyModel.CreateRoot(Callers(fire, exact, guess));
            var a = root.Children[0];
            var b = root.Children[1];
            Assert.True(CallHierarchyModel.IsUncertain(b));
            Assert.False(CallHierarchyModel.IsVisible(b, null, false));
            Assert.True(CallHierarchyModel.IsVisible(b, null, true));
            Assert.True(CallHierarchyModel.IsVisible(a, "tick", false));
            Assert.False(CallHierarchyModel.IsVisible(a, "tick zzz", false));
            Assert.True(CallHierarchyModel.IsVisible(root, "zzz", false)); // the root always stays
            // a site row stays visible when its function matches
            CallHierarchyModel.ApplyLevel(a, Callers(exact.Function));
            Assert.True(CallHierarchyModel.IsVisible(a.Children[0], "AMyActor::Tick", false));
        }

        [Fact]
        public void EmptyLevelsSayWhy()
        {
            var root = CallHierarchyModel.CreateRoot(new CallHierarchyResponse { Root = Fn("A", "F"), Direction = CallHierarchyDirection.Callees, Reason = "no-body" });
            Assert.Equal(NavGapStrings.NoBody, root.Children.Single().Message);
            var failed = new CallNode { Kind = CallNodeKind.Function, Function = Fn("A", "G"), Loaded = true };
            CallHierarchyModel.FailLevel(failed, null);
            Assert.True(failed.CanLoad); // expanding again retries
            Assert.Equal(NavGapStrings.LevelFailed, failed.Children.Single().Message);
            Assert.Equal(3, CallHierarchyModel.Walk(CallHierarchyModel.CreateRoot(Callers(Fn("A", "F"), new CallHierarchyItem { DisplayName = "(file scope) A.cpp", Sites = { Site("a", 1) } }))).Count() + 1);
        }
    }

    public class TakeoverFeaturesTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        [Fact]
        public void CoversTheRequiredFeaturesAndEveryReplacementIsACommand()
        {
            var en = TakeoverFeatures.All.Select(f => f.FeatureEn).ToList();
            foreach (var name in new[] { "Call Hierarchy", "Peek Definition", "Class View", "Object Browser", "Find All References", "Go To Definition", "Quick Info", "Parameter Info", "List Members", "Rename", "Extract Function", "Code Snippets" })
                Assert.Contains(en, f => f.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var f in TakeoverFeatures.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(f.FeatureZh));
                foreach (var id in f.Replacements) Assert.NotNull(CommandCatalog.ById(id));
                // a feature that stops working names a replacement or says there is none
                if (f.State == TakeoverFeatureState.Unavailable) Assert.True(f.Replacements.Length > 0 || f.NoteEn != null, f.FeatureEn);
            }
            Assert.Contains(TakeoverFeatures.All, f => f.State == TakeoverFeatureState.Unknown);
            Assert.Contains(TakeoverFeatures.Unavailable, f => f.Replacements.Contains(CommandCatalog.CallHierarchy));
        }

        [Fact]
        public void ListsTheVsBackgroundFeaturesTheScopeTurnsOff()
        {
            var off = TakeoverFeatures.All.Where(f => f.State == TakeoverFeatureState.TurnedOff).Select(f => f.FeatureEn).ToList();
            Assert.Contains(off, f => f.Contains("browse database"));
            Assert.Contains(off, f => f.Contains("Background code analysis"));
            Assert.Contains(off, f => f.Contains("Blueprint support"));
            Assert.Contains(off, f => f.Contains("semantic index"));
            Assert.Contains(off, f => f.Contains("UHT code analysis"));
            Assert.Contains(off, f => f.StartsWith("CodeLens"));
            Loc.Language = UiLanguage.Chinese;
            Assert.Equal("已关闭", TakeoverFeatures.StateText(TakeoverFeatureState.TurnedOff));
            // v0.5.1: "检查更新" is a page of its own, the last one
            Assert.Equal(HostSettingsSchema.Updates, HostSettingsSchema.Categories.Last().Id);
            Assert.Equal(HostSettingsSchema.Updates, HostSettingsSchema.Build(8).Single(x => x.Key == "update.check").Category);
        }

        [Fact]
        public void TextsFollowTheUiLanguage()
        {
            var ch = TakeoverFeatures.All.First(f => f.FeatureEn == "Call Hierarchy");
            Loc.Language = UiLanguage.English;
            Assert.Equal("Call Hierarchy (Ctrl+K, Ctrl+T)", TakeoverFeatures.FeatureText(ch));
            Assert.Equal("Call Hierarchy (Ctrl+Shift+Alt+U, H)", TakeoverFeatures.ReplacementText(ch));
            Assert.Equal("To be confirmed in VS", TakeoverFeatures.StateText(TakeoverFeatureState.Unknown));
            Loc.Language = UiLanguage.Chinese;
            Assert.Equal("调用层次（Ctrl+Shift+Alt+U, H）", TakeoverFeatures.ReplacementText(ch));
            Assert.Equal("需在 VS 中确认", TakeoverFeatures.StateText(TakeoverFeatureState.Unknown));
            Assert.Equal("暂无替代", TakeoverFeatures.ReplacementText(TakeoverFeatures.All.First(f => f.FeatureEn.StartsWith("Extract", StringComparison.Ordinal))));
        }

        [Fact]
        public void SettingsSchemaShowsTheTableUnderCppIntelliSense()
        {
            var d = HostSettingsSchema.Build(8).Single(x => x.ActionId == TakeoverFeatures.SettingsInfoId);
            Assert.Equal(UEToVS.UI.Logic.SettingKind.Info, d.Kind);
            Assert.Equal(HostSettingsSchema.General, d.Category);
            Assert.Equal("C++ IntelliSense", d.GroupEn);
        }
    }

    public class GeneratedCodeQuickInfoTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        [Fact]
        public void ShowsTheUhtOutputOrTellsToBuild()
        {
            var m = QuickInfoModel.Build(new QuickInfoResponse
            {
                Declaration = "GENERATED_BODY()",
                GeneratedCodeState = GeneratedCodeState.Available,
                GeneratedCode = "public:\r\n// INCLASS\r\nstatic UClass* StaticClass();",
                GeneratedCodeFile = @"C:\P\Intermediate\Build\Inc\Game\UHT\MyActor.generated.h",
            });
            Assert.Equal("MyActor.generated.h", m.GeneratedCodeFile);
            Assert.Equal(3, m.GeneratedCodeLines);
            Assert.DoesNotContain("\r", m.GeneratedCode);
            Assert.Null(m.GeneratedNote);

            Loc.Language = UiLanguage.English;
            var notBuilt = QuickInfoModel.Build(new QuickInfoResponse { GeneratedCodeState = GeneratedCodeState.NotBuilt });
            Assert.False(notBuilt.IsEmpty);
            Assert.Contains("build once", notBuilt.GeneratedNote);
            Assert.Null(notBuilt.GeneratedCode);
            Assert.Equal(NavGapStrings.GeneratedCodeStale, QuickInfoModel.Build(new QuickInfoResponse { GeneratedCodeState = GeneratedCodeState.Stale }).GeneratedNote);
            Assert.True(QuickInfoModel.Build(new QuickInfoResponse()).IsEmpty);

            var big = string.Join("\n", Enumerable.Range(0, 1000).Select(i => "int X" + i + ";"));
            Assert.Equal(QuickInfoModel.MaxGeneratedLines, QuickInfoModel.Build(new QuickInfoResponse { GeneratedCodeState = GeneratedCodeState.Available, GeneratedCode = big }).GeneratedCodeLines);
        }
    }

    public class NavGapStringsTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        [Fact]
        public void EveryStringExistsInBothLanguagesAndDiffers()
        {
            var props = typeof(NavGapStrings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)).ToArray();
            Assert.True(props.Length > 25);
            Loc.Language = UiLanguage.Chinese;
            var zh = props.ToDictionary(p => p.Name, p => (string)p.GetValue(null));
            string zhSummary = NavGapStrings.Summary(3, 5, 1, 12.4, true);
            Loc.Language = UiLanguage.English;
            var en = props.ToDictionary(p => p.Name, p => (string)p.GetValue(null));
            foreach (var key in zh.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(zh[key]), key);
                Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
                Assert.NotEqual(zh[key], en[key]);
                Assert.DoesNotMatch("[\u4e00-\u9fff]", en[key]);
            }
            Assert.Equal("3 functions · 5 sites (1 delegate bindings) · engine 12 ms · truncated", NavGapStrings.Summary(3, 5, 1, 12.4, true));
            Assert.Contains("委托绑定", zhSummary);
            Assert.Contains("Delegate binding", NavGapStrings.BindingVia("AddDynamic"));
        }
    }
}
