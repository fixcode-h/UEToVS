using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using Xunit;
using static UEToVS.Engine.Syntax.Tests.TestUtil;

namespace UEToVS.Engine.Syntax.Tests
{
    public class PreprocessorTests
    {
        private static ParseOptions Defs(params (string, string)[] defs) =>
            new ParseOptions { Defines = defs.ToDictionary(d => d.Item1, d => d.Item2) };

        [Fact]
        public void KnownConditionsSelectOneBranch()
        {
            const string code = "#if WITH_EDITOR\nint EditorOnly;\n#else\nint GameOnly;\n#endif\nint Always;";
            var s = Parse(code, Defs(("WITH_EDITOR", "1")));
            Assert.False(One(s, "EditorOnly").IsConditional);
            Assert.DoesNotContain(s.Declarations, d => d.Name == "GameOnly");
            Assert.False(One(s, "Always").IsConditional);
            Assert.Single(s.InactiveRanges);

            var s0 = Parse(code, Defs(("WITH_EDITOR", "0")));
            Assert.DoesNotContain(s0.Declarations, d => d.Name == "EditorOnly");
            One(s0, "GameOnly");
        }

        [Fact]
        public void UnknownConditionsParseBothBranchesAsConditional()
        {
            var s = Parse("#if WITH_EDITOR\nint A;\n#elif PLATFORM_X\nint B;\n#else\nint C;\n#endif\nint D;");
            Assert.True(One(s, "A").IsConditional);
            Assert.True(One(s, "B").IsConditional);
            Assert.True(One(s, "C").IsConditional);
            Assert.False(One(s, "D").IsConditional);
            Assert.Empty(s.InactiveRanges);
        }

        [Fact]
        public void IfZeroAndIfOne()
        {
            var s = Parse("#if 0\nint Dead;\n#endif\n#if 1\nint Live;\n#else\nint Dead2;\n#endif");
            Assert.DoesNotContain(s.Declarations, d => d.Name.StartsWith("Dead"));
            Assert.False(One(s, "Live").IsConditional);
            Assert.Equal(2, s.InactiveRanges.Count);
        }

        [Fact]
        public void IfdefIfndefAndUndefinedList()
        {
            var opt = new ParseOptions
            {
                Defines = new Dictionary<string, string> { ["UE_BUILD_SHIPPING"] = "" },
                Undefines = new[] { "WITH_EDITORONLY_DATA" },
            };
            var s = Parse("#ifdef UE_BUILD_SHIPPING\nint Ship;\n#endif\n#ifndef WITH_EDITORONLY_DATA\nint NoEd;\n#endif\n#ifdef WITH_EDITORONLY_DATA\nint Ed;\n#endif", opt);
            One(s, "Ship");
            One(s, "NoEd");
            Assert.DoesNotContain(s.Declarations, d => d.Name == "Ed");
        }

        [Fact]
        public void ExpressionsWithDefinedArithmeticAndShortCircuit()
        {
            var opt = Defs(("ENGINE_MAJOR_VERSION", "5"), ("ENGINE_MINOR_VERSION", "3"), ("PLATFORM_WINDOWS", "1"));
            const string code =
                "#if ENGINE_MAJOR_VERSION > 4 && (ENGINE_MINOR_VERSION >= 2 || UNKNOWN_FLAG)\nint V53;\n#endif\n" +
                "#if defined(PLATFORM_WINDOWS) && !defined PLATFORM_WINDOWS\nint Never;\n#endif\n" +
                "#if 0 && UNKNOWN_FLAG\nint Never2;\n#endif\n" +
                "#if (2 * 3 + 1) == 7 ? 1 : 0\nint Math;\n#endif\n" +
                "#if UNKNOWN_FLAG || 1\nint AlwaysTrue;\n#endif\n" +
                "#if UE_VERSION_NEWER_THAN(5, 1, 0)\nint Maybe;\n#endif";
            var s = Parse(code, opt);
            Assert.False(One(s, "V53").IsConditional);
            Assert.DoesNotContain(s.Declarations, d => d.Name.StartsWith("Never"));
            Assert.False(One(s, "Math").IsConditional);
            Assert.False(One(s, "AlwaysTrue").IsConditional);
            Assert.True(One(s, "Maybe").IsConditional);
        }

        [Fact]
        public void MacrosDefinedInFileAffectLaterConditions()
        {
            var s = Parse("#define USE_FOO 1\n#if USE_FOO\nint Foo;\n#endif\n#undef USE_FOO\n#ifdef USE_FOO\nint Bar;\n#endif");
            Assert.False(One(s, "Foo").IsConditional);
            Assert.DoesNotContain(s.Declarations, d => d.Name == "Bar");
        }

