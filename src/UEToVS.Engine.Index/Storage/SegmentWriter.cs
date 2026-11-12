using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Engine.Index.Storage
{
    /// <summary>Builds one immutable segment file from a set of <see cref="FileSymbols"/>.</summary>
    internal static class SegmentWriter
    {
        private static readonly Logger L = Log.For("Index");

        public sealed class WriteInfo
        {
            public int Files;
            public int Symbols;
            public int Names;
            public long Occurrences;
            public long Bytes;
        }

        /// <summary>
        /// Writes the segment to <paramref name="path"/> atomically (temp file + rename).
        /// Duplicate paths: the last one wins.
        /// </summary>
        public static WriteInfo Write(string path, SegmentKind kind, string kbVersion, string engineBuildId,
            IEnumerable<FileSymbols> input, CancellationToken ct)
        {
            using (L.Time("index.segment.write", double.MaxValue, ("path", path)))
            {
                // 1. files: dedupe by normalised path, sort.
                var byKey = new Dictionary<string, FileSymbols>(StringComparer.Ordinal);
                foreach (var f in input)
                {
                    if (f == null || string.IsNullOrEmpty(f.Path)) continue;
                    byKey[PathKey.Normalize(f.Path)] = f;
                }
                var keys = byKey.Keys.ToArray();
                Array.Sort(keys, StringComparer.Ordinal);
                var files = new FileSymbols[keys.Length];
                for (int i = 0; i < keys.Length; i++) files[i] = byKey[keys[i]];
                byKey = null;
                ct.ThrowIfCancellationRequested();

                // 2. names first in the pool so NameId == pool id and NameEntry index == NameId - 1.
                var nameSet = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in files)
                {
                    if (f.Decls != null) foreach (var d in f.Decls) if (!string.IsNullOrEmpty(d.Name)) nameSet.Add(d.Name);
                    if (f.Occurrences != null) foreach (var o in f.Occurrences) if (!string.IsNullOrEmpty(o.Name)) nameSet.Add(o.Name);
                }
                var names = nameSet.ToArray();
                nameSet = null;
                Array.Sort(names, StringComparer.Ordinal);
                var pool = new StringPoolBuilder();
                foreach (var n in names) pool.Add(n);
                int nameCount = names.Length;
                ct.ThrowIfCancellationRequested();

                // 3. files + symbols in source order.
                var fileRecs = new FileRecord[files.Length];
                var symbols = new List<SymbolRecord>();
                var includes = new List<uint>();
                var symCountPerName = new uint[nameCount];
                var occCountPerName = new uint[nameCount];
                long totalOcc = 0;
                for (int fi = 0; fi < files.Length; fi++)
                {
                    if ((fi & 255) == 0) ct.ThrowIfCancellationRequested();
                    var f = files[fi];
                    var decls = f.Decls ?? Array.Empty<DeclInput>();
                    var order = SortedDeclOrder(decls);
                    ref var fr = ref fileRecs[fi];
                    fr.PathId = pool.Add(f.Path);
                    fr.ModuleId = pool.Add(f.Module);
                    fr.FileNameId = pool.Add(Path.GetFileName(f.Path));
                    fr.Origin = (byte)f.Origin;
                    fr.MtimeTicks = f.MtimeTicks;
                    fr.Length = f.Length;
                    fr.ContentHash = f.ContentHash;
                    fr.FirstSymbol = (uint)symbols.Count;
                    foreach (int di in order)
                    {
                        var d = decls[di];
                        if (string.IsNullOrEmpty(d.Name)) continue;
                        pool.TryGet(d.Name, out uint nameId);
                        symCountPerName[nameId - 1]++;
                        symbols.Add(new SymbolRecord
                        {
                            NameId = nameId,
                            ContainerId = pool.Add(d.Container),
                            FileId = (uint)fi,
                            Offset = (uint)Math.Max(0, d.Offset),
                            SignatureId = pool.Add(d.Signature),
                            SpecifiersId = pool.Add(d.Specifiers),
                            LineColumn = SymbolRecord.PackLineColumn(d.Line, d.Column),
                            Ue = (ushort)d.Ue,
                            Kind = (byte)d.Kind,
                            Flags = (byte)((d.IsDefinition ? SymbolFlags.IsDefinition : 0) | (d.IsConditional ? SymbolFlags.IsConditional : 0)),
                        });
                    }
                    fr.SymbolCount = (uint)symbols.Count - fr.FirstSymbol;
                    fr.FirstInclude = (uint)includes.Count;
                    if (f.Includes != null) foreach (var inc in f.Includes) if (!string.IsNullOrEmpty(inc)) includes.Add(pool.Add(inc));
                    fr.IncludeCount = (uint)includes.Count - fr.FirstInclude;
                    if (f.Occurrences != null)
                    {
                        foreach (var o in f.Occurrences)
                        {
                            if (string.IsNullOrEmpty(o.Name)) continue;
                            pool.TryGet(o.Name, out uint id);
                            occCountPerName[id - 1]++;
                            totalOcc++;
                        }
                    }
                }
                var symArr = symbols.ToArray();
                symbols = null;

                // 4. name -> symbol postings (counting sort keeps source order inside each name).
                var nameEntries = new NameEntry[nameCount];
                uint run = 0;
                for (int n = 0; n < nameCount; n++)
                {
                    nameEntries[n].SymStart = run;
                    nameEntries[n].SymCount = symCountPerName[n];
                    run += symCountPerName[n];
                }
                var nameSymbols = new uint[symArr.Length];
                var fill = new uint[nameCount];
                for (uint s = 0; s < symArr.Length; s++)
                {
                    int n = (int)symArr[s].NameId - 1;
                    nameSymbols[nameEntries[n].SymStart + fill[n]++] = s;
                }
                ct.ThrowIfCancellationRequested();

                // 5. occurrences: bucket by name (files already in order; offsets sorted per file), then delta+varint.
                byte[] occBlob = BuildOccurrences(files, pool, occCountPerName, nameEntries, totalOcc, ct);
                ct.ThrowIfCancellationRequested();

                // 6. lowercase names, trigram + initials postings over names that have symbols.
                var lowerOffsets = new uint[nameCount + 1];
                var lowerData = new MemoryStream();
                var lowerBytes = new byte[nameCount][];
                var searchable = new List<uint>();
                var tri = new Dictionary<ulong, List<uint>>();
                var ini = new Dictionary<uint, List<uint>>();
                var triSet = new HashSet<ulong>();
                var iniSet = new HashSet<uint>();
                for (int n = 0; n < nameCount; n++)
                {
                    string lower = names[n].ToLowerInvariant();
                    var bytes = Encoding.UTF8.GetBytes(lower);
                    lowerData.Write(bytes, 0, bytes.Length);
                    lowerOffsets[n + 1] = (uint)lowerData.Length;
                    if (nameEntries[n].SymCount == 0) continue;
                    lowerBytes[n] = bytes;
                    searchable.Add((uint)n);
                    NameKeys.Trigrams(lower, triSet);
                    foreach (var k in triSet)
                    {
                        if (!tri.TryGetValue(k, out var list)) tri[k] = list = new List<uint>();
                        list.Add((uint)n);
                    }
                    NameKeys.Initials(names[n], iniSet);
                    foreach (var k in iniSet)
                    {
                        if (!ini.TryGetValue(k, out var list)) ini[k] = list = new List<uint>();
                        list.Add((uint)n);
                    }
                }
                // names with symbols ordered by lowercase UTF-8 bytes: prefix ranges by binary search
                var lowerOrder = searchable.ToArray();
                Array.Sort(lowerOrder, (a, b) =>
                {
                    int c = lowerBytes[a].AsSpan().SequenceCompareTo(lowerBytes[b]);
                    return c != 0 ? c : a.CompareTo(b);
                });
                lowerBytes = null;
                FlattenCompressed(tri, out ulong[] triKeys, out uint[] triOffsets, out byte[] triPostings);
                Flatten(ini, out uint[] iniKeys, out uint[] iniOffsets, out uint[] iniPostings);
                tri = null;
                ini = null;

                // 7. lowercase file names for file search; path hash table.
                var fnOffsets = new uint[files.Length + 1];
                var fnData = new MemoryStream();
                for (int i = 0; i < files.Length; i++)
                {
                    var bytes = Encoding.UTF8.GetBytes(Path.GetFileName(files[i].Path).ToLowerInvariant());
                    fnData.Write(bytes, 0, bytes.Length);
                    fnOffsets[i + 1] = (uint)fnData.Length;
                }
                var fileBuckets = BuildFileBuckets(keys);
                ct.ThrowIfCancellationRequested();

                var sections = new List<(SectionId id, byte[] data)>
                {
                    (SectionId.StrOffsets, Bytes(pool.BuildOffsets())),
                    (SectionId.StrData, pool.BuildData()),
                    (SectionId.StrBuckets, Bytes(pool.BuildBuckets())),
                    (SectionId.Files, Bytes(fileRecs)),
                    (SectionId.FileBuckets, Bytes(fileBuckets)),
                    (SectionId.Symbols, Bytes(symArr)),
                    (SectionId.Names, Bytes(nameEntries)),
                    (SectionId.NameSymbols, Bytes(nameSymbols)),
                    (SectionId.Occurrences, occBlob),
                    (SectionId.TrigramKeys, Bytes(triKeys)),
                    (SectionId.TrigramOffsets, Bytes(triOffsets)),
                    (SectionId.TrigramPostings, triPostings),
                    (SectionId.InitialKeys, Bytes(iniKeys)),
                    (SectionId.InitialOffsets, Bytes(iniOffsets)),
                    (SectionId.InitialPostings, Bytes(iniPostings)),
                    (SectionId.NameLowerOffsets, Bytes(lowerOffsets)),
                    (SectionId.NameLowerData, lowerData.ToArray()),
                    (SectionId.FileNameLowerOffsets, Bytes(fnOffsets)),
                    (SectionId.FileNameLowerData, fnData.ToArray()),
                    (SectionId.Includes, Bytes(includes.ToArray())),
                    (SectionId.NameLowerOrder, Bytes(lowerOrder)),
                };

                var info = new WriteInfo { Files = files.Length, Symbols = symArr.Length, Names = nameCount, Occurrences = totalOcc };
                info.Bytes = WriteFile(path, kind, kbVersion, engineBuildId, sections, info, pool.Count);
                L.Info("index.segment.written", "Segment written", ("path", path), ("kind", kind.ToString()), ("files", info.Files),
                    ("symbols", info.Symbols), ("names", info.Names), ("occurrences", info.Occurrences), ("bytes", info.Bytes));
                return info;
            }
        }

        private static int[] SortedDeclOrder(DeclInput[] decls)
        {
            var order = new int[decls.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            // stable by offset
            Array.Sort(order, (a, b) =>
            {
                int c = decls[a].Offset.CompareTo(decls[b].Offset);
                return c != 0 ? c : a.CompareTo(b);
            });
            return order;
        }

        private static byte[] BuildOccurrences(FileSymbols[] files, StringPoolBuilder pool, uint[] countPerName, NameEntry[] entries,
            long total, CancellationToken ct)
        {
            if (total > int.MaxValue) throw new InvalidOperationException("too many occurrences in one segment");
            int nameCount = countPerName.Length;
            var start = new int[nameCount + 1];
            for (int n = 0; n < nameCount; n++) start[n + 1] = start[n] + (int)countPerName[n];
            var occFile = new uint[total];
            var occOffset = new int[total];
            var occRole = new byte[total];
            var fill = new int[nameCount];
            var scratch = new List<OccurrenceInput>();
            for (int fi = 0; fi < files.Length; fi++)
            {
                if ((fi & 255) == 0) ct.ThrowIfCancellationRequested();
                var occ = files[fi].Occurrences;
                if (occ == null || occ.Length == 0) continue;
                scratch.Clear();
                foreach (var o in occ) if (!string.IsNullOrEmpty(o.Name)) scratch.Add(o);
                scratch.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                foreach (var o in scratch)
                {
                    pool.TryGet(o.Name, out uint id);
                    int n = (int)id - 1;
                    int at = start[n] + fill[n]++;
                    occFile[at] = (uint)fi;
                    occOffset[at] = Math.Max(0, o.Offset);
                    occRole[at] = o.Role;
                }
            }

            var ms = new MemoryStream();
            for (int n = 0; n < nameCount; n++)
            {
                if (ms.Length > uint.MaxValue) throw new InvalidOperationException("occurrence stream exceeds 4 GB in one segment");
                entries[n].OccOffset = (uint)ms.Length;
                entries[n].OccCount = countPerName[n];
                uint prevFile = 0;
                int prevOffset = 0;
                for (int i = start[n]; i < start[n + 1]; i++)
                {
                    uint fdelta = occFile[i] - prevFile;
                    Varint.Write(ms, fdelta);
                    Varint.Write(ms, fdelta == 0 && i > start[n] ? (uint)(occOffset[i] - prevOffset) : (uint)occOffset[i]);
                    ms.WriteByte(occRole[i]);
                    prevFile = occFile[i];
                    prevOffset = occOffset[i];
                }
                entries[n].OccLength = (uint)(ms.Length - entries[n].OccOffset);
            }
            return ms.ToArray();
        }

        private static void Flatten<TKey>(Dictionary<TKey, List<uint>> map, out TKey[] keys, out uint[] offsets, out uint[] postings)
            where TKey : unmanaged, IComparable<TKey>
        {
            keys = map.Keys.ToArray();
            Array.Sort(keys);
            offsets = new uint[keys.Length + 1];
            long total = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                total += map[keys[i]].Count;
                offsets[i + 1] = (uint)total;
            }
            postings = new uint[total];
            for (int i = 0; i < keys.Length; i++) map[keys[i]].CopyTo(postings, (int)offsets[i]);
        }

        /// <summary>Postings as delta + varint byte streams; offsets are byte offsets.</summary>
        private static void FlattenCompressed(Dictionary<ulong, List<uint>> map, out ulong[] keys, out uint[] offsets, out byte[] postings)
        {
            keys = map.Keys.ToArray();
            Array.Sort(keys);
            offsets = new uint[keys.Length + 1];
            var ms = new MemoryStream();
            for (int i = 0; i < keys.Length; i++)
            {
                uint prev = 0;
                foreach (var v in map[keys[i]])
                {
                    Varint.Write(ms, v - prev);
                    prev = v;
                }
                offsets[i + 1] = (uint)ms.Length;
            }
            postings = ms.ToArray();
        }

        private static uint[] BuildFileBuckets(string[] sortedKeys)
        {
            int size = 16;
            while (size < sortedKeys.Length * 2) size <<= 1;
            var buckets = new uint[size];
            int mask = size - 1;
            for (int i = 0; i < sortedKeys.Length; i++)
            {
                int b = (int)(StringPoolBuilder.Hash(Encoding.UTF8.GetBytes(sortedKeys[i])) & (uint)mask);
                while (buckets[b] != 0) b = (b + 1) & mask;
                buckets[b] = (uint)i + 1;
            }
            return buckets;
        }

        private static byte[] Bytes<T>(T[] arr) where T : unmanaged => MemoryMarshal.AsBytes(arr.AsSpan()).ToArray();

        private static long WriteFile(string path, SegmentKind kind, string kbVersion, string engineBuildId,
            List<(SectionId id, byte[] data)> sections, WriteInfo info, int stringCount)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            var header = new byte[SegmentFormat.HeaderSize];
            long offset = SegmentFormat.HeaderSize;
            var table = new List<(SectionId id, long off, long len, ulong hash)>();
            foreach (var (id, data) in sections)
            {
                table.Add((id, offset, data.Length, XxHash64.HashToUInt64(data)));
                offset = SegmentFormat.Align(offset + data.Length);
            }
            long fileLength = offset;

            var h = header.AsSpan();
            SegmentFormat.Magic.CopyTo(h);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffVersion), SegmentFormat.Version);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffHeaderSize), (uint)SegmentFormat.HeaderSize);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffKind), (uint)kind);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffSectionCount), (uint)table.Count);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffFileLength), (ulong)fileLength);
            SegmentFormat.WriteShortString(h.Slice(SegmentFormat.OffKbVersion), kbVersion);
            SegmentFormat.WriteShortString(h.Slice(SegmentFormat.OffEngineBuild), engineBuildId);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffCreated), DateTime.UtcNow.Ticks);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffFileCount), (uint)info.Files);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffSymbolCount), (uint)info.Symbols);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffNameCount), (uint)info.Names);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffStringCount), (uint)stringCount);
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffOccurrenceCount), (ulong)info.Occurrences);
            int t = SegmentFormat.OffSectionTable;
            foreach (var e in table)
            {
                BitConverter.TryWriteBytes(h.Slice(t), (uint)e.id);
                BitConverter.TryWriteBytes(h.Slice(t + 8), (ulong)e.off);
                BitConverter.TryWriteBytes(h.Slice(t + 16), (ulong)e.len);
                BitConverter.TryWriteBytes(h.Slice(t + 24), e.hash);
                t += SegmentFormat.SectionEntrySize;
            }
            BitConverter.TryWriteBytes(h.Slice(SegmentFormat.OffHeaderChecksum), XxHash64.HashToUInt64(header));

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                fs.Write(header, 0, header.Length);
                for (int i = 0; i < sections.Count; i++)
                {
                    fs.Position = table[i].off;
                    fs.Write(sections[i].data, 0, sections[i].data.Length);
                }
                fs.SetLength(fileLength);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
            return fileLength;
        }
    }
}
