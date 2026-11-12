using System.Linq;
using UEToVS.Host.Logic.Editing;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class ClipboardRingTests
    {
        [Fact]
        public void Ring_NewestFirst_DedupesAndCaps()
        {
            var r = new ClipboardRing(3);
            Assert.True(r.Add("a"));
            r.Add("b");
            r.Add("c");
            r.Add("a");
            Assert.Equal(new[] { "a", "c", "b" }, r.Items);
            r.Add("d");
            Assert.Equal(new[] { "d", "a", "c" }, r.Items);
            r.Capacity = 2;
            Assert.Equal(new[] { "d", "a" }, r.Items);
        }

        [Fact]
        public void Ring_SkipsEmptyAndHuge()
        {
            var r = new ClipboardRing();
            Assert.False(r.Add(null));
            Assert.False(r.Add("  \r\n"));
            Assert.False(r.Add(new string('x', ClipboardRing.MaxBytes + 1)));
            Assert.False(r.Add(new string('中', ClipboardRing.MaxBytes / 2))); // 3 bytes each in UTF-8
            Assert.True(r.Add(new string('x', ClipboardRing.MaxBytes)));
            Assert.Equal(1, r.Count);
            Assert.Equal(ClipboardRing.DefaultCapacity, r.Capacity);
        }

        [Fact]
        public void Preview_CollapsesWhitespace()
        {
            Assert.Equal("if (X) { Y(); }", ClipboardRing.Preview("  if (X)\r\n\t{\n\t\tY();\n\t}\n"));
            Assert.Equal("abc…", ClipboardRing.Preview("abcdef", 3));
            Assert.Equal(3, ClipboardRing.LineCount("a\nb\nc"));
        }
    }

    public sealed class HashtagScannerTests
    {
        [Fact]
        public void Scan_CommentsOnly()
        {
            string text = "#include \"A.h\"\n" +
                          "// #todo fix the spawn\n" +
                          "void F() { const char* s = \"// #notatag\"; } // #Perf: hot path\n" +
                          "/* block\n   #review-me later */\n" +
                          "// #if 0 commented-out code, #include too\n" +
                          "// issue#12 and C# and &#x41;\n" +
                          "auto r = R\"x(// #raw)x\"; // #todo second\n";
            var hits = HashtagScanner.Scan(text, "A.cpp");
            Assert.Equal(new[] { "todo", "perf", "review-me", "todo" }, hits.Select(h => h.Tag));
            Assert.Equal(1, hits[0].Line);
            Assert.Equal(3, hits[0].Column);
            Assert.Equal("fix the spawn", hits[0].Text);
            Assert.Equal("hot path", hits[1].Text);
            Assert.Equal(4, hits[2].Line);
            Assert.Equal("later", hits[2].Text);
            Assert.Equal(7, hits[3].Line);
        }

        [Fact]
        public void Group_ByTagMostFirst()
        {
            var hits = HashtagScanner.Scan("// #b x\n// #a y\n// #b z\n", "F.h");
            var g = HashtagScanner.Group(hits);
            Assert.Equal(new[] { "b", "a" }, g.Select(x => x.Tag));
            Assert.Equal(new[] { 0, 2 }, g[0].Hits.Select(h => h.Line));
            Assert.Empty(HashtagScanner.Scan("int x; // no tags", "F.h"));
        }
    }
}