        [Fact]
        public void UnlistedMacrosAreUndefinedWhenRequested()
        {
            var s = Parse("#if SOMETHING\nint A;\n#else\nint B;\n#endif", new ParseOptions { UnlistedMacrosAreUndefined = true });
            Assert.DoesNotContain(s.Declarations, d => d.Name == "A");
            Assert.False(One(s, "B").IsConditional);
        }

        [Fact]
        public void IncludeGuardsAreNotConditional()
        {
            var s = Parse("#ifndef MY_HEADER_H\n#define MY_HEADER_H\nclass FGuarded {};\n#endif\n#if !defined(OTHER_H)\n#define OTHER_H\nint Other;\n#endif");
            Assert.False(One(s, "FGuarded").IsConditional);
            Assert.False(One(s, "Other").IsConditional);
        }

        [Fact]
        public void NestedConditionalsInsideInactiveBranchesStayInactive()
        {
            var s = Parse("#if 0\n#if 1\nint A;\n#else\nint B;\n#endif\n#endif\nint C;");
            Assert.DoesNotContain(s.Declarations, d => d.Name == "A" || d.Name == "B");
            One(s, "C");
        }

        [Fact]
        public void UnknownBranchesWithUnbalancedBracesKeepOnlyTheFirst()
        {
            const string code = "#if WITH_X\nvoid F(int A) {\n#else\nvoid F() {\n#endif\n  int Local = 1;\n}\nint After;";
            var s = Parse(code);
            var f = One(s, "F", SymbolKind.Function);
            Assert.True(f.IsConditional);
            Assert.Equal("int A", f.Parameters);
            Assert.False(One(s, "After").IsConditional);
        }

        [Fact]
        public void DefinesBecomeMacroDeclarations()
        {
            var s = Parse("/** Doubles x. */\n#define TWICE(x) ((x) * 2) // comment\n#define FLAG\n#define LONG_ONE a \\\n  + b\nint X;");
            var twice = One(s, "TWICE", SymbolKind.Macro);
            Assert.Equal("x", twice.Parameters);
            Assert.Equal("((x) * 2)", twice.Value);
            Assert.Equal("#define TWICE(x) ((x) * 2)", twice.Signature);
            Assert.True((twice.Modifiers & DeclModifiers.FunctionLike) != 0);
            Assert.False(twice.DocComment.IsNone);
            Assert.Equal(1, twice.Line);
            Assert.Equal("", One(s, "FLAG", SymbolKind.Macro).Value);
            Assert.Equal("a + b", One(s, "LONG_ONE", SymbolKind.Macro).Value);
            // declarations are in source order
            Assert.Equal(new[] { "TWICE", "FLAG", "LONG_ONE", "X" }, s.Declarations.Select(d => d.Name).ToArray());
        }

        [Fact]
        public void IncludesAreCollected()
        {
            var s = Parse("#include \"A/B.h\"\n#include <vector>\n  #  include \"C.h\" // why\n#if 0\n#include \"Dead.h\"\n#endif\n#if MAYBE\n#include \"Maybe.h\"\n#endif\n#include COMPILED_PLATFORM_HEADER(Foo.h)");
            Assert.Equal(new[] { "A/B.h", "vector", "C.h", "Maybe.h", "COMPILED_PLATFORM_HEADER(Foo.h)" }, s.Includes.Select(i => i.Path).ToArray());
            Assert.True(s.Includes[1].IsAngled);
            Assert.False(s.Includes[0].IsAngled);
            Assert.Equal(2, s.Includes[2].Line);
            Assert.True(s.Includes[3].IsConditional);
            Assert.True(s.Includes[4].IsMacro);
        }

        [Fact]
        public void StrayDirectivesAreReportedNotFatal()
        {
            var s = Parse("#endif\n#else\nint A;\n#if X\nint B;");
            One(s, "A");
            One(s, "B");
            Assert.NotEmpty(s.Diagnostics);
        }

        [Fact]
        public void EmptyMacrosDefinedInFileActAsNoOps()
        {
            var s = Parse("#define MYGAME_INLINE\n#define DEPRECATED_IN(v)\nMYGAME_INLINE DEPRECATED_IN(5) int Compute(int A);");
            var f = One(s, "Compute", SymbolKind.Function);
            Assert.Equal("int Compute(int A)", f.Signature);
        }
    }
}
