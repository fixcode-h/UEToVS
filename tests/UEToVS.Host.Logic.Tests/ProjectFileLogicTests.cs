using System.IO;
using UEToVS.Host.Logic.ProjectFiles;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class ProjectFileLogicTests
    {
        private const string UProject = @"{
	""FileVersion"": 3,
	""EngineAssociation"": ""5.4"",
	""Modules"": [
		{
			""Name"": ""MyGame"",
			""Type"": ""Runtime"",
			""LoadingPhase"": ""Default"",
			""AdditionalDependencies"": [ ""Engine"", ""CoreUObject"" ]
		}
	],
	""Plugins"": [
		{ ""Name"": ""GameplayAbilities"", ""Enabled"": true, ""PlatformAllowList"": [ ""Win64"" ] }
	]
}";

        private static JsonCaret At(string marker, int delta = 1) => ProjectFileLogic.At(UProject, UProject.IndexOf(marker) + delta);

        [Fact]
        public void Json_ModuleAndPluginNames()
        {
            var c = At("\"MyGame\"");
            Assert.True(c.InValue);
            Assert.Equal("Name", c.Property);
            Assert.Equal("Modules", c.Array);
            Assert.Equal("MyGame", c.Value);
            Assert.Equal(DescriptorTargetKind.Module, ProjectFileLogic.TargetOf(c));

            c = At("\"CoreUObject\"");
            Assert.Equal("AdditionalDependencies", c.Property);
            Assert.Equal(DescriptorTargetKind.Module, ProjectFileLogic.TargetOf(c));

            c = At("\"GameplayAbilities\"");
            Assert.Equal("Plugins", c.Array);
            Assert.Equal(DescriptorTargetKind.Plugin, ProjectFileLogic.TargetOf(c));
        }

        [Fact]
        public void Json_KeysAndOtherValuesAreNotTargets()
        {
            var key = At("\"Name\"");
            Assert.False(key.InValue);
            Assert.Equal(DescriptorTargetKind.None, ProjectFileLogic.TargetOf(key));
            Assert.Equal(DescriptorTargetKind.None, ProjectFileLogic.TargetOf(At("\"5.4\"")));
            Assert.Null(ProjectFileLogic.At(UProject, UProject.IndexOf("3,")));
        }

        [Fact]
        public void Json_ValueLists()
        {
            Assert.Contains("Editor", ProjectFileLogic.ValuesFor(At("\"Runtime\"")));
            Assert.Contains("PostEngineInit", ProjectFileLogic.ValuesFor(At("\"Default\"")));
            Assert.Contains("Linux", ProjectFileLogic.ValuesFor(At("\"Win64\"")));
            Assert.Null(ProjectFileLogic.ValuesFor(At("\"MyGame\"")));
            Assert.Null(ProjectFileLogic.ValuesFor(At("\"Type\"")));
        }

        [Fact]
        public void Json_UnterminatedStringWhileTyping()
        {
            string text = "{ \"Modules\": [ { \"Type\": \"Edi";
            var c = ProjectFileLogic.At(text, text.Length);
            Assert.Equal("Type", c.Property);
            Assert.Equal("Edi", c.Value);
            Assert.NotNull(ProjectFileLogic.ValuesFor(c));
        }

        [Fact]
        public void ModuleCandidates_UnderSource()
        {
            string root = Path.Combine(Path.GetTempPath(), "P");
            var list = ProjectFileLogic.ModuleCandidates(Path.Combine(root, "My.uproject"), "MyGame");
            Assert.Equal(Path.Combine(root, "Source", "MyGame", "MyGame.Build.cs"), list[0]);
        }

        [Theory]
        [InlineData("[/Script/Engine.Engine]", "Engine", "Engine")]
        [InlineData("  [/Script/MyGame.MyDeveloperSettings]  ", "MyGame", "MyDeveloperSettings")]
        [InlineData("[Core.Log]", null, null)]
        [InlineData("[/Script/Engine]", null, null)]
        [InlineData("[/Script/Engine.Foo Bar]", null, null)]
        public void Ini_ScriptSection(string line, string module, string cls)
        {
            var r = ProjectFileLogic.ScriptSection(line);
            if (module == null) Assert.Null(r);
            else Assert.Equal((module, cls), r.Value);
        }

        [Theory]
        [InlineData("bEnab", 5, 0, "bEnab")]
        [InlineData("+Paths", 6, 1, "Paths")]
        [InlineData("  .Item", 7, 3, "Item")]
        [InlineData("Key=Va", 6, -1, "")]
        [InlineData("; comment", 3, -1, "")]
        [InlineData("[/Script/X.Y]", 3, -1, "")]
        public void Ini_KeyStart(string line, int column, int expected, string prefix)
        {
            Assert.Equal(expected, ProjectFileLogic.IniKeyStart(line, column, out string p));
            Assert.Equal(prefix, p);
        }

        [Fact]
        public void Ini_SectionAboveAndConfigProperty()
        {
            var lines = new[] { "[/Script/A.B]", "X=1", "", "[/Script/C.D]", "Y" };
            Assert.Equal("[/Script/C.D]", ProjectFileLogic.SectionAbove(lines, 4));
            Assert.Equal("[/Script/A.B]", ProjectFileLogic.SectionAbove(lines, 1));
            Assert.True(ProjectFileLogic.IsConfigProperty("EditAnywhere, Config, Category=X"));
            Assert.True(ProjectFileLogic.IsConfigProperty("GlobalConfig"));
            Assert.False(ProjectFileLogic.IsConfigProperty("EditAnywhere, Category=Config"));
            Assert.False(ProjectFileLogic.IsConfigProperty(null));
        }
    }
}
