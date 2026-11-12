using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

namespace UEToVS.Engine.Index.Storage
{
    /// <summary>
    /// Builds a deduplicated UTF-8 string pool with 32-bit ids (0 = null) and an open-addressing hash table
    /// for lookup. The serialised form is three arrays (offsets, data, buckets) read back by <see cref="StringTableView"/>
    /// directly from mapped memory.
    /// </summary>
    internal sealed class StringPoolBuilder
    {
        private readonly Dictionary<string, uint> _ids = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly List<uint> _offsets = new List<uint> { 0 };
        private readonly MemoryStream _data = new MemoryStream();
        private byte[] _scratch = new byte[256];

        public int Count => _offsets.Count - 1;

        public long DataBytes => _data.Length;

        /// <summary>Returns the id for <paramref name="s"/>, adding it if new. null and "" map to 0.</summary>
        public uint Add(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (_ids.TryGetValue(s, out uint id)) return id;
            int max = Encoding.UTF8.GetMaxByteCount(s.Length);
            if (_scratch.Length < max) _scratch = new byte[Math.Max(max, _scratch.Length * 2)];
            int n = Encoding.UTF8.GetBytes(s, 0, s.Length, _scratch, 0);
            _data.Write(_scratch, 0, n);
            if (_data.Length > uint.MaxValue) throw new InvalidOperationException("string pool exceeds 4 GB");
            _offsets.Add((uint)_data.Length);
            id = (uint)(_offsets.Count - 1);
            _ids.Add(s, id);
            return id;
        }

        public bool TryGet(string s, out uint id)
        {
            id = 0;
            return !string.IsNullOrEmpty(s) && _ids.TryGetValue(s, out id);
        }

        public uint[] BuildOffsets() => _offsets.ToArray();

        public byte[] BuildData() => _data.ToArray();

        public uint[] BuildBuckets()
        {
            var data = _data.GetBuffer();
            return BuildBuckets(_offsets, new ReadOnlySpan<byte>(data, 0, (int)_data.Length));
        }

        internal static uint[] BuildBuckets(IReadOnlyList<uint> offsets, ReadOnlySpan<byte> data)
        {
            int count = offsets.Count - 1;
            int size = 16;
            while (size < count * 2) size <<= 1;
            var buckets = new uint[size];
            int mask = size - 1;
            for (uint id = 1; id <= count; id++)
            {
                var bytes = data.Slice((int)offsets[(int)id - 1], (int)(offsets[(int)id] - offsets[(int)id - 1]));
                int b = (int)(Hash(bytes) & (uint)mask);
                while (buckets[b] != 0) b = (b + 1) & mask;
                buckets[b] = id;
            }
            return buckets;
        }

        internal static ulong Hash(ReadOnlySpan<byte> utf8) => XxHash3.HashToUInt64(utf8);

        /// <summary>Standalone serialisation: [count][offsets][dataLen][data][bucketCount][buckets].</summary>
        public void WriteTo(Stream s)
        {
            var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
            var offsets = BuildOffsets();
            var data = BuildData();
            var buckets = BuildBuckets();
            w.Write(offsets.Length);
            w.Write(MemoryMarshal.AsBytes(offsets.AsSpan()));
            w.Write(data.Length);
            w.Write(data);
            w.Write(buckets.Length);
            w.Write(MemoryMarshal.AsBytes(buckets.AsSpan()));
            w.Flush();
        }
    }

    /// <summary>An immutable, deserialised string table (used for the standalone pool round trip and tests).</summary>
    internal sealed class StringTable
    {
        private readonly uint[] _offsets;
        private readonly byte[] _data;
        private readonly uint[] _buckets;

        public StringTable(uint[] offsets, byte[] data, uint[] buckets)
        {
            _offsets = offsets;
            _data = data;
            _buckets = buckets;
        }

        public StringTableView View => new StringTableView(_offsets, _data, _buckets);

        public static StringTable ReadFrom(Stream s)
        {
            var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
            var offsets = new uint[r.ReadInt32()];
            r.Read(MemoryMarshal.AsBytes(offsets.AsSpan()));
            var data = r.ReadBytes(r.ReadInt32());
            var buckets = new uint[r.ReadInt32()];
            r.Read(MemoryMarshal.AsBytes(buckets.AsSpan()));
            return new StringTable(offsets, data, buckets);
        }
    }

    /// <summary>Zero-copy reader over a serialised string pool.</summary>
    internal readonly ref struct StringTableView
    {
        private readonly ReadOnlySpan<uint> _offsets;
        private readonly ReadOnlySpan<byte> _data;
        private readonly ReadOnlySpan<uint> _buckets;

        public StringTableView(ReadOnlySpan<uint> offsets, ReadOnlySpan<byte> data, ReadOnlySpan<uint> buckets)
        {
            _offsets = offsets;
            _data = data;
            _buckets = buckets;
        }

        public int Count => Math.Max(0, _offsets.Length - 1);

        public ReadOnlySpan<byte> GetBytes(uint id)
        {
            if (id == 0 || id >= _offsets.Length) return default;
            uint start = _offsets[(int)id - 1];
            return _data.Slice((int)start, (int)(_offsets[(int)id] - start));
        }

        public string GetString(uint id) => id == 0 ? null : Encoding.UTF8.GetString(GetBytes(id));

        public uint Find(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int max = Encoding.UTF8.GetMaxByteCount(s.Length);
            byte[] rented = null;
            Span<byte> buf = max <= 512 ? stackalloc byte[max] : (rented = ArrayPool<byte>.Shared.Rent(max));
            try
            {
                int n = Encoding.UTF8.GetBytes(s, buf);
                return Find(buf.Slice(0, n));
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public uint Find(ReadOnlySpan<byte> utf8)
        {
            if (_buckets.Length == 0 || utf8.Length == 0) return 0;
            int mask = _buckets.Length - 1;
            int b = (int)(StringPoolBuilder.Hash(utf8) & (uint)mask);
            for (int probes = 0; probes < _buckets.Length; probes++)
            {
                uint id = _buckets[b];
                if (id == 0) return 0;
                if (GetBytes(id).SequenceEqual(utf8)) return id;
                b = (b + 1) & mask;
            }
            return 0;
        }
    }
}
