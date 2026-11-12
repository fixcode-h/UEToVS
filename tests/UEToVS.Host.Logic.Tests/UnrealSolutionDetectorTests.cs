using System.IO;
using UEToVS.Host.Logic.Unreal;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class UnrealSolutionDetectorTests
    {
        [Fact]
        public void UProjectNextToSolution()
        {
            using (var t = new TempDir())
            {
                var sln = t.File("Game.sln", "Microsoft Visual Studio Solution File");
                var up = t.File("Game.uproject", "{}");
                var r = UnrealSolutionDetector.Detect(sln);
                Assert.Equal(UnrealSolutionKind.Project, r.Kind);
                Assert.Equal("uproject.sibling", r.Reason);
                Assert.Equal(Path.GetFullPath(up), Path.GetFullPath(r.ProjectFile));
            }
        }

        [Fact]
        public void UProjectInParent()
        {
            using (var t = new TempDir())
            {
                t.File("Game.uproject", "{}");
                var sln = t.File(Path.Combine("Intermediate", "ProjectFiles", "Game.sln"), "x");
                var r = UnrealSolutionDetector.Detect(sln);
                Assert.True(r.IsUnreal);
                Assert.Equal("uproject.parent", r.Reason);
            }
        }

        [Fact]
        public void EngineLayoutWithSingleGameFolder()
        {
            using (var t = new TempDir())
            {
                t.Sub("Engine", "Source");
                t.File(Path.Combine("Engine", "Build", "Build.version"), "{}");
                var game = t.File(Path.Combine("MyGame", "MyGame.uproject"), "{}");
                var sln = t.File("UE5.sln", "x");
                var r = UnrealSolutionDetector.Detect(sln);
                Assert.Equal(UnrealSolutionKind.Project, r.Kind);
                Assert.Equal("uproject.child", r.Reason);
                Assert.Equal(Path.GetFullPath(game), Path.GetFullPath(r.ProjectFile));
                Assert.Equal(Path.GetFullPath(t.Path), Path.GetFullPath(r.EngineRoot));
            }
        }

        [Fact]
        public void EngineLayoutWithoutGame()
        {
            using (var t = new TempDir())
            {
                t.Sub("Engine", "Source");
                t.File(Path.Combine("Engine", "Build", "Build.version"), "{}");
                t.File(Path.Combine("A", "A.uproject"), "{}");
                t.File(Path.Combine("B", "B.uproject"), "{}");
                var r = UnrealSolutionDetector.Detect(t.File("UE5.sln", "x"));
                Assert.Equal(UnrealSolutionKind.Engine, r.Kind); // two games → ambiguous → engine
                Assert.Equal("engine.layout", r.Reason);
            }
        }

        [Fact]
        public void UbtGeneratedSolutionText()
        {
            const string text = "Project(\"{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}\") = \"UE5\", \"Engine\\Intermediate\\ProjectFiles\\UE5.vcxproj\", \"{GUID}\"";
            Assert.True(UnrealSolutionDetector.IsUbtGeneratedSolutionText(text));
            Assert.False(UnrealSolutionDetector.IsUbtGeneratedSolutionText("Project(\"x\") = \"App\", \"App\\App.vcxproj\""));
            Assert.False(UnrealSolutionDetector.IsUbtGeneratedSolutionText(null));
        }

        [Fact]
        public void UbtSolutionWithoutUProjectIsEngineKind()
        {
            using (var t = new TempDir())
            {
                var sln = t.File("UE5.sln", "= \"UE5\", \"Engine\\Intermediate\\ProjectFiles\\UE5.vcxproj\"");
                var r = UnrealSolutionDetector.Detect(sln);
                Assert.Equal(UnrealSolutionKind.Engine, r.Kind);
                Assert.Equal("ubt.sln", r.Reason);
            }
        }

        [Fact]
        public void PlainSolutionIsNotUnreal()
        {
            using (var t = new TempDir())
            {
                var sln = t.File(Path.Combine("deep", "er", "still", "deeper", "App.sln"), "Project(\"x\") = \"App\"");
                Assert.False(UnrealSolutionDetector.Detect(sln).IsUnreal);
            }
            Assert.False(UnrealSolutionDetector.Detect(null, null).IsUnreal);
            Assert.False(UnrealSolutionDetector.Detect(Path.Combine(Path.GetTempPath(), "does-not-exist-xyz", "a.sln")).IsUnreal);
        }
    }
}
