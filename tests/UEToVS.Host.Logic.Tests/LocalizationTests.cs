using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using UEToVS.Contracts;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Engine;
using UEToVS.Host.Logic.Localization;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class LocalizationTests : IDisposable
    {
        private readonly UiLanguage _saved = Loc.Language;

        public void Dispose() => Loc.Language = _saved;

        private static PropertyInfo[] StringProperties() =>
            typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)).ToArray();

        /// <summary>Strings that are intentionally identical in both languages (product names).</summary>
        private static readonly string[] SameInBoth = { nameof(Strings.ProductName), nameof(Strings.OutputPaneName) };

        [Fact]
        public void EveryStringExistsInBothLanguagesAndDiffers()
        {
            Loc.Language = UiLanguage.Chinese;
            var zh = StringProperties().ToDictionary(p => p.Name, p => (string)p.GetValue(null));
            Loc.Language = UiLanguage.English;
            var en = StringProperties().ToDictionary(p => p.Name, p => (string)p.GetValue(null));
            Assert.True(zh.Count > 50);
            foreach (var key in zh.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(zh[key]), key + " (zh) empty");
                Assert.False(string.IsNullOrWhiteSpace(en[key]), key + " (en) empty");
                if (!SameInBoth.Contains(key)) Assert.True(zh[key] != en[key], key + " is not translated");
            }
            // English strings must not contain CJK characters.
            foreach (var kv in en)
                Assert.False(kv.Value.Any(c => c >= 0x4E00 && c <= 0x9FFF), kv.Key + " (en) contains Chinese: " + kv.Value);
        }

        [Fact]
        public void FormattedStringsFollowTheLanguage()
        {
            Loc.Language = UiLanguage.English;
            Assert.Equal("UEToVS: indexing 5/10", Strings.StatusIndexing(5, 10));
            Assert.Contains("3 failed", Strings.BannerRestoredPartially(3));
            Loc.Language = UiLanguage.Chinese;
            Assert.Contains("3 项失败", Strings.BannerRestoredPartially(3));
            Assert.Contains("C:\\x", Strings.BannerBackupUnreadable("C:\\x"));
        }

        [Fact]
        public void LanguageFollowsVsLocale()
        {
            Loc.InitializeFromLcid(1033); // en-US
            Assert.Equal("Restart Engine", Strings.CmdRestartEngine);
            Loc.InitializeFromLcid(2052); // zh-CN
            Assert.Equal("重启引擎", Strings.CmdRestartEngine);
            Loc.InitializeFromLcid(1041); // ja-JP → Chinese (user rule: anything but English)
            Assert.Equal("重启引擎", Strings.CmdRestartEngine);
        }

        [Fact]
        public void OptionAttributesResolveThroughTheStringTable()
        {
            Loc.Language = UiLanguage.English;
            Assert.Equal("Log level", new UiDisplayNameAttribute(nameof(Strings.OptLogLevel)).DisplayName);
            Assert.StartsWith("Error / Warn", new UiDescriptionAttribute(nameof(Strings.OptLogLevelDesc)).Description);
            Assert.Equal("Engine", new UiCategoryAttribute(nameof(Strings.CatEngine)).Category);
            Loc.Language = UiLanguage.Chinese;
            Assert.Equal("日志级别", new UiDisplayNameAttribute(nameof(Strings.OptLogLevel)).DisplayName);
            Assert.Equal("NoSuchKey", StringKeys.Resolve("NoSuchKey"));
            Assert.False(StringKeys.Exists("NoSuchKey"));

            // Every Opt* display name has a matching *Desc description.
            foreach (var p in StringProperties().Where(p => p.Name.StartsWith("Opt") && !p.Name.EndsWith("Desc")))
                Assert.True(StringKeys.Exists(p.Name + "Desc"), p.Name + "Desc missing");
        }

        [Fact]
        public void PhaseNamesCoverEveryIndexPhase()
        {
            Loc.Language = UiLanguage.English;
            var names = Enum.GetValues(typeof(IndexPhase)).Cast<IndexPhase>().Select(p => Strings.PhaseName((int)p)).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }

        [Fact]
        public void EngineStatusDisplay()
        {
            Loc.Language = UiLanguage.English;
            var parsing = EngineStatusText.For(new EngineStatus { Phase = IndexPhase.Parsing, FilesParsed = 30, FilesToParse = 100 });
            Assert.Equal(30, parsing.Done);
            Assert.Equal(100, parsing.Total);
            var ready = EngineStatusText.For(new EngineStatus { Phase = IndexPhase.Ready, SymbolCount = 1200, FileCount = 30 });
            Assert.Equal(0, ready.Total);
            Assert.Contains("1,200 symbols", ready.Text);
            Assert.True(EngineStatusText.For(new EngineStatus { Phase = IndexPhase.Failed }).IsError);
            Assert.Equal(Strings.StatusEngineStarting, EngineStatusText.For(null).Text);
        }
    
        [Fact]
        public void EngineCodesAreLocalized()
        {
            Loc.Language = UiLanguage.English;
            Assert.Equal("Parsing project files", EngineCodes.WorkName("index.parse.project"));
            Assert.Equal("Merging index", EngineCodes.WorkName("index.Merging"));
            Assert.Equal("custom.work", EngineCodes.WorkName("custom.work"));
            Assert.Equal("Indexing failed (Parsing): IOException: disk", EngineCodes.Error("index.failed:Parsing: IOException: disk"));
            Assert.Equal("plain error", EngineCodes.Error("plain error"));
            Assert.Null(EngineCodes.Error(null));
            Assert.Equal("engine source files=10 cacheHits=3", EngineCodes.Detail("engine files=10 cacheHits=3"));
            Loc.Language = UiLanguage.Chinese;
            Assert.Equal("解析项目文件", EngineCodes.WorkName("index.parse.project"));
            Assert.StartsWith("索引失败（正在解析）", EngineCodes.Error("index.failed:Parsing: x"));
        }
    }
}
