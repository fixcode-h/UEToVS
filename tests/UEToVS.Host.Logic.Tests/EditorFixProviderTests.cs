using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class EditorFixProviderTests
    {
        private static CodeDiagnostic D(string code, int line, int col, int len, params string[] args) =>
            new CodeDiagnostic { Code = code, Line = line, Column = col, Length = len, Args = args.ToList() };

        [Fact]
        public void Deprecated_OffersReplacementOnlyWhenMechanical()
        {
            var c = new RefactorContext
            {
                FilePath = "C:/P/Source/A.cpp",
                Diagnostics = new List<CodeDiagnostic>
                {
                    D(CodeDiagnosticCodes.DeprecatedApi, 3, 1, 8, "OldThing", "5.1", "Use NewThing instead.", "NewThing"),
                    D(CodeDiagnosticCodes.DeprecatedApi, 4, 1, 4, "Gone", "5.0", "no replacement", ""),
                },
            };
            var items = new EditorFixProvider().GetActions(c).ToList();
            var item = Assert.Single(items);
            Assert.Equal(EditorFeatureIds.ReplaceDeprecated, item.ActionId);
            var fix = Assert.IsType<DiagnosticFix>(item.Argument);
            Assert.Equal("OldThing", fix.Old);
            Assert.Equal("NewThing", fix.New);
            Assert.Equal(3, fix.Line);
        }

        [Fact]
        public void Naming_OffersRename()
        {
            var c = new RefactorContext { Diagnostics = new List<CodeDiagnostic> { D(CodeDiagnosticCodes.NamingConvention, 0, 6, 9, "Inventory", "FInventory", "F", "type") } };
            var item = Assert.Single(new EditorFixProvider().GetActions(c));
            Assert.Equal(EditorFeatureIds.FixNaming, item.ActionId);
            Assert.Equal("FInventory", ((DiagnosticFix)item.Argument).New);
        }

        [Fact]
        public void NothingOutsideProjectOrForBadNames()
        {
            var d = D(CodeDiagnosticCodes.DeprecatedApi, 3, 1, 8, "OldThing", "5.1", "m", "NewThing");
            Assert.Empty(new EditorFixProvider().GetActions(new RefactorContext { IsProjectFile = false, Diagnostics = new List<CodeDiagnostic> { d } }));
            var bad = D(CodeDiagnosticCodes.DeprecatedApi, 3, 1, 8, "OldThing", "5.1", "m", "New Thing");
            Assert.Empty(new EditorFixProvider().GetActions(new RefactorContext { Diagnostics = new List<CodeDiagnostic> { bad } }));
            Assert.Empty(new EditorFixProvider().GetActions(new RefactorContext()));
        }

        [Fact]
        public void At_SelectsCaretSpan()
        {
            var items = new[] { D("UE0011", 2, 4, 3, "Abc"), D("UE0012", 2, 10, 2, "Xy"), D("UE0011", 3, 4, 3, "Abc") };
            Assert.Equal(new[] { "UE0011" }, EditorFixProvider.At(items, 2, 7).Select(d => d.Code));
            Assert.Empty(EditorFixProvider.At(items, 2, 8));
            Assert.Single(EditorFixProvider.At(items, 2, 4));
        }

        [Theory]
        [InlineData("FInventory", true)]
        [InlineData("_x1", true)]
        [InlineData("1x", false)]
        [InlineData("a::b", false)]
        [InlineData("", false)]
        public void IsIdentifier(string s, bool ok) => Assert.Equal(ok, EditorFixProvider.IsIdentifier(s));
    }
}
