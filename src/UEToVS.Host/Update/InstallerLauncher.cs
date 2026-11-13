using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Update;

namespace UEToVS.Host.Update
{
    /// <summary>
    /// Starts VSIXInstaller.exe so that it survives devenv: the installer asks the user to close VS and only then replaces
    /// the extension, so anything tying it to devenv (job object, inherited handles, VS's environment) can take it down
    /// exactly when VS goes away. Order comes from <see cref="InstallerLaunch.Plan"/>; every attempt, devenv's job state
    /// and an early installer exit are logged (update.installer.*) so a crash report can be matched to its cause.
    /// </summary>
    internal static class InstallerLauncher
    {
        private static readonly Logger L = Log.For("Update");

        /// <summary>Starts the installer; returns the method that worked. Throws the last failure when none did.</summary>
        public static InstallerLaunchMethod Start(string installer, string vsix)
        {
            var job = QueryJob();
            var plan = InstallerLaunch.Plan(job.InJob, job.LimitFlags);
            L.Info("update.installer.job", null, ("inJob", job.InJob), ("limitFlags", "0x" + job.LimitFlags.ToString("X")),
                   ("killOnClose", (job.LimitFlags & InstallerLaunch.JobLimitKillOnJobClose) != 0),
                   ("breakawayOk", InstallerLaunch.CanBreakAway(job.LimitFlags)), ("queryError", job.Error),
                   ("plan", string.Join(",", plan)));
            Exception last = null;
            foreach (var method in plan)
            {
                try
                {
                    int pid = Launch(method, installer, vsix, job);
                    L.Info("update.installer.started", null, ("method", method.ToString()), ("pid", pid), ("installer", installer), ("file", vsix));
                    WatchEarlyExit(pid, method);
                    return method;
                }
                catch (Exception ex) when (IsLaunchError(ex))
                {
                    last = ex;
                    L.Warn("update.installer.launchFailed", "Installer start failed; trying the next method", ex, ("method", method.ToString()));
                }
            }
            throw last ?? new InvalidOperationException("No installer launch method");
        }

        private static int Launch(InstallerLaunchMethod method, string installer, string vsix, JobInfo job)
        {
            switch (method)
            {
                case InstallerLaunchMethod.Detached: return StartDetached(installer, vsix, job);
                case InstallerLaunchMethod.Wmi: return StartWithWmi(installer, vsix);
                default:
                    using (var p = Process.Start(new ProcessStartInfo(installer, InstallerLaunch.Arguments(vsix)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer) }))
                        return p?.Id ?? 0;
            }
        }

        private static bool IsLaunchError(Exception ex) =>
            ex is Win32Exception || ex is ManagementException || ex is COMException || ex is UnauthorizedAccessException
            || ex is InvalidOperationException || ex is TypeInitializationException || ex is FileNotFoundException;

        /// <summary>
        /// CreateProcess with no inherited handles, the user's default environment (not devenv's) and, when devenv sits in a
        /// job that allows it, CREATE_BREAKAWAY_FROM_JOB.
        /// </summary>
        private static int StartDetached(string installer, string vsix, JobInfo job)
        {
            uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_PROCESS_GROUP | CREATE_DEFAULT_ERROR_MODE;
            if (job.InJob && InstallerLaunch.CanBreakAway(job.LimitFlags)) flags |= CREATE_BREAKAWAY_FROM_JOB;
            IntPtr env = UserEnvironment();
            try
            {
                var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
                var cmd = new StringBuilder(InstallerLaunch.CommandLine(installer, vsix));
                if (!CreateProcessW(installer, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, env, Path.GetDirectoryName(installer), ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            finally
            {
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            }
        }

        /// <summary>The logged-on user's environment as a fresh logon would see it; IntPtr.Zero (inherit) when unavailable.</summary>
        private static IntPtr UserEnvironment()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!CreateEnvironmentBlock(out IntPtr env, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return env;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                L.Warn("update.installer.environment", "User environment unavailable; the installer inherits devenv's", ex);
                return IntPtr.Zero;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        /// <summary>WMI Win32_Process.Create: started by the WMI service on the user's desktop, outside devenv's job.</summary>
        private static int StartWithWmi(string installer, string vsix)
        {
            using (var startup = new ManagementClass("root\\cimv2", "Win32_ProcessStartup", null))
            using (var startupInfo = startup.CreateInstance())
            using (var process = new ManagementClass("root\\cimv2", "Win32_Process", null))
            using (var input = process.GetMethodParameters("Create"))
            {
                startupInfo["ShowWindow"] = (ushort)1; // SW_SHOWNORMAL
                input["CommandLine"] = InstallerLaunch.CommandLine(installer, vsix);
                input["CurrentDirectory"] = Path.GetDirectoryName(installer);
                input["ProcessStartupInformation"] = startupInfo;
                using (var output = process.InvokeMethod("Create", input, null))
                {
                    uint rv = Convert.ToUInt32(output?["ReturnValue"] ?? uint.MaxValue);
                    if (rv != 0) throw new InvalidOperationException("Win32_Process.Create returned " + rv);
                    return Convert.ToInt32(output["ProcessId"]);
                }
            }
        }

        /// <summary>
        /// Logs the installer's exit code when it ends while VS is still running: a crash shows up as an exit right after
        /// start (or right after the user closed a VS window) with a non-zero code, instead of a window that just vanished.
        /// </summary>
        private static void WatchEarlyExit(int pid, InstallerLaunchMethod method)
        {
            if (pid <= 0) return;
            try
            {
                var started = Stopwatch.StartNew();
                var p = Process.GetProcessById(pid);
                p.EnableRaisingEvents = true;
                p.Exited += (s, e) =>
                {
                    int? code = null;
                    try { code = p.ExitCode; } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    L.Info("update.installer.exited", null, ("method", method.ToString()), ("pid", pid), ("exitCode", code),
                           ("exitCodeHex", code.HasValue ? "0x" + code.Value.ToString("X8") : null), ("ms", Math.Round(started.Elapsed.TotalMilliseconds)));
                    p.Dispose();
                };
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
            {
                // Already gone (or not ours to watch): that alone is the interesting part.
                L.Warn("update.installer.exited", "Installer exited before it could be watched", ex, ("method", method.ToString()), ("pid", pid));
            }
        }

        private struct JobInfo
        {
            public bool InJob;
            public uint LimitFlags;
            public string Error;
        }

        private static JobInfo QueryJob()
        {
            var info = new JobInfo();
            try
            {
                if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out bool inJob)) throw new Win32Exception(Marshal.GetLastWin32Error());
                info.InJob = inJob;
                if (!inJob) return info;
                var limits = new JOBOBJECT_BASIC_LIMIT_INFORMATION();
                // A null job handle means "the job of the calling process".
                if (!QueryInformationJobObject(IntPtr.Zero, JobObjectBasicLimitInformation, ref limits, Marshal.SizeOf(typeof(JOBOBJECT_BASIC_LIMIT_INFORMATION)), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                info.LimitFlags = limits.LimitFlags;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is EntryPointNotFoundException)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        private const uint CREATE_DEFAULT_ERROR_MODE = 0x04000000;
        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const int JobObjectBasicLimitInformation = 2;

        // Filled or read by the marshaller only.
#pragma warning disable CS0649, CS0169
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
#pragma warning restore CS0649, CS0169

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_BASIC_LIMIT_INFORMATION info, int length, out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    }
}
