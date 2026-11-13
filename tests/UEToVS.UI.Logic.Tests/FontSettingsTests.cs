using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class FontSettingsTests
    {
        [Theory]
        [InlineData("", FontChoiceKind.FollowVs, null)]
        [InlineData(null, FontChoiceKind.FollowVs, null)]
        [InlineData("VS", FontChoiceKind.FollowVs, null)]
        [InlineData("keep", FontChoiceKind.Keep, null)]
        [InlineData("Code", FontChoiceKind.SameAsCode, null)]
        [InlineData("ui", FontChoiceKind.SameAsUi, null)]
        [InlineData("bundled:IBM Plex Sans", FontChoiceKind.Bundled, "IBM Plex Sans")]
        [InlineData("bundled:maple mono nl", FontChoiceKind.Bundled, "Maple Mono NL")]
        [InlineData("Noto Sans SC", FontChoiceKind.Bundled, "Noto Sans SC")] // a bundled name typed by hand
        [InlineData("bundled:Not Shipped", FontChoiceKind.System, "Not Shipped")]
        [InlineData("  Consolas ", FontChoiceKind.System, "Consolas")]
        public void ParsesStoredValues(string stored, FontChoiceKind kind, string family)
        {
            var c = FontChoice.Parse(stored);
            Assert.Equal(kind, c.Kind);
            Assert.Equal(family, c.Family);
            Assert.Equal(c, FontChoice.Parse(c.ToString())); // round trip
        }

        [Fact]
        public void DefaultsMatchAgentPanel()
        {
            var s = new FontSettings();
            Assert.Equal("bundled:IBM Plex Sans", s.UiLatin.ToString());
            Assert.Equal("bundled:Noto Sans SC", s.UiCjk.ToString()); // v0.5.17: back to Noto, UI text is 500 (Agent Panel)
            Assert.Equal("bundled:Maple Mono NL", s.Code.ToString());
            Assert.True(s.Code.Bundled.Monospace);
        }

        [Fact]
        public void DisplayMarksBundledFonts()
        {
            Assert.Equal("IBM Plex Sans（内置）", FontSettings.DefaultUiLatin.Display(false));
            Assert.Equal("思源黑体 Noto Sans SC（内置）", FontChoice.OfBundled(BundledFontCatalog.NotoSansSc).Display(false));
            Assert.Equal("思源黑体 Noto Sans SC（内置）", FontSettings.DefaultUiCjk.Display(false));
            Assert.Equal("Maple Mono CN（内置）", FontChoice.OfBundled(BundledFontCatalog.MapleMonoCn).Display(false));
            Assert.Equal("Microsoft YaHei UI", FontSettings.SystemCjk.Display(false));
            Assert.Equal("Maple Mono NL (bundled)", FontSettings.DefaultCode.Display(true));
            Assert.Equal("跟随 VS", FontChoice.FollowVs.Display(false));
            Assert.Equal("不修改（使用 VS 当前设置）", FontChoice.Keep.Display(false));
            Assert.Equal("Consolas", FontChoice.OfSystem("Consolas").Display(false));
        }

        [Fact]
        public void CatalogFilesAndReferences()
        {
            Assert.Equal(6, BundledFontCatalog.All.Count);
            Assert.Equal(new[] { "Maple Mono NL", "JetBrains Mono" }, BundledFontCatalog.All.Where(b => b.Monospace).Select(b => b.Family).ToArray());
            // UI text is 500: every bundled family has a Medium face
            Assert.All(BundledFontCatalog.All, b => Assert.Contains(b.Files, f => f.EndsWith("-Medium.ttf", StringComparison.Ordinal)));
            Assert.Equal("./IBMPlexSans/#IBM Plex Sans", BundledFontCatalog.IbmPlexSans.WpfReference);
            Assert.All(BundledFontCatalog.All, b => Assert.All(b.Files, f => Assert.EndsWith(".ttf", f)));
            Assert.Null(BundledFontCatalog.Find("Segoe UI"));
        }

        [Fact]
        public void UiFontIsLatinThenCjkThenVsFont()
        {
            var refs = new FontSettings().ComposeUi("Segoe UI");
            Assert.Equal("./IBMPlexSans/#IBM Plex Sans, ./NotoSansSC/#Noto Sans SC, Segoe UI", FontSettings.ToWpfFamilyList(refs));
        }

        [Fact]
        public void UiFontFollowingVsIsEmptyAndPartialFollowUsesVsFont()
        {
            var both = new FontSettings { UiLatin = FontChoice.FollowVs, UiCjk = FontChoice.FollowVs };
            Assert.Empty(both.ComposeUi("Segoe UI"));

            var latinVs = new FontSettings { UiLatin = FontChoice.FollowVs, UiCjk = FontChoice.OfBundled(BundledFontCatalog.NotoSansSc) };
            Assert.Equal("Microsoft YaHei UI, ./NotoSansSC/#Noto Sans SC",
                FontSettings.ToWpfFamilyList(latinVs.ComposeUi("Microsoft YaHei UI")));

            var system = new FontSettings { UiLatin = FontChoice.OfSystem("Segoe UI"), UiCjk = FontChoice.OfSystem("Microsoft YaHei UI") };
            Assert.Equal("Segoe UI, Microsoft YaHei UI", FontSettings.ToWpfFamilyList(system.ComposeUi("Segoe UI")));
        }

        [Fact]
        public void CodeFontAddsCjkAndMonospaceFallbacks()
        {
            var refs = new FontSettings { UiCjk = FontChoice.OfBundled(BundledFontCatalog.NotoSansSc) }.ComposeCode("Consolas", "Cascadia Mono, Consolas");
            Assert.Equal("./MapleMonoNL/#Maple Mono NL, ./JetBrainsMono/#JetBrains Mono, ./NotoSansSC/#Noto Sans SC, Cascadia Mono, Consolas", FontSettings.ToWpfFamilyList(refs));

            var jb = new FontSettings { Code = FontChoice.OfBundled(BundledFontCatalog.JetBrainsMono), UiCjk = FontChoice.OfBundled(BundledFontCatalog.MapleMonoCn) }.ComposeCode(null, "Consolas");
            Assert.Equal("./JetBrainsMono/#JetBrains Mono, ./MapleMonoCN/#Maple Mono CN, Consolas", FontSettings.ToWpfFamilyList(jb));

            var follow = new FontSettings { Code = FontChoice.FollowVs, UiCjk = FontChoice.FollowVs };
            Assert.Equal("Fira Code, Cascadia Mono", FontSettings.ToWpfFamilyList(follow.ComposeCode("Fira Code", "Cascadia Mono")));
        }

        [Fact]
        public void OptionsListKeywordsBundledThenInstalledSortedWithoutDuplicates()
        {
            var options = FontSettings.Options(new[] { FontChoice.FollowVs }, new[] { "Segoe UI", "consolas", "IBM Plex Sans", "@SimSun", "Arial", "Segoe UI", "" }, null);
            Assert.Equal(new[] { "vs", "bundled:IBM Plex Sans", "bundled:Inter", "bundled:Noto Sans SC", "bundled:Maple Mono CN", "bundled:Maple Mono NL", "bundled:JetBrains Mono", "Arial", "consolas", "Segoe UI" },
                         options.Select(o => o.ToString()).ToArray());

            var mono = FontSettings.Options(null, null, FontChoice.OfSystem("Gone Font"), monospaceFirst: true);
            Assert.Equal("bundled:Maple Mono NL", mono[0].ToString());
            Assert.Equal("Gone Font", mono.Last().ToString());
        }

        [Fact]
        public void OpenChoiceKeepsCustomValuesAndResolvesDynamicOptions()
        {
            var d = new SettingDescriptor
            {
                Key = "fonts.code", Kind = SettingKind.Choice, Default = "bundled:Maple Mono NL", AllowCustomValue = true,
                Options = new[] { new SettingOption("vs", "跟随 VS", "Follow VS"), new SettingOption("bundled:Maple Mono NL", "M", "M") },
            };
            Assert.Equal("Consolas", SettingsModel.Normalize(d, " Consolas ", out bool ok));
            Assert.True(ok);
            Assert.Equal("vs", SettingsModel.Normalize(d, "VS", out _));
            Assert.Equal("bundled:Maple Mono NL", SettingsModel.Normalize(d, "  ", out ok));
            Assert.False(ok);

            Assert.Equal(3, d.ResolveOptions("Consolas").Count); // current value appended
            d.OptionsSource = () => new[] { new SettingOption("Consolas", "Consolas", "Consolas") };
            Assert.Single(d.ResolveOptions("Consolas"));
            d.OptionsSource = () => throw new InvalidOperationException("boom");
            Assert.Equal(2, d.ResolveOptions(null).Count); // broken provider → static options
        }

        [Fact]
        public void ClosedChoiceStillRejectsUnknownValues()
        {
            var d = new SettingDescriptor { Key = "k", Kind = SettingKind.Choice, Default = "a", Options = new[] { new SettingOption("a", "A", "A") } };
            Assert.Equal("a", SettingsModel.Normalize(d, "zzz", out bool ok));
            Assert.False(ok);
            Assert.Single(d.ResolveOptions("zzz"));
        }
    }
}
