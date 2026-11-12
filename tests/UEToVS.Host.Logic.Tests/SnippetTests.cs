using System.Linq;
using UEToVS.Host.Logic.Snippets;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class SnippetTests
    {
        [Fact]
        public void Catalog_HasUeTemplates_UniqueShortcuts()
        {
            var shortcuts = SnippetCatalog.BuiltIn.Select(t => t.Shortcut).ToList();
            Assert.Equal(shortcuts.Count, shortcuts.Distinct().Count());
            foreach (var s in new[] { "uclass", "ustruct", "uenum", "uprop", "ufunc", "uinterface", "uelog", "uecheck", "dmdel", "mdel" })
                Assert.Contains(s, shortcuts);
            // every body expands without leftover '$' fields
            foreach (var t in SnippetCatalog.BuiltIn)
            {
                var e = SnippetExpander.Expand(t, "", "\t", "\n", "C:/P/Source/MyGame/MyActor.h");
                Assert.DoesNotContain("$", e.Text);
                Assert.InRange(e.EndOffset, 0, e.Text.Length);
                Assert.False(string.IsNullOrEmpty(t.Title));
            }
        }

        [Fact]
        public void Expand_IndentsFieldsAndLinks()
        {
            var t = SnippetCatalog.Find("uclass");
            var e = SnippetExpander.Expand(t, "    ", "  ", "\r\n", "C:/P/Source/MyGame/AMyActor.h");
            Assert.StartsWith("UCLASS()\r\n    class AMyActor : public AActor\r\n    {\r\n      GENERATED_BODY()", e.Text);
            var name = e.Fields.Where(f => f.Name == "Name").ToList();
            Assert.Equal(2, name.Count);
            Assert.True(name[0].IsPrimary);
            Assert.False(name[1].IsPrimary);
            Assert.Equal("AMyActor", e.Text.Substring(name[1].Start, name[1].Length));
            Assert.Equal(new[] { "Specifiers", "Name", "Base" }, e.TabStops().Select(f => f.Name));
            Assert.Equal("", e.Text.Substring(e.Fields[0].Start, e.Fields[0].Length));
        }

        [Fact]
        public void Expand_EndAndEscapes()
        {
            var e = SnippetExpander.Expand("a$$b $X=1$ $END$c", "", "\t", "\n", null);
            Assert.Equal("a$b 1 c", e.Text);
            Assert.Equal(6, e.EndOffset);
            Assert.Equal("Thing", SnippetExpander.StripUePrefix("AThing"));
            Assert.Equal("Health", SnippetExpander.StripUePrefix("UHealth"));
            Assert.Equal("ABC", SnippetExpander.StripUePrefix("ABC"));
        }

        [Fact]
        public void SyncLinked_CopiesPrimaryText()
        {
            string text = "Foo Bar Foo";
            var fields = new[] { ("N", 0, 3, true), ("M", 4, 3, true), ("N", 8, 3, false) };
            string buffer = "Baz Bar Foo";
            var edits = SnippetExpander.SyncLinked(fields, (s, l) => buffer.Substring(s, l));
            Assert.Single(edits);
            Assert.Equal((8, 3, "Baz"), edits[0]);
            Assert.Empty(SnippetExpander.SyncLinked(fields, (s, l) => text.Substring(s, l)));
        }

        [Theory]
        [InlineData("    uclass", 10, 4, "uclass")]
        [InlineData("uprop", 5, 0, "uprop")]
        [InlineData("  x = uprop", 11, -1, null)]
        [InlineData("    uclass", 6, -1, null)]
        [InlineData("    ", 4, -1, null)]
        public void ShortcutAtLineStart(string line, int caret, int expected, string word)
        {
            Assert.Equal(expected, SnippetCatalog.ShortcutAtLineStart(line, caret, out string w));
            Assert.Equal(word, w);
        }

        [Theory]
        [InlineData("\tActor.if", "Actor", true, "if")]
        [InlineData("\tGetOwner()->GetWorld().notnull", "GetOwner()->GetWorld()", true, "notnull")]
        [InlineData("\tauto X = Obj.cast", "Obj", false, "cast")]
        [InlineData("\tFoo::Bar<int>(A, B)[0].", "Foo::Bar<int>(A, B)[0]", true, "")]
        [InlineData("\tif (*Ptr.isv", "*Ptr", false, "isv")]
        [InlineData("\tItems.for", "Items", true, "for")]
        [InlineData("\t\"abc\".text", "\"abc\"", true, "text")]
        public void Postfix_Analyze(string line, string expr, bool atStart, string typed)
        {
            var site = PostfixTemplates.Analyze(line, line.Length);
            Assert.NotNull(site);
            Assert.Equal(expr, site.Expression);
            Assert.Equal(atStart, site.AtLineStart);
            Assert.Equal(typed, site.Typed);
        }

        [Theory]
        [InlineData("\t// Actor.if")]
        [InlineData("\tFoo(\"a.if")]
        [InlineData("\t.if")]
        [InlineData("\t1.f")]
        [InlineData("\tthis.if")]
        public void Postfix_NotApplicable(string line) => Assert.Null(PostfixTemplates.Analyze(line, line.Length));

        [Fact]
        public void Postfix_TemplatesForSite()
        {
            var mid = PostfixTemplates.Analyze("\tauto X = Obj.", 14);
            Assert.DoesNotContain(PostfixTemplates.For(mid), t => t.Statement);
            Assert.Contains(PostfixTemplates.For(mid), t => t.Key == "cast");
            var start = PostfixTemplates.Analyze("\tObj.no", 7);
            Assert.Equal(new[] { "notnull", "not" }, PostfixTemplates.For(start).Select(t => t.Key));

            var e = SnippetExpander.Expand(PostfixTemplates.Body(PostfixTemplates.Find("cast"), "GetOwner()"), "", "\t", "\n", null);
            Assert.Equal("Cast<UObject>(GetOwner())", e.Text);
            Assert.Equal("Type", e.TabStops().Single().Name);
            var f = SnippetExpander.Expand(PostfixTemplates.Body(PostfixTemplates.Find("notnull"), "A"), "\t", "\t", "\n", null);
            Assert.Equal("if (A != nullptr)\n\t{\n\t\t\n\t}", f.Text);
            Assert.Equal("if (A != nullptr)\n\t{\n\t\t".Length, f.EndOffset);
            Assert.Equal("IsValid(Weird$Name)", SnippetExpander.Expand(PostfixTemplates.Body(PostfixTemplates.Find("isvalid"), "Weird$Name"), "", "\t", "\n", null).Text);
        }
    }
}
