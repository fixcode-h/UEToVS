using System;
using System.IO;
using UEToVS.Engine.Navigation;
using Xunit;

namespace UEToVS.Engine.Tests
{
    public class GeneratedBodyLocatorTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "uetovs-genbody-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        [Fact]
        public void FindsThePerLineExpansionInTheNewestGeneratedHeader()
        {
            string module = Path.Combine(_root, "Source", "MyGame");
            Directory.CreateDirectory(Path.Combine(module, "Public"));
            File.WriteAllText(Path.Combine(_root, "MyGame.uproject"), "{}");
            File.WriteAllText(Path.Combine(module, "MyGame.Build.cs"), "");
            string header = Path.Combine(module, "Public", "Foo.h");
            string text = "#pragma once\n#include \"Foo.generated.h\"\nUCLASS()\nclass AFoo : public AActor\n{\n\tGENERATED_BODY()\n};\n";
            File.WriteAllText(header, text);
            string inc = Path.Combine(_root, "Intermediate", "Build", "Win64", "UnrealEditor", "Inc", "MyGame", "UHT");
            Directory.CreateDirectory(inc);
            File.WriteAllText(Path.Combine(inc, "Foo.generated.h"),
                "#pragma once\n#define FID_MyGame_Source_MyGame_Public_Foo_h_6_INCLASS \\\n\tx\n#define FID_MyGame_Source_MyGame_Public_Foo_h_6_GENERATED_BODY \\\n\ty\n#define FID_MyGame_Source_MyGame_Public_Foo_h_6_GENERATED_BODY_LEGACY \\\n\tz\n");

            var s = GeneratedBodyLocator.Find(header, text, "GENERATED_BODY", 5); // 0-based line of GENERATED_BODY()
            Assert.NotNull(s);
            Assert.EndsWith("Foo.generated.h", s.Location.FilePath);
            Assert.Equal(3, s.Location.Line);
            var legacy = GeneratedBodyLocator.Find(header, text, "GENERATED_UCLASS_BODY", 5);
            Assert.Equal(5, legacy.Location.Line);
            Assert.Null(GeneratedBodyLocator.Find(header, text, "GENERATED_BODY", 20)); // other line: no such macro
            Assert.Null(GeneratedBodyLocator.Find(header, text, "UCLASS", 2));
        }

        [Fact]
        public void NoBuildOutputMeansNoTarget()
        {
            string module = Path.Combine(_root, "Source", "MyGame");
            Directory.CreateDirectory(module);
            File.WriteAllText(Path.Combine(module, "MyGame.Build.cs"), "");
            string header = Path.Combine(module, "Foo.h");
            File.WriteAllText(header, "#include \"Foo.generated.h\"\n");
            Assert.Null(GeneratedBodyLocator.Find(header, File.ReadAllText(header), "GENERATED_BODY", 0));
        }
    }
}
