using System.Linq;
using UEToVS.Host.Logic.IntelliSense;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class MacroExpansionFormatterTests
    {
        [Fact]
        public void LuaTagCheckBecomesReadable()
        {
            var steps = MacroExpansionFormatter.Steps("checktag((o), LUA_VLIGHTUSERDATA)", "((((o))->tt_) == (((2) | ((0) << 4))))");
            Assert.Equal(new[] { MacroStepKind.OneLevel, MacroStepKind.Full, MacroStepKind.Folded }, steps.Select(s => s.Kind));
            Assert.Equal("checktag(o, LUA_VLIGHTUSERDATA)", steps[0].Text);
            Assert.Equal("o->tt_ == (2 | (0 << 4))", steps[1].Text);
            Assert.Equal("o->tt_ == 2", steps[2].Text);
        }

        [Theory]
        [InlineData("((2) * (3) * 2)", "12")]
        [InlineData("(1 << 4) | 0x0F", "0x1F")]
        [InlineData("(-(1))", "-1")]
        public void ConstantsAreComputed(string input, string folded) => Assert.Equal(folded, MacroExpansionFormatter.Fold(input));

        [Theory]
        [InlineData("(int)(x)", "(int)(x)")]         // cast: conservative, nothing removed
        [InlineData("(a.b) + 1", "a.b + 1")]
        [InlineData("(ns::T)x", "(ns::T)x")]
        [InlineData("(T)-x", "(T)-x")]
        [InlineData("f((a), (b))", "f(a, b)")]
        [InlineData("sizeof(x)", "sizeof(x)")]
        [InlineData("return (x)", "return (x)")]
        [InlineData("((a + b)) * c", "(a + b) * c")]
        [InlineData("(a + b)", "a + b")]
        [InlineData("TEXT(\"(x)\")", "TEXT(\"(x)\")")]
        [InlineData("a / (0)", "a / 0")]
        public void OnlyRedundantParenthesesGo(string input, string output) => Assert.Equal(output, MacroExpansionFormatter.Simplify(input));

        [Fact]
        public void DivisionByZeroAndFloatsAreLeftAlone()
        {
            Assert.Equal("1 / 0", MacroExpansionFormatter.Fold("(1 / 0)"));
            Assert.Equal("1.5 * 2", MacroExpansionFormatter.Fold("(1.5 * 2)"));
        }

        [Fact]
        public void LongDeclarationListsBreakAtSemicolonsAndBraces()
        {
            string body = string.Join(" ", Enumerable.Range(0, 8).Select(i => "static void Function" + i + "(int32 Value" + i + ");")) + " struct FInner { int32 A; };";
            string text = MacroExpansionFormatter.Simplify(body);
            var lines = text.Split('\n');
            Assert.Equal("static void Function0(int32 Value0);", lines[0]);
            Assert.Contains("struct FInner {", lines);
            Assert.Contains("    int32 A;", lines);
            Assert.Equal("};", lines[lines.Length - 1]);
        }

        [Fact]
        public void SingleStepWhenNothingNests()
        {
            var steps = MacroExpansionFormatter.Steps("GADGET_SELF + 1", "GADGET_SELF + 1");
            Assert.Single(steps);
            Assert.Equal("GADGET_SELF + 1", steps[0].Text);
        }

        [Fact]
        public void UnbalancedTextIsKept() => Assert.Equal("((a)", MacroExpansionFormatter.Simplify("((a)"));
    }
}
