using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UEToVS.Host.Logic.Performance;

namespace UEToVS.Host.Diagnostics
{
    /// <summary>
    /// Walks devenv's own address space (VirtualQuery) and its native heaps (GetProcessHeaps + HeapSummary) into a
    /// <see cref="HostMemoryMap"/>. Background thread only. A walk is a few thousand to a few tens of thousands of
    /// VirtualQuery calls (≈10–30 ms) plus one GetMappedFileName per DLL / mapped file; nothing is kept between walks
    /// except the name cache. HeapSummary only reads each heap's segment totals (no per-block walk).
    /// </summary>
    internal static class ProcessMemoryMap
    {
        private static readonly Dictionary<long, string> NameCache = new Dictionary<long, string>();
        private static readonly object Gate = new object();

        public static HostMemoryMap Take(long managedBytes)
        {
            var sw = Stopwatch.StartNew();
            var regions = new List<MemoryRegion>(8192);
            lock (Gate)
            {
                IntPtr process = GetCurrentProcess();
                var seen = new HashSet<long>();
                long address = 0;
                int size = Marshal.SizeOf(typeof(MemoryBasicInformation));
                while (VirtualQuery(new IntPtr(address), out var mbi, new IntPtr(size)) != IntPtr.Zero)
                {
                    long regionSize = mbi.RegionSize.ToInt64();
                    if (regionSize <= 0) break;
                    if (mbi.State == MemCommit)
                    {
                        var kind = mbi.Type == MemImage ? RegionKind.Image : mbi.Type == MemMapped ? RegionKind.Mapped : RegionKind.Private;
                        string owner = null;
                        if (kind != RegionKind.Private)
                        {
                            long allocBase = mbi.AllocationBase.ToInt64();
                            seen.Add(allocBase);
                            owner = NameOf(process, allocBase);
                        }
                        regions.Add(new MemoryRegion { Kind = kind, Bytes = regionSize, Owner = owner });
                    }
                    long next = mbi.BaseAddress.ToInt64() + regionSize;
                    if (next <= address) break;
                    address = next;
                }
                // Forget DLLs / files that were unloaded so the cache never grows without bound.
                if (NameCache.Count > seen.Count + 256)
                    foreach (var k in new List<long>(NameCache.Keys))
                        if (!seen.Contains(k)) NameCache.Remove(k);
            }
            var map = HostMemoryMap.Build(regions, Heaps(), managedBytes);
            map.WalkMs = sw.Elapsed.TotalMilliseconds;
            return map;
        }

        private static string NameOf(IntPtr process, long allocBase)
        {
            if (NameCache.TryGetValue(allocBase, out string name)) return name;
            var sb = new StringBuilder(520);
            int n = GetMappedFileNameW(process, new IntPtr(allocBase), sb, sb.Capacity);
            name = n > 0 ? HostMemoryMap.ShortName(sb.ToString(0, n)) : "?";
            NameCache[allocBase] = name;
            return name;
        }

        private static List<NativeHeapInfo> Heaps()
        {
            var list = new List<NativeHeapInfo>();
            try
            {
                int count = GetProcessHeaps(0, null);
                if (count <= 0) return list;
                var handles = new IntPtr[count + 16];
                count = GetProcessHeaps(handles.Length, handles);
                long defaultHeap = GetProcessHeap().ToInt64();
                for (int i = 0; i < count && i < handles.Length; i++)
                {
                    var s = new HeapSummaryInfo { cb = Marshal.SizeOf(typeof(HeapSummaryInfo)) };
                    if (!HeapSummary(handles[i], 0, ref s)) continue;
                    list.Add(new NativeHeapInfo
                    {
                        Handle = handles[i].ToInt64(),
                        IsDefault = handles[i].ToInt64() == defaultHeap,
                        CommittedBytes = s.cbCommitted.ToInt64(),
                        AllocatedBytes = s.cbAllocated.ToInt64(),
                    });
                }
            }
            catch (Exception) // EntryPointNotFound on very old Windows, access errors: no heap split
            {
            }
            return list;
        }

        // ---------------------------------------------------------------- Win32

        private const int MemCommit = 0x1000;
        private const int MemImage = 0x1000000;
        private const int MemMapped = 0x40000;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public int AllocationProtect;
            public short PartitionId;
            public IntPtr RegionSize;
            public int State;
            public int Protect;
            public int Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HeapSummaryInfo
        {
            public int cb;
            public IntPtr cbAllocated;
            public IntPtr cbCommitted;
            public IntPtr cbReserved;
            public IntPtr cbMaxReserve;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation info, IntPtr length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "K32GetMappedFileNameW")]
        private static extern int GetMappedFileNameW(IntPtr process, IntPtr address, StringBuilder name, int size);

        [DllImport("kernel32.dll")]
        private static extern int GetProcessHeaps(int count, [Out] IntPtr[] heaps);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll")]
        private static extern bool HeapSummary(IntPtr heap, int flags, ref HeapSummaryInfo summary);
    }
}
