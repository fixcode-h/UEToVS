using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Core.Localization;
using UEToVS.Host.Logic.Performance;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class HostMemoryMapTests
    {
        private const long Mb = 1L << 20, Gb = 1L << 30;

        private static MemoryRegion R(RegionKind k, long bytes, string owner = null) => new MemoryRegion { Kind = k, Bytes = bytes, Owner = owner };

        [Fact]
        public void SplitsPrivateMemoryIntoManagedHeapsAndOtherAndRanksDllsAndFiles()
        {
            var regions = new[]
            {
                R(RegionKind.Private, 10 * Gb), R(RegionKind.Private, 2 * Gb),
                R(RegionKind.Image, 30 * Mb, "VCPkgSrv.dll"), R(RegionKind.Image, 10 * Mb, "vcpkg.dll"), R(RegionKind.Image, 25 * Mb, "vcpkg.dll"),
                R(RegionKind.Mapped, 500 * Mb, "Browse.VC.db"), R(RegionKind.Mapped, 0, "empty"),
            };
            var heaps = new[]
            {
                new NativeHeapInfo { Handle = 0x1000, IsDefault = true, CommittedBytes = 9 * Gb, AllocatedBytes = 8 * Gb },
                new NativeHeapInfo { Handle = 0x2000, CommittedBytes = 100 * Mb },
            };
            var m = HostMemoryMap.Build(regions, heaps, managedBytes: 900 * Mb);

            Assert.Equal(12 * Gb, m.PrivateCommitBytes);
            Assert.Equal(900 * Mb, m.ManagedBytes);
            Assert.Equal(9 * Gb + 100 * Mb, m.NativeHeapBytes);
            Assert.Equal(12 * Gb - 900 * Mb - 9 * Gb - 100 * Mb, m.OtherPrivateBytes);
            Assert.Equal("vcpkg.dll", m.TopImages[0].Name);
            Assert.Equal(35 * Mb, m.TopImages[0].Bytes);
            Assert.Single(m.TopMapped);
            Assert.Equal(6, m.RegionCount);
            Assert.True(m.Heaps[0].IsDefault);
        }

        [Fact]
        public void PartsNeverExceedThePrivateTotal()
        {
            var m = HostMemoryMap.Build(new[] { R(RegionKind.Private, 1 * Gb) },
                new[] { new NativeHeapInfo { CommittedBytes = 5 * Gb } }, managedBytes: 600 * Mb);
            Assert.Equal(600 * Mb, m.ManagedBytes);
            Assert.Equal(1 * Gb - 600 * Mb, m.NativeHeapBytes);
            Assert.Equal(0, m.OtherPrivateBytes);
        }

        [Fact]
        public void ShortNameKeepsTheFileName()
        {
            Assert.Equal("Foo.dll", HostMemoryMap.ShortName(@"\Device\HarddiskVolume3\Program Files\Foo.dll"));
            Assert.Equal("x", HostMemoryMap.ShortName("x"));
        }

        [Fact]
        public void PanelRowsPutTheBiggestKindFirstWithItsHeapsAndNameTheSpikeModules()
        {
            Loc.Language = UiLanguage.Chinese;
            var m = HostMemoryMap.Build(
                new[] { R(RegionKind.Private, 12 * Gb), R(RegionKind.Image, 300 * Mb, "a.dll"), R(RegionKind.Mapped, 512 * Kb(1), "small.bin") },
                new[] { new NativeHeapInfo { Handle = 0x1000, IsDefault = true, CommittedBytes = 10 * Gb, AllocatedBytes = 9 * Gb },
                        new NativeHeapInfo { Handle = 0x2000, CommittedBytes = 4 * Mb } },
                managedBytes: 800 * Mb);
            var rows = PerformanceViewMapper.HostMemoryRows(m);
            Assert.Equal("原生堆", rows[0].Name);
            Assert.False(rows[0].IsChild);
            Assert.True(rows[0].Share > 0.75);
            Assert.Equal("默认进程堆", rows[1].Name);
            Assert.True(rows[1].IsChild);
            Assert.DoesNotContain(rows, r => r.Name.Contains("0x2000")); // < 16 MB heaps are not listed
            Assert.DoesNotContain(rows, r => r.Name == "small.bin");     // < 1 MB files are not listed
            Assert.Contains(rows, r => r.Name == "a.dll" && r.IsChild);

            // Without a heap split (the panel no longer sums heaps): one native row with everything private but managed.
            var plain = PerformanceViewMapper.HostMemoryRows(HostMemoryMap.Build(new[] { R(RegionKind.Private, 10 * Gb) }, null, 1 * Gb));
            Assert.Equal("原生内存", plain[0].Name);
            Assert.Equal(9 * Gb, plain[0].Bytes);
            Assert.DoesNotContain(plain, r => r.Name == "其他私有内存");

            var shared = PerformanceViewMapper.HostMemoryRows(HostMemoryMap.Build(new[] { R(RegionKind.Mapped, 224 * Mb, null) }, null, 0));
            Assert.Contains(shared, r => r.Name == "共享内存（无文件）" && r.IsChild);

            var note = PerformanceViewMapper.SpikeNote(new HostMemorySpike
            {
                TakenUtc = DateTime.UtcNow, DeltaBytes = 10 * Gb,
                BusyModules = new[] { new KeyValuePair<string, double>("VCPkgSrv.dll", 42.5) },
            });
            Assert.Contains("+10.00 GB", note);
            Assert.Contains("VCPkgSrv.dll（42.5 秒）", note);
            Assert.Contains("还没有", PerformanceViewMapper.SpikeNote(null));
        }

        private static long Kb(long n) => n * 1024;
    }
}
