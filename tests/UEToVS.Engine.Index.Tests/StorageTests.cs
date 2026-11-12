using System;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Index.Storage;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    public class StorageTests
    {
        [Fact]
        public void StringPool_RoundTrip()
        {
            var b = new StringPoolBuilder();
            Assert.Equal(0u, b.Add(null));
            Assert.Equal(0u, b.Add(""));
            uint a = b.Add("AActor");
            uint u = b.Add("Ünïcödé 名前");
            Assert.Equal(a, b.Add("AActor"));
            var words = Enumerable.Range(0, 5000).Select(i => "Name" + i).ToArray();
            var ids = words.Select(b.Add).ToArray();

            var ms = new MemoryStream();
            b.WriteTo(ms);
            ms.Position = 0;
            var table = StringTable.ReadFrom(ms);
            var v = table.View;
            Assert.Equal(b.Count, v.Count);
            Assert.Equal("AActor", v.GetString(a));
            Assert.Equal("Ünïcödé 名前", v.GetString(u));
            Assert.Null(v.GetString(0));
            Assert.Equal(u, v.Find("Ünïcödé 名前"));
            Assert.Equal(0u, v.Find("aactor")); // exact lookup is case-sensitive
            Assert.Equal(0u, v.Find("Missing"));
            for (int i = 0; i < words.Length; i++)
            {
                Assert.Equal(ids[i], v.Find(words[i]));
                Assert.Equal(words[i], v.GetString(ids[i]));
            }
        }

        [Fact]
        public void Varint_RoundTrip()
        {
            var ms = new MemoryStream();
            uint[] values = { 0, 1, 127, 128, 300, 16383, 16384, uint.MaxValue };
            foreach (var x in values) Varint.Write(ms, x);
            var data = ms.ToArray();
            int pos = 0;
            foreach (var x in values) Assert.Equal(x, Varint.Read(data, ref pos));
            Assert.Equal(data.Length, pos);
        }

        [Fact]
        public void Records_HaveDocumentedSizes()
        {
            Assert.Equal(32, System.Runtime.CompilerServices.Unsafe.SizeOf<SymbolRecord>());
            Assert.Equal(56, System.Runtime.CompilerServices.Unsafe.SizeOf<FileRecord>());
        }

        private static FileSymbols[] Sample() => new[]
        {
            T.F("/proj/Source/Game/MyActor.h", ItemOrigin.Project,
                new[]
                {
                    T.D("AMyActor", SymbolKind.Class, ue: UeFlags.UClass, spec: "Blueprintable", line: 10, col: 6),
                    T.D("Tick", SymbolKind.Method, "AMyActor", offset: 500, line: 20, col: 4, sig: "virtual void Tick(float DeltaSeconds) override"),
                    T.D("Health", SymbolKind.Field, "AMyActor", offset: 300, line: 15, ue: UeFlags.UProperty),
                },
                new[] { T.O("AActor", 120, 1), T.O("Tick", 520), T.O("AActor", 40, 2), T.O("FVector", 700) },
                new[] { "CoreMinimal.h", "MyActor.generated.h" }),
            T.F("/proj/Source/Game/MyActor.cpp", ItemOrigin.Project,
                new[] { T.D("Tick", SymbolKind.Method, "AMyActor", offset: 200, line: 30, def: true) },
                new[] { T.O("Tick", 200), T.O("AActor", 10) }),
            T.F("/engine/Source/Runtime/Engine/Actor.h", ItemOrigin.Engine,
                new[] { T.D("AActor", SymbolKind.Class, ue: UeFlags.UClass), T.D("Tick", SymbolKind.Method, "AActor", offset: 900, line: 99) },
                new[] { T.O("AActor", 5) }, module: "Engine"),
        };

        [Fact]
        public void Segment_WriteRead_RoundTrip()
        {
            using var dir = new TempDir();
            string path = Path.Combine(dir.Path, "test.seg");
            var info = SegmentWriter.Write(path, SegmentKind.Project, "kb1", "build1", Sample(), CancellationToken.None);
            Assert.Equal(3, info.Files);
            Assert.Equal(6, info.Symbols);
            Assert.Equal(0, new FileInfo(path).Length % SegmentFormat.PageSize);

            var status = Segment.TryOpen(path, "kb1", "build1", true, out var seg, out string detail);
            Assert.True(status == SegmentLoadStatus.Ok, detail);
            try
            {
                Assert.Equal(SegmentKind.Project, seg.Kind);
                Assert.Equal("kb1", seg.KnowledgeBaseVersion);
                Assert.Equal(3, seg.FileCount);
                Assert.Equal(6, seg.SymbolCount);
                Assert.Equal(7, seg.OccurrenceCount);

                // files sorted by normalised path
                var paths = Enumerable.Range(0, seg.FileCount).Select(i => seg.GetString(seg.Files[i].PathId)).ToArray();
                Assert.Equal(paths.OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal), paths);

                int fi = seg.FindFile(PathKey.Normalize(@"\PROJ\Source\Game\MyActor.h"));
                Assert.True(fi >= 0);
                var f = seg.Files[fi];
                Assert.Equal(3u, f.SymbolCount);
                Assert.Equal(ItemOrigin.Project, f.ItemOrigin);
                Assert.Equal("Game", seg.GetString(f.ModuleId));
                // symbols of a file are in source order
                var names = Enumerable.Range((int)f.FirstSymbol, (int)f.SymbolCount).Select(i => seg.GetString(seg.Symbols[i].NameId)).ToArray();
                Assert.Equal(new[] { "AMyActor", "Health", "Tick" }, names);
                var tick = seg.Symbols[(int)f.FirstSymbol + 2];
                Assert.Equal(20, tick.Line);
                Assert.Equal(4, tick.Column);
                Assert.Equal("virtual void Tick(float DeltaSeconds) override", seg.GetString(tick.SignatureId));
                var inc = seg.Includes.Slice((int)f.FirstInclude, (int)f.IncludeCount).ToArray().Select(seg.GetString).ToArray();
                Assert.Equal(new[] { "CoreMinimal.h", "MyActor.generated.h" }, inc);

                // name postings
                int n = seg.FindName("Tick");
                Assert.True(n >= 0);
                Assert.Equal(3, seg.SymbolsOfName(n).Length);
                Assert.Equal(-1, seg.FindName("tick"));

                // occurrence postings decode in (file, offset) order
                int an = seg.FindName("AActor");
                var reader = seg.ReadOccurrences(an);
                var list = new System.Collections.Generic.List<(uint, int, byte)>();
                while (reader.Next(out uint file, out int off, out byte role)) list.Add((file, off, role));
                Assert.Equal(4, list.Count);
                Assert.Equal(list.OrderBy(x => x.Item1).ThenBy(x => x.Item2), list);
                Assert.Contains(list, x => x.Item2 == 40 && x.Item3 == 2);

                // acceleration keys present
                Assert.True(seg.InitialPostings('a').Length > 0);
                Assert.True(seg.TrigramPostings(NameKeys.Trigram('t', 'i', 'c')).Length > 0);
            }
            finally
            {
                seg.Release();
            }
        }

        private static string WriteSample(TempDir dir, string kb = "kb1", string build = "b1", SegmentKind kind = SegmentKind.Engine)
        {
            string path = Path.Combine(dir.Path, Guid.NewGuid().ToString("N") + ".seg");
            SegmentWriter.Write(path, kind, kb, build, Sample(), CancellationToken.None);
            return path;
        }

        private static SegmentLoadStatus Open(string path, string kb = "kb1", string build = "b1", bool verify = true)
        {
            var st = Segment.TryOpen(path, kb, build, verify, out var seg, out _);
            seg?.Release();
            return st;
        }

        [Fact]
        public void Segment_DetectsCorruptSection()
        {
            using var dir = new TempDir();
            string path = WriteSample(dir);
            var bytes = File.ReadAllBytes(path);
            bytes[SegmentFormat.PageSize * 3 + 5] ^= 0xFF; // inside a section
            File.WriteAllBytes(path, bytes);
            Assert.Equal(SegmentLoadStatus.Corrupt, Open(path));
        }

        [Fact]
        public void Segment_DetectsCorruptHeader_Truncation_BadMagic()
        {
            using var dir = new TempDir();
            string p1 = WriteSample(dir);
            var b1 = File.ReadAllBytes(p1);
            b1[SegmentFormat.OffFileCount] ^= 1;
            File.WriteAllBytes(p1, b1);
            Assert.Equal(SegmentLoadStatus.Corrupt, Open(p1));

            string p2 = WriteSample(dir);
            var b2 = File.ReadAllBytes(p2);
            File.WriteAllBytes(p2, b2.AsSpan(0, b2.Length - SegmentFormat.PageSize).ToArray());
            Assert.Equal(SegmentLoadStatus.Corrupt, Open(p2));

            string p3 = WriteSample(dir);
            var b3 = File.ReadAllBytes(p3);
            b3[0] = (byte)'X';
            File.WriteAllBytes(p3, b3);
            Assert.Equal(SegmentLoadStatus.Corrupt, Open(p3));

            string p4 = Path.Combine(dir.Path, "tiny.seg");
            File.WriteAllBytes(p4, new byte[10]);
            Assert.Equal(SegmentLoadStatus.Corrupt, Open(p4));

            Assert.Equal(SegmentLoadStatus.Missing, Open(Path.Combine(dir.Path, "nope.seg")));
        }

        [Fact]
        public void Segment_DetectsVersionMismatches()
        {
            using var dir = new TempDir();
            string path = WriteSample(dir, kb: "kb1", build: "b1", kind: SegmentKind.Engine);
            Assert.Equal(SegmentLoadStatus.Ok, Open(path));
            Assert.Equal(SegmentLoadStatus.KnowledgeBaseMismatch, Open(path, kb: "kb2"));
            Assert.Equal(SegmentLoadStatus.EngineBuildMismatch, Open(path, build: "b2"));

            // project segments do not care about the engine build
            string proj = WriteSample(dir, kind: SegmentKind.Project);
            Assert.Equal(SegmentLoadStatus.Ok, Open(proj, build: "other"));

            var bytes = File.ReadAllBytes(path);
            BitConverter.TryWriteBytes(bytes.AsSpan(SegmentFormat.OffVersion), SegmentFormat.Version + 1);
            File.WriteAllBytes(path, bytes);
            Assert.Equal(SegmentLoadStatus.FormatVersionMismatch, Open(path));
        }

        [Fact]
        public void Segment_EmptyInput_IsValid()
        {
            using var dir = new TempDir();
            string path = Path.Combine(dir.Path, "empty.seg");
            SegmentWriter.Write(path, SegmentKind.Project, "kb1", "b1", Array.Empty<FileSymbols>(), CancellationToken.None);
            Assert.Equal(SegmentLoadStatus.Ok, Open(path));
        }
    }
}
