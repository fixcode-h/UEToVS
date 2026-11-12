using System;
using System.Text;

namespace UEToVS.Engine.Index.Storage
{
    internal enum SectionId : uint
    {
        StrOffsets = 1,
        StrData = 2,
        StrBuckets = 3,
        Files = 4,
        FileBuckets = 5,
        Symbols = 6,
        Names = 7,
        NameSymbols = 8,
        Occurrences = 9,
        TrigramKeys = 10,
        TrigramOffsets = 11,
        TrigramPostings = 12,
        InitialKeys = 13,
        InitialOffsets = 14,
        InitialPostings = 15,
        NameLowerOffsets = 16,
        NameLowerData = 17,
        FileNameLowerOffsets = 18,
        FileNameLowerData = 19,
        Includes = 20,
        NameLowerOrder = 21,

        // ---- format 2: inheritance ----
        /// <summary><see cref="BaseEntry"/> per symbol that has bases, sorted by symbol index.</summary>
        BaseEntries = 22,
        /// <summary>String ids of base texts as written, sliced by <see cref="BaseEntry"/>.</summary>
        BaseTexts = 23,
        /// <summary>Sorted string ids of simple base names (<see cref="UEToVS.Engine.Index.BaseNames.Simple"/>): the base → derived table keys.</summary>
        DerivedKeys = 24,
        DerivedOffsets = 25,
        /// <summary>Symbol indices of derived types, sliced by <see cref="DerivedOffsets"/>, source order per key.</summary>
        DerivedSymbols = 26,
    }

    /// <summary>
    /// On-disk layout of a segment (little endian):
    /// <code>
    /// page 0  header (4096 bytes)
    ///   0   magic "UETVSSEG"
    ///   8   u32 format version
    ///   12  u32 header size (4096)
    ///   16  u32 segment kind
    ///   20  u32 section count
    ///   24  u64 file length
    ///   32  u64 header checksum (xxHash64 of the header page with this field zeroed)
    ///   40  [64] knowledge base version (u8 length + UTF-8)
    ///   104 [64] engine build id (u8 length + UTF-8)
    ///   168 i64 created (UTC ticks)
    ///   176 u32 files, u32 symbols, u32 names, u32 strings
    ///   192 u64 occurrences
    ///   200 section table: { u32 id, u32 reserved, u64 offset, u64 length, u64 xxHash64 } x count
    /// sections, each starting on a 4096-byte boundary
    /// </code>
    /// </summary>
    internal static class SegmentFormat
    {
        /// <summary>2: base classes + base → derived table.</summary>
        public const uint Version = 2;
        public const int PageSize = 4096;
        public const int HeaderSize = PageSize;
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("UETVSSEG");

        public const int OffVersion = 8;
        public const int OffHeaderSize = 12;
        public const int OffKind = 16;
        public const int OffSectionCount = 20;
        public const int OffFileLength = 24;
        public const int OffHeaderChecksum = 32;
        public const int OffKbVersion = 40;
        public const int OffEngineBuild = 104;
        public const int OffCreated = 168;
        public const int OffFileCount = 176;
        public const int OffSymbolCount = 180;
        public const int OffNameCount = 184;
        public const int OffStringCount = 188;
        public const int OffOccurrenceCount = 192;
        public const int OffSectionTable = 200;
        public const int SectionEntrySize = 32;
        public const int MaxShortString = 63;

        public static long Align(long v) => (v + PageSize - 1) & ~(long)(PageSize - 1);

        public static void WriteShortString(Span<byte> dest, string s)
        {
            dest.Slice(0, 64).Clear();
            var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            int n = Math.Min(bytes.Length, MaxShortString);
            dest[0] = (byte)n;
            bytes.AsSpan(0, n).CopyTo(dest.Slice(1));
        }

        public static string ReadShortString(ReadOnlySpan<byte> src)
        {
            int n = Math.Min(src[0], (byte)MaxShortString);
            return Encoding.UTF8.GetString(src.Slice(1, n));
        }

        /// <summary>Header strings are truncated to 63 bytes; compare against the truncated form.</summary>
        public static string Truncate(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            return bytes.Length <= MaxShortString ? (s ?? string.Empty) : Encoding.UTF8.GetString(bytes, 0, MaxShortString);
        }
    }

    internal static class PathKey
    {
        /// <summary>Case-insensitive, separator-insensitive identity of a path.</summary>
        public static string Normalize(string path) => (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
    }
}
