using System.Collections.Generic;
using System.IO;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.IntelliSense;
using Xunit;

namespace UEToVS.Engine.Tests
{
    public sealed class GeneratedCodePreviewTests
    {
        private const string Header = "Source/MyGame/Public/Foo.h";
        private const string Generated = "Intermediate/Build/Win64/UnrealEditor/Inc/MyGame/UHT/Foo.generated.h";

        private const string HeaderText = "#pragma once\n#include \"CoreMinimal.h\"\n#include \"Foo.generated.h\"\n\nUCLASS()\nclass AFoo : public AActor\n{\n\tGENERATED_BODY()\npublic:\n\tUFUNCTION()\n\tvoid Bar();\n};\n";

        private const string GeneratedText =
            "#pragma once\n" +
            "#define FID_MyGame_Source_MyGame_Public_Foo_h_8_RPC_WRAPPERS_NO_PURE_DECLS \\\n" +
            "\tDECLARE_FUNCTION(execBar);\n\n" +
            "#define FID_MyGame_Source_MyGame_Public_Foo_h_8_ARCHIVESERIALIZER\n\n" +
            "#define FID_MyGame_Source_MyGame_Public_Foo_h_8_INCLASS_NO_PURE_DECLS \\\n" +
            "private: \\\n" +
            "\tstatic void StaticRegisterNativesAFoo(); \\\n" +
            "\tfriend struct Z_Construct_UClass_AFoo_Statics; \\\n" +
            "public: \\\n" +
            "\tDECLARE_CLASS(AFoo, AActor, COMPILED_IN_FLAGS(0), CASTCLASS_None, TEXT(\"/Script/MyGame\"), NO_API) \\\n" +
            "\tDECLARE_SERIALIZER(AFoo)\n\n" +
            "#define FID_MyGame_Source_MyGame_Public_Foo_h_8_GENERATED_BODY \\\n" +
            "PRAGMA_DISABLE_DEPRECATION_WARNINGS \\\n" +
            "public: \\\n" +
            "\tFID_MyGame_Source_MyGame_Public_Foo_h_8_RPC_WRAPPERS_NO_PURE_DECLS \\\n" +
            "\tFID_MyGame_Source_MyGame_Public_Foo_h_8_ARCHIVESERIALIZER \\\n" +
            "\tFID_MyGame_Source_MyGame_Public_Foo_h_8_INCLASS_NO_PURE_DECLS \\\n" +
            "private: \\\n" +
            "PRAGMA_ENABLE_DEPRECATION_WARNINGS\n\n" +
            "#define CURRENT_FILE_ID FID_MyGame_Source_MyGame_Public_Foo_h\n";

        private static MiniWorkspace Tree(bool built)
        {
            var files = new Dictionary<string, string>
            {
                ["MyGame.uproject"] = "{}",
                ["Source/MyGame/MyGame.Build.cs"] = "",
                [Header] = HeaderText,
            };
            var w = new MiniWorkspace(files);
            Directory.CreateDirectory(w.PathOf("Intermediate"));
            if (built)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(w.PathOf(Generated)));
                File.WriteAllText(w.PathOf(Generated), GeneratedText);
            }
            GeneratedCodePreview.ClearCaches();
            return w;
        }

        [Fact]
        public void ExpandsTheLineMacroWithSubMacrosOneStatementPerLine()
        {
            var defines = GeneratedCodePreview.ParseDefines(GeneratedText.Split('\n'));
            Assert.Equal("", defines["FID_MyGame_Source_MyGame_Public_Foo_h_8_ARCHIVESERIALIZER"]);
            string code = GeneratedCodePreview.Expand(defines, "FID_MyGame_Source_MyGame_Public_Foo_h_8_GENERATED_BODY", 8);
            Assert.Contains("// RPC_WRAPPERS_NO_PURE_DECLS", code);
            Assert.Contains("DECLARE_FUNCTION(execBar);", code);
            Assert.Contains("static void StaticRegisterNativesAFoo();", code);
            Assert.Contains("DECLARE_SERIALIZER(AFoo)", code);
            Assert.DoesNotContain("PRAGMA_", code);
            Assert.DoesNotContain("ARCHIVESERIALIZER", code); // empty sub-macro left out
            Assert.DoesNotContain("\\", code);
            Assert.True(code.IndexOf("execBar", System.StringComparison.Ordinal) < code.IndexOf("StaticRegisterNatives", System.StringComparison.Ordinal));
        }

        [Fact]
        public void QuickInfoShowsTheGeneratedCode()
        {
            using var w = Tree(built: true);
            var svc = new IntelliSenseService(w.Backend, w.Files);
            var q = svc.GetQuickInfo(w.Caret(Header, "GENERATED_BODY", into: 3), CancellationToken.None);
            Assert.Equal(GeneratedCodeState.Available, q.GeneratedCodeState);
            Assert.EndsWith("Foo.generated.h", q.GeneratedCodeFile);
            Assert.Contains("DECLARE_FUNCTION(execBar);", q.GeneratedCode);
            Assert.Equal("GENERATED_BODY()", q.Declaration);

            // cached by timestamp: a rebuilt header (other line numbers) is read again
            File.WriteAllText(w.PathOf(Generated), GeneratedText.Replace("_h_8_", "_h_9_") + "\n");
            File.SetLastWriteTimeUtc(w.PathOf(Generated), System.DateTime.UtcNow.AddMinutes(1));
            var stale = svc.GetQuickInfo(w.Caret(Header, "GENERATED_BODY", into: 3), CancellationToken.None);
            Assert.Equal(GeneratedCodeState.Stale, stale.GeneratedCodeState);
            Assert.Null(stale.GeneratedCode);
        }

        [Fact]
        public void NotBuiltYetIsReported()
        {
            using var w = Tree(built: false);
            var svc = new IntelliSenseService(w.Backend, w.Files);
            var q = svc.GetQuickInfo(w.Caret(Header, "GENERATED_BODY", into: 3), CancellationToken.None);
            Assert.Equal(GeneratedCodeState.NotBuilt, q.GeneratedCodeState);
            Assert.Null(q.GeneratedCode);
            Assert.Equal("Foo.generated.h", q.GeneratedCodeFile);
        }

        [Fact]
        public void ShortNameDropsTheFileIdAndLine()
        {
            Assert.Equal("INCLASS_NO_PURE_DECLS", GeneratedCodePreview.ShortName("FID_A_h_12_INCLASS_NO_PURE_DECLS", "_12_"));
            Assert.Equal("X", GeneratedCodePreview.ShortName("X", "_12_"));
        }
    }
}
