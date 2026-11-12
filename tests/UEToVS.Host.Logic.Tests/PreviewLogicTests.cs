using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Host.Logic.Search;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class CppLexerTests
    {
        private static List<CppToken> Lex(string line, CppLexState state, out CppLexState end)
        {
            var list = new List<CppToken>();
            end = CppLexer.LexLine(line, state, list);
            return list;
        }

        private static string[] Kinds(string line, CppTokenKind kind)
        {
            var t = Lex(line, CppLexState.Normal, out _);
            return t.Where(x => x.Kind == kind).Select(x => line.Substring(x.Start, x.Length)).ToArray();
        }

        [Fact]
        public void KeywordsAndIdentifiers()
        {
            Assert.Equal(new[] { "class", "public" }, Kinds("class UMyThing : public UObject", CppTokenKind.Keyword));
            Assert.Empty(Kinds("classy publicity", CppTokenKind.Keyword));
        }

        [Fact]
        public void CommentsAndStrings()
        {
            const string l = "int x = 1; // hi \"not a string\"";
            Assert.Equal(new[] { "// hi \"not a string\"" }, Kinds(l, CppTokenKind.Comment));
            Assert.Equal(new[] { "1" }, Kinds(l, CppTokenKind.Number));
            Assert.Equal(new[] { "\"a\\\"b\"", "'c'" }, Kinds("f(\"a\\\"b\", 'c');", CppTokenKind.String));
            Assert.Equal(new[] { "L\"wide\"", "u8\"utf\"" }, Kinds("auto a = L\"wide\"; auto b = u8\"utf\";", CppTokenKind.String));
        }

        [Fact]
        public void PreprocessorAndIncludePath()
        {
            Assert.Equal(new[] { "#include" }, Kinds("#include <CoreMinimal.h>", CppTokenKind.Preprocessor));
            Assert.Equal(new[] { "<CoreMinimal.h>" }, Kinds("#include <CoreMinimal.h>", CppTokenKind.String));
            Assert.Equal(new[] { "\"Foo.generated.h\"" }, Kinds("#include \"Foo.generated.h\"", CppTokenKind.String));
            Assert.Equal(new[] { "#  pragma" }, Kinds("  #  pragma once", CppTokenKind.Preprocessor));
            Assert.Empty(Kinds("a # b", CppTokenKind.Preprocessor));
            Assert.Empty(Kinds("if (a < b) {}", CppTokenKind.String));
        }

        [Fact]
        public void Numbers()
        {
            Assert.Equal(new[] { "0x1F", "1.5e-3f", "1'000", ".5" }, Kinds("a(0x1F, 1.5e-3f, 1'000, .5)", CppTokenKind.Number));
        }

        [Fact]
        public void BlockCommentSpansLines()
        {
            var t = Lex("int a; /* start", CppLexState.Normal, out var s1);
            Assert.True(s1.InComment);
            Assert.Equal(CppTokenKind.Comment, t.Last().Kind);
            var t2 = Lex("still comment", s1, out var s2);
            Assert.True(s2.InComment);
            Assert.Single(t2);
            var t3 = Lex("end */ int b;", s2, out var s3);
            Assert.True(s3.IsNormal);
            Assert.Equal(CppTokenKind.Comment, t3[0].Kind);
            Assert.Equal(6, t3[0].Length);
            Assert.Equal(CppTokenKind.Keyword, t3[1].Kind);
        }

        [Fact]
        public void RawStringSpansLines()
        {
            Lex("auto s = R\"xy(line one", CppLexState.Normal, out var s1);
            Assert.Equal(")xy\"", s1.RawDelimiter);
            var t = Lex("two )\" still )xy\"; int c;", s1, out var s2);
            Assert.True(s2.IsNormal);
            Assert.Equal(CppTokenKind.String, t[0].Kind);
            Assert.Equal("two )\" still )xy\"".Length, t[0].Length);
            Assert.Contains(t, x => x.Kind == CppTokenKind.Keyword);
            Assert.Equal(new[] { "R\"(a)\"" }, Kinds("x = R\"(a)\";", CppTokenKind.String));
        }

        [Fact]
        public void LineStartStatesFollowEditorLineBreaks()
        {
            const string text = "a\r\n/* x\r\ny\rz */\nb\u2028/*\n*/";
            var states = CppLexer.LineStartStates(text, out int lines);
            Assert.Equal(7, lines);
            Assert.Equal(new[] { 2, 3, 6 }, states.Keys.OrderBy(k => k).ToArray());
            Assert.True(states[2].InComment);
        }

        [Fact]
        public void UnterminatedLiteralsStopAtLineEnd()
        {
            Assert.Equal(new[] { "\"abc" }, Kinds("x = \"abc", CppTokenKind.String));
            var t = Lex("\"a\\", CppLexState.Normal, out var s);
            Assert.True(s.IsNormal);
            Assert.Equal(3, t[0].Length);
        }
    }

    public class PreviewTextCacheTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static PreviewText P(string path, int chars, DateTime? t = null) => new PreviewText(path, new string('x', chars), t ?? T0, chars);

        [Fact]
        public void HitRequiresSameWriteTime()
        {
            var c = new PreviewTextCache();
            c.Add(P("a.h", 10));
            Assert.NotNull(c.TryGet("A.H", T0));
            Assert.Null(c.TryGet("a.h", T0.AddSeconds(1)));
            Assert.Equal(0, c.Count);
        }

        [Fact]
        public void EvictsLeastRecentlyUsedByCountAndSize()
        {
            var c = new PreviewTextCache(maxEntries: 2, maxChars: 100);
            c.Add(P("a", 10));
            c.Add(P("b", 10));
            Assert.NotNull(c.TryGet("a", T0)); // a is now most recent
            c.Add(P("c", 10));
            Assert.Null(c.TryGet("b", T0));
            Assert.NotNull(c.TryGet("a", T0));
            c.Add(P("d", 95));
            Assert.Equal(1, c.Count);
            Assert.Equal(95, c.Chars);
            c.Add(P("huge", 1000));
            Assert.Null(c.TryGet("huge", T0));
            Assert.NotNull(c.TryGet("d", T0));
        }

        [Fact]
        public void ReplacingAnEntryKeepsCharsConsistent()
        {
            var c = new PreviewTextCache();
            c.Add(P("a", 10));
            c.Add(P("a", 30));
            Assert.Equal(1, c.Count);
            Assert.Equal(30, c.Chars);
            c.Clear();
            Assert.Equal(0, c.Chars);
        }

        [Fact]
        public void PreviewTextKnowsLineStates()
        {
            var p = new PreviewText("f.h", "a\n/*\nb\n*/\nc", T0, 12);
            Assert.Equal(5, p.LineCount);
            Assert.True(p.StateAt(2).InComment);
            Assert.True(p.StateAt(4).IsNormal);
        }
    }
}
