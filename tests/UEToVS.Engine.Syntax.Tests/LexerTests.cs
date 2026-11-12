using System.Linq;
using System.Text;
using Xunit;
using static UEToVS.Engine.Syntax.Tests.TestUtil;

namespace UEToVS.Engine.Syntax.Tests
{
    public class LexerTests
    {
        [Fact]
        public void IdentifiersKeywordsNumbers()
        {
            const string code = "class Foo : public Bar { int32 x = 0x1F'00u + 1.5e-3f; };";
            var toks = Lex(code);
            Assert.Equal(TokenKind.Keyword, toks[0].Kind);
            Assert.Equal(Keyword.Class, toks[0].Keyword);
            Assert.Equal(TokenKind.Identifier, toks[1].Kind);
            Assert.Equal("Foo", Text(code, toks[1]));
            var numbers = toks.Where(t => t.Kind == TokenKind.Number).Select(t => Text(code, t)).ToArray();
            Assert.Equal(new[] { "0x1F'00u", "1.5e-3f" }, numbers);
        }

        [Fact]
        public void Punctuators()
        {
            var texts = Texts("a::b->c...d.*e->*f<<=g>>=h<=>i&&j||k##");
            Assert.Equal(new[] { "a", "::", "b", "->", "c", "...", "d", ".*", "e", "->*", "f", "<<=", "g", ">>=", "h", "<=>", "i", "&&", "j", "||", "k", "##" }, texts);
            var toks = Lex("x::y");
            Assert.Equal(Punct.ColonColon, toks[1].Punct);
        }

        [Fact]
        public void StringAndCharLiteralsWithEscapesAndPrefixes()
        {
            var texts = Texts("a = \"x\\\"{\"; b = L\"w\"; c = u8\"u\"; d = '\\''; e = U'x'; f = TEXT(\"}\");");
            Assert.Contains("\"x\\\"{\"", texts);
            Assert.Contains("L\"w\"", texts);
            Assert.Contains("u8\"u\"", texts);
            Assert.Contains("'\\''", texts);
            Assert.Contains("U'x'", texts);
            Assert.Contains("\"}\"", texts);
            Assert.DoesNotContain("{", texts);
        }

        [Fact]
        public void RawStrings()
        {
            const string code = "auto s = R\"delim(a \" ) } \n )\" more)delim\"; int after;";
            var toks = Lex(code);
            var str = Assert.Single(toks, t => t.Kind == TokenKind.String);
            Assert.EndsWith(")delim\"", Text(code, str));
            Assert.Contains(toks, t => Text(code, t) == "after");
            Assert.Equal(TokenKind.String, Lex("LR\"(x)\"")[0].Kind);
        }

        [Fact]
        public void CommentsAreSkippedButDocCommentsKept()
        {
            const string code = "// plain\n/* block { */ int /** doc */ x; /// line doc\n//! bang\n/*! qt */ //// banner\n/**/ y;";
            var toks = Lex(code);
            Assert.Equal(new[] { "int", "/** doc */", "x", ";", "/// line doc", "//! bang", "/*! qt */", "y", ";" }, toks.Select(t => Text(code, t)).ToArray());
            Assert.Equal(TokenKind.DocComment, toks[1].Kind);
            var withComments = Lex(code, keepComments: true);
            Assert.Contains(withComments, t => t.Kind == TokenKind.Comment && Text(code, t) == "// plain");
        }

        [Fact]
        public void TrailingMemberDocIsNotADocComment()
        {
            var toks = Lex("int x; ///< about x\nint y; /**< about y */");
            Assert.DoesNotContain(toks, t => t.Kind == TokenKind.DocComment);
        }

        [Fact]
        public void LineCommentContinuesWithBackslash()
        {
            Assert.Equal(new[] { "b" }, Texts("// a \\\n still comment\nb"));
        }

        [Fact]
        public void DirectivesAreSingleTokensWithContinuations()
        {
            const string code = "#define M(x) \\\n  do { x; } \\\n  while(0)\n  #  include <a/b.h>\n#if /* multi\nline */ X\nint a;\n#endif";
            var toks = Lex(code);
            Assert.Equal(DirectiveKind.Define, toks[0].Directive);
            Assert.EndsWith("while(0)", Text(code, toks[0]));
            Assert.Equal(DirectiveKind.Include, toks[1].Directive);
            Assert.Equal(DirectiveKind.If, toks[2].Directive);
            Assert.EndsWith("X", Text(code, toks[2]));
            Assert.Equal("int", Text(code, toks[3]));
            Assert.Equal(DirectiveKind.Endif, toks[^1].Directive);
        }

        [Fact]
        public void HashNotAtLineStartIsPunctuator()
        {
            var toks = Lex("a # b");
            Assert.Equal(Punct.Hash, toks[1].Punct);
        }

        [Fact]
        public void BomCrLfAndContinuationsInCode()
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("int\r\nx \\\r\n = 1;\r\n")).ToArray();
            var toks = CppParser.Tokenize(bytes);
            Assert.Equal(3, toks[0].Start); // BOM skipped, offsets count it
            Assert.Equal(new[] { "int", "x", "=", "1", ";" }, toks.Select(t => Encoding.UTF8.GetString(bytes, t.Start, t.Length)).ToArray());
            Assert.True((toks[1].Flags & TokenFlags.AtLineStart) != 0);
            Assert.True((toks[2].Flags & TokenFlags.AtLineStart) == 0); // continuation does not start a line
        }

        [Fact]
        public void Utf8IdentifiersAndStrings()
        {
            const string code = "int Größe = 1; auto s = \"日本\";";
            var texts = Texts(code);
            Assert.Contains("Größe", texts);
            Assert.Contains("\"日本\"", texts);
        }

        [Fact]
        public void UnterminatedLiteralsStopAtLineEnd()
        {
            var texts = Texts("a = \"open\nb = 'x\nc;");
            Assert.Contains("b", texts);
            Assert.Contains("c", texts);
        }

        [Fact]
        public void UnterminatedBlockCommentRunsToEnd()
        {
            Assert.Equal(new[] { "a" }, Texts("a /* never closed"));
        }

        [Fact]
        public void LineMapConvertsOffsetsToUtf16Columns()
        {
            const string code = "ab\n日本x\r\n😀y";
            var bytes = Encoding.UTF8.GetBytes(code);
            var map = LineMap.Build(bytes);
            Assert.Equal(3, map.LineCount);
            int xOffset = System.Array.IndexOf(bytes, (byte)'x');
            Assert.Equal((1, 2), map.GetPosition(bytes, xOffset));
            int yOffset = System.Array.IndexOf(bytes, (byte)'y');
            Assert.Equal((2, 2), map.GetPosition(bytes, yOffset)); // surrogate pair counts 2
            Assert.Equal(yOffset, map.GetOffset(bytes, 2, 2));
            Assert.Equal(xOffset, map.GetOffset(bytes, 1, 2));
        }

        [Fact]
        public void Utf16InputIsTranscoded()
        {
            var text = "// c\nclass FÄrger { int X; };";
            var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();
            var s = CppParser.Parse(bytes);
            var cls = One(s, "FÄrger", UEToVS.Contracts.SymbolKind.Class);
            Assert.Equal(1, cls.Line);
            Assert.NotNull(s.TranscodedText);
            Assert.Equal("FÄrger", Encoding.UTF8.GetString(s.TranscodedText, cls.Offset, cls.NameLength));
        }

        [Fact]
        public void NumbersStartingWithDot()
        {
            Assert.Equal(new[] { "x", "=", ".5f", ";" }, Texts("x = .5f;"));
        }
    }
}
