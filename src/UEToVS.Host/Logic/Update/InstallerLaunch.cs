using System.Collections.Generic;

namespace UEToVS.Host.Logic.Update
{
    /// <summary>How VSIXInstaller.exe is started, in the order <see cref="InstallerLaunch.Plan"/> tries them.</summary>
    public enum InstallerLaunchMethod
    {
        /// <summary>CreateProcess without inherited handles, with the user's own environment, out of devenv's job when it has one.</summary>
        Detached,
        /// <summary>WMI Win32_Process.Create: the parent is WmiPrvSE, so neither devenv's job nor its handles or environment carry over.</summary>
        Wmi,
        /// <summary>Plain Process.Start as a child of devenv (the only method before v0.5.16).</summary>
        Child,
    }

    /// <summary>
    /// Pure rules for starting the VSIX installer from inside devenv. The installer asks the user to close VS and then
    /// replaces the extension, so it must outlive devenv: started as an ordinary child it can sit in devenv's job object
    /// (killed with VS when the job kills on close), inherit devenv's handles (the old extension's files stay open) and
    /// inherit devenv's environment (VS-only variables). Detached first; WMI when the job forbids breaking away; the old
    /// child start only as the last resort, so a broken WMI never means "no installer at all".
    /// </summary>
    public static class InstallerLaunch
    {
        public const uint JobLimitBreakawayOk = 0x00000800;
        public const uint JobLimitSilentBreakawayOk = 0x00001000;
        public const uint JobLimitKillOnJobClose = 0x00002000;

        /// <param name="inJob">devenv runs inside a job object (IsProcessInJob).</param>
        /// <param name="limitFlags">The job's LimitFlags (JOBOBJECT_BASIC_LIMIT_INFORMATION); 0 when unknown or not in a job.</param>
        public static IReadOnlyList<InstallerLaunchMethod> Plan(bool inJob, uint limitFlags)
        {
            if (!inJob || CanBreakAway(limitFlags))
                return new[] { InstallerLaunchMethod.Detached, InstallerLaunchMethod.Wmi, InstallerLaunchMethod.Child };
            // A job without breakaway rights would pull a CreateProcess child back in (or refuse CREATE_BREAKAWAY_FROM_JOB).
            return new[] { InstallerLaunchMethod.Wmi, InstallerLaunchMethod.Detached, InstallerLaunchMethod.Child };
        }

        /// <summary>CREATE_BREAKAWAY_FROM_JOB is only useful (and only allowed) when the job permits it.</summary>
        public static bool CanBreakAway(uint limitFlags) => (limitFlags & (JobLimitBreakawayOk | JobLimitSilentBreakawayOk)) != 0;

        /// <summary>Command line for VSIXInstaller: the quoted path of the downloaded VSIX.</summary>
        public static string Arguments(string vsixPath) => "\"" + vsixPath + "\"";

        /// <summary>Full command line (quoted installer + arguments), as CreateProcess and WMI expect it.</summary>
        public static string CommandLine(string installerPath, string vsixPath) => "\"" + installerPath + "\" " + Arguments(vsixPath);
    }
}
