using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using Xunit;

namespace UEToVS.Engine.Index.Tests
{
    public class IndexTests
    {
        internal static FileSymbols[] EngineFiles() => new[]
        {
            T.F("/ue/Engine/Source/Runtime/Engine/Classes/GameFramework/Actor.h", ItemOrigin.Engine,
                new[]
                {
                    T.D("AActor", SymbolKind.Class, ue: UeFlags.UClass),
                    T.D("Tick", SymbolKind.Method, "AActor", sig: "virtual void Tick(float DeltaSeconds)"),
                    T.D("BeginPlay", SymbolKind.Method, "AActor", ue: UeFlags.UFunction),
                },
                new[] { T.O("AActor", 10), T.O("Tick", 50) }, module: "Engine"),
            T.F("/ue/Engine/Source/Runtime/Engine/Classes/GameFramework/Pawn.h", ItemOrigin.Engine,
                new[] { T.D("APawn", SymbolKind.Class, ue: UeFlags.UClass), T.D("Tick", SymbolKind.Method, "APawn") },
                new[] { T.O("AActor", 20), T.O("APawn", 30) }, module: "Engine"),
            T.F("/ue/Engine/Source/Runtime/Engine/Classes/Camera/PlayerCameraManager.h", ItemOrigin.Engine,
                new[] { T.D("APlayerCameraManager", SymbolKind.Class, ue: UeFlags.UClass), T.D("UpdateCamera", SymbolKind.Method, "APlayerCameraManager") },
                null, module: "Engine"),
        };

        internal static FileSymbols[] ProjectFiles() => new[]
        {
            T.F("/proj/Source/Game/MyActor.h", ItemOrigin.Project,
                new[]
                {
                    T.D("AMyActor", SymbolKind.Class, ue: UeFlags.UClass),
                    T.D("Tick", SymbolKind.Method, "AMyActor"),
                    T.D("Health", SymbolKind.Field, "AMyActor", ue: UeFlags.UProperty),
                },
                new[] { T.O("AActor", 5), T.O("AMyActor", 40), T.O("Tick", 90) }),
            T.F("/proj/Source/Game/MyActor.cpp", ItemOrigin.Project,
                new[] { T.D("Tick", SymbolKind.Method, "AMyActor", def: true) },
                new[] { T.O("AMyActor", 3), T.O("Tick", 11), T.O("Health", 60) }, new[] { "MyActor.h" }),
            T.F("/proj/Source/Game/MyPawn.h", ItemOrigin.Project,
                new[] { T.D("AMyPawn", SymbolKind.Class, ue: UeFlags.UClass) },
                new[] { T.O("APawn", 7) }),
        };

        internal static SymbolIndex BuildIndex(string dir)
        {
            var idx = T.Open(dir);
            idx.ReplaceBase(SegmentKind.Engine, EngineFiles());
            idx.ReplaceBase(SegmentKind.Project, ProjectFiles());
            return idx;
        }

        [Fact]
        public void FreshIndex_ReportsRebuildNeeded()
        {
            using var dir = new TempDir();
            using var idx = SymbolIndex.Open(T.Options(dir.Path), out var r);
            Assert.True(r.ManifestMissing);
            Assert.True(r.NeedsEngineRebuild);
            Assert.True(r.NeedsProjectRebuild);
            Assert.Empty(idx.Search(T.Req("Actor")).Hits);
            Assert.True(File.Exists(Path.Combine(dir.Path, "manifest.json")));
        }

        [Fact]
        public void BasicQueries_AndReopen()
        {
            using var dir = new TempDir();
            using (var idx = BuildIndex(dir.Path))
            {
                var ticks = idx.FindByName("Tick");
                Assert.Equal(4, ticks.Count);
                Assert.Single(idx.FindByName("Tick", "AActor"));
                Assert.Equal(2, idx.FindByName("Tick", "AMyActor").Count);

                var fs = idx.GetFileSymbols(@"\proj\source\game\myactor.h");
                Assert.Equal(new[] { "AMyActor", "Tick", "Health" }, fs.Select(s => s.Name));
                Assert.Equal("/proj/Source/Game/MyActor.h", fs[0].Location.FilePath);

                var file = idx.GetFile("/proj/Source/Game/MyActor.cpp");
                Assert.NotNull(file);
                Assert.Equal(new[] { "MyActor.h" }, file.Includes);
                Assert.False(file.InDelta);
                Assert.Null(idx.GetFile("/proj/none.h"));

                Assert.Equal(6, idx.ListFiles().Count());
                var occ = idx.FindOccurrences("AActor").ToList();
                Assert.Equal(3, occ.Count);
                // engine segment first, FileId order inside
                Assert.Equal(new[] { "Actor.h", "Pawn.h", "MyActor.h" }, occ.Select(o => Path.GetFileName(o.Path)));

                var st = idx.Stats;
                Assert.Equal(2, st.SegmentCount);
                Assert.Equal(6, st.FileCount);
                Assert.Equal(12, st.SymbolCount);
                Assert.True(st.BytesOnDisk > 0);
            }

            using (var idx = SymbolIndex.Open(T.Options(dir.Path), out var r))
            {
                Assert.False(r.NeedsEngineRebuild);
                Assert.False(r.NeedsProjectRebuild);
                Assert.All(r.Segments, s => Assert.Equal(SegmentLoadStatus.Ok, s.Status));
                Assert.Equal(4, idx.FindByName("Tick").Count);
            }
        }

