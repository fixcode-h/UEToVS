using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.Performance
{
    /// <summary>Kind of a committed address-space region (VirtualQuery MEM_PRIVATE / MEM_IMAGE / MEM_MAPPED).</summary>
    public enum RegionKind
    {
        Private = 0,
        Image = 1,
        Mapped = 2,
    }

    /// <summary>One committed region; <see cref="Owner"/> is the DLL / file name for image and mapped regions.</summary>
    public struct MemoryRegion
    {
        public RegionKind Kind;
        public long Bytes;
        public string Owner;
    }

    /// <summary>A native heap of the process (GetProcessHeaps + HeapSummary).</summary>
    public sealed class NativeHeapInfo
    {
        public long Handle { get; set; }
        public bool IsDefault { get; set; }
        public long CommittedBytes { get; set; }
        public long AllocatedBytes { get; set; }
    }

    public sealed class NamedBytes
    {
        public NamedBytes(string name, long bytes)
        {
            Name = name;
            Bytes = bytes;
        }

        public string Name { get; }
        public long Bytes { get; }
    }

    /// <summary>
    /// What devenv's memory is made of, from one walk of its address space (Diagnostics/ProcessMemoryMap in the host,
    /// every few seconds while the performance panel is open). Native heap allocations carry no owner, so the split is by
    /// kind: managed heap (live .NET objects of VS and every extension), native heaps (each heap's committed bytes),
    /// other private memory (VirtualAlloc by components: GC segments beyond live objects, JIT code, thread stacks, …),
    /// DLL images and mapped files (by name).
    /// </summary>
    public sealed class HostMemoryMap
    {
        public DateTime TakenUtc { get; set; }
        public long PrivateCommitBytes { get; set; }
        public long ManagedBytes { get; set; }
        public long NativeHeapBytes { get; set; }
        public long OtherPrivateBytes { get; set; }
        public long ImageBytes { get; set; }
        public long MappedBytes { get; set; }
        public int RegionCount { get; set; }
        public double WalkMs { get; set; }
        public IReadOnlyList<NativeHeapInfo> Heaps { get; set; } = Array.Empty<NativeHeapInfo>();
        public IReadOnlyList<NamedBytes> TopImages { get; set; } = Array.Empty<NamedBytes>();
        public IReadOnlyList<NamedBytes> TopMapped { get; set; } = Array.Empty<NamedBytes>();

        /// <summary>Builds the map from committed regions, the process heaps and the managed heap size.</summary>
        public static HostMemoryMap Build(IEnumerable<MemoryRegion> regions, IEnumerable<NativeHeapInfo> heaps, long managedBytes, int top = 8)
        {
            var map = new HostMemoryMap { TakenUtc = DateTime.UtcNow };
            var images = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var mapped = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (var r in regions ?? Enumerable.Empty<MemoryRegion>())
            {
                if (r.Bytes <= 0) continue;
                count++;
                switch (r.Kind)
                {
                    case RegionKind.Private:
                        map.PrivateCommitBytes += r.Bytes;
                        break;
                    case RegionKind.Image:
                        map.ImageBytes += r.Bytes;
                        Add(images, r.Owner, r.Bytes);
                        break;
                    default:
                        map.MappedBytes += r.Bytes;
                        Add(mapped, r.Owner, r.Bytes);
                        break;
                }
            }
            map.RegionCount = count;
            var heapList = (heaps ?? Enumerable.Empty<NativeHeapInfo>()).Where(h => h != null)
                .OrderByDescending(h => h.CommittedBytes).ToList();
            map.Heaps = heapList;
            // Heaps and the managed heap live in private regions; cap so the parts never exceed the measured total.
            map.ManagedBytes = Clamp(managedBytes, map.PrivateCommitBytes);
            map.NativeHeapBytes = Clamp(heapList.Sum(h => h.CommittedBytes), map.PrivateCommitBytes - map.ManagedBytes);
            map.OtherPrivateBytes = map.PrivateCommitBytes - map.ManagedBytes - map.NativeHeapBytes;
            map.TopImages = Top(images, top);
            map.TopMapped = Top(mapped, top);
            return map;
        }

        private static long Clamp(long v, long max) => Math.Max(0, Math.Min(v, Math.Max(0, max)));

        private static void Add(Dictionary<string, long> d, string name, long bytes)
        {
            name = string.IsNullOrEmpty(name) ? "?" : name;
            d.TryGetValue(name, out long v);
            d[name] = v + bytes;
        }

        private static IReadOnlyList<NamedBytes> Top(Dictionary<string, long> d, int n) =>
            d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(n)
             .Select(kv => new NamedBytes(kv.Key, kv.Value)).ToList();

        /// <summary>"\Device\HarddiskVolume3\…\Foo.dll" → "Foo.dll".</summary>
        public static string ShortName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 && i < path.Length - 1 ? path.Substring(i + 1) : path;
        }
    }

    /// <summary>The last devenv memory jump seen by the spike probe (60 s samples): when, how much, busiest thread modules.</summary>
    public sealed class HostMemorySpike
    {
        public DateTime TakenUtc { get; set; }
        public long DeltaBytes { get; set; }
        public long PrivateBytes { get; set; }

        /// <summary>Modules the busiest threads started in, with their CPU seconds over the interval, busiest first.</summary>
        public IReadOnlyList<KeyValuePair<string, double>> BusyModules { get; set; } = Array.Empty<KeyValuePair<string, double>>();

        public IReadOnlyList<string> NewModules { get; set; } = Array.Empty<string>();
    }
}
