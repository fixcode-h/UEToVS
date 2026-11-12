using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.IntelliSense;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class InlayHintStoreTests
    {
        private static InlayHint H(int line, int col, string text, InlayHintKind kind = InlayHintKind.ParameterName) =>
            new InlayHint { Line = line, Column = col, Text = text, Kind = kind };

        [Fact]
        public void FirstRequestCoversViewportPlusMargin_ThenCachedForTheVersion()
        {
            var s = new InlayHintStore();
            var r = s.Missing(1, 100, 140, 1000);
            Assert.Equal(new LineRange(100 - InlayHintStore.MarginLines, 140 + InlayHintStore.MarginLines), r);
            Assert.True(s.Add(1, r, new[] { H(110, 4, "bSweep"), H(500, 0, "OutOfRange") }));
            Assert.Equal(1, s.Count);
            Assert.True(s.Missing(1, 90, 150, 1000).IsEmpty);   // inside the covered lines
            Assert.False(s.Missing(1, 180, 220, 1000).IsEmpty); // scrolled out
            Assert.False(s.Missing(2, 100, 140, 1000).IsEmpty); // edited: new version
        }

        [Fact]
        public void SameVersionMerges_NewVersionStartsOver_OlderIsIgnored()
        {
            var s = new InlayHintStore();
            s.Add(5, new LineRange(0, 50), new[] { H(10, 1, "A") });
            s.Add(5, new LineRange(51, 100), new[] { H(60, 1, "B") });
            Assert.Equal(2, s.Count);
            Assert.Single(s.Covered);
            Assert.Equal(new LineRange(0, 100), s.Covered[0]);
            Assert.Equal(new[] { "A", "B" }, s.Between(0, 100).Select(h => h.Text));
            // re-answer of lines 0..50 replaces their hints
            s.Add(5, new LineRange(0, 50), new[] { H(11, 1, "C") });
            Assert.Equal(new[] { "C", "B" }, s.Between(0, 100).Select(h => h.Text));
            Assert.False(s.Add(4, new LineRange(0, 10), new[] { H(1, 1, "Old") }));
            Assert.True(s.Add(6, new LineRange(0, 10), new[] { H(1, 1, "New") }));
            Assert.Equal(new[] { "New" }, s.Between(0, 1000).Select(h => h.Text));
        }

        [Fact]
        public void BoundedByMaxLines()
        {
            var s = new InlayHintStore();
            s.Add(1, new LineRange(0, InlayHintStore.MaxLines - 10), new[] { H(1, 0, "A") });
            s.Add(1, new LineRange(InlayHintStore.MaxLines, InlayHintStore.MaxLines + 100), new[] { H(InlayHintStore.MaxLines + 1, 0, "B") });
            Assert.Equal(new[] { "B" }, s.Between(0, int.MaxValue).Select(h => h.Text));
        }

        [Fact]
        public void Display()
        {
            Assert.Equal("bSweep:", InlayHintStore.Display(H(0, 0, "bSweep")));
            Assert.Equal(": FVector", InlayHintStore.Display(H(0, 0, "FVector", InlayHintKind.Type)));
        }
    }
}