        [Fact]
        public void CorruptSegment_IsDroppedAndReported()
        {
            using var dir = new TempDir();
            BuildIndex(dir.Path).Dispose();
            var proj = Directory.GetFiles(dir.Path, "project-*.seg").Single();
            var bytes = File.ReadAllBytes(proj);
            bytes[bytes.Length / 2] ^= 0x5A;
            bytes[4096 + 1] ^= 0x5A;
            File.WriteAllBytes(proj, bytes);

            using var idx = SymbolIndex.Open(T.Options(dir.Path), out var r);
            Assert.False(r.NeedsEngineRebuild);
            Assert.True(r.NeedsProjectRebuild);
            Assert.Contains(r.Segments, s => s.Kind == SegmentKind.Project && s.Status == SegmentLoadStatus.Corrupt);
            Assert.False(File.Exists(proj));
            // engine data is still there
            Assert.Single(idx.FindByName("AActor"));
            Assert.Empty(idx.FindByName("AMyActor"));
        }

        [Fact]
        public void EngineBuildChange_RebuildsEngineOnly()
        {
            using var dir = new TempDir();
            BuildIndex(dir.Path).Dispose();
            using var idx = SymbolIndex.Open(T.Options(dir.Path, build: "ue5.5-2"), out var r);
            Assert.True(r.NeedsEngineRebuild);
            Assert.False(r.NeedsProjectRebuild);
            Assert.Contains(r.Segments, s => s.Status == SegmentLoadStatus.EngineBuildMismatch);

        }

        [Fact]
        public void KnowledgeBaseChange_RebuildsEverything()
        {
            using var dir = new TempDir();
            BuildIndex(dir.Path).Dispose();
            using var idx = SymbolIndex.Open(T.Options(dir.Path, kb: "kb2"), out var r);
            Assert.True(r.NeedsEngineRebuild);
            Assert.True(r.NeedsProjectRebuild);
            Assert.All(r.Segments, s => Assert.Equal(SegmentLoadStatus.KnowledgeBaseMismatch, s.Status));
            Assert.Empty(Directory.GetFiles(dir.Path, "*.seg"));
        }

        [Fact]
        public void DeltaUpdate_OverridesBase()
        {
            using var dir = new TempDir();
            using var idx = BuildIndex(dir.Path);
            using var before = idx.AcquireSnapshot();

            idx.Update(T.F("/proj/Source/Game/MyActor.h", ItemOrigin.Project,
                new[] { T.D("AMyActor", SymbolKind.Class, ue: UeFlags.UClass), T.D("Mana", SymbolKind.Field, "AMyActor") },
                new[] { T.O("AMyActor", 1) }));

            Assert.Empty(idx.FindByName("Health"));
            Assert.Single(idx.FindByName("Mana"));
            Assert.Single(idx.FindByName("AMyActor"));
            Assert.Equal(new[] { "AMyActor", "Mana" }, idx.GetFileSymbols("/proj/Source/Game/MyActor.h").Select(s => s.Name));
            Assert.True(idx.GetFile("/proj/Source/Game/MyActor.h").InDelta);
            Assert.Equal(6, idx.ListFiles().Count());
            Assert.DoesNotContain(idx.FindOccurrences("Tick"), o => o.Path == "/proj/Source/Game/MyActor.h");
            Assert.Contains(idx.Search(T.Req("Mana")).Hits, h => h.Symbol?.Name == "Mana");
            Assert.DoesNotContain(idx.Search(T.Req("Health")).Hits, h => h.Symbol?.Name == "Health");

            // an older snapshot is unaffected
            Assert.Single(before.FindByName("Health"));
            Assert.Empty(before.FindByName("Mana"));

            var st = idx.Stats;
            Assert.Equal(1, st.DeltaFileCount);
            Assert.Equal(2, st.DeltaSymbolCount);
            Assert.True(st.DeltaBytes > 0);
            Assert.Equal(1, st.TombstoneCount);
            Assert.Equal(6, st.FileCount);
        }

