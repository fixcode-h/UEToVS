using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Index.Storage
{
    /// <summary>
    /// A read-only, memory-mapped segment. All arrays are read as spans straight from the mapping; nothing is
    /// deserialised into the managed heap. Lifetime is reference counted so readers holding an old snapshot keep
    /// the mapping alive across compaction; the last release unmaps (and deletes the file if it was retired).
    /// </summary>
    internal sealed unsafe class Segment
    {
        private static readonly Logger L = Log.For("Index");

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _base;
        private readonly (long off, long len)[] _sections = new (long, long)[32];
        private int _refs = 1;
        private volatile bool _deleteOnClose;

        private Segment(string path, MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* basePtr, long length)
        {
            FullPath = path;
            FileName = Path.GetFileName(path);
            _mmf = mmf;
            _view = view;
            _base = basePtr;
            Length = length;
        }

        public string FullPath { get; }
        public string FileName { get; }
        public long Length { get; }
        public SegmentKind Kind { get; private set; }
        public string KnowledgeBaseVersion { get; private set; }
        public string EngineBuildId { get; private set; }
        public int FileCount { get; private set; }
        public int SymbolCount { get; private set; }
        public int NameCount { get; private set; }
        public long OccurrenceCount { get; private set; }

        /// <summary>Whether any file has Project/ProjectPlugin origin (lets searches skip engine-only segments cheaply).</summary>
        public bool HasProjectFiles { get; private set; }

        public bool HasNonProjectFiles { get; private set; }

        // ---------------------------------------------------------------- open / validate

        public static SegmentLoadStatus TryOpen(string path, string expectedKb, string expectedEngineBuild, bool verifyChecksums,
            out Segment segment, out string detail)
        {
            segment = null;
            detail = null;
            if (!File.Exists(path))
            {
                detail = "file not found";
                return SegmentLoadStatus.Missing;
            }

            FileStream fs = null;
            MemoryMappedFile mmf = null;
            MemoryMappedViewAccessor view = null;
            byte* ptr = null;
            bool acquired = false;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                long length = fs.Length;
                if (length < SegmentFormat.HeaderSize)
                {
                    fs.Dispose();
                    detail = $"file too short ({length} bytes)";
                    return SegmentLoadStatus.Corrupt;
                }
                mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
                view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                acquired = true;
                ptr += view.PointerOffset;
                var seg = new Segment(path, mmf, view, ptr, length);
                var status = seg.Validate(expectedKb, expectedEngineBuild, verifyChecksums, out detail);
                if (status != SegmentLoadStatus.Ok)
                {
                    seg.Close();
                    return status;
                }
                segment = seg;
                return SegmentLoadStatus.Ok;
            }
            catch (Exception ex)
            {
                L.Error("index.segment.open_failed", "Could not open segment " + path, ex);
                if (acquired) view.SafeMemoryMappedViewHandle.ReleasePointer();
                view?.Dispose();
                mmf?.Dispose();
                fs?.Dispose();
                detail = ex.GetType().Name + ": " + ex.Message;
                return SegmentLoadStatus.IoError;
            }
        }

        private SegmentLoadStatus Validate(string expectedKb, string expectedEngineBuild, bool verifyChecksums, out string detail)
        {
            detail = null;
            var header = new ReadOnlySpan<byte>(_base, SegmentFormat.HeaderSize);
            if (!header.Slice(0, 8).SequenceEqual(SegmentFormat.Magic))
            {
                detail = "bad magic";
                return SegmentLoadStatus.Corrupt;
            }
            uint version = MemoryMarshal.Read<uint>(header.Slice(SegmentFormat.OffVersion));
            if (version != SegmentFormat.Version)
            {
                detail = $"format version {version}, expected {SegmentFormat.Version}";
                return SegmentLoadStatus.FormatVersionMismatch;
            }
            var copy = header.ToArray();
            ulong stored = MemoryMarshal.Read<ulong>(header.Slice(SegmentFormat.OffHeaderChecksum));
            copy.AsSpan(SegmentFormat.OffHeaderChecksum, 8).Clear();
            if (XxHash64.HashToUInt64(copy) != stored)
            {
                detail = "header checksum mismatch";
                return SegmentLoadStatus.Corrupt;
            }
            ulong fileLength = MemoryMarshal.Read<ulong>(header.Slice(SegmentFormat.OffFileLength));
            if ((long)fileLength != Length)
            {
                detail = $"length {Length}, header says {fileLength}";
                return SegmentLoadStatus.Corrupt;
            }

            Kind = (SegmentKind)MemoryMarshal.Read<uint>(header.Slice(SegmentFormat.OffKind));
            KnowledgeBaseVersion = SegmentFormat.ReadShortString(header.Slice(SegmentFormat.OffKbVersion));
            EngineBuildId = SegmentFormat.ReadShortString(header.Slice(SegmentFormat.OffEngineBuild));
            FileCount = (int)MemoryMarshal.Read<uint>(header.Slice(SegmentFormat.OffFileCount));
            SymbolCount = (int)MemoryMarshal.Read<uint>(header.Slice(SegmentFormat.OffSymbolCount));
            NameCount = (int)MemoryMarshal.Read<uint>(header.Slice(SegmentFormat.OffNameCount));
            OccurrenceCount = (long)MemoryMarshal.Read<ulong>(header.Slice(SegmentFormat.OffOccurrenceCount));

            if (expectedKb != null && KnowledgeBaseVersion != SegmentFormat.Truncate(expectedKb))
            {
                detail = $"knowledge base '{KnowledgeBaseVersion}', expected '{expectedKb}'";
                return SegmentLoadStatus.KnowledgeBaseMismatch;
            }
            if (Kind == SegmentKind.Engine && expectedEngineBuild != null && EngineBuildId != SegmentFormat.Truncate(expectedEngineBuild))
            {
                detail = $"engine build '{EngineBuildId}', expected '{expectedEngineBuild}'";
                return SegmentLoadStatus.EngineBuildMismatch;
            }

            uint count = MemoryMarshal.Read<uint>(header.Slice(SegmentFormat.OffSectionCount));
            if (count > 64 || SegmentFormat.OffSectionTable + count * SegmentFormat.SectionEntrySize > SegmentFormat.HeaderSize)
            {
                detail = "bad section count";
                return SegmentLoadStatus.Corrupt;
            }
            for (int i = 0; i < count; i++)
            {
                var e = header.Slice(SegmentFormat.OffSectionTable + i * SegmentFormat.SectionEntrySize);
                uint id = MemoryMarshal.Read<uint>(e);
                long off = (long)MemoryMarshal.Read<ulong>(e.Slice(8));
                long len = (long)MemoryMarshal.Read<ulong>(e.Slice(16));
                ulong hash = MemoryMarshal.Read<ulong>(e.Slice(24));
                if (id == 0 || id >= _sections.Length || off < SegmentFormat.HeaderSize || len < 0 || off + len > Length || len > int.MaxValue)
                {
                    detail = $"section {id} out of bounds";
                    return SegmentLoadStatus.Corrupt;
                }
                if (verifyChecksums && XxHash64.HashToUInt64(new ReadOnlySpan<byte>(_base + off, (int)len)) != hash)
                {
                    detail = $"section {(SectionId)id} checksum mismatch";
                    return SegmentLoadStatus.Corrupt;
                }
                _sections[id] = (off, len);
            }
            foreach (SectionId required in Enum.GetValues(typeof(SectionId)))
            {
                if (_sections[(int)required].off == 0)
                {
                    detail = $"section {required} missing";
                    return SegmentLoadStatus.Corrupt;
                }
            }
            if (Files.Length != FileCount || Symbols.Length != SymbolCount || Names.Length != NameCount)
            {
                detail = "count mismatch between header and sections";
                return SegmentLoadStatus.Corrupt;
            }
            foreach (ref readonly var f in Files)
            {
                if (f.IsProject) HasProjectFiles = true;
                else HasNonProjectFiles = true;
            }
            return SegmentLoadStatus.Ok;
        }

        // ---------------------------------------------------------------- raw sections

        private ReadOnlySpan<T> Get<T>(SectionId id) where T : unmanaged
        {
            var (off, len) = _sections[(int)id];
            return new ReadOnlySpan<T>(_base + off, (int)(len / sizeof(T)));
        }

        public StringTableView Strings => new StringTableView(Get<uint>(SectionId.StrOffsets), Get<byte>(SectionId.StrData), Get<uint>(SectionId.StrBuckets));
        public ReadOnlySpan<FileRecord> Files => Get<FileRecord>(SectionId.Files);
        public ReadOnlySpan<SymbolRecord> Symbols => Get<SymbolRecord>(SectionId.Symbols);
        public ReadOnlySpan<NameEntry> Names => Get<NameEntry>(SectionId.Names);
        public ReadOnlySpan<uint> NameSymbols => Get<uint>(SectionId.NameSymbols);
        public ReadOnlySpan<byte> Occurrences => Get<byte>(SectionId.Occurrences);
        public ReadOnlySpan<uint> Includes => Get<uint>(SectionId.Includes);

        public string GetString(uint id) => Strings.GetString(id);

        /// <summary>Section sizes in bytes (diagnostics).</summary>
        public IEnumerable<(SectionId Id, long Bytes)> SectionSizes()
        {
            for (int i = 1; i < _sections.Length; i++)
                if (_sections[i].off != 0) yield return ((SectionId)i, _sections[i].len);
        }

        public ReadOnlySpan<byte> NameLower(int nameIndex)
        {
            var offs = Get<uint>(SectionId.NameLowerOffsets);
            uint s = offs[nameIndex];
            return Get<byte>(SectionId.NameLowerData).Slice((int)s, (int)(offs[nameIndex + 1] - s));
        }

        public ReadOnlySpan<byte> FileNameLower(int fileIndex)
        {
            var offs = Get<uint>(SectionId.FileNameLowerOffsets);
            uint s = offs[fileIndex];
            return Get<byte>(SectionId.FileNameLowerData).Slice((int)s, (int)(offs[fileIndex + 1] - s));
        }

        /// <summary>Indices of names that have symbols, sorted by lowercase UTF-8 bytes.</summary>
        public ReadOnlySpan<uint> NameLowerOrder => Get<uint>(SectionId.NameLowerOrder);

        /// <summary>The slice of <see cref="NameLowerOrder"/> whose lowercase names start with <paramref name="lowerPrefix"/>.</summary>
        public ReadOnlySpan<uint> NamesWithLowerPrefix(ReadOnlySpan<byte> lowerPrefix)
        {
            var order = NameLowerOrder;
            int lo = 0, hi = order.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (NameLower((int)order[mid]).SequenceCompareTo(lowerPrefix) < 0) lo = mid + 1;
                else hi = mid;
            }
            int start = lo;
            hi = order.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (NameLower((int)order[mid]).StartsWith(lowerPrefix)) lo = mid + 1;
                else hi = mid;
            }
            return order.Slice(start, lo - start);
        }

        public ReadOnlySpan<uint> SymbolsOfName(int nameIndex)
        {
            var e = Names[nameIndex];
            return NameSymbols.Slice((int)e.SymStart, (int)e.SymCount);
        }

        /// <summary>
        /// Delta + varint encoded, ascending name indices (0-based) whose lowercase form contains the trigram.
        /// Empty when the trigram does not occur. Decode with <see cref="Varint.Read"/>, summing deltas.
        /// </summary>
        public ReadOnlySpan<byte> TrigramPostings(ulong key)
        {
            var keys = Get<ulong>(SectionId.TrigramKeys);
            int i = keys.BinarySearch(key);
            if (i < 0) return default;
            var offs = Get<uint>(SectionId.TrigramOffsets);
            return Get<byte>(SectionId.TrigramPostings).Slice((int)offs[i], (int)(offs[i + 1] - offs[i]));
        }

        /// <summary>Name indices (0-based, ascending) having a word start with this lowercase character.</summary>
        public ReadOnlySpan<uint> InitialPostings(uint lowerChar)
        {
            var keys = Get<uint>(SectionId.InitialKeys);
            int i = keys.BinarySearch(lowerChar);
            if (i < 0) return default;
            var offs = Get<uint>(SectionId.InitialOffsets);
            return Get<uint>(SectionId.InitialPostings).Slice((int)offs[i], (int)(offs[i + 1] - offs[i]));
        }

        /// <summary>0-based name index for an exact (case-sensitive) name, or -1.</summary>
        public int FindName(string name)
        {
            uint id = Strings.Find(name);
            return id >= 1 && id <= NameCount ? (int)id - 1 : -1;
        }

        /// <summary>File index for a normalised path key, or -1.</summary>
        public int FindFile(string pathKey)
        {
            var buckets = Get<uint>(SectionId.FileBuckets);
            if (buckets.Length == 0 || string.IsNullOrEmpty(pathKey)) return -1;
            var keyBytes = Encoding.UTF8.GetBytes(pathKey);
            int mask = buckets.Length - 1;
            int b = (int)(StringPoolBuilder.Hash(keyBytes) & (uint)mask);
            var files = Files;
            for (int probes = 0; probes < buckets.Length; probes++)
            {
                uint v = buckets[b];
                if (v == 0) return -1;
                int fi = (int)v - 1;
                if (PathKey.Normalize(GetString(files[fi].PathId)) == pathKey) return fi;
                b = (b + 1) & mask;
            }
            return -1;
        }

        /// <summary>Decodes the occurrence postings of one name in file order.</summary>
        public OccurrenceReader ReadOccurrences(int nameIndex)
        {
            var e = Names[nameIndex];
            return new OccurrenceReader(Occurrences.Slice((int)e.OccOffset, (int)e.OccLength), (int)e.OccCount);
        }

        // ---------------------------------------------------------------- lifetime

        public bool TryAddRef()
        {
            while (true)
            {
                int r = Volatile.Read(ref _refs);
                if (r <= 0) return false;
                if (Interlocked.CompareExchange(ref _refs, r + 1, r) == r) return true;
            }
        }

        public void AddRef()
        {
            if (!TryAddRef()) throw new ObjectDisposedException(FileName);
        }

        public void Release()
        {
            int r = Interlocked.Decrement(ref _refs);
            if (r == 0) Close();
        }

        /// <summary>The file will be deleted when the last reference goes away.</summary>
        public void Retire() => _deleteOnClose = true;

        public bool IsClosed => Volatile.Read(ref _refs) <= 0;

        private void Close()
        {
            Volatile.Write(ref _refs, 0);
            try
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _view.Dispose();
                _mmf.Dispose();
            }
            catch (Exception ex)
            {
                L.Error("index.segment.close_failed", "Error unmapping segment " + FileName, ex);
            }
            if (_deleteOnClose)
            {
                try
                {
                    File.Delete(FullPath);
                    L.Debug("index.segment.deleted", "Retired segment deleted", ("file", FileName));
                }
                catch (Exception ex)
                {
                    L.Warn("index.segment.delete_failed", "Could not delete retired segment (will be cleaned up on next open)", ex, ("file", FileName));
                }
            }
        }
    }

    /// <summary>Streaming decoder for one name's (FileId, Offset, Role) postings.</summary>
    internal ref struct OccurrenceReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _count;
        private int _pos;
        private int _read;
        private uint _file;
        private int _offset;

        public OccurrenceReader(ReadOnlySpan<byte> data, int count)
        {
            _data = data;
            _count = count;
            _pos = 0;
            _read = 0;
            _file = 0;
            _offset = 0;
        }

        public int Count => _count;

        /// <summary>Position in the stream; lets an iterator resume after yielding (spans cannot cross yields).</summary>
        public int Position => _pos;
        public int ReadCount => _read;
        public uint LastFile => _file;
        public int LastOffset => _offset;

        public void Restore(int pos, int read, uint file, int offset)
        {
            _pos = pos;
            _read = read;
            _file = file;
            _offset = offset;
        }

        public bool Next(out uint file, out int offset, out byte role)
        {
            if (_read >= _count)
            {
                file = 0;
                offset = 0;
                role = 0;
                return false;
            }
            uint fdelta = Varint.Read(_data, ref _pos);
            uint off = Varint.Read(_data, ref _pos);
            role = _data[_pos++];
            if (fdelta != 0 || _read == 0)
            {
                _file += fdelta;
                _offset = (int)off;
            }
            else
            {
                _offset += (int)off;
            }
            _read++;
            file = _file;
            offset = _offset;
            return true;
        }
    }
}
