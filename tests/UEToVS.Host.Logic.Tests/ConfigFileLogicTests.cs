using System.Collections.Generic;
using System.Linq;
using UEToVS.Host.Logic.ProjectFiles;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class IniLogicTests
    {
        private static readonly string[] Lines =
        {
            "; comment",
            "[/Script/MyGame.MySettings]",
            "bEnabled=True",
            "+Paths=/Game/A",
            "-Paths=/Game/B",
            "Values[0]=3",
            "Unknown = 1",
            "",
            "[Core.Log]",
            "LogTemp=Verbose",
            "[Broken",
            "Orphan=1",
        };

        [Fact]
        public void ParsesSectionsAndKeys()
        {
            var s = IniLogic.Parse(Lines);
            Assert.Equal(2, s.Count);
            Assert.Equal("/Script/MyGame.MySettings", s[0].Name);
            Assert.Equal("MyGame", s[0].Module);
            Assert.Equal("MySettings", s[0].Class);
            Assert.Equal(1, s[0].Line);
            Assert.Equal(1, s[0].Start);
            Assert.Equal(new[] { "bEnabled", "Paths", "Paths", "Values", "Unknown" }, s[0].Keys.Select(k => k.Name));
            Assert.Equal('+', s[0].Keys[1].Op);
            Assert.Equal(1, s[0].Keys[1].Start);
            Assert.Null(s[1].Module);
            Assert.Equal("LogTemp", s[1].Keys.Single().Name); // keys after a broken header belong to no section
        }

        [Fact]
        public void UnknownKeysNeedAKnownMemberList()
        {
            var s = IniLogic.Parse(Lines)[0];
            Assert.Equal(new[] { "Unknown" }, IniLogic.UnknownKeys(s, new[] { "benabled", "Paths", "Values" }).Select(k => k.Name));
            Assert.Empty(IniLogic.UnknownKeys(s, new string[0])); // class read by hand (GConfig): say nothing
            Assert.Empty(IniLogic.UnknownKeys(null, new[] { "x" }));
        }

        [Theory]
        [InlineData("[", 1, 1, "")]
        [InlineData("[/Script/Eng", 12, 1, "/Script/Eng")]
        [InlineData("  [Core", 7, 3, "Core")]
        [InlineData("[A]", 3, -1, "")]
        [InlineData("Key=[", 5, -1, "")]
        public void SectionStart(string line, int col, int expected, string typed)
        {
            Assert.Equal(expected, IniLogic.SectionStart(line, col, out string t));
            Assert.Equal(typed, t);
        }

        [Fact]
        public void ConfigClassesAndScriptNames()
        {
            Assert.True(IniLogic.IsConfigClass("config=Game, defaultconfig"));
            Assert.True(IniLogic.IsConfigClass("Config = Engine"));
            Assert.False(IniLogic.IsConfigClass("BlueprintType, Blueprintable"));
            Assert.False(IniLogic.IsConfigClass(null));
            Assert.Equal("GameMapsSettings", IniLogic.ScriptName("UGameMapsSettings"));
            Assert.Equal("MyActor", IniLogic.ScriptName("AMyActor"));
            Assert.Equal("Unreal", IniLogic.ScriptName("Unreal"));
        }

        [Theory]
        [InlineData("UPROPERTY(Config, EditAnywhere) bool bEnabled;", "bEnabled", "bool")]
        [InlineData("TArray<FString> Paths", "Paths", "TArray<FString>")]
        [InlineData("UPROPERTY(config, meta=(Foo=\"(x)\")) TSoftObjectPtr<UWorld> Map;", "Map", "TSoftObjectPtr<UWorld>")]
        [InlineData("static const int32 MaxCount", "MaxCount", "int32")]
        [InlineData("Paths", "Paths", null)]
        public void TypeFromSignature(string sig, string name, string expected) => Assert.Equal(expected, IniLogic.TypeFromSignature(sig, name));

        [Fact]
        public void HintsByType()
        {
            Assert.Equal(new[] { "True", "False" }, IniLogic.HintFor("bool").Values);
            Assert.Contains("+Key", IniLogic.HintFor("TArray<FString>").HintEn);
            Assert.Contains("整数", IniLogic.HintFor("int32").HintZh);
            Assert.Contains("/Game", IniLogic.HintFor("FSoftObjectPath").HintEn);
            Assert.Contains("Enum", IniLogic.HintFor("EMyMode").HintEn);
            Assert.Contains("Struct", IniLogic.HintFor("FMyStruct").HintEn);
            Assert.Empty(IniLogic.HintFor(null).Values);
            Assert.NotNull(IniLogic.HintFor("UObject*").Hint(true));
        }

        [Fact]
        public void ValueStart()
        {
            Assert.Equal(9, IniLogic.ValueStart("bEnabled=Tr", 11, out string key));
            Assert.Equal("bEnabled", key);
            Assert.Equal(-1, IniLogic.ValueStart("bEnabled=Tr", 4, out _));
            Assert.Equal(-1, IniLogic.ValueStart("; x=1", 5, out _));
        }
    }

    public class DescriptorLogicTests
    {
        private const string Project = "{\n  \"FileVersion\": 3,\n  \"Modules\": [\n    { \"Name\": \"MyGame\", \"Type\": \"Runtime\", \"\" },\n    { \"Name\": \"MyEditor\", \"Type\": \"Editor\" }\n  ],\n  \"Plugins\": [ { \"Name\": \"Niag\", \"Enabled\": true } ],\n  \"\"\n}";

        [Fact]
        public void KeysPerLevel()
        {
            int rootKey = Project.LastIndexOf("\"\"", System.StringComparison.Ordinal) + 1;
            var t = DescriptorLogic.StringAt(Project, rootKey);
            Assert.True(t.IsKey);
            Assert.Same(DescriptorLogic.ProjectKeys, DescriptorLogic.KeysFor("X.uproject", t));
            Assert.Same(DescriptorLogic.PluginKeys, DescriptorLogic.KeysFor("X.uplugin", t));
            int moduleKey = Project.IndexOf("\"\" }", System.StringComparison.Ordinal) + 1;
            var m = DescriptorLogic.StringAt(Project, moduleKey);
            Assert.Same(DescriptorLogic.ModuleKeys, DescriptorLogic.KeysFor("X.uproject", m));
            var existing = DescriptorLogic.ExistingKeys(Project, m);
            Assert.Contains("Name", existing);
            Assert.Contains("Type", existing);
            Assert.DoesNotContain("FileVersion", existing);
            var rootExisting = DescriptorLogic.ExistingKeys(Project, t);
            Assert.Contains("FileVersion", rootExisting);
            Assert.Contains("Plugins", rootExisting);
            Assert.DoesNotContain("Name", rootExisting);
            // a value is not a key position
            Assert.Null(DescriptorLogic.KeysFor("X.uproject", DescriptorLogic.StringAt(Project, Project.IndexOf("Runtime", System.StringComparison.Ordinal))));
        }

        [Fact]
        public void PluginNamesAndModuleNames()
        {
            var p = DescriptorLogic.StringAt(Project, Project.IndexOf("Niag", System.StringComparison.Ordinal) + 2);
            Assert.True(DescriptorLogic.IsPluginName(p));
            Assert.False(DescriptorLogic.IsPluginName(DescriptorLogic.StringAt(Project, Project.IndexOf("MyGame", System.StringComparison.Ordinal))));
            var names = DescriptorLogic.ModuleNames(Project);
            Assert.Equal(new[] { "MyGame", "MyEditor" }, names.Select(n => n.Value));
            Assert.Equal(Project.IndexOf("MyGame", System.StringComparison.Ordinal), names[0].Start);
        }

        [Fact]
        public void LoadingPolicyValues()
        {
            const string plugin = "{ \"LocalizationTargets\": [ { \"Name\": \"X\", \"LoadingPolicy\": \"\" } ] }";
            var c = ProjectFileLogic.At(plugin, plugin.LastIndexOf('"'));
            Assert.Same(DescriptorLogic.LoadingPolicies, DescriptorLogic.ValuesFor(c));
            Assert.Same(ProjectFileLogic.LoadingPhases, DescriptorLogic.ValuesFor(new JsonCaret { InValue = true, Property = "LoadingPhase" }));
            Assert.Null(DescriptorLogic.ValuesFor(null));
        }
    }
}
