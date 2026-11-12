using System.Linq;
using UEToVS.Host.Logic.Snippets;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class SurroundWithTests
    {
        [Fact]
        public void Catalog_HasTheUeTemplates_AndNoTry()
        {
            var keys = SurroundCatalog.BuiltIn.Select(t => t.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            foreach (var k in new[] { "isvalid", "if", "for", "block", "witheditor", "notshipping", "cyclecounter", "cpuprofiler", "quickcounter", "region" })
                Assert.Contains(k, keys);
            Assert.DoesNotContain(SurroundCatalog.BuiltIn, t => t.Body.Contains("try"));
            foreach (var t in SurroundCatalog.BuiltIn)
            {
                Assert.Contains(SurroundCatalog.Selection, t.Body);
                var e = SurroundExpander.Expand(t, "Foo();", "", "\t", "\n");
                Assert.Contains("Foo();", e.Text);
                Assert.DoesNotContain("$", e.Text);
                Assert.InRange(e.EndOffset, 0, e.Text.Length);
                Assert.False(string.IsNullOrEmpty(t.Title));
                Assert.False(string.IsNullOrEmpty(t.Preview));
            }
        }

        [Fact]
        public void IsValid_IndentsBodyByFirstLineAndKeepsFields()
        {
            var t = SurroundCatalog.Find("isvalid");
            // first line's indent removed by the caller; the second line is indented like the first
            var e = SurroundExpander.Expand(t, "Foo();\n    Bar();", "    ", "\t", "\n");
            Assert.Equal("if (IsValid(Object))\n    {\n    \tFoo();\n    \tBar();\n    }", e.Text);
            var obj = e.TabStops().Single();
            Assert.Equal("Object", obj.Name);
            Assert.Equal("Object", e.Text.Substring(obj.Start, obj.Length));
            Assert.Equal(e.Text.Length, e.EndOffset);
        }

        [Fact]
        public void FieldsAfterTheSelectionAreShifted()
        {
            var e = SurroundExpander.Expand("{\n\t$SELECTION$\n\t$After=X$\n}$END$", "A;\nB;", "", "  ", "\r\n");
            Assert.Equal("{\r\n  A;\r\n  B;\r\n  X\r\n}", e.Text);
            var f = e.Fields.Single();
            Assert.Equal("X", e.Text.Substring(f.Start, f.Length));
            Assert.Equal(e.Text.Length, e.EndOffset);
        }

        [Fact]
        public void PreprocessorTemplatesDoNotAddIndent_AndSelectionIsVerbatim()
        {
            var t = SurroundCatalog.Find("witheditor");
            var e = SurroundExpander.Expand(t, "Log($Money$);\n\tOther();\n#define X 1\n\n\tLast();", "\t", "    ", "\n");
            Assert.Equal("#if WITH_EDITOR\n\tLog($Money$);\n\tOther();\n#define X 1\n\n\tLast();\n\t#endif // WITH_EDITOR", e.Text);
            Assert.Empty(e.TabStops());
        }

        [Fact]
        public void Region_HasNameField()
        {
            var e = SurroundExpander.Expand(SurroundCatalog.Find("region"), "int32 A;", "", "\t", "\n");
            Assert.Equal("#pragma region Region\nint32 A;\n#pragma endregion", e.Text);
            Assert.Equal("Name", e.TabStops().Single().Name);
        }

        [Fact]
        public void CycleCounter_FieldBeforeSelection()
        {
            var e = SurroundExpander.Expand(SurroundCatalog.Find("cyclecounter"), "Work();", "", "\t", "\n");
            Assert.Equal("{\n\tSCOPE_CYCLE_COUNTER(STAT_MyStat);\n\tWork();\n}", e.Text);
            var f = e.TabStops().Single();
            Assert.Equal("STAT_MyStat", e.Text.Substring(f.Start, f.Length));
        }

        [Theory]
        [InlineData(3, 5, 0, 3, 4)]
        [InlineData(3, 5, 2, 3, 5)]
        [InlineData(3, 3, 0, 3, 3)]
        public void LineRange_DropsTrailingLineAtColumnZero(int s, int e, int col, int first, int last)
        {
            Assert.Equal((first, last), SurroundExpander.LineRange(s, e, col));
        }
    }
}
