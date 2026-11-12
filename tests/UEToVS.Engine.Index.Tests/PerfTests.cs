using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UEToVS.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Index.Tests
{
    /// <summary>Synthetic UE-sized corpus. Run alone with: dotnet test --filter Category=Perf</summary>
    [Trait("Category", "Perf")]
    public class PerfTests
    {
        private readonly ITestOutputHelper _out;

        public PerfTests(ITestOutputHelper output) => _out = output;

        private static readonly string[] Words =
        {
            "Actor", "Component", "Player", "Camera", "Update", "Controller", "Pawn", "Character", "Movement", "Input", "Widget", "Slate",
            "Render", "Scene", "Proxy", "Mesh", "Static", "Skeletal", "Anim", "Instance", "Blueprint", "Function", "Library", "World",
            "Level", "Streaming", "Volume", "Light", "Shadow", "Material", "Texture", "Shader", "Compute", "Buffer", "Vertex", "Index",
            "Physics", "Body", "Constraint", "Collision", "Query", "Trace", "Hit", "Result", "Damage", "Event", "Delegate", "Multicast",
            "Timer", "Manager", "Subsystem", "Game", "Mode", "State", "Session", "Online", "Net", "Driver", "Connection", "Channel",
            "Replication", "Graph", "Node", "Pin", "Editor", "Asset", "Registry", "Package", "Object", "Class", "Property", "Struct",
            "Enum", "Interface", "Async", "Task", "Thread", "Pool", "Allocator", "Memory", "Stats", "Profiler", "Audio", "Sound", "Cue",
            "Wave", "Niagara", "Particle", "Emitter", "System", "Data", "Table", "Row", "Curve", "Float", "Vector", "Rotator",
            "Transform", "Quat", "Matrix", "Box", "Sphere", "Capsule", "Navigation", "Path", "Ability", "Effect", "Attribute", "Tag",
            "Spec", "Handle", "Context", "Settings", "Config", "Cache", "Loader", "Factory", "Builder", "Visitor", "Iterator", "Array",
            "Map", "Set", "String", "Name", "Text", "Archive", "Serialize", "Begin", "End", "Play", "Tick", "Init", "Shutdown", "Get",
            "Set", "Is", "Has", "Can", "Should", "Find", "Create", "Destroy", "Spawn", "Attach", "Detach", "Register", "Unregister",
            "Apply", "Remove", "Add", "Clear", "Reset", "Load", "Save", "Begin", "Handle", "On", "Pre", "Post", "Internal",
        };

        private static readonly string[] TypePrefixes = { "A", "U", "F", "F", "F", "E", "I", "T", "S" };

        private static string Compose(Random r, int min, int max)
        {
            int n = r.Next(min, max + 1);
            var parts = new string[n];
            for (int i = 0; i < n; i++) parts[i] = Words[r.Next(Words.Length)];
            return string.Concat(parts);
        }

        private static IEnumerable<FileSymbols> Generate(int seed, int fileCount, int startIndex, ItemOrigin origin, int symbolsPerFile,
            List<string> sampleNames, List<(string container, string name)> sampleMembers)
        {
            var r = new Random(seed);
            for (int fi = 0; fi < fileCount; fi++)
            {
                int n = symbolsPerFile + r.Next(-10, 11);
                var decls = new DeclInput[n];
                var occ = new List<OccurrenceInput>();
                string type = TypePrefixes[r.Next(TypePrefixes.Length)] + Compose(r, 1, 3) + (r.Next(4) == 0 ? (fi + startIndex).ToString() : "");
                decls[0] = new DeclInput { Name = type, Kind = SymbolKind.Class, Ue = UeFlags.UClass, Offset = 10, Line = 5 };
                for (int i = 1; i < n; i++)
                {
                    int roll = r.Next(10);
                    bool isType = roll == 0;
                    string name = isType ? TypePrefixes[r.Next(TypePrefixes.Length)] + Compose(r, 1, 3) : Compose(r, 1, 4);
                    decls[i] = new DeclInput
                    {
                        Name = name,
                        Container = isType ? null : type,
                        Kind = isType ? SymbolKind.Struct : roll < 7 ? SymbolKind.Method : SymbolKind.Field,
                        Ue = roll == 3 ? UeFlags.UFunction : roll == 8 ? UeFlags.UProperty : UeFlags.None,
                        Signature = roll < 7 && !isType ? "void " + name + "(int32 Value)" : null,
                        Offset = 100 + i * 80,
                        Line = 10 + i * 3,
                        Column = 4,
                        IsDefinition = r.Next(2) == 0,
                    };
                    if (sampleNames != null && r.Next(400) == 0) sampleNames.Add(name);
                    if (sampleMembers != null && !isType && r.Next(800) == 0) sampleMembers.Add((type, name));
                }
                for (int k = 0; k < 60; k++)
                {
                    var target = decls[r.Next(n)].Name;
                    occ.Add(new OccurrenceInput { Name = target, Offset = 50 + k * 37, Role = (byte)r.Next(3) });
                }
                string module = "Module" + ((fi + startIndex) / 50);
                yield return new FileSymbols
                {
                    Path = $"/{(origin == ItemOrigin.Engine ? "ue/Engine/Source/Runtime" : "proj/Source")}/{module}/Private/{type}_{fi + startIndex}.h",
                    Module = module,
                    Origin = origin,
                    MtimeTicks = 638000000000000000L + fi,
                    Length = 4000 + n * 80,
                    ContentHash = (ulong)r.NextInt64(),
                    Decls = decls,
                    Occurrences = occ.ToArray(),
                    Includes = new[] { "CoreMinimal.h", type + ".generated.h" },
                };
            }
        }

        private static List<string> BuildQueries(Random r, List<string> names, List<(string container, string name)> members, int count)
        {
            var q = new List<string>();
            while (q.Count < count)
            {
                string name = names[r.Next(names.Count)];
                int roll = r.Next(100);
                if (roll < 20) q.Add(name); // exact
                else if (roll < 45) q.Add(name.Substring(0, Math.Min(name.Length, r.Next(3, 9)))); // prefix while typing
                else if (roll < 65)
                {
                    // camel humps: initials of the words
                    var initials = new string(name.Where(char.IsUpper).ToArray());
                    q.Add(initials.Length >= 2 ? initials : name.Substring(0, Math.Min(3, name.Length)));
                }
                else if (roll < 80)
                {
                    int s = r.Next(1, Math.Max(2, name.Length - 4));
                    q.Add(name.Substring(s, Math.Min(4, name.Length - s)).ToLowerInvariant()); // substring
                }
                else if (roll < 88) q.Add(name.Substring(0, Math.Min(name.Length, r.Next(1, 3)))); // 1-2 chars
                else if (roll < 96 && members.Count > 0)
                {
                    var (c, n) = members[r.Next(members.Count)];
                    q.Add(c + "::" + n.Substring(0, Math.Min(n.Length, 5)));
                }
                else q.Add(Words[r.Next(Words.Length)] + "*" + Words[r.Next(Words.Length)]);
            }
            return q;
        }

        [Fact]
        public void Perf_250kSymbols_6kFiles()
        {
            using var dir = new TempDir();
            var names = new List<string>();
            var members = new List<(string, string)>();
            var sw = Stopwatch.StartNew();
            using (var idx = T.Open(dir.Path))
            {
                idx.ReplaceBase(SegmentKind.Engine, Generate(1, 4000, 0, ItemOrigin.Engine, 42, names, members));
                idx.ReplaceBase(SegmentKind.Project, Generate(2, 2000, 4000, ItemOrigin.Project, 42, names, members));
            }
            double buildMs = sw.Elapsed.TotalMilliseconds;
            long diskBytes = Directory.GetFiles(dir.Path, "*.seg").Sum(f => new FileInfo(f).Length);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); // decommit freed corpus memory
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            long privBefore = proc.PrivateMemorySize64;
            long managedBefore = GC.GetTotalMemory(true);

            sw.Restart();
            using var index = SymbolIndex.Open(new IndexOptions { Directory = dir.Path, KnowledgeBaseVersion = "kb1", EngineBuildId = "ue5.4-1", VerifyChecksums = false }, out var openResult);
            double openMs = sw.Elapsed.TotalMilliseconds;
            proc.Refresh();
            long privOpened = proc.PrivateMemorySize64;
            Assert.False(openResult.NeedsEngineRebuild);
            var stats = index.Stats;

            var queries = BuildQueries(new Random(7), names, members, 1000);
            // warm-up (JIT)
            foreach (var q in queries.Take(50)) index.Search(new SearchRequest { Pattern = q, IncludeNonProject = true });

            var times = new List<double>(queries.Count);
            var perQuery = new List<(double ms, string q, int hits)>();
            int totalHits = 0;
            int i = 0;
            foreach (var q in queries)
            {
                var req = new SearchRequest { Pattern = q, IncludeNonProject = (i++ & 1) == 0, MaxResultsPerCategory = 100 };
                var t0 = Stopwatch.GetTimestamp();
                var resp = index.Search(req);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                times.Add(ms);
                perQuery.Add((ms, q, resp.Hits.Count));
                totalHits += resp.Hits.Count;
            }
            times.Sort();
            double p50 = times[times.Count / 2], p95 = times[(int)(times.Count * 0.95)], max = times[^1];

            // exact lookups
            sw.Restart();
            int found = 0;
            foreach (var n in names.Take(1000)) found += index.FindByName(n).Count;
            double findMs = sw.Elapsed.TotalMilliseconds;

            proc.Refresh();
            long privAfter = proc.PrivateMemorySize64;
            long managedAfter = GC.GetTotalMemory(true);
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            proc.Refresh();
            long privSettled = proc.PrivateMemorySize64;

            _out.WriteLine($"corpus: {stats}");
            using (var snap = index.AcquireSnapshot())
            {
                var sizes = snap.Inner.Bases.SelectMany(b => b.Segment.SectionSizes()).GroupBy(x => x.Id)
                    .Select(g => (g.Key, Bytes: g.Sum(x => x.Bytes))).OrderByDescending(x => x.Bytes);
                _out.WriteLine("sections: " + string.Join(", ", sizes.Select(x => $"{x.Key}={x.Bytes / 1024}K")));
            }
            _out.WriteLine($"build (2 segments): {buildMs:F0} ms; segments on disk: {diskBytes / 1024.0 / 1024.0:F1} MB; open: {openMs:F1} ms");
            _out.WriteLine($"search x{times.Count}: p50 {p50:F2} ms, p95 {p95:F2} ms, max {max:F2} ms, avg hits {totalHits / (double)times.Count:F1}");
            foreach (var s in perQuery.OrderByDescending(x => x.ms).Take(12)) _out.WriteLine($"  slow: {s.ms,7:F1} ms  '{s.q}' hits={s.hits}");
            _out.WriteLine($"FindByName x{Math.Min(1000, names.Count)}: {findMs:F1} ms total ({found} symbols)");
            _out.WriteLine($"process private memory: before open {privBefore / 1048576.0:F1} MB, after open {privOpened / 1048576.0:F1} MB, after open+queries {privAfter / 1048576.0:F1} MB " +
                           $"(delta {(privAfter - privBefore) / 1048576.0:F1} MB), after GC {privSettled / 1048576.0:F1} MB; managed heap {managedBefore / 1048576.0:F1} -> {managedAfter / 1048576.0:F1} MB");

            Assert.InRange(stats.SymbolCount, 240_000, 260_000);
            Assert.Equal(6000, stats.FileCount);
            Assert.True(found >= Math.Min(1000, names.Count));
            Assert.True(p95 < 100, $"search p95 {p95:F1} ms"); // target < 30 ms (Release); loose bound for debug/CI noise
        }
    }
}