        [Fact]
        public void Remove_TombstonesBaseAndDelta()
        {
            using var dir = new TempDir();
            using (var idx = BuildIndex(dir.Path))
            {
                Assert.True(idx.Remove("/ue/Engine/Source/Runtime/Engine/Classes/GameFramework/Pawn.h"));
                Assert.False(idx.Remove("/ue/nothing.h"));
                Assert.Empty(idx.FindByName("APawn"));
                Assert.Null(idx.GetFile("/ue/Engine/Source/Runtime/Engine/Classes/GameFramework/Pawn.h"));
                Assert.DoesNotContain(idx.FindOccurrences("AActor"), o => o.Path.EndsWith("Pawn.h"));
                Assert.DoesNotContain(idx.Search(T.Req("APawn")).Hits, h => h.Symbol?.Name == "APawn");
                Assert.DoesNotContain(idx.Search(T.Req("Pawn.h")).Hits, h => h.File?.Name == "Pawn.h");

                idx.Update(T.F("/proj/Source/Game/New.h", ItemOrigin.Project, T.D("ANew")));
                Assert.Single(idx.FindByName("ANew"));
                Assert.True(idx.Remove("/proj/Source/Game/New.h"));
                Assert.Empty(idx.FindByName("ANew"));
                idx.Flush();
            }

            // removal tombstones are persisted
            using (var idx = T.Open(dir.Path))
            {
                Assert.Empty(idx.FindByName("APawn"));
                Assert.Single(idx.FindByName("AActor"));
            }
        }

        [Fact]
        public void UnpersistedDeltaUpdates_RevertOnReopen()
        {
            using var dir = new TempDir();
            using (var idx = BuildIndex(dir.Path))
            {
                idx.Update(T.F("/proj/Source/Game/MyPawn.h", ItemOrigin.Project, T.D("AMyPawn2")));
                idx.Flush();
            }
            using (var idx = T.Open(dir.Path))
            {
                Assert.Single(idx.FindByName("AMyPawn"));
                Assert.Empty(idx.FindByName("AMyPawn2"));
            }
        }

