using System;
using System.Collections.Generic;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>Large-project memory behaviour: compact asset table, segment writes that survive running out of memory.</summary>
    public class IndexMemoryTests
    {
        /// <summary>Throws OutOfMemoryException for batches larger than <see cref="MaxBatch"/> (and for one poisoned file).</summary>
        private sealed class OomBackend : ISymbolIndexBackend
        {
            public int MaxBatch = int.MaxValue;
            public string Poison;
            public readonly List<int> Written = new List<int>();
            public readonly HashSet<string> Files = new HashSet<string>();

            public void AppendBatch(bool engine, IReadOnlyList<(string Path, ParseResult Result)> batch, CancellationToken ct)
            {
                if (batch.Count > MaxBatch) throw new OutOfMemoryException();
                foreach (var (p, _) in batch) if (p == Poison) throw new OutOfMemoryException();
                lock (Written)
                {
                    Written.Add(batch.Count);
                    foreach (var (p, _) in batch) Files.Add(p);
                }
            }

            public string Name => "oom";
            public string FormatVersion => "1";
            public SymbolSearchResult Search(SymbolQuery query, CancellationToken ct) => new SymbolSearchResult();
            public IReadOnlyList<SymbolInfo> GetFileSymbols(string filePath) => Array.Empty<SymbolInfo>();
            public IReadOnlyList<SymbolInfo> FindSymbols(string name, string container) => Array.Empty<SymbolInfo>();
            public void UpdateFile(string filePath, IReadOnlyList<SymbolInfo> symbols) { }
            public void RemoveFile(string filePath) { }
            public bool ContainsFile(string filePath) => Files.Contains(filePath);
            public IndexStats GetStats() => new IndexStats();
            public IReadOnlyList<MemoryPart> GetMemoryParts() => Array.Empty<MemoryPart>();
            public bool TryLoad(string indexDir, string key) => true;
            public void Save(string indexDir, string key) { }
            public void Dispose() { }
        }

        private static void Feed(BulkSink sink, int n)
        {
            for (int i = 0; i < n; i++) sink.Add(false, "f" + i + ".h", new ParseResult(), 10);
            sink.Flush(false);
        }

        [Fact]
        public void SegmentWrite_OutOfMemory_SplitsTheBatchInsteadOfFailingTheRun()
        {
            var backend = new OomBackend { MaxBatch = 30 };
            using (var sink = new BulkSink(backend, CancellationToken.None, batchFiles: 100))
                Feed(sink, 250); // batches 100, 100, 50 → halves until ≤ 30
            Assert.Equal(250, backend.Files.Count);
            Assert.All(backend.Written, n => Assert.True(n <= 30));
        }

        [Fact]
        public void SegmentWrite_OutOfMemoryOnOneFile_DropsOnlyThatFile()
        {
            var backend = new OomBackend { Poison = "f7.h" };
            using (var sink = new BulkSink(backend, CancellationToken.None, batchFiles: 16))
                Feed(sink, 40);
            Assert.Equal(39, backend.Files.Count);
            Assert.DoesNotContain("f7.h", backend.Files);
        }

        [Fact]
        public void AssetTable_KeepsCompactEntries_AndRebuildsObjectPathForHits()
        {
            var table = new AssetTable();
            table.Upsert(new AssetItem { Name = "BP_Hero", FilePath = "/p/Content/Characters/BP_Hero.uasset", ObjectPath = "/Game/Characters/BP_Hero.BP_Hero", Origin = ItemOrigin.Project });
            table.Upsert(new AssetItem { Name = "BP_Villain", FilePath = "/p/Content/Characters/BP_Villain.uasset", ObjectPath = "/Game/Characters/BP_Villain.BP_Villain", Origin = ItemOrigin.Project });
            table.Upsert(new AssetItem { Name = "Main", FilePath = "/p/Content/Maps/Main.umap", ObjectPath = "/Game/Maps/Main.Main", ClassName = "World", Origin = ItemOrigin.Project });

            Assert.Equal(3, table.Count);
            Assert.Equal(2, table.PackageDirCount); // one shared folder string per package folder
            var snap = table.Snapshot();
            var hero = Array.Find(snap, a => a.Name == "BP_Hero").ToItem();
            Assert.Equal("/Game/Characters/BP_Hero.BP_Hero", hero.ObjectPath);
            Assert.Null(hero.ClassName);
            var villain = Array.Find(snap, a => a.Name == "BP_Villain");
            Assert.Same(Array.Find(snap, a => a.Name == "BP_Hero").PackageDir, villain.PackageDir);
            var map = Array.Find(snap, a => a.Name == "Main").ToItem();
            Assert.Equal("/Game/Maps/Main.Main", map.ObjectPath);
            Assert.Equal("World", map.ClassName);
            Assert.Equal(ItemOrigin.Project, map.Origin);
        }
    }
}
