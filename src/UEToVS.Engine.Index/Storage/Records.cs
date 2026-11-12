using System;
using System.Runtime.InteropServices;
using UEToVS.Contracts;

namespace UEToVS.Engine.Index.Storage
{
    [Flags]
    internal enum SymbolFlags : byte
    {
        None = 0,
        IsDefinition = 1 << 0,
        IsConditional = 1 << 1,
        /// <summary>v0.4.5: the declaration carries a deprecation marker (details in the DeprecationEntries section).</summary>
        IsDeprecated = 1 << 2,
    }

    /// <summary>
    /// One declaration, 32 bytes, stored in source order (FileId, Offset) inside a segment.
    /// All string fields are ids in the segment's string pool (0 = none).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct SymbolRecord
    {
        public const int MaxLine = (1 << 20) - 1;
        public const int MaxColumn = (1 << 12) - 1;

        public uint NameId;
        public uint ContainerId;
        public uint FileId;
        public uint Offset;
        public uint SignatureId;
        public uint SpecifiersId;

        /// <summary>Bits 0..19 line, bits 20..31 column (both clamped).</summary>
        public uint LineColumn;

        public ushort Ue;
        public byte Kind;
        public byte Flags;

        public int Line => (int)(LineColumn & MaxLine);
        public int Column => (int)(LineColumn >> 20);
        public SymbolKind SymbolKind => (SymbolKind)Kind;
        public UeFlags UeFlags => (UeFlags)Ue;
        public bool IsDefinition => (Flags & (byte)SymbolFlags.IsDefinition) != 0;
        public bool IsConditional => (Flags & (byte)SymbolFlags.IsConditional) != 0;
        public bool IsDeprecated => (Flags & (byte)SymbolFlags.IsDeprecated) != 0;

        public static uint PackLineColumn(int line, int column)
        {
            uint l = (uint)Math.Clamp(line, 0, MaxLine);
            uint c = (uint)Math.Clamp(column, 0, MaxColumn);
            return l | (c << 20);
        }
    }

    /// <summary>One file, 56 bytes. Files are sorted by normalised path.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct FileRecord
    {
        public uint PathId;
        public uint ModuleId;
        public uint FileNameId;
        public byte Origin;
        public byte Flags;
        public ushort Reserved;
        public long MtimeTicks;
        public long Length;
        public ulong ContentHash;
        public uint FirstSymbol;
        public uint SymbolCount;
        public uint FirstInclude;
        public uint IncludeCount;

        public ItemOrigin ItemOrigin => (ItemOrigin)Origin;
        public bool IsProject => Origin == (byte)ItemOrigin.Project || Origin == (byte)ItemOrigin.ProjectPlugin;
    }

    /// <summary>
    /// Per name (NameId = index + 1; names occupy the first ids of the pool):
    /// a slice of the name→symbol postings and a byte range of the occurrence stream.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct NameEntry
    {
        public uint SymStart;
        public uint SymCount;
        public uint OccOffset;
        public uint OccLength;
        public uint OccCount;
    }

    /// <summary>Bases of one symbol: a slice of the BaseTexts section. 12 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct BaseEntry
    {
        public uint Symbol;
        public uint First;
        public uint Count;
    }

    /// <summary>v0.4.5: deprecation of one symbol, 12 bytes. Version and message are string-pool ids (0 = none).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct DeprecationEntry
    {
        public uint Symbol;
        public uint VersionId;
        public uint MessageId;
    }

    internal static class Varint
    {
        public static void Write(System.IO.Stream s, uint v)
        {
            while (v >= 0x80)
            {
                s.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            s.WriteByte((byte)v);
        }

        public static uint Read(ReadOnlySpan<byte> data, ref int pos)
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                byte b = data[pos++];
                result |= (uint)(b & 0x7F) << shift;
                if (b < 0x80) return result;
                shift += 7;
                if (shift > 28) throw new System.IO.InvalidDataException("varint too long");
            }
        }
    }
}
