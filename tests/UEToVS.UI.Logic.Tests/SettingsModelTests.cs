using System.Collections.Generic;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class SettingsModelTests
    {
        private static readonly SettingDescriptor Threads = new SettingDescriptor
        {
            Key = "indexing.cppThreads", Category = "indexing", Kind = SettingKind.Int, TitleZh = "C++ 索引线程数", TitleEn = "C++ indexing threads",
            Default = "0", Min = 0, Max = 64, Keywords = "cores 核心",
        };

        private static readonly SettingDescriptor Policy = new SettingDescriptor
        {
            Key = "indexing.duringBuild", Category = "indexing", Kind = SettingKind.Choice, TitleZh = "编译期间", TitleEn = "During builds", Default = "SingleThread",
            Options = new[] { new SettingOption("Pause", "暂停", "Pause"), new SettingOption("SingleThread", "单线程", "One thread"), new SettingOption("FullSpeed", "全速", "Full speed") },
        };

        [Fact]
        public void InlineSettingsJoinTheirHostRowOnlyWhenItIsShown()
        {
            var family = new SettingDescriptor { Key = "f", Kind = SettingKind.Choice };
            var size = new SettingDescriptor { Key = "s", Kind = SettingKind.Int, InlineWithKey = "f" };
            var other = new SettingDescriptor { Key = "o", Kind = SettingKind.Bool };

            var own = SettingsModel.MergeInlineRows(new[] { family, size, other }, out var inline);
            Assert.Equal(new[] { "f", "o" }, own.ConvertAll(d => d.Key).ToArray());
            Assert.Same(size, Assert.Single(inline["f"]));

            // Search hit on the size alone: it keeps a row of its own.
            own = SettingsModel.MergeInlineRows(new[] { size }, out inline);
            Assert.Same(size, Assert.Single(own));
            Assert.Empty(inline);
        }

        [Fact]
        public void IntIsClampedAndFlaggedInvalid()
        {
            Assert.Equal("64", SettingsModel.Normalize(Threads, "200", out bool ok));
            Assert.False(ok);
            Assert.Equal("8", SettingsModel.Normalize(Threads, " 8 ", out ok));
            Assert.True(ok);
            Assert.Equal("0", SettingsModel.Normalize(Threads, "abc", out ok));
            Assert.False(ok);
        }

        [Fact]
        public void ChoiceIsCaseInsensitiveAndFallsBack()
        {
            Assert.Equal("FullSpeed", SettingsModel.Normalize(Policy, "fullspeed", out bool ok));
            Assert.True(ok);
            Assert.Equal("SingleThread", SettingsModel.Normalize(Policy, "turbo", out ok));
            Assert.False(ok);
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("线程", true)]
        [InlineData("THREADS c++", true)]
        [InlineData("核心", true)]
        [InlineData("blueprint", false)]
        public void SearchMatchesBothLanguagesAndKeywords(string q, bool expected) =>
            Assert.Equal(expected, SettingsModel.Matches(Threads, q));

        [Fact]
        public void DiffComparesNormalizedValues()
        {
            var schema = new[] { Threads, Policy };
            var a = new Dictionary<string, string> { ["indexing.cppThreads"] = "4" };
            var b = new Dictionary<string, string> { ["indexing.cppThreads"] = " 4", ["indexing.duringBuild"] = "singlethread" };
            Assert.Empty(SettingsModel.Diff(schema, a, b));
            b["indexing.duringBuild"] = "Pause";
            Assert.Equal(new[] { "indexing.duringBuild" }, SettingsModel.Diff(schema, a, b));
        }

        [Fact]
        public void ValidateFindsSchemaProblems()
        {
            var cats = new[] { new SettingCategory("indexing", "索引", "Indexing") };
            Assert.Empty(SettingsModel.Validate(new[] { Threads, Policy }, cats));
            var bad = new SettingDescriptor { Key = "indexing.cppThreads", Category = "nope", Kind = SettingKind.Action, TitleZh = "x" };
            var problems = SettingsModel.Validate(new[] { Threads, bad }, cats);
            Assert.Contains(problems, p => p.Contains("duplicate"));
            Assert.Contains(problems, p => p.Contains("unknown category"));
            Assert.Contains(problems, p => p.Contains("missing title"));
            Assert.Contains(problems, p => p.Contains("missing action id"));
        }
    }

    public class WindowPlacementTests
    {
        [Fact]
        public void RoundTrips()
        {
            var p = new WindowPlacement { Left = -1200.4, Top = 80, Width = 900, Height = 640 };
            Assert.True(WindowPlacement.TryParse(p.Format(), out var back));
            Assert.Equal(-1200, back.Left);
            Assert.Equal(640, back.Height);
            Assert.False(WindowPlacement.TryParse("1,2,3", out _));
            Assert.False(WindowPlacement.TryParse("1,2,0,5", out _));
            Assert.False(WindowPlacement.TryParse("a,b,c,d", out _));
        }

        [Fact]
        public void OffscreenWindowIsCentered()
        {
            var p = new WindowPlacement { Left = 5000, Top = 5000, Width = 800, Height = 600 };
            var r = p.EnsureVisible(new[] { new RectD(0, 0, 1920, 1040) }, 400, 300);
            Assert.Equal(560, r.Left);
            Assert.Equal(220, r.Top);
        }

        [Fact]
        public void PartlyVisibleWindowIsPulledInAndShrunk()
        {
            var p = new WindowPlacement { Left = 1800, Top = -20, Width = 3000, Height = 600 };
            var r = p.EnsureVisible(new[] { new RectD(0, 0, 1920, 1040) }, 400, 300);
            Assert.Equal(1920, r.Width);
            Assert.Equal(0, r.Left);
            Assert.Equal(0, r.Top);
        }
    }
}
