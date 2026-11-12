using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class TokenDerivationTests
    {
        public static IEnumerable<object[]> Palettes() => new[]
        {
            new object[] { "dark" },
            new object[] { "light" },
            new object[] { "blue" },
            new object[] { "hc" },
        };

        private static ThemePalette P(string name) =>
            name == "dark" ? ThemePalette.FallbackDark()
            : name == "light" ? ThemePalette.FallbackLight()
            : name == "blue" ? ThemePalette.FallbackBlue()
            : ThemePalette.FallbackHighContrast();

        [Theory]
        [MemberData(nameof(Palettes))]
        public void TextHasReadableContrast(string name)
        {
            var t = TokenDerivation.Derive(P(name), new AppearanceSettings());
            Assert.True(Rgba.Contrast(t.Colors[TokenNames.TextPrimary], t.Colors[TokenNames.SurfacePopup]) >= 4.5);
            Assert.True(Rgba.Contrast(t.Colors[TokenNames.TextSecondary], t.Colors[TokenNames.SurfacePopup]) >= 3.0);
        }

        [Theory]
        [MemberData(nameof(Palettes))]
        public void BadgeForegroundsAreReadable(string name)
        {
            var t = TokenDerivation.Derive(P(name), new AppearanceSettings());
            foreach (BadgeKind k in Enum.GetValues(typeof(BadgeKind)))
                Assert.True(Rgba.Contrast(t.Colors[TokenNames.BadgeForeground(k)], t.Colors[TokenNames.BadgeBackground(k)]) >= 3.0, k.ToString());
        }

        [Fact]
        public void RowHeightFollowsPresetAndFont()
        {
            var s = new AppearanceSettings();
            s.ApplyPreset(StylePreset.Compact);
            var compact = TokenDerivation.Derive(ThemePalette.FallbackDark(), s).Numbers[TokenNames.RowHeight];
            s.ApplyPreset(StylePreset.Comfortable);
            var comfy = TokenDerivation.Derive(ThemePalette.FallbackDark(), s).Numbers[TokenNames.RowHeight];
            Assert.True(comfy > compact);
            s.FontScale = AppearanceSettings.MaxFontScale;
            var big = TokenDerivation.Derive(ThemePalette.FallbackDark(), s);
            Assert.True(big.Numbers[TokenNames.RowHeight] >= big.Numbers[TokenNames.FontSizeBody] * 1.45);
        }
    }

    public class RgbaTests
    {
        [Fact]
        public void ParseAndFormatRoundTrip()
        {
            Assert.True(Rgba.TryParse("#3574F0", out var c));
            Assert.Equal(new Rgba(0x35, 0x74, 0xF0), c);
            Assert.True(Rgba.TryParse(c.ToHex(), out var c2));
            Assert.Equal(c, c2);
            Assert.False(Rgba.TryParse("nope", out _));
        }

        [Fact]
        public void EnsureContrastReachesTarget()
        {
            var bg = new Rgba(0x2B, 0x2D, 0x30);
            var fg = new Rgba(0x40, 0x40, 0x40).EnsureContrast(bg, 4.5);
            Assert.True(Rgba.Contrast(fg, bg) >= 4.5);
        }
    }

    public class AppearanceSettingsTests
    {
        [Fact]
        public void StorageRoundTrip()
        {
            var s = new AppearanceSettings();
            s.ApplyPreset(StylePreset.Compact);
            s.FontScale = 1.2;
            s.AccentMode = AccentMode.Custom;
            s.CustomAccent = "#FF8800";
            s.MatchStyle = MatchHighlightStyle.Bold | MatchHighlightStyle.Underline;
            s.SetBadgeColor(BadgeKind.UClass, "#AA5500");
            var back = AppearanceSettings.Parse(s.ToStorageString(), out var problems);
            Assert.Empty(problems);
            Assert.Equal(s.ToStorageString(), back.ToStorageString());
            Assert.Equal("#AA5500", back.GetBadgeColor(BadgeKind.UClass));
        }

        [Fact]
        public void NormalizeClampsOutOfRange()
        {
            var s = new AppearanceSettings { FontScale = 99, RowHeight = -3 };
            var notes = s.Normalize();
            Assert.NotEmpty(notes);
            Assert.InRange(s.FontScale, AppearanceSettings.MinFontScale, AppearanceSettings.MaxFontScale);
            Assert.True(s.RowHeight > 0);
        }

        [Fact]
        public void ParseToleratesGarbage()
        {
            var s = AppearanceSettings.Parse("fontScale=abc\nunknownKey=1\n\n###", out var problems);
            Assert.NotEmpty(problems);
            Assert.Equal(1.0, s.FontScale);
        }
    }

    public class HighlightRangeTests
    {
        [Fact]
        public void NormalizeMergesAndClamps()
        {
            var r = HighlightRanges.Normalize(new[] { new TextRange(5, 3), new TextRange(0, 2), new TextRange(1, 3), new TextRange(9, 50) }, 10);
            Assert.Equal(new[] { new TextRange(0, 4), new TextRange(5, 3), new TextRange(9, 1) }, r);
        }

        [Fact]
        public void SegmentsCoverWholeText()
        {
            var segs = HighlightRanges.Segments(new[] { new TextRange(2, 2) }, 6);
            Assert.Equal(3, segs.Count);
            Assert.Equal(6, segs.Sum(s => s.Length));
            Assert.True(segs[1].IsMatch);
        }

        [Fact]
        public void SubsequenceMatch()
        {
            Assert.True(HighlightRanges.TryMatchSubsequence("UAbilitySystemComponent", "asc", out var r));
            Assert.Equal(3, r.Sum(x => x.Length));
            Assert.False(HighlightRanges.TryMatchSubsequence("Foo", "xyz", out _));
        }
    }

    public class ListNavigatorTests
    {
        [Fact]
        public void SkipsHeadersAndWraps()
        {
            var n = new ListNavigator { IsSelectable = i => i % 5 != 0 };
            n.Reset(10);
            Assert.Equal(1, n.SelectedIndex);
            n.Select(4);
            n.Move(NavKey.Down);
            Assert.Equal(6, n.SelectedIndex);
            n.Select(1);
            n.Move(NavKey.Up);
            Assert.Equal(9, n.SelectedIndex);
            n.WrapAround = false;
            n.Move(NavKey.Down);
            Assert.Equal(9, n.SelectedIndex);
        }

        [Fact]
        public void KeepsSelectionVisible()
        {
            var n = new ListNavigator();
            n.Reset(1000);
            n.SetViewportRows(10);
            n.Select(500);
            Assert.InRange(500, n.ScrollOffsetRows, n.ScrollOffsetRows + 9);
            n.Move(NavKey.Home);
            Assert.Equal(0, n.SelectedIndex);
            Assert.Equal(0, n.ScrollOffsetRows);
            n.Move(NavKey.End);
            Assert.Equal(999, n.SelectedIndex);
            Assert.Equal(990, n.ScrollOffsetRows);
        }
    }

    public class TreeFlattenerTests
    {
        [Fact]
        public void ExpandCollapseInPlace()
        {
            var a = new TreeNode<string>("a", new[] { new TreeNode<string>("a1"), new TreeNode<string>("a2") }, false);
            var b = new TreeNode<string>("b", new[] { new TreeNode<string>("b1") });
            var f = new TreeFlattener<string>();
            f.SetRoots(new[] { a, b });
            Assert.Equal(new[] { "a", "b", "b1" }, f.Rows.Select(r => r.Node.Item));
            Assert.True(f.SetExpanded(0, true));
            Assert.Equal(new[] { "a", "a1", "a2", "b", "b1" }, f.Rows.Select(r => r.Node.Item));
            Assert.Equal(0, f.ParentOf(2));
            Assert.Equal(-1, f.ParentOf(3));
            Assert.True(f.SetExpanded(3, false));
            Assert.Equal(4, f.Rows.Count);
        }
    }

    public class PlacementTests
    {
        private static readonly RectD Screen = new RectD(0, 0, 1920, 1040);

        [Fact]
        public void FlipsAboveNearBottom()
        {
            var r = PopupPlacement.Place(new RectD(100, 1000, 10, 20), 400, 300, Screen, AnchorMode.Below);
            Assert.True(r.Bottom <= 1000);
        }

        [Fact]
        public void StaysOnScreen()
        {
            var r = PopupPlacement.Place(new RectD(1900, 10, 10, 20), 400, 300, Screen, AnchorMode.Below);
            Assert.True(r.X >= 0 && r.Right <= Screen.Right && r.Y >= 0 && r.Bottom <= Screen.Bottom);
        }
    }

    public class SmallLogicTests
    {
        [Fact]
        public void HistoryBrowsesAndRestoresDraft()
        {
            var h = new SearchHistory(3);
            h.Add("a"); h.Add("b"); h.Add("c"); h.Add("d"); h.Add("b");
            Assert.Equal(new[] { "b", "d", "c" }, h.Items);
            Assert.Equal("b", h.Older("draft"));
            Assert.Equal("d", h.Older("b"));
            Assert.Equal("b", h.Newer());
            Assert.Equal("draft", h.Newer());
            Assert.Null(h.Newer());
        }

        [Fact]
        public void QueryGateCancelsPrevious()
        {
            var g = new QueryGate();
            var (g1, t1) = g.Begin();
            var (g2, t2) = g.Begin();
            Assert.True(t1.IsCancellationRequested);
            Assert.False(t2.IsCancellationRequested);
            Assert.False(g.IsCurrent(g1));
            Assert.True(g.IsCurrent(g2));
        }

        [Fact]
        public void ShortcutParsing()
        {
            var c = ShortcutText.Parse("ctrl+k, ctrl+c");
            Assert.Equal(2, c.Count);
            Assert.Equal(new[] { "Ctrl", "K" }, c[0]);
            Assert.Equal(new[] { "Ctrl", "+" }, ShortcutText.Parse("Ctrl++")[0]);
        }
    }

    public class PerfMathTests
    {
        [Theory]
        [InlineData(512, "512 B")]
        [InlineData(1536, "1.5 KB")]
        [InlineData(734003200, "700 MB")]
        public void Bytes(double v, string s) => Assert.Equal(s, PerfMath.FormatBytes(v));

        [Theory]
        [InlineData(0.42, "0.42 ms")]
        [InlineData(1530, "1.53 s")]
        [InlineData(125000, "2m 05s")]
        public void Durations(double v, string s) => Assert.Equal(s, PerfMath.FormatDuration(v));

        [Fact]
        public void NiceAxisCoversRange()
        {
            var (min, max, step) = PerfMath.NiceAxis(3, 87);
            Assert.True(min <= 3 && max >= 87 && step > 0);
        }

        [Fact]
        public void SortIsStable()
        {
            int[] keys = { 2, 1, 2, 1 };
            Assert.Equal(new[] { 1, 3, 0, 2 }, PerfMath.SortPermutation(4, (a, b) => keys[a].CompareTo(keys[b]), false));
        }

        [Fact]
        public void SampleBufferWraps()
        {
            var b = new SampleBuffer(3);
            foreach (var v in new double[] { 1, 2, 3, 4 }) b.Add(v);
            Assert.Equal((2.0, 4.0), b.Range());
        }
    }
}
