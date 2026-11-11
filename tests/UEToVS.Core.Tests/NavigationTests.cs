using System.IO;
using UEToVS.Core.Navigation;
using Xunit;

namespace UEToVS.Core.Tests
{
    public class NavigationTests
    {
        private static CodeLocation L(string f, int line) => new CodeLocation(f, line, 0);

        [Fact]
        public void BackAndForward()
        {
            var h = new NavigationHistory();
            h.RecordJump(L("a.cpp", 10), L("b.h", 50));
            h.RecordJump(L("b.h", 50), L("c.cpp", 5));
            Assert.Equal(L("b.h", 50), h.GoBack(L("c.cpp", 5)));
            Assert.Equal(L("a.cpp", 10), h.GoBack(L("b.h", 50)));
            Assert.Null(h.GoBack(L("a.cpp", 10)));
            Assert.Equal(L("b.h", 50), h.GoForward(L("a.cpp", 10)));
            Assert.Equal(L("c.cpp", 5), h.GoForward(L("b.h", 50)));
            Assert.False(h.CanGoForward);
        }

        [Fact]
        public void SmallMovesNotRecorded()
        {
            var h = new NavigationHistory(lineThreshold: 15);
            h.OnCaretMoved(L("a.cpp", 10), L("a.cpp", 12));
            Assert.False(h.CanGoBack);
            h.OnCaretMoved(L("a.cpp", 12), L("a.cpp", 100));
            Assert.True(h.CanGoBack);
        }

        [Fact]
        public void NearbyEntriesMerge()
        {
            var h = new NavigationHistory();
            h.RecordJump(L("a.cpp", 10), L("b.cpp", 0));
            h.RecordJump(L("a.cpp", 12), L("c.cpp", 0));
            Assert.Single(h.BackEntries);
        }

        [Fact]
        public void NewJumpClearsForward()
        {
            var h = new NavigationHistory();
            h.RecordJump(L("a.cpp", 10), L("b.cpp", 0));
            h.GoBack(L("b.cpp", 0));
            Assert.True(h.CanGoForward);
            h.RecordJump(L("a.cpp", 10), L("d.cpp", 0));
            Assert.False(h.CanGoForward);
        }

        [Fact]
        public void Capacity()
        {
            var h = new NavigationHistory(capacity: 3);
            for (int i = 0; i < 10; i++) h.RecordJump(L("f" + i, 0), L("x", 0));
            Assert.Equal(3, System.Linq.Enumerable.Count(h.BackEntries));
        }

        [Fact]
        public void SaveLoadRoundTrip()
        {
            var h = new NavigationHistory();
            h.RecordJump(L("a.cpp", 10), L("b.cpp", 0));
            h.RecordJump(L("b.cpp", 40), L("c.cpp", 0));
            h.GoBack(L("c.cpp", 0));
            var sw = new StringWriter();
            h.Save(sw);
            var h2 = new NavigationHistory();
            h2.Load(new StringReader(sw.ToString()));
            Assert.Equal(L("a.cpp", 10), h2.GoBack(L("b.cpp", 40)));
            Assert.Equal(L("b.cpp", 40), h2.GoForward(L("a.cpp", 10)));
            Assert.Equal(L("c.cpp", 0), h2.GoForward(L("b.cpp", 40)));
        }

        [Fact]
        public void RemoveFile()
        {
            var h = new NavigationHistory();
            h.RecordJump(L("a.cpp", 10), L("b.cpp", 0));
            h.RecordJump(L("b.cpp", 40), L("c.cpp", 0));
            h.RemoveFile("A.CPP");
            Assert.Single(h.BackEntries);
        }

        [Fact]
        public void EditLocationsWalk()
        {
            var e = new EditLocations();
            e.RecordEdit(L("a.cpp", 1));
            e.RecordEdit(L("a.cpp", 2)); // merges
            e.RecordEdit(L("b.cpp", 100));
            e.RecordEdit(L("c.cpp", 7));
            Assert.Equal(3, e.Items.Count);
            Assert.Equal(L("b.cpp", 100), e.Previous(L("c.cpp", 7)));
            Assert.Equal(L("a.cpp", 2), e.Previous(L("b.cpp", 100)));
            Assert.Null(e.Previous(L("a.cpp", 2)));
            Assert.Equal(L("b.cpp", 100), e.Next(L("a.cpp", 2)));
        }

        [Fact]
        public void RecentFilesMru()
        {
            var r = new RecentFiles(3);
            var t = System.DateTime.Now;
            r.Touch("a", t); r.Touch("b", t); r.Touch("c", t); r.Touch("a", t); r.Touch("d", t);
            Assert.Equal(new[] { "d", "a", "c" }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(r.Items, i => i.Path)));
            r.MarkEdited("c", t);
            Assert.Single(r.EditedOnly);
        }
    }
}
