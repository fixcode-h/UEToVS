using System.IO;
using System.Linq;
using UEToVS.Core.Bookmarks;
using UEToVS.Core.Keymap;
using UEToVS.Core.Navigation;
using UEToVS.Core.Unreal;
using Xunit;

namespace UEToVS.Core.Tests
{
    public class UnrealTests
    {
        [Theory]
        [InlineData(@"C:\P\Intermediate\Build\Win64\UnrealEditor\Inc\P\UHT\Actor.generated.h", true)]
        [InlineData(@"C:\P\Source\P\Actor.gen.cpp", true)]
        [InlineData(@"C:\P\Source\P\P.init.gen.cpp", true)]
        [InlineData("/p/Intermediate/x/Def.h", true)]
        [InlineData(@"C:\P\Source\P\Actor.h", false)]
        [InlineData(@"C:\P\Source\P\Actor.cpp", false)]
        [InlineData(@"E:\UE\Engine\Source\Runtime\CoreUObject\Public\UObject\ObjectMacros.h", false)]
        [InlineData(@"C:\P\Source\IntermediateStuff\A.h", false)]
        [InlineData(null, false)]
        public void GeneratedFilesAreRecognised(string path, bool expected) => Assert.Equal(expected, GeneratedFiles.IsGenerated(path));

        [Fact]
        public void RpcPairs()
        {
            Assert.Equal(new[] { "Server_Fire_Implementation", "Server_Fire_Validate" }, RelatedNames.RelatedFunctionNames("Server_Fire"));
            Assert.Equal(new[] { "Server_Fire", "Server_Fire_Validate" }, RelatedNames.RelatedFunctionNames("Server_Fire_Implementation"));
            Assert.Equal(new[] { "OnHit", "OnHit_Implementation" }, RelatedNames.RelatedFunctionNames("OnHit_Validate"));
        }

        [Fact]
        public void Counterparts()
        {
            Assert.Contains("MyActor.cpp", RelatedNames.CounterpartFileNames("C:/P/Source/G/Public/MyActor.h"));
            Assert.Contains("MyActor.generated.h", RelatedNames.CounterpartFileNames("C:/P/Source/G/Public/MyActor.h"));
            Assert.Contains("MyActor.h", RelatedNames.CounterpartFileNames("MyActor.generated.h"));
            var cands = RelatedNames.CounterpartPathCandidates(Path.Combine("P", "G", "Public", "MyActor.h"));
            Assert.Contains(Path.Combine("P", "G", "Private", "MyActor.cpp"), cands);
        }

        private const string Rules = @"using UnrealBuildTool;

public class MyGame : ModuleRules
{
	public MyGame(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(new string[] { ""Core"", ""CoreUObject"", ""Engine"", ""InputCore"" });

		PrivateDependencyModuleNames.AddRange(
			new string[]
			{
				""Slate"",
				""SlateCore"",
			}
		);
		// PrivateDependencyModuleNames.Add(""Commented"");
	}
}
";

        [Fact]
        public void ParseBuildCs()
        {
            var b = new BuildCsFile("MyGame", Rules);
            Assert.Contains("Engine", b.PublicDependencies);
            Assert.Contains("SlateCore", b.PrivateDependencies);
            Assert.DoesNotContain("Commented", b.PrivateDependencies);
        }

