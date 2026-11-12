using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Host.Logic.Search;
using UEToVS.Host.Logic.Shaders;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class HlslLexerTests
    {
        private static List<(string Text, HlslTokenKind Kind)> Lex(string line, CppLexState state = default)
        {
            var list = new List<HlslToken>();
            HlslLexer.LexLine(line, state, list);
            return list.Select(t => (line.Substring(t.Start, t.Length), t.Kind)).ToList();
        }

        [Fact]
        public void ClassifiesHlsl()
        {
            var t = Lex("float4 MainPS(in float2 UV : TEXCOORD0, out float4 Color : SV_Target0) { return saturate(Texture2DSample(Tex, Samp, UV)) * 0.5f; } // done");
            Assert.Contains(("float4", HlslTokenKind.Type), t);
            Assert.Contains(("in", HlslTokenKind.Keyword), t);
            Assert.Contains(("float2", HlslTokenKind.Type), t);
            Assert.Contains(("SV_Target0", HlslTokenKind.Semantic), t);
            Assert.Contains(("return", HlslTokenKind.Keyword), t);
            Assert.Contains(("saturate", HlslTokenKind.Intrinsic), t);
            Assert.Contains(("0.5f", HlslTokenKind.Number), t);
            Assert.Contains(("// done", HlslTokenKind.Comment), t);
            Assert.DoesNotContain(t, x => x.Text == "MainPS" || x.Text == "UV");
        }

        [Fact]
        public void PreprocessorAndBlockComments()
        {
            Assert.Contains(Lex("#include \"/Engine/Private/Common.ush\""), x => x.Kind == HlslTokenKind.Preprocessor);
            var list = new List<HlslToken>();
            var state = HlslLexer.LexLine("/* start", default, list);
            Assert.True(state.InComment);
            var t = Lex("still */ float3 X;", state);
            Assert.Equal(HlslTokenKind.Comment, t[0].Kind);
            Assert.Contains(("float3", HlslTokenKind.Type), t);
        }

        [Theory]
        [InlineData("float4x4", true)]
        [InlineData("half3", true)]
        [InlineData("uint2", true)]
        [InlineData("RWTexture2D", true)]
        [InlineData("float5", false)]
        [InlineData("floaty", false)]
        public void Types(string w, bool expected) => Assert.Equal(expected, HlslLexer.IsType(w));

        [Fact]
        public void Includes()
        {
            string line = "#include \"/Engine/Private/Common.ush\"";
            Assert.Equal("/Engine/Private/Common.ush", HlslLexer.IncludeAt(line, 15, out int s, out int l));
            Assert.Equal(10, s);
            Assert.Null(HlslLexer.IncludeAt(line, 2, out _, out _));
            Assert.Null(HlslLexer.IncludeAt("float X;", 2, out _, out _));

            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ue"));
            var r = HlslLexer.ResolveInclude("/Engine/Private/Common.ush", null, Path.Combine(root, "Engine"), null, null);
            Assert.Equal(Path.Combine(root, "Engine", "Shaders", "Private", "Common.ush"), r.Single());
            var plugins = new Dictionary<string, string> { ["/Plugin/Water"] = Path.Combine(root, "Water", "Shaders") };
            r = HlslLexer.ResolveInclude("/Plugin/Water/Private/W.ush", null, null, null, plugins);
            Assert.Equal(Path.Combine(root, "Water", "Shaders", "Private", "W.ush"), r.Single());
            r = HlslLexer.ResolveInclude("Helpers.ush", Path.Combine(root, "Game", "Shaders", "My.usf"), null, null, null);
            Assert.Equal(Path.Combine(root, "Game", "Shaders", "Helpers.ush"), r.Single());
            r = HlslLexer.ResolveInclude("/Project/X.ush", null, null, Path.Combine(root, "Game"), null);
            Assert.Equal(Path.Combine(root, "Game", "Shaders", "X.ush"), r.Single());
        }
    }
}
