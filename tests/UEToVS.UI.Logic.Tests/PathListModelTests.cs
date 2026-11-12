using UEToVS.UI.Logic;
using Xunit;

namespace UEToVS.UI.Logic.Tests
{
    public class PathListModelTests
    {
        [Fact]
        public void Parse_AcceptsOldSemicolonFormat()
        {
            Assert.Equal(new[] { "Content/Script", "D:\\Lua", "x" }, PathListModel.Parse(" Content/Script ;\"D:\\Lua\";; x ;content\\script\\"));
            Assert.Empty(PathListModel.Parse("  "));
            Assert.Empty(PathListModel.Parse(null));
        }

        [Fact]
        public void Join_DropsEmptyAndDuplicates()
        {
            Assert.Equal("a;B", PathListModel.Join(new[] { "a", " ", "B", "A", null }));
            Assert.Equal("", PathListModel.Join(null));
        }

        [Fact]
        public void ToStored_RelativeInsideBase_AbsoluteOutside()
        {
            const string b = "D:\\Games\\Lyra";
            Assert.Equal("Content/Script", PathListModel.ToStored("D:\\Games\\Lyra\\Content\\Script\\", b));
            Assert.Equal("Content/Script", PathListModel.ToStored("d:/games/lyra/Content/Script", b + "\\"));
            Assert.Equal(".", PathListModel.ToStored("D:\\Games\\Lyra", b));
            Assert.Equal("D:\\Games\\LyraOther\\Lua", PathListModel.ToStored("D:\\Games\\LyraOther\\Lua", b));
            Assert.Equal("E:\\Shared\\Lua", PathListModel.ToStored("E:\\Shared\\Lua", b));
            Assert.Equal("E:\\Shared\\Lua", PathListModel.ToStored("E:\\Shared\\Lua", null));
        }

        [Fact]
        public void ToAbsolute_ResolvesAgainstBase()
        {
            const string b = "D:\\Games\\Lyra";
            Assert.Equal("D:\\Games\\Lyra\\Content\\Script", PathListModel.ToAbsolute("Content/Script", b));
            Assert.Equal("D:\\Games\\Shared", PathListModel.ToAbsolute("../Shared", b));
            Assert.Equal("D:\\Games\\Lyra", PathListModel.ToAbsolute(".", b));
            Assert.Equal("E:\\Lua", PathListModel.ToAbsolute("E:/Lua/", b));
            Assert.Null(PathListModel.ToAbsolute("Content", null));
            Assert.Equal("\\\\srv\\share\\Lua", PathListModel.ToAbsolute("\\\\srv\\share\\Lua", b));
        }

        [Fact]
        public void IsRelative_And_BrowseStart()
        {
            Assert.True(PathListModel.IsRelative("Content/Script"));
            Assert.False(PathListModel.IsRelative("C:\\x"));
            Assert.False(PathListModel.IsRelative("\\\\srv\\x"));
            Assert.Equal("D:\\P\\Content", PathListModel.BrowseStart("Content", "D:\\P", _ => true));
            Assert.Equal("D:\\P", PathListModel.BrowseStart("Missing", "D:\\P", _ => false));
            Assert.Null(PathListModel.BrowseStart("", null, _ => true));
        }
    }
}
