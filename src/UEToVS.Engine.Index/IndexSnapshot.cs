using System;
using System.Collections.Generic;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index.Query;
using UEToVS.Engine.Index.Segments;
using UEToVS.Engine.Index.Storage;

namespace UEToVS.Engine.Index
{
    /// <summary>
    /// A consistent, immutable view of the index. Holding one keeps its segments mapped even if a compaction
    /// swaps them out; dispose it promptly. Thread-safe: any number of threads may query the same snapshot.
    /// </summary>
    public sealed class IndexSnapshot : IDisposable
    {
        private static readonly Logger L = Log.For("Index");
        private Snapshot _snap;

        internal IndexSnapshot(Snapshot snap) => _snap = snap;

        internal Snapshot Inner => _snap ?? throw new ObjectDisposedException(nameof(IndexSnapshot));

        public long Version => Inner.Version;

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _snap, null);
            s?.Release();
        }

        // ------------------------------------------------------------------ search

        public SearchResponse Search(SearchRequest request, CancellationToken ct = default)
        {
            using (var scope = L.Time("index.search", 30, ("pattern", request?.Pattern)))
            {
                try
                {
                    var resp = SearchExecutor.Run(Inner, request, ct);
                    resp.ElapsedMs = scope.ElapsedMs;
                    return resp;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    scope.Fail();
                    L.Error("index.search.failed", "Search failed", ex, ("pattern", request?.Pattern));
                    throw;
                }
            }
        }

        // ------------------------------------------------------------------ lookups

        /// <summary>
        /// All declarations/definitions with exactly this name (case-sensitive). When <paramref name="container"/> is
        /// given, the symbol's container must equal it or end with "::" + it.
        /// </summary>
        public IReadOnlyList<SymbolInfo> FindByName(string name, string container = null, CancellationToken ct = default)
        {
            var result = new List<SymbolInfo>();
            if (string.IsNullOrEmpty(name)) return result;
            var snap = Inner;
            using (L.Time("index.find_by_name", 30))
            {
                for (int si = 0; si < snap.Bases.Length; si++)
                {
                    ct.ThrowIfCancellationRequested();
                    var view = snap.Bases[si];
                    var seg = view.Segment;
                    int n = seg.FindName(name);
                    if (n < 0) continue;
                    uint lastContainer = uint.MaxValue;
                    bool lastMatched = false;
                    var syms = seg.SymbolsOfName(n);
                    for (int k = 0; k < syms.Length; k++)
                    {
                        var r = seg.Symbols[(int)syms[k]];
                        if (view.IsDead(r.FileId)) continue;
                        if (container != null)
                        {
                            if (r.ContainerId != lastContainer)
                            {
                                lastContainer = r.ContainerId;
                                lastMatched = ContainerMatches(seg.GetString(r.ContainerId), container);
                            }
                            if (!lastMatched) continue;
                        }
                        result.Add(Dto.Symbol(seg, syms[k]));
                    }
                }
                foreach (var df in snap.DeltaSorted)
                {
                    for (int i = 0; i < df.Decls.Length; i++)
                    {
                        if (!string.Equals(df.Decls[i].Name, name, StringComparison.Ordinal)) continue;
                        if (container != null && !ContainerMatches(df.Decls[i].Container, container)) continue;
                        result.Add(Dto.Symbol(df, i));
                    }
                }
            }
            return result;
        }

        private static bool ContainerMatches(string actual, string wanted)
        {
            if (string.IsNullOrEmpty(actual)) return wanted.Length == 0;
            if (string.Equals(actual, wanted, StringComparison.Ordinal)) return true;
            return actual.Length > wanted.Length + 2 && actual.EndsWith(wanted, StringComparison.Ordinal) &&
                   actual[actual.Length - wanted.Length - 1] == ':' && actual[actual.Length - wanted.Length - 2] == ':';
        }

        /// <summary>Symbols of one file in source order; empty when the file is not indexed.</summary>
        public IReadOnlyList<SymbolInfo> GetFileSymbols(string path)
        {
            var result = new List<SymbolInfo>();
            var snap = Inner;
            string key = PathKey.Normalize(path);
            if (snap.Delta.TryGetValue(key, out var df))
            {
                for (int i = 0; i < df.Decls.Length; i++) result.Add(Dto.Symbol(df, i));
                return result;
            }
            foreach (var view in snap.Bases)
            {
                int fi = view.Segment.FindFile(key);
                if (fi < 0 || view.IsDead((uint)fi)) continue;
                var f = view.Segment.Files[fi];
                for (uint s = f.FirstSymbol; s < f.FirstSymbol + f.SymbolCount; s++) result.Add(Dto.Symbol(view.Segment, s));
                return result;
            }
            return result;
        }

        public IndexedFile GetFile(string path)
        {
            var snap = Inner;
            string key = PathKey.Normalize(path);
            if (snap.Delta.TryGetValue(key, out var df)) return df.ToIndexedFile();
            foreach (var view in snap.Bases)
            {
                int fi = view.Segment.FindFile(key);
                if (fi >= 0 && !view.IsDead((uint)fi)) return Dto.File(view.Segment, fi);
            }
            return null;
        }

        /// <summary>All live files: base segments in FileId order (engine first), then the delta.</summary>
        public IEnumerable<IndexedFile> ListFiles(CancellationToken ct = default)
        {
            var snap = Inner;
            foreach (var view in snap.Bases)
            {
                for (int fi = 0; fi < view.Segment.FileCount; fi++)
                {
                    if ((fi & 1023) == 0) ct.ThrowIfCancellationRequested();
                    if (view.IsDead((uint)fi)) continue;
                    yield return Dto.File(view.Segment, fi);
                }
            }
            foreach (var df in snap.DeltaSorted) yield return df.ToIndexedFile();
        }

        /// <summary>
        /// Occurrences of a name, streamed: per base segment in FileId order (engine first), then delta files in path
        /// order; offsets ascending within a file. Decoding is lazy, a batch at a time.
        /// </summary>
        public IEnumerable<OccurrenceHit> FindOccurrences(string name, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(name)) yield break;
            var snap = Inner;
            var batch = new List<OccurrenceHit>(256);
            foreach (var view in snap.Bases)
            {
                int n = view.Segment.FindName(name);
                if (n < 0) continue;
                var state = new DecodeState();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    batch.Clear();
                    bool more = DecodeBatch(view, n, ref state, batch, 256);
                    foreach (var h in batch) yield return h;
                    if (!more) break;
                }
            }
            foreach (var df in snap.DeltaSorted)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var o in df.Occurrences)
                    if (string.Equals(o.Name, name, StringComparison.Ordinal))
                        yield return new OccurrenceHit(df.Source.Path, o.Offset, o.Role);
            }
        }

        private struct DecodeState
        {
            public int Pos, Read, Offset;
            public uint File;
            public uint CachedFile;
            public string CachedPath;
            public bool HasCache;
        }

        private static bool DecodeBatch(SegmentView view, int nameIndex, ref DecodeState st, List<OccurrenceHit> into, int max)
        {
            var seg = view.Segment;
            var reader = seg.ReadOccurrences(nameIndex);
            reader.Restore(st.Pos, st.Read, st.File, st.Offset);
            bool more = true;
            while (into.Count < max)
            {
                if (!reader.Next(out uint file, out int offset, out byte role))
                {
                    more = false;
                    break;
                }
                if (view.IsDead(file)) continue;
                if (!st.HasCache || st.CachedFile != file)
                {
                    st.CachedFile = file;
                    st.CachedPath = seg.GetString(seg.Files[(int)file].PathId);
                    st.HasCache = true;
                }
                into.Add(new OccurrenceHit(st.CachedPath, offset, role));
            }
            st.Pos = reader.Position;
            st.Read = reader.ReadCount;
            st.File = reader.LastFile;
            st.Offset = reader.LastOffset;
            return more && st.Read < reader.Count;
        }

        // ------------------------------------------------------------------ stats

        public IndexStats GetStats()
        {
            var snap = Inner;
            var stats = new IndexStats
            {
                SegmentCount = snap.Bases.Length,
                DeltaFileCount = snap.Delta.Count,
                DeltaSymbolCount = snap.DeltaSymbolCount,
                DeltaBytes = snap.DeltaBytes,
                SnapshotVersion = snap.Version,
            };
            foreach (var view in snap.Bases)
            {
                var seg = view.Segment;
                stats.BytesOnDisk += seg.Length;
                stats.OccurrenceCount += seg.OccurrenceCount;
                stats.TombstoneCount += view.TombstoneCount;
                int files = seg.FileCount, symbols = seg.SymbolCount;
                foreach (int fi in view.DeadFiles())
                {
                    files--;
                    symbols -= (int)seg.Files[fi].SymbolCount;
                }
                stats.FileCount += files;
                stats.SymbolCount += symbols;
            }
            stats.FileCount += snap.Delta.Count;
            stats.SymbolCount += snap.DeltaSymbolCount;
            foreach (var df in snap.Delta.Values) stats.OccurrenceCount += df.Occurrences.Length;
            return stats;
        }
    }
}
