using System.Collections.Generic;
using System.Linq;
using UEToVS.Host.Logic.Build;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>v0.4.5 构建结果 / 错误列表 / 新建 UE 类: settings, commands, toolbar button, UBT failure tail.</summary>
    public class BuildFeatureLogicTests
    {
        [Fact]
        public void SettingsHaveDefaultsAndRoundTrip()
        {
            var schema = HostSettingsSchema.Build(8);
            Assert.Equal("true", schema.Single(d => d.Key == "build.squiggles").Default);
            Assert.Equal("true", schema.Single(d => d.Key == "errorList.enabled").Default);
            Assert.Equal("false", schema.Single(d => d.Key == "errorList.includeBuild").Default);
            var s = new HostSettings();
            Assert.True(s.BuildErrorSquiggles);
            Assert.True(s.ErrorListEnabled);
            Assert.False(s.ErrorListIncludeBuild);

            var changed = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string>
            {
                ["build.squiggles"] = "false", ["errorList.enabled"] = "false", ["errorList.includeBuild"] = "true",
            }, schema, new HostSettings());
            Assert.False(changed.BuildErrorSquiggles);
            Assert.False(changed.ErrorListEnabled);
            Assert.True(changed.ErrorListIncludeBuild);
            var values = HostSettingsSchema.Codec.ToValues(changed);
            Assert.Equal("false", values["build.squiggles"]);
            Assert.Equal("true", values["errorList.includeBuild"]);
        }

        [Fact]
        public void CommandsUseTheReservedRangeAndKeys()
        {
            var mine = CommandCatalog.All.Where(c => c.Id >= 0x0170 && c.Id <= 0x017F).ToList();
            Assert.Equal(3, mine.Count);
            Assert.Equal("Ctrl+Shift+Alt+U, N", CommandCatalog.ById(CommandCatalog.NewUeClass).VaKeys);
            Assert.Equal("Ctrl+Shift+Alt+U, Q", CommandCatalog.ById(CommandCatalog.NextBuildError).VaKeys);
            Assert.Equal("Ctrl+Shift+Alt+U, X", CommandCatalog.ById(CommandCatalog.ClearBuildErrors).VaKeys);
            Assert.All(mine, c => Assert.Equal(CommandOwner.Host, c.Owner));
            Assert.Equal("生成 Visual Studio 项目文件", CommandCatalog.ById(CommandCatalog.GenerateProjectFiles).TitleZh);
        }

        [Fact]
        public void GenerateButtonSitsAfterFindReferencesAndCanBeHidden()
        {
            var keys = ToolbarButtons.All.Select(b => b.Key).ToList();
            int gen = keys.IndexOf("generateProjectFiles");
            Assert.Equal(keys.IndexOf("findReferences") + 1, gen);
            Assert.Equal(keys.IndexOf("performance") - 1, gen);
            var button = ToolbarButtons.All[gen];
            Assert.Equal(0x0487, button.CommandId);
            Assert.Equal(CommandCatalog.ActionIds.GenerateProjectFiles, button.ActionId);
            Assert.False(ToolbarButtons.IsVisible("generateProjectFiles", true, ToolbarButtons.ParseHidden("generateProjectFiles")));
            Assert.Contains(HostSettingsSchema.Build(8), d => d.Key == "toolbar.button.generateProjectFiles");
            var hidden = HostSettingsSchema.Codec.FromValues(new Dictionary<string, string> { ["toolbar.button.generateProjectFiles"] = "false" },
                                                             HostSettingsSchema.Build(8), new HostSettings());
            Assert.Equal("generateProjectFiles", hidden.HiddenToolbarButtons);
        }

        [Fact]
        public void UbtTailPrefersErrorLines()
        {
            var t = new UbtOutputTail(3);
            for (int i = 0; i < 10; i++) t.Add("Discovering modules " + i);
            Assert.Equal(new[] { "Discovering modules 7", "Discovering modules 8", "Discovering modules 9" }, t.FailureLines());
            t.Add("ERROR: Unable to find plugin 'Foo'");
            t.Add("Writing project files... 0 errors");
            t.Add("Something on stderr", fromStdErr: true);
            t.Add("   ");
            Assert.Equal(new[] { "ERROR: Unable to find plugin 'Foo'", "Something on stderr" }, t.FailureLines());
            Assert.Equal(13, t.Lines);
            Assert.True(UbtOutputTail.LooksLikeError("Generating project files failed"));
            Assert.False(UbtOutputTail.LooksLikeError("Result: 0 error(s)"));
            Assert.False(UbtOutputTail.LooksLikeError(null));
        }
    }
}
