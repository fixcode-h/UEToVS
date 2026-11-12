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

        /// <summary>Parallel-capable fake: slow, thread-safe prepare; publish records order and detects overlap.</summary>
        private sealed class ParallelBackend : ISymbolIndexBackend, IParallelBulkBackend
        {
            public readonly List<string> Published = new List<string>();
            public int Preparing, MaxPreparing, Publishing, OverlappingPublishes, Discarded;
            public string FailOn;

            private sealed class Prepared : IDisposable
            {
                public ParallelBackend Owner;
                public string First;
                public List<string> Files;
                public bool Done;
                public void Dispose() { if (!Done) Interlocked.Increment(ref Owner.Discarded); }
            }

            public IDisposable PrepareBatch(bool engine, IReadOnlyList<(string Path, ParseResult Result)> batch, CancellationToken ct)
            {
                int now = Interlocked.Increment(ref Preparing);
                lock (Published) MaxPreparing = Math.Max(MaxPreparing, now);
                try
                {
                    // later batches finish sooner, so out-of-order completion is likely
                    Thread.Sleep(batch[0].Path == "f0.h" ? 60 : 15);
                    if (FailOn != null && batch[0].Path == FailOn) throw new System.IO.IOException("disk full");
                    var files = new List<string>();
                    foreach (var (p, _) in batch) files.Add(p);
                    return new Prepared { Owner = this, First = batch[0].Path, Files = files };
                }
                finally
                {
                    Interlocked.Decrement(ref Preparing);
                }
            }

            public void PublishBatch(IDisposable prepared)
            {
                if (Interlocked.Increment(ref Publishing) > 1) Interlocked.Increment(ref OverlappingPublishes);
                var p = (Prepared)prepared;
                Thread.Sleep(2);
                lock (Published) Published.AddRange(p.Files);
                p.Done = true;
                Interlocked.Decrement(ref Publishing);
            }

            public void AppendBatch(bool engine, IReadOnlyList<(string Path, ParseResult Result)> batch, CancellationToken ct) =>
                throw new InvalidOperationException("parallel path expected");

            public string Name => "parallel";
            public string FormatVersion => "1";
            public SymbolSearchResult Search(SymbolQuery query, CancellationToken ct) => new SymbolSearchResult();
            public IReadOnlyList<SymbolInfo> GetFileSymbols(string filePath) => Array.Empty<SymbolInfo>();
            public IReadOnlyList<SymbolInfo> FindSymbols(string name, string container) => Array.Empty<SymbolInfo>();
            public void UpdateFile(string filePath, IReadOnlyList<SymbolInfo> symbols) { }
            public void RemoveFile(string filePath) { }
            public bool ContainsFile(string filePath) => false;
            public IndexStats GetStats() => new IndexStats();
            public IReadOnlyList<MemoryPart> GetMemoryParts() => Array.Empty<MemoryPart>();
            public bool TryLoad(string indexDir, string key) => true;
            public void Save(string indexDir, string key) { }
            public void Dispose() { }
        }

        [Fact]
        public void SegmentWrite_Parallel_WritesConcurrently_PublishesInBatchOrder()
        {
            var backend = new ParallelBackend();
            using (var sink = new BulkSink(backend, CancellationToken.None, batchFiles: 10, writers: 4))
            {
                Assert.Equal(4, sink.Writers);
                Feed(sink, 200); // 20 batches from one producer: queue order = file order
                Assert.Equal(20, sink.SegmentsWritten);
            }
            Assert.Equal(200, backend.Published.Count);
            for (int i = 0; i < 200; i++) Assert.Equal("f" + i + ".h", backend.Published[i]);
            Assert.True(backend.MaxPreparing > 1, "batches should be written in parallel");
            Assert.Equal(0, backend.OverlappingPublishes);
            Assert.Equal(0, backend.Discarded);
        }

        [Fact]
        public void SegmentWrite_Parallel_FailureStopsPublishing_AndFailsTheStage()
        {
            var backend = new ParallelBackend { FailOn = "f30.h" };
            using (var sink = new BulkSink(backend, CancellationToken.None, batchFiles: 10, writers: 3))
            {
                Assert.Throws<SegmentWriteFailedException>(() => Feed(sink, 100));
            }
            // nothing at or after the failed batch is published; everything published is a prefix in order
            Assert.DoesNotContain("f30.h", backend.Published);
            for (int i = 0; i < backend.Published.Count; i++) Assert.Equal("f" + i + ".h", backend.Published[i]);
            Assert.True(backend.Published.Count <= 30);
        }

        [Fact]
        public void BulkSink_DefaultWriters_IsHalfTheParseThreads_Capped()
        {
            Assert.Equal(1, BulkSink.DefaultWriters(1));
            Assert.Equal(1, BulkSink.DefaultWriters(2));
            Assert.Equal(4, BulkSink.DefaultWriters(8));
            Assert.Equal(BulkSink.MaxWriters, BulkSink.DefaultWriters(64));
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
