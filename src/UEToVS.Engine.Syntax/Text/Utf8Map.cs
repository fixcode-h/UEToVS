using System;
using System.Text;

namespace UEToVS.Engine.Syntax
{
    /// <summary>Open-addressing hash map keyed by UTF-8 bytes, so lookups need no string allocation. Not thread-safe for writes.</summary>
    internal sealed class Utf8Map<T>
    {
        private byte[][] _keys;
        private T[] _values;
        private uint[] _hashes;
        private int _count;

        public Utf8Map(int capacity = 16)
        {
            int size = 16;
            while (size < capacity * 2) size <<= 1;
            _keys = new byte[size][];
            _values = new T[size];
            _hashes = new uint[size];
        }

        public int Count => _count;

        public static uint Hash(ReadOnlySpan<byte> key)
        {
            uint h = 2166136261;
            for (int i = 0; i < key.Length; i++) h = (h ^ key[i]) * 16777619;
            return h;
        }

        public bool TryGetValue(ReadOnlySpan<byte> key, out T value) => TryGetValue(key, Hash(key), out value);

        public bool TryGetValue(ReadOnlySpan<byte> key, uint hash, out T value)
        {
            int mask = _keys.Length - 1;
            int i = (int)hash & mask;
            while (true)
            {
                var k = _keys[i];
                if (k == null) break;
                if (_hashes[i] == hash && key.SequenceEqual(k))
                {
                    value = _values[i];
                    return true;
                }
                i = (i + 1) & mask;
            }
            value = default;
            return false;
        }

        public bool ContainsKey(ReadOnlySpan<byte> key) => TryGetValue(key, out _);

        public void Set(string key, T value) => Set(Encoding.UTF8.GetBytes(key), value);

        public void Set(ReadOnlySpan<byte> key, T value) => Set(key, Hash(key), value);

        public void Set(ReadOnlySpan<byte> key, uint hash, T value)
        {
            int mask = _keys.Length - 1;
            int i = (int)hash & mask;
            while (true)
            {
                var k = _keys[i];
                if (k == null) break;
                if (_hashes[i] == hash && key.SequenceEqual(k))
                {
                    _values[i] = value;
                    return;
                }
                i = (i + 1) & mask;
            }
            _keys[i] = key.ToArray();
            _values[i] = value;
            _hashes[i] = hash;
            if (++_count * 2 > _keys.Length) Grow();
        }

        private void Grow()
        {
            var oldKeys = _keys;
            var oldValues = _values;
            var oldHashes = _hashes;
            int size = oldKeys.Length * 2;
            _keys = new byte[size][];
            _values = new T[size];
            _hashes = new uint[size];
            int mask = size - 1;
            for (int j = 0; j < oldKeys.Length; j++)
            {
                if (oldKeys[j] == null) continue;
                int i = (int)oldHashes[j] & mask;
                while (_keys[i] != null) i = (i + 1) & mask;
                _keys[i] = oldKeys[j];
                _values[i] = oldValues[j];
                _hashes[i] = oldHashes[j];
            }
        }
    }

    /// <summary>
    /// Interns identifier names from UTF-8 bytes: each distinct name is decoded to a <see cref="string"/> once.
    /// One instance may be reused across files on the same thread to share strings; it is not thread-safe.
    /// </summary>
    public sealed class NameTable
    {
        private readonly Utf8Map<string> _map = new Utf8Map<string>(1024);

        public int Count => _map.Count;

        public string Intern(ReadOnlySpan<byte> utf8)
        {
            uint h = Utf8Map<string>.Hash(utf8);
            if (_map.TryGetValue(utf8, h, out var s)) return s;
            s = Encoding.UTF8.GetString(utf8);
            _map.Set(utf8, h, s);
            return s;
        }
    }
}
