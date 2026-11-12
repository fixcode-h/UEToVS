using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using UEToVS.Engine.Index.Storage;

namespace UEToVS.Engine.Index.Segments
{
    /// <summary>A file in the in-memory delta segment (recently changed). Immutable once created.</summary>
    internal sealed class DeltaFile
    {
        public DeltaFile(FileSymbols source)
        {
            Source = source;
            Key = PathKey.Normalize(source.Path);
            var decls = (source.Decls ?? Array.Empty<DeclInput>()).Where(d => !string.IsNullOrEmpty(d.Name)).ToArray();
            // stable sort by offset (source order)
            var order = Enumerable.Range(0, decls.Length).OrderBy(i => decls[i].Offset).ThenBy(i => i).ToArray();
            Decls = order.Select(i => decls[i]).ToArray();
            NameLower = new string[Decls.Length];
            long bytes = 128 + (source.Path?.Length ?? 0) * 2;
            for (int i = 0; i < Decls.Length; i++)
            {
                NameLower[i] = Decls[i].Name.ToLowerInvariant();
                if (Decls[i].Bases != null) foreach (var b in Decls[i].Bases) bytes += 24 + (b?.Length ?? 0) * 2;
                bytes += 64 + Decls[i].Name.Length * 4 + (Decls[i].Container?.Length ?? 0) * 2 + (Decls[i].Signature?.Length ?? 0) * 2 +
                         (Decls[i].Specifiers?.Length ?? 0) * 2;
            }
            Occurrences = (source.Occurrences ?? Array.Empty<OccurrenceInput>()).Where(o => !string.IsNullOrEmpty(o.Name))
                .OrderBy(o => o.Offset).ToArray();
            bytes += Occurrences.Length * 24L;
            if (source.Includes != null) foreach (var inc in source.Includes) bytes += 24 + (inc?.Length ?? 0) * 2;
            FileNameLower = System.IO.Path.GetFileName(source.Path).ToLowerInvariant();
            PathHash = StringPoolBuilder.Hash(System.Text.Encoding.UTF8.GetBytes(source.Path));
            ApproxBytes = bytes;
        }

        public FileSymbols Source { get; }
        public string Key { get; }
        public DeclInput[] Decls { get; }
        public string[] NameLower { get; }
        public OccurrenceInput[] Occurrences { get; }
        public string FileNameLower { get; }
        public ulong PathHash { get; }
        public long ApproxBytes { get; }
        public bool IsProject => Source.Origin == Contracts.ItemOrigin.Project || Source.Origin == Contracts.ItemOrigin.ProjectPlugin;

        public IndexedFile ToIndexedFile() => new IndexedFile
        {
            Path = Source.Path,
            Module = Source.Module,
            Origin = Source.Origin,
            MtimeTicks = Source.MtimeTicks,
            Length = Source.Length,
            ContentHash = Source.ContentHash,
            SymbolCount = Decls.Length,
            Includes = (Source.Includes ?? Array.Empty<string>()).ToArray(),
            InDelta = true,
        };
    }

    /// <summary>A base segment as seen by one snapshot: the segment plus the FileIds it has superseded (tombstones).</summary>
    internal sealed class SegmentView
    {
        public SegmentView(Segment segment, ulong[] tombstones, int tombstoneCount)
        {
            Segment = segment;
            Tombstones = tombstones;
            TombstoneCount = tombstoneCount;
        }

        public Segment Segment { get; }

        /// <summary>Bitmap over FileIds; null when empty. Never mutated after publication (copy-on-write).</summary>
        public ulong[] Tombstones { get; }

        public int TombstoneCount { get; }

        public bool IsDead(uint fileId) => Tombstones != null && (Tombstones[fileId >> 6] & (1UL << (int)(fileId & 63))) != 0;

        public SegmentView WithTombstone(int fileId)
        {
            if (fileId < 0 || IsDead((uint)fileId)) return this;
            var bits = Tombstones != null ? (ulong[])Tombstones.Clone() : new ulong[(Segment.FileCount + 63) / 64];
            bits[fileId >> 6] |= 1UL << (fileId & 63);
            return new SegmentView(Segment, bits, TombstoneCount + 1);
        }

        public IEnumerable<int> DeadFiles()
        {
            if (Tombstones == null) yield break;
            for (int i = 0; i < Segment.FileCount; i++)
                if (IsDead((uint)i)) yield return i;
        }
    }

    /// <summary>
    /// An immutable, consistent view of the whole index: base segments (engine first, then project) with their
    /// tombstones, plus the delta. Readers acquire a reference; the index owns one reference to the current snapshot.
    /// </summary>
    internal sealed class Snapshot
    {
        private int _refs = 1;
        private DeltaFile[] _deltaSorted;

        public Snapshot(SegmentView[] bases, ImmutableDictionary<string, DeltaFile> delta, long version)
        {
            Bases = bases;
            Delta = delta;
            Version = version;
            foreach (var b in bases) b.Segment.AddRef();
            foreach (var d in delta.Values)
            {
                DeltaSymbolCount += d.Decls.Length;
                DeltaBytes += d.ApproxBytes;
            }
        }

        public static Snapshot Empty() => new Snapshot(Array.Empty<SegmentView>(), ImmutableDictionary.Create<string, DeltaFile>(StringComparer.Ordinal), 0);

        public SegmentView[] Bases { get; }
        public ImmutableDictionary<string, DeltaFile> Delta { get; }
        public long Version { get; }
        public int DeltaSymbolCount { get; }
        public long DeltaBytes { get; }

        /// <summary>Delta files ordered by path key (stable "FileId order" for the delta).</summary>
        public DeltaFile[] DeltaSorted
        {
            get
            {
                var d = Volatile.Read(ref _deltaSorted);
                if (d != null) return d;
                d = Delta.Values.OrderBy(f => f.Key, StringComparer.Ordinal).ToArray();
                Volatile.Write(ref _deltaSorted, d);
                return d;
            }
        }

        public bool TryAcquire()
        {
            while (true)
            {
                int r = Volatile.Read(ref _refs);
                if (r <= 0) return false;
                if (Interlocked.CompareExchange(ref _refs, r + 1, r) == r) return true;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0) return;
            foreach (var b in Bases) b.Segment.Release();
        }
    }
}