        private static string Fingerprint(SymbolIndex idx)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in new[] { "Tick", "Actor", "AMy", "PCM", "my", "a", "Health", "Mana", "My*h", "Up", "act" })
            {
                sb.AppendLine("search " + p).AppendLine(T.Describe(idx.Search(T.Req(p))));
                sb.AppendLine(T.Describe(idx.Search(T.Req(p, nonProject: false))));
            }
            sb.AppendLine(T.Describe(idx.Search(new SearchRequest { Pattern = "Tick", Container = "AMyActor", IncludeNonProject = true })));
            var files = idx.ListFiles().Select(T.Describe).OrderBy(x => x, StringComparer.Ordinal).ToList();
            files.ForEach(f => sb.AppendLine(f));
            foreach (var f in idx.ListFiles().Select(f => f.Path).OrderBy(x => x, StringComparer.Ordinal))
                foreach (var s in idx.GetFileSymbols(f)) sb.AppendLine(T.Describe(s));
            foreach (var n in new[] { "Tick", "AActor", "AMyActor", "Health", "APawn", "Mana", "Spell" })
            {
                foreach (var s in idx.FindByName(n).Select(T.Describe).OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(s);
                foreach (var o in idx.FindOccurrences(n).Select(o => o.ToString()).OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine(o);
            }
            return sb.ToString();
        }

        private static void MutateAfterBase(SymbolIndex idx)
        {
            idx.Update(T.F("/proj/Source/Game/MyActor.h", ItemOrigin.Project,
                new[] { T.D("AMyActor", SymbolKind.Class, ue: UeFlags.UClass), T.D("Mana", SymbolKind.Field, "AMyActor"), T.D("Tick", SymbolKind.Method, "AMyActor") },
                new[] { T.O("AMyActor", 1), T.O("Mana", 70) }, new[] { "CoreMinimal.h" }));
            idx.Update(T.F("/proj/Source/Game/Spell.h", ItemOrigin.Project,
                new[] { T.D("FSpell", SymbolKind.Struct, ue: UeFlags.UStruct), T.D("Cast", SymbolKind.Method, "FSpell") },
                new[] { T.O("Spell", 3), T.O("Mana", 9) }));
            // an engine file edited locally: moves into the project segment on compaction, engine copy stays hidden
            idx.Update(T.F("/ue/Engine/Source/Runtime/Engine/Classes/Camera/PlayerCameraManager.h", ItemOrigin.Engine,
                new[] { T.D("APlayerCameraManager", SymbolKind.Class, ue: UeFlags.UClass), T.D("UpdateCameraNew", SymbolKind.Method, "APlayerCameraManager") },
                null, module: "Engine"));
            idx.Remove("/proj/Source/Game/MyPawn.h");
            idx.Remove("/ue/Engine/Source/Runtime/Engine/Classes/GameFramework/Pawn.h");
        }

        [Fact]
        public void Compaction_IsEquivalent_AndPersists()
        {
            using var dir = new TempDir();
            string before, after, reopened;
            using (var idx = BuildIndex(dir.Path))
            {
                MutateAfterBase(idx);
                before = Fingerprint(idx);
                var oldProject = Directory.GetFiles(dir.Path, "project-*.seg").Single();
                Assert.True(idx.Compact());
                after = Fingerprint(idx);
                Assert.Equal(before, after);
                var st = idx.Stats;
                Assert.Equal(0, st.DeltaFileCount);
                Assert.Equal(2, st.SegmentCount);
                Assert.False(File.Exists(oldProject)); // retired and deleted once unreferenced
                Assert.False(idx.Compact()); // nothing left to do
            }
            using (var idx = T.Open(dir.Path))
            {
                reopened = Fingerprint(idx);
            }
            Assert.Equal(before, reopened);
        }

        [Fact]
        public void Compaction_KeepsUpdatesMadeDuringCompaction()
        {
            using var dir = new TempDir();
            using var idx = BuildIndex(dir.Path);
            MutateAfterBase(idx);

            // Simulate concurrent writes by interleaving: updates through a second thread while compaction runs.
            var started = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                started.Wait();
                for (int i = 0; i < 50; i++)
                    idx.Update(T.F("/proj/Source/Game/Live" + i + ".h", ItemOrigin.Project, T.D("ALive" + i)));
                idx.Update(T.F("/proj/Source/Game/Spell.h", ItemOrigin.Project, T.D("FSpell2", SymbolKind.Struct)));
                idx.Remove("/proj/Source/Game/MyActor.cpp");
            });
            t.Start();
            started.Set();
            idx.Compact();
            t.Join();

            for (int i = 0; i < 50; i++) Assert.Single(idx.FindByName("ALive" + i));
            Assert.Empty(idx.FindByName("FSpell"));
            Assert.Single(idx.FindByName("FSpell2"));
            Assert.Null(idx.GetFile("/proj/Source/Game/MyActor.cpp"));
            Assert.Single(idx.FindByName("Mana"));

            string before = Fingerprint(idx);
            idx.Compact();
            Assert.Equal(before, Fingerprint(idx));
            Assert.Equal(0, idx.Stats.DeltaFileCount);
        }

        [Fact]
        public void ReplaceBase_DropsDeltaEntriesItContains()
        {
            using var dir = new TempDir();
            using var idx = BuildIndex(dir.Path);
            idx.Update(T.F("/proj/Source/Game/MyPawn.h", ItemOrigin.Project, T.D("AMyPawnDelta")));
            idx.Update(T.F("/proj/Source/Game/Other.h", ItemOrigin.Project, T.D("AOther")));
            idx.ReplaceBase(SegmentKind.Project, ProjectFiles());
            Assert.Single(idx.FindByName("AMyPawn"));
            Assert.Empty(idx.FindByName("AMyPawnDelta"));
            Assert.Single(idx.FindByName("AOther"));
            Assert.Single(Directory.GetFiles(dir.Path, "project-*.seg"));
        }

        [Fact]
        public void ReplaceBase_Cancelled_LeavesIndexUnchanged()
        {
            using var dir = new TempDir();
            using var idx = BuildIndex(dir.Path);
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => idx.ReplaceBase(SegmentKind.Project, ProjectFiles(), cts.Token));
            Assert.Single(idx.FindByName("AMyActor"));
            Assert.Single(Directory.GetFiles(dir.Path, "project-*.seg"));
            Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        }

        [Fact]
        public void SnapshotsOutliveIndexDispose()
        {
            using var dir = new TempDir();
            var idx = BuildIndex(dir.Path);
            var snap = idx.AcquireSnapshot();
            idx.Dispose();
            Assert.Single(snap.FindByName("AActor"));
            snap.Dispose();
            Assert.Throws<ObjectDisposedException>(() => idx.Search(T.Req("x")));
        }
    }
}
