using UEToVS.Core.Query;
using Xunit;

namespace UEToVS.Core.Tests
{
    public class QueryTests
    {
        [Fact]
        public void FileWithLineAndColumn()
        {
            var q = SearchQuery.Parse("MyActor.cpp:120:8");
            Assert.Equal("MyActor.cpp", q.Pattern);
            Assert.True(q.LooksLikeFile);
            Assert.Equal(120, q.Line);
            Assert.Equal(8, q.Column);
        }

        [Fact]
        public void QualifiedCpp()
        {
            var q = SearchQuery.Parse("AActor::Tick");
            Assert.Equal("AActor", q.Container);
            Assert.Equal("Tick", q.Pattern);
            Assert.Null(q.Line);
        }

        [Fact]
        public void QualifiedDot()
        {
            var q = SearchQuery.Parse("AActor.Tick");
            Assert.Equal("AActor", q.Container);
            Assert.Equal("Tick", q.Pattern);
        }

        [Fact]
        public void SymbolWithLine()
        {
            var q = SearchQuery.Parse("AActor::Tick:30");
            Assert.Equal("Tick", q.Pattern);
            Assert.Equal(30, q.Line);
        }

        [Fact]
        public void PathFragments()
        {
            var q = SearchQuery.Parse("Engine/Ren/SceneRen");
            Assert.Equal("SceneRen", q.Pattern);
            Assert.Equal(new[] { "Engine", "Ren" }, q.PathFragments);
        }

        [Fact]
        public void PrefixCommand()
        {
            var q = SearchQuery.Parse("/types Actor");
            Assert.Equal(SearchTab.Types, q.TabOverride);
            Assert.Equal("Actor", q.Pattern);
            Assert.True(SearchQuery.Parse("/").IsPrefixCommandListing);
            Assert.True(SearchQuery.Parse("/ty").IsPrefixCommandListing);
        }

        [Fact]
        public void PartialExtensionIsFile() => Assert.True(SearchQuery.Parse("MyActor.cp").LooksLikeFile);
    }
}
