using System;
using System.Collections.Generic;
using UEToVS.Contracts;

namespace UEToVS.Engine.Index
{
    /// <summary>Which base segment a set of files belongs to.</summary>
    public enum SegmentKind : byte
    {
        Engine = 1,
        Project = 2,
    }

    /// <summary>Outcome of loading one segment file.</summary>
    public enum SegmentLoadStatus
    {
        Ok = 0,

        /// <summary>The manifest lists it but the file is gone.</summary>
        Missing = 1,

        /// <summary>Bad magic, truncated file, bad header or section checksum.</summary>
        Corrupt = 2,

        /// <summary>Written by a different on-disk format version.</summary>
        FormatVersionMismatch = 3,

        /// <summary>Written with a different knowledge base (parser rules) version.</summary>
        KnowledgeBaseMismatch = 4,

        /// <summary>Engine segment built for a different engine build id.</summary>
        EngineBuildMismatch = 5,

        /// <summary>The file exists but could not be opened or mapped.</summary>
        IoError = 6,
    }

    public sealed class SegmentLoadResult
    {
        public string FileName { get; internal set; }
        public SegmentKind Kind { get; internal set; }
        public SegmentLoadStatus Status { get; internal set; }
        public string Detail { get; internal set; }
    }

    /// <summary>Result of <see cref="SymbolIndex.Open"/>. Segments that failed to load were dropped; rebuild them.</summary>
    public sealed class IndexOpenResult
    {
        public List<SegmentLoadResult> Segments { get; } = new List<SegmentLoadResult>();

        /// <summary>True when the manifest itself was missing or unreadable (a fresh index).</summary>
        public bool ManifestMissing { get; internal set; }

        /// <summary>No usable engine segment: the caller should index the engine and call <see cref="SymbolIndex.ReplaceBase"/>.</summary>
        public bool NeedsEngineRebuild { get; internal set; }

        /// <summary>No usable project segment: the caller should index the project and call <see cref="SymbolIndex.ReplaceBase"/>.</summary>
        public bool NeedsProjectRebuild { get; internal set; }
    }

    public sealed class IndexOptions
    {
        /// <summary>Folder holding segments and manifest.json, normally &lt;solution&gt;/.vs/UEToVS/Index.</summary>
        public string Directory { get; set; }

        /// <summary>Version of parser rules / knowledge base. Segments written with another value are rebuilt.</summary>
        public string KnowledgeBaseVersion { get; set; } = "0";

        /// <summary>Engine build id (e.g. from Build.version + changelist). Engine segments are keyed by it.</summary>
        public string EngineBuildId { get; set; } = "unknown";

        /// <summary>Verify every section checksum when opening (touches all pages once). Header is always verified.</summary>
        public bool VerifyChecksums { get; set; } = true;

        /// <summary>Delta size after which <see cref="SymbolIndex.NeedsCompaction"/> becomes true.</summary>
        public int CompactionFileThreshold { get; set; } = 2000;

        public long CompactionByteThreshold { get; set; } = 32L * 1024 * 1024;
    }

    /// <summary>File metadata as stored in the index.</summary>
    public sealed class IndexedFile
    {
        public string Path { get; set; }
        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }
        public long MtimeTicks { get; set; }
        public long Length { get; set; }
        public ulong ContentHash { get; set; }
        public int SymbolCount { get; set; }
        public string[] Includes { get; set; }

        /// <summary>True when the data comes from the in-memory delta rather than a base segment.</summary>
        public bool InDelta { get; set; }

        public FileItem ToFileItem() => new FileItem
        {
            Path = Path,
            Name = System.IO.Path.GetFileName(Path),
            Module = Module,
            Origin = Origin,
        };
    }

    /// <summary>One reference occurrence.</summary>
    public readonly struct OccurrenceHit
    {
        public OccurrenceHit(string path, int offset, byte role)
        {
            Path = path;
            Offset = offset;
            Role = role;
        }

        public string Path { get; }
        public int Offset { get; }
        public byte Role { get; }

        public override string ToString() => $"{Path}@{Offset}#{Role}";
    }

    public sealed class IndexStats
    {
        public int SegmentCount { get; set; }
        public int FileCount { get; set; }
        public int SymbolCount { get; set; }
        public long OccurrenceCount { get; set; }
        public long BytesOnDisk { get; set; }
        public int DeltaFileCount { get; set; }
        public int DeltaSymbolCount { get; set; }
        public long DeltaBytes { get; set; }
        public int TombstoneCount { get; set; }
        public long SnapshotVersion { get; set; }

        public override string ToString() =>
            $"segments={SegmentCount} files={FileCount} symbols={SymbolCount} occurrences={OccurrenceCount} disk={BytesOnDisk} " +
            $"deltaFiles={DeltaFileCount} deltaSymbols={DeltaSymbolCount} deltaBytes={DeltaBytes} tombstones={TombstoneCount}";
    }
}
