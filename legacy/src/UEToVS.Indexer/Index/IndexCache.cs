using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UEToVS.Core.Protocol;
using UEToVS.Indexer.Parsing;

namespace UEToVS.Indexer.Index
{
    /// <summary>
    /// Binary on-disk cache of parsed symbols, keyed by file path + mtime + size. One cache file per
    /// group (engine / project) so the engine cache is shared between projects of the same engine.
    /// </summary>
    internal static class IndexCache
    {
        private const int FormatVersion = 3;

        public static string CacheFileFor(string cacheDir, string groupName, string key)
        {
            using var sha = SHA1.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToLowerInvariant()))).Substring(0, 16);
            return Path.Combine(cacheDir, $"{groupName}-{hash}.idx");
        }

        public static Dictionary<string, FileRecord> Load(string file)
        {
            var result = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
            if (file == null || !File.Exists(file)) return result;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                using var r = new BinaryReader(fs, Encoding.UTF8);
                if (r.ReadInt32() != FormatVersion) return result;
                var strings = new string[r.ReadInt32()];
                for (int i = 0; i < strings.Length; i++) strings[i] = string.Intern(r.ReadString());
                string S(int idx) => idx < 0 ? null : strings[idx];

                int files = r.ReadInt32();
                for (int f = 0; f < files; f++)
                {
                    var rec = new FileRecord
                    {
                        Path = r.ReadString(),
                        MtimeTicks = r.ReadInt64(),
                        Length = r.ReadInt64(),
                    };
                    int n = r.ReadInt32();
                    var syms = new ScannedSymbol[n];
                    for (int i = 0; i < n; i++)
                    {
                        syms[i] = new ScannedSymbol
                        {
                            Name = S(r.ReadInt32()),
                            Container = S(r.ReadInt32()),
                            Kind = (SymbolKind)r.ReadByte(),
                            Ue = (UeFlags)r.ReadInt16(),
                            Specifiers = S(r.ReadInt32()),
                            Line = r.ReadInt32(),
                            Column = r.ReadInt32(),
                            IsDefinition = r.ReadBoolean(),
                            Signature = S(r.ReadInt32()),
                        };
                    }
                    rec.Symbols = syms;
                    result[rec.Path] = rec;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Ignoring corrupt cache {file}: {ex.Message}");
                result.Clear();
            }
            return result;
        }

        public static void Save(string file, IEnumerable<FileRecord> records)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var table = new Dictionary<string, int>(StringComparer.Ordinal);
            var strings = new List<string>();
            int Id(string s)
            {
                if (s == null) return -1;
                if (!table.TryGetValue(s, out int id))
                {
                    id = strings.Count;
                    strings.Add(s);
                    table[s] = id;
                }
                return id;
            }

            var list = new List<FileRecord>();
            foreach (var rec in records)
            {
                if (!rec.Parseable) continue;
                list.Add(rec);
                foreach (var s in rec.Symbols)
                {
                    Id(s.Name); Id(s.Container); Id(s.Specifiers); Id(s.Signature);
                }
            }

            string tmp = file + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(FormatVersion);
                w.Write(strings.Count);
                foreach (var s in strings) w.Write(s);
                w.Write(list.Count);
                foreach (var rec in list)
                {
                    w.Write(rec.Path);
                    w.Write(rec.MtimeTicks);
                    w.Write(rec.Length);
                    w.Write(rec.Symbols.Length);
                    foreach (var s in rec.Symbols)
                    {
                        w.Write(Id(s.Name));
                        w.Write(Id(s.Container));
                        w.Write((byte)s.Kind);
                        w.Write((short)s.Ue);
                        w.Write(Id(s.Specifiers));
                        w.Write(s.Line);
                        w.Write(s.Column);
                        w.Write(s.IsDefinition);
                        w.Write(Id(s.Signature));
                    }
                }
            }
            File.Move(tmp, file, true);
        }
    }
}
