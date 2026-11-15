using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// v0.7.5: the host pushes the unsaved text of open editors. Navigation and the light bulb in another file must see a
    /// declaration added to an unsaved header and an implementation deleted from an unsaved .cpp, and go back to the disk
    /// when the buffer is dropped (saved / closed without saving).
    /// </summary>
    public class UnsavedBufferTests
    {
        private const string HeaderRel = "Game/Source/MyGame/Public/MyCharacter.h";
        private const string CppRel = "Game/Source/MyGame/Private/MyCharacter.cpp";

        private static (int Line, int Col) Find(string text, string needle, int nth = 0)
        {
            int at = -1;
            for (int i = 0; i <= nth; i++) at = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
            Assert.True(at >= 0, needle);
            string before = text.Substring(0, at);
            int line = before.Count(ch => ch == '\n');
            return (line, at - (before.LastIndexOf('\n') + 1));
        }

        private static CaretRequest Caret(string path, string text, string needle, long version, int nth = 0)
        {
            var (l, c) = Find(text, needle, nth);
            return new CaretRequest { FilePath = path, Line = l, Column = c + 1, Text = text, TextVersion = version };
        }

        [Fact]
        public async Task UnsavedHeaderAndCpp_AreSeenByNavigationAndRefactoring()
        {
            using var t = new FakeUnrealTree();
            var h = new EngineHost(new EngineOptions { SolutionDir = t.ProjectRoot, MaxParallelism = 2, WatchEngine = false, NoWatch = true });
            try
            {
                _ = h.StartIndexing();
                Assert.True(await h.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)));
                string header = t.P(HeaderRel), cpp = t.P(CppRel);
                string headerDisk = File.ReadAllText(header), cppDisk = File.ReadAllText(cpp);

                // 1. "Deme" declared in the header (unsaved) and implemented in the .cpp (unsaved)
                string headerEdited = headerDisk.Replace("\tvoid Fire(float Power);", "\tvoid Fire(float Power);\n\tUFUNCTION()\n\tvoid Deme();");
                string cppEdited = cppDisk + "\nvoid AMyCharacter::Deme()\n{\n}\n";
                await h.Index.ApplyBufferAsync(header, headerEdited, CancellationToken.None);
                await h.Index.ApplyBufferAsync(cpp, cppEdited, CancellationToken.None);

                // cpp → header: the declaration exists only in the unsaved header
                var nav = h.Navigation.Navigate(new NavigationRequest { Caret = Caret(cpp, cppEdited, "Deme", 2), Kind = NavigationKind.Toggle }, CancellationToken.None);
                Assert.Contains(nav.Targets, x => string.Equals(x.Symbol?.Location?.FilePath, header, StringComparison.OrdinalIgnoreCase));

                // header: the light bulb sees the implementation in the unsaved .cpp
                var ctx = h.Refactoring.GetContext(new RefactoringContextRequest { Caret = Caret(header, headerEdited, "Deme", 3) }, CancellationToken.None);
                Assert.Equal("Deme", ctx.Function?.Name);
                Assert.Single(ctx.Definitions);

                // 2. implementation deleted from the .cpp (still unsaved): no definition, no jump into the .cpp
                await h.Index.ApplyBufferAsync(cpp, cppDisk, CancellationToken.None);
                ctx = h.Refactoring.GetContext(new RefactoringContextRequest { Caret = Caret(header, headerEdited, "Deme", 3) }, CancellationToken.None);
                Assert.Empty(ctx.Definitions);
                nav = h.Navigation.Navigate(new NavigationRequest { Caret = Caret(header, headerEdited, "Deme", 3), Kind = NavigationKind.Definition }, CancellationToken.None);
                Assert.DoesNotContain(nav.Targets, x => string.Equals(x.Symbol?.Location?.FilePath, cpp, StringComparison.OrdinalIgnoreCase));

                // 3. header closed without saving: the disk is current again, Deme is gone from the index
                await h.Index.ApplyBufferAsync(header, null, CancellationToken.None);
                Assert.DoesNotContain(h.Index.Backend.FindSymbols("Deme", "AMyCharacter"), s => s.Kind == SymbolKind.Method);
            }
            finally
            {
                await h.StopAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task SavedEditThenRevertedOnDisk_LeavesTheIndex()
        {
            using var t = new FakeUnrealTree();
            var h = new EngineHost(new EngineOptions { SolutionDir = t.ProjectRoot, MaxParallelism = 2, WatchEngine = false, NoWatch = true });
            try
            {
                _ = h.StartIndexing();
                Assert.True(await h.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)));
                h.Index.SaveState(); // the saved state now holds the original hash of the .cpp
                string cpp = t.P(CppRel);
                string original = File.ReadAllText(cpp);

                async Task Save(string text)
                {
                    File.WriteAllText(cpp, text);
                    var batch = new Vfs.ChangeBatch { Source = "test" };
                    batch.Changes.Add(new FileChange { FilePath = cpp, Kind = FileChangeKind.Changed });
                    await h.Index.ApplyChangesAsync(batch, CancellationToken.None);
                }

                await Save(original + "\nvoid AMyCharacter::Deme() {}\n");
                Assert.Contains(h.Index.Backend.FindSymbols("Deme", "AMyCharacter"), s => s.Location?.FilePath == cpp);
                await Save(original); // implementation deleted again and saved
                Assert.DoesNotContain(h.Index.Backend.FindSymbols("Deme", "AMyCharacter"), s => s.Location?.FilePath == cpp);
            }
            finally
            {
                await h.StopAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
