using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class RowTextFitTests
    {
        // Monospace stand-in for FormattedText: 7 px per character.
        private static double W(string s) => s.Length * 7.0;

        private const string Path = "Plugins/SPProject/SPLevelEditor/Source/SPLevelEditor/Private/HomeDecoration";

        [Fact]
        public void TextThatFitsIsNotTrimmed()
        {
            var r = RowTextFit.TrimMiddle("Source/Foo", 1000, W);
            Assert.False(r.IsTrimmed);
            Assert.Equal("Source/Foo", r.Text);
        }

        [Fact]
        public void PathKeepsLeadingAndTrailingFolders()
        {
            var r = RowTextFit.TrimMiddle(Path, W("Plugins/SPProject/…/Private/HomeDecoration"), W);
            Assert.True(r.IsTrimmed);
            Assert.Equal("Plugins/SPProject/…/Private/HomeDecoration", r.Text);
        }

        [Fact]
        public void PathGrowsTailFirstWhenOnlyOneMoreSegmentFits()
        {
            var r = RowTextFit.TrimMiddle(Path, W("Plugins/…/Private/HomeDecoration") + 3, W);
            Assert.Equal("Plugins/…/Private/HomeDecoration", r.Text);
        }

        [Fact]
        public void PathFallsBackToLastSegmentThenCharacters()
        {
            var r = RowTextFit.TrimMiddle(Path, W("…/HomeDecoration"), W);
            Assert.Equal("…/HomeDecoration", r.Text);
            var c = RowTextFit.TrimMiddle(Path, W("Plu…ation"), W);
            Assert.Contains("…", c.Text);
            Assert.True(W(c.Text) <= W("Plu…ation"));
            Assert.StartsWith("Plu", c.Text);
            Assert.EndsWith("tion", c.Text);
        }

        [Fact]
        public void BackslashPathsKeepTheirSeparator()
        {
            var r = RowTextFit.TrimMiddle(@"Engine\Source\Runtime\Core\Public\Foo", W(@"Engine\…\Public\Foo"), W);
            Assert.Equal(@"Engine\…\Public\Foo", r.Text);
        }

        [Fact]
        public void PlainTextIsTrimmedInTheMiddleAndAlwaysFits()
        {
            const string s = "UMyVeryLongContainerName  SomeFile.cpp:120";
            for (int w = 7; w < W(s); w += 5)
            {
                var r = RowTextFit.TrimMiddle(s, w, W);
                Assert.True(W(r.Text) <= w, $"width {w}: '{r.Text}'");
                Assert.True(r.Text.Length == 0 || r.Text.Contains('…'));
            }
        }

        [Fact]
        public void NothingFitsGivesEmptyText()
        {
            Assert.Equal(string.Empty, RowTextFit.TrimMiddle(Path, 3, W).Text);
            Assert.Equal(string.Empty, RowTextFit.TrimMiddle(Path, 0, W).Text);
        }

        [Fact]
        public void RangesAreMappedThroughTheTrim()
        {
            // Highlight "SPProject" (8..17) and "HomeDecoration" (61..75).
            int home = Path.IndexOf("HomeDecoration", StringComparison.Ordinal);
            var ranges = new[] { new TextRange(8, 9), new TextRange(home, 14), new TextRange(20, 5) };
            var r = RowTextFit.TrimMiddle(Path, W("Plugins/SPProject/…/Private/HomeDecoration"), W);
            var mapped = r.MapRanges(ranges);
            Assert.Equal(2, mapped.Count);
            Assert.Equal("SPProject", r.Text.Substring(mapped[0].Start, mapped[0].Length));
            Assert.Equal("HomeDecoration", r.Text.Substring(mapped[1].Start, mapped[1].Length));
        }

        [Fact]
        public void UntrimmedRangesAreReturnedAsIs()
        {
            var ranges = new[] { new TextRange(0, 3) };
            Assert.Same(ranges, RowTextFit.TrimMiddle("abc", 100, W).MapRanges(ranges));
        }

        [Fact]
        public void PrimaryGetsItsFullWidthBeforeSecondary()
        {
            var c = RowTextFit.Allocate(available: 600, wantPrimary: 220, wantSecondary: 500, wantRight: 120, gap: 6);
            Assert.Equal(220, c.Primary);
            Assert.Equal(120, c.RightText);
            Assert.Equal(600 - 120 - 6 - 220 - 6, c.Secondary);
        }

        [Fact]
        public void EverythingFitsKeepsNaturalWidths()
        {
            var c = RowTextFit.Allocate(1200, 220, 300, 120, 6);
            Assert.Equal(220, c.Primary);
            Assert.Equal(300, c.Secondary);
            Assert.Equal(120, c.RightText);
        }

        [Fact]
        public void TinyLeftoverHidesSecondary()
        {
            var c = RowTextFit.Allocate(360, 220, 300, 120, 6);
            Assert.Equal(220, c.Primary);
            Assert.Equal(0, c.Secondary);
        }

        [Fact]
        public void NameIsOnlyShortenedWhenTheRowCannotHoldIt()
        {
            // Right text gives back down to its minimum share first.
            var c = RowTextFit.Allocate(450, 330, 200, 150, 6);
            Assert.Equal(330, c.Primary);
            Assert.Equal(450 - 6 - 330, c.RightText);
            Assert.Equal(0, c.Secondary);
            var d = RowTextFit.Allocate(300, 600, 200, 150, 6);
            Assert.Equal(300 * RowTextFit.MinRightShare, d.RightText, 6);
            Assert.Equal(300 - 6 - d.RightText, d.Primary, 6);
            Assert.Equal(0, d.Secondary);
        }

        [Fact]
        public void InfiniteWidthReturnsWanted()
        {
            var c = RowTextFit.Allocate(double.PositiveInfinity, 10, 20, 30, 6);
            Assert.Equal(10, c.Primary);
            Assert.Equal(20, c.Secondary);
            Assert.Equal(30, c.RightText);
        }

        [Fact]
        public void NegativeAvailableIsSafe()
        {
            var c = RowTextFit.Allocate(-5, 10, 20, 0, 6);
            Assert.Equal(0, c.Primary);
            Assert.Equal(0, c.Secondary);
        }
    }
}
