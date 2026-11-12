using UEToVS.Host.Logic.Editing;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class SmartSelectionTests
    {
        private const string Code = "void AFoo::Bar(int32 A)\n{\n\tif (IsValid(Target))\n\t{\n\t\tTarget->Hit(Damage * 2, TEXT(\"Ouch now\"));\n\t}\n}\n";

        private static string Step(ref int s, ref int e)
        {
            var r = SmartSelection.Expand(Code, s, e);
            Assert.NotNull(r);
            s = r.Value.Start;
            e = r.Value.End;
            return Code.Substring(s, e - s);
        }

        [Fact]
        public void ExpandsThroughTheSyntax()
        {
            int s = Code.IndexOf("Damage") + 2, e = s;
            Assert.Equal("Damage", Step(ref s, ref e));
            Assert.Equal("Damage * 2", Step(ref s, ref e));
            Assert.Equal("Damage * 2, TEXT(\"Ouch now\")", Step(ref s, ref e));
            Assert.Equal("(Damage * 2, TEXT(\"Ouch now\"))", Step(ref s, ref e));
            Assert.Equal("Target->Hit(Damage * 2, TEXT(\"Ouch now\"))", Step(ref s, ref e));
            Assert.Equal("Target->Hit(Damage * 2, TEXT(\"Ouch now\"));", Step(ref s, ref e));
            Assert.Equal("{\n\t\tTarget->Hit(Damage * 2, TEXT(\"Ouch now\"));\n\t}", Step(ref s, ref e));
            Assert.StartsWith("if (IsValid(Target))\n\t{", Step(ref s, ref e));
        }

        [Fact]
        public void StringContentsThenString()
        {
            int s = Code.IndexOf("Ouch") + 1, e = s;
            Assert.Equal("Ouch", Step(ref s, ref e));
            Assert.Equal("Ouch now", Step(ref s, ref e));
            Assert.Equal("\"Ouch now\"", Step(ref s, ref e));
            Assert.Equal("(\"Ouch now\")", Step(ref s, ref e));
        }

        [Fact]
        public void EndsWithTheWholeText()
        {
            Assert.Null(SmartSelection.Expand(Code, 0, Code.Length));
            Assert.Null(SmartSelection.Expand("", 0, 0));
            int s = 0, e = Code.Length - 1;
            Assert.Equal(Code, Step(ref s, ref e));
        }
    }
}
