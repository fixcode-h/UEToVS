using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Diagnostics
{
    /// <summary>
    /// Names the component behind a sudden jump of devenv's private memory. Called with every resource sample (60 s,
    /// timer thread); between samples it only keeps the previous private bytes, the per-thread CPU times and the module
    /// names (a few hundred entries). When private bytes grow by <see cref="ThresholdBytes"/> or more it logs
    /// <c>resource.spike</c> with:
    ///   busyThreads — threads that used the most CPU since the last sample, with the module their start address is in
    ///                 (e.g. a VS C++ / Unreal component DLL, clr.dll for managed thread-pool work);
    ///   newModules  — DLLs loaded since the last sample;
    ///   children    — child processes of devenv with their private memory.
    /// Nothing heavier than this runs, and only on a spike.
    /// </summary>
    internal sealed class MemorySpikeProbe
    {
        private static readonly Logger L = Log.For("Diagnostics");

        public long ThresholdBytes { get; set; } = 2L * 1024 * 1024 * 1024;

        private long _lastPrivate = -1;
        private Dictionary<int, double> _lastThreadCpu = new Dictionary<int, double>();
        private HashSet<string> _lastModules;
        private readonly object _gate = new object();

        public void Sample()
        {
            lock (_gate)
            {
                try
                {
                    using (var p = Process.GetCurrentProcess())
                    {
                        p.Refresh();
                        long priv = p.PrivateMemorySize64;
                        var cpu = ThreadCpu(p);
                        var modules = ModuleRanges(p);
                        var names = new HashSet<string>(modules.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
                        if (_lastPrivate >= 0 && priv - _lastPrivate >= ThresholdBytes)
                            LogSpike(p, priv, cpu, modules, names);
                        _lastPrivate = priv;
                        _lastThreadCpu = cpu;
                        _lastModules = names;
                    }
                }
                catch (Exception ex)
                {
                    L.Debug("resource.spike.probe.failed", "Memory spike probe failed", ("error", ex.GetType().Name + ": " + ex.Message));
                }
            }
        }

        private void LogSpike(Process p, long priv, Dictionary<int, double> cpu, List<ModuleRange> modules, HashSet<string> names)
        {
            var busy = cpu
                .Select(kv => (Id: kv.Key, Delta: kv.Value - (_lastThreadCpu.TryGetValue(kv.Key, out double before) ? before : 0)))
                .Where(t => t.Delta > 0.05)
                .OrderByDescending(t => t.Delta)
                .Take(12)
                .Select(t => new Dictionary<string, object>
                {
                    ["tid"] = t.Id,
                    ["cpuSec"] = Math.Round(t.Delta, 2),
                    ["module"] = StartModule(t.Id, modules),
                })
                .ToList();
            var added = _lastModules == null ? new List<string>() : names.Where(n => !_lastModules.Contains(n)).OrderBy(n => n).Take(40).ToList();
            L.Warn("resource.spike", "devenv private memory jumped; busiest threads and new modules since the last sample",
                ("privateMB", Mb(priv)), ("deltaMB", Mb(priv - _lastPrivate)), ("managedMB", Mb(GC.GetTotalMemory(false))),
                ("threads", cpu.Count), ("busyThreads", busy), ("newModules", added), ("children", Children(p.Id)));
        }

        private static Dictionary<int, double> ThreadCpu(Process p)
        {
            var map = new Dictionary<int, double>();
            foreach (ProcessThread t in p.Threads)
            {
                try { map[t.Id] = t.TotalProcessorTime.TotalSeconds; }
                catch (InvalidOperationException) { } // exited
                catch (Win32Exception) { }
                finally { t.Dispose(); }
            }
            return map;
        }

        private struct ModuleRange
        {
            public string Name;
            public long Start;
            public long End;
        }

        private static List<ModuleRange> ModuleRanges(Process p)
        {
            var list = new List<ModuleRange>();
            try
            {
                foreach (ProcessModule m in p.Modules)
                {
                    try
                    {
                        long start = m.BaseAddress.ToInt64();
                        list.Add(new ModuleRange { Name = m.ModuleName, Start = start, End = start + m.ModuleMemorySize });
                    }
                    finally { m.Dispose(); }
                }
            }
            catch (Win32Exception) { }
            return list;
        }

        // ---------------------------------------------------------------- Win32

        private const int ThreadQuerySetWin32StartAddress = 9;
        private const int ThreadQueryLimitedInformation = 0x0800;
        private const int ThreadQueryInformation = 0x0040;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(int access, bool inherit, int threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, out IntPtr info, int length, IntPtr returnLength);

        private static string StartModule(int threadId, List<ModuleRange> modules)
        {
            IntPtr h = OpenThread(ThreadQueryInformation, false, threadId);
            if (h == IntPtr.Zero) h = OpenThread(ThreadQueryLimitedInformation, false, threadId);
            if (h == IntPtr.Zero) return null;
            try
            {
                if (NtQueryInformationThread(h, ThreadQuerySetWin32StartAddress, out IntPtr start, IntPtr.Size, IntPtr.Zero) != 0) return null;
                long a = start.ToInt64();
                foreach (var m in modules)
                    if (a >= m.Start && a < m.End) return m.Name;
                return "0x" + a.ToString("x");
            }
            finally { CloseHandle(h); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public int dwSize;
            public int cntUsage;
            public int th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public int th32ModuleID;
            public int cntThreads;
            public int th32ParentProcessID;
            public int pcPriClassBase;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        private const int Th32csSnapProcess = 0x2;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

        private static List<Dictionary<string, object>> Children(int parentId)
        {
            var result = new List<Dictionary<string, object>>();
            IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
            try
            {
                var e = new ProcessEntry32 { dwSize = Marshal.SizeOf(typeof(ProcessEntry32)) };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    if (e.th32ParentProcessID != parentId) continue;
                    double? mb = null;
                    try
                    {
                        using (var c = Process.GetProcessById(e.th32ProcessID)) mb = Mb(c.PrivateMemorySize64);
                    }
                    catch (Exception) { } // exited or no access
                    result.Add(new Dictionary<string, object> { ["pid"] = e.th32ProcessID, ["exe"] = e.szExeFile, ["privateMB"] = mb });
                    if (result.Count >= 30) break;
                }
            }
            finally { CloseHandle(snap); }
            return result;
        }

        private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);
    }
}