        [Fact]
        public void AddToSingleLineList()
        {
            var b = new BuildCsFile("MyGame", Rules);
            Assert.True(b.AddDependency("GameplayTags", true));
            Assert.Contains(@"""InputCore"", ""GameplayTags"" }", b.Text);
            Assert.False(b.AddDependency("GameplayTags", false));
        }

        [Fact]
        public void AddToMultiLineList()
        {
            var b = new BuildCsFile("MyGame", Rules);
            Assert.True(b.AddDependency("UMG", false));
            Assert.Contains("\"SlateCore\",", b.Text);
            Assert.Contains("\t\t\t\t\"UMG\",", b.Text);
            Assert.Contains("UMG", new BuildCsFile("MyGame", b.Text).PrivateDependencies);
        }

        [Fact]
        public void AddWhenNoList()
        {
            const string text = "public class Foo : ModuleRules\n{\n\tpublic Foo(ReadOnlyTargetRules Target) : base(Target)\n\t{\n\t\tPCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;\n\t}\n}\n";
            var b = new BuildCsFile("Foo", text);
            Assert.True(b.AddDependency("Engine", false));
            Assert.Contains("PrivateDependencyModuleNames.Add(\"Engine\");", b.Text);
            Assert.True(b.Text.IndexOf("Add(\"Engine\")") < b.Text.LastIndexOf("\t}"));
        }

        [Fact]
        public void ModuleLocatorWalksUp()
        {
            string root = Path.Combine("X", "Source", "MyGame");
            var loc = new ModuleLocator(dir => dir == root ? new[] { Path.Combine(root, "MyGame.Build.cs") } : new string[0]);
            Assert.Equal("MyGame", loc.FindModuleName(Path.Combine(root, "Private", "Sub", "A.cpp")));
            Assert.Null(loc.FindModuleName(Path.Combine("Y", "B.cpp")));
        }

        [Fact]
        public void Includes()
        {
            Assert.Equal("GameFramework/Actor.h", IncludeDirective.Parse("#include \"GameFramework/Actor.h\""));
            Assert.Equal("vector", IncludeDirective.Parse("  #  include <vector>"));
            Assert.Null(IncludeDirective.Parse("// nothing"));
            Assert.True(IncludeDirective.Matches(@"C:\UE\Engine\Source\Runtime\Engine\Classes\GameFramework\Actor.h", "GameFramework/Actor.h"));
            Assert.False(IncludeDirective.Matches(@"C:\UE\MyActor.h", "Actor.h"));
        }

        [Fact]
        public void Bookmarks()
        {
            var s = new BookmarkStore();
            var loc = new CodeLocation("a.cpp", 5, 3);
            Assert.True(s.Toggle(loc));
            Assert.False(s.Toggle(loc));
            s.SetMnemonic(loc, '1');
            s.SetMnemonic(new CodeLocation("b.cpp", 9, 0), '1'); // moves
            Assert.Equal("b.cpp", s.Get('1').Location.FilePath);
            Assert.Single(s.Items);
            s.SetMnemonic(new CodeLocation("b.cpp", 9, 0), '1'); // toggles off
            Assert.Empty(s.Items);
        }

        [Fact]
        public void PresetsHaveNoDuplicateKeysPerScope()
        {
            foreach (var p in KeymapPresets.All)
            {
                var dups = p.Bindings.GroupBy(b => b.DteBinding.ToLowerInvariant()).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                Assert.Empty(dups);
            }
        }
    }
}

namespace UEToVS.Core.Tests
{
    public class VisibilityTests
    {
        [Xunit.Fact]
        public void TransitivePublicDependencies()
        {
            var rules = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Game"] = "PublicDependencyModuleNames.AddRange(new string[] { \"Engine\" }); PrivateDependencyModuleNames.Add(\"Slate\");",
                ["Engine"] = "PublicDependencyModuleNames.AddRange(new string[] { \"CoreUObject\" }); PrivateDependencyModuleNames.Add(\"Renderer\");",
                ["CoreUObject"] = "PublicDependencyModuleNames.Add(\"Core\");",
                ["Slate"] = "PublicDependencyModuleNames.Add(\"SlateCore\"); PublicIncludePathModuleNames.Add(\"InputCore\");",
            };
            var visible = UEToVS.Core.Unreal.BuildCsFile.VisibleModules("Game", m => rules.TryGetValue(m, out var t) ? new UEToVS.Core.Unreal.BuildCsFile(m, t) : null);
            foreach (var m in new[] { "Game", "Engine", "CoreUObject", "Core", "Slate", "SlateCore", "InputCore" }) Xunit.Assert.Contains(m, visible);
            Xunit.Assert.DoesNotContain("Renderer", visible);
        }
    }
}
