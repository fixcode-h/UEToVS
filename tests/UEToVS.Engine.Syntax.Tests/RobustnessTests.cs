using System;
using System.IO;
using System.Linq;
using System.Text;
using UEToVS.Contracts;
using Xunit;
using static UEToVS.Engine.Syntax.Tests.TestUtil;

namespace UEToVS.Engine.Syntax.Tests
{
    public class RobustnessTests
    {
        private static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

        [Fact]
        public void MissingSemicolonAfterClassDoesNotLoseTheRest()
        {
            var s = Parse("class A { int X; }\nclass B { int Y; };\nvoid F();");
            One(s, "A");
            One(s, "B");
            One(s, "F", SymbolKind.Function);
        }

        [Fact]
        public void MissingSemicolonAfterMemberResyncsAtReflectionMacro()
        {
            var s = Parse("class A {\n\tint32 Broken\n\tUPROPERTY()\n\tint32 Good;\n\tUFUNCTION()\n\tvoid Fn();\n};\nint After;");
            Assert.Equal(UeFlags.UProperty, One(s, "Good").Ue);
            Assert.Equal(UeFlags.UFunction, One(s, "Fn").Ue);
            One(s, "After");
        }

        [Fact]
        public void UnterminatedFunctionBodyStopsAtNextUClass()
        {
            var s = Parse("void Broken() {\n\tif (x) {\n\t\tFoo();\n}\nUCLASS()\nclass ANext : public AActor { GENERATED_BODY() int32 V; };");
            One(s, "Broken");
            Assert.Equal(UeFlags.UClass | UeFlags.GeneratedBody, One(s, "ANext").Ue);
            One(s, "V", SymbolKind.Field);
            Assert.NotEmpty(s.Diagnostics);
        }

        [Fact]
        public void GarbageBetweenDeclarations()
        {
            var s = Parse("int A;\n@@@ 123 \"str\" ))) ]]] ;\n}}}\nint B;\n= = = ;\nint C;");
            One(s, "A");
            One(s, "B");
            One(s, "C");
        }

        [Fact]
        public void UnbalancedParensInDeclarations()
        {
            var s = Parse("void F(int A, ;\nvoid G();\nclass X { void H(int; void I(); };");
            One(s, "G");
            One(s, "I");
        }

        [Fact]
        public void DeepNestingDoesNotOverflow()
        {
            var code = string.Concat(Enumerable.Repeat("namespace N { class C { ", 2000)) + "int Deep;";
            var s = Parse(code);
            Assert.NotNull(s);
            var braces = new string('{', 100000);
            Assert.NotNull(Parse(braces));
            Assert.NotNull(Parse("void F() " + braces));
            Assert.NotNull(Parse(new string('(', 100000)));
            Assert.NotNull(Parse("template" + string.Concat(Enumerable.Repeat("<", 50000))));
        }

        [Fact]
        public void EveryPrefixOfTheSamplesParses()
        {
            foreach (var name in new[] { "MyCharacter.h", "MyCharacter.cpp" })
            {
                var bytes = Encoding.UTF8.GetBytes(Sample(name));
                for (int len = 0; len <= bytes.Length; len++)
                {
                    var s = CppParser.Parse(bytes.AsSpan(0, len));
                    Assert.NotNull(s);
                    Assert.All(s.Declarations, d => Assert.InRange(d.Offset, 0, len));
                }
            }
        }

        [Fact]
        public void RandomBytesNeverThrow()
        {
            var rng = new Random(12345);
            for (int i = 0; i < 300; i++)
            {
                var bytes = new byte[rng.Next(0, 4000)];
                rng.NextBytes(bytes);
                var s = CppParser.Parse(bytes);
                Assert.DoesNotContain(s.Diagnostics, d => d.Message.StartsWith("internal"));
            }
        }

        [Fact]
        public void RandomTokenSoupNeverThrows()
        {
            string[] vocab =
            {
                "class", "struct", "enum", "namespace", "template", "<", ">", ">>", "(", ")", "{", "}", "[", "]", ";", ":", "::", ",",
                "=", "*", "&", "~", "operator", "UCLASS()", "UPROPERTY(", "GENERATED_BODY()", "FOO_API", "Foo", "Bar", "int", "void",
                "\"s\"", "'c'", "0", "#if X\n", "#else\n", "#endif\n", "#define M(x) x\n", "\n", "public:", "virtual", "const", "typedef",
                "using", "DECLARE_DELEGATE(", "extern \"C\"", "[[", "]]", "R\"(", ")\"", "/*", "*/", "//", "\\\n", "->", ".", "...", "TEXT(",
            };
            var rng = new Random(777);
            for (int i = 0; i < 1500; i++)
            {
                var sb = new StringBuilder();
                int n = rng.Next(1, 120);
                for (int k = 0; k < n; k++) sb.Append(vocab[rng.Next(vocab.Length)]).Append(rng.Next(3) == 0 ? "\n" : " ");
                var s = Parse(sb.ToString());
                Assert.DoesNotContain(s.Diagnostics, d => d.Message.StartsWith("internal"));
            }
        }

        [Fact]
        public void MutatedSamplesNeverThrow()
        {
            var original = Encoding.UTF8.GetBytes(Sample("MyCharacter.h") + Sample("MyCharacter.cpp"));
            var rng = new Random(4242);
            for (int i = 0; i < 400; i++)
            {
                var bytes = (byte[])original.Clone();
                int edits = rng.Next(1, 20);
                for (int e = 0; e < edits; e++)
                {
                    int at = rng.Next(bytes.Length);
                    const string pool = "{}();<>\"'#/*\\\n:";
                    bytes[at] = (byte)pool[rng.Next(pool.Length)];
                }
                var s = CppParser.Parse(bytes);
                Assert.DoesNotContain(s.Diagnostics, d => d.Message.StartsWith("internal"));
            }
        }

        [Fact]
        public void EmptyAndWhitespaceInputs()
        {
            Assert.Empty(CppParser.Parse(ReadOnlySpan<byte>.Empty).Declarations);
            Assert.Empty(Parse("   \n\t\r\n").Declarations);
            Assert.Empty(CppParser.Parse(new byte[] { 0xEF, 0xBB, 0xBF }).Declarations);
            Assert.Empty(CppParser.Parse(new byte[] { 0xFF, 0xFE }).Declarations);
            Assert.Empty(CppParser.Parse(new byte[] { 0xFF, 0xFE, 0x41 }).Declarations);
        }

        [Fact]
        public void ParseFileHandlesMissingFiles()
        {
            var s = CppParser.ParseFile(Path.Combine(Path.GetTempPath(), "uetovs-does-not-exist-" + Guid.NewGuid() + ".h"));
            Assert.Same(FileSummary.Empty, s);
        }

        [Fact]
        public void ParseFileReadsFromDisk()
        {
            var s = CppParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "Samples", "MyCharacter.h"));
            One(s, "AMyCharacter", SymbolKind.Class);
        }
    }
}
