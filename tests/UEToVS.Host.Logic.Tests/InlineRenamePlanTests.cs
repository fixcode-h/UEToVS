using System.Collections.Generic;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class InlineRenamePlanTests
    {
        private static RenameLocation Loc(string file, int line, int col, int len = 5, bool check = true, bool editable = true, bool include = false) =>
            new RenameLocation { FilePath = file, Line = line, Column = col, Length = len, Checked = check, IsEditable = editable, IsInclude = include };

        [Fact]
        public void Occurrences_CurrentFileCheckedEditableOnly_SortedDistinct()
        {
            const string a = "C:/Game/Source/A.cpp";
            var locations = new List<RenameLocation>
            {
                Loc(a, 9, 2), Loc(a, 3, 4), Loc(a, 3, 4), Loc("C:/Game/Source/B.cpp", 1, 1),
                Loc(a, 5, 0, check: false), Loc(a, 6, 0, editable: false), Loc(a, 7, 0, include: true), Loc(a, 8, 0, len: 9),
                Loc("c:/game/source/a.cpp", 1, 0),
            };
            var r = InlineRenamePlan.Occurrences(locations, a, "Count");
            Assert.Equal(new[] { new InlineOccurrence(1, 0, 5), new InlineOccurrence(3, 4, 5), new InlineOccurrence(9, 2, 5) }, r);
            Assert.Empty(InlineRenamePlan.Occurrences(locations, null, "Count"));
        }

        [Theory]
        [InlineData("Count", "NewCount", true)]
        [InlineData("Count", "Count", false)]
        [InlineData("Count", "1Count", false)]
        [InlineData("Count", "", false)]
        public void IsAcceptable(string oldName, string newName, bool ok) => Assert.Equal(ok, InlineRenamePlan.IsAcceptable(oldName, newName));

        [Fact]
        public void RenameMode_DefaultsToInline() => Assert.Equal(RenameMode.Inline, new HostSettings().RenameMode);
    }
}
