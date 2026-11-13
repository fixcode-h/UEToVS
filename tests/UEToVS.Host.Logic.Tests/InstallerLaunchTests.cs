using UEToVS.Host.Logic.Update;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class InstallerLaunchTests
    {
        [Fact]
        public void OutsideAJobStartsDetachedFirst()
        {
            var plan = InstallerLaunch.Plan(false, 0);
            Assert.Equal(new[] { InstallerLaunchMethod.Detached, InstallerLaunchMethod.Wmi, InstallerLaunchMethod.Child }, plan);
        }

        [Theory]
        [InlineData(InstallerLaunch.JobLimitBreakawayOk)]
        [InlineData(InstallerLaunch.JobLimitSilentBreakawayOk)]
        [InlineData(InstallerLaunch.JobLimitBreakawayOk | InstallerLaunch.JobLimitKillOnJobClose)]
        public void AJobThatAllowsBreakawayStillStartsDetachedFirst(uint flags)
        {
            Assert.Equal(InstallerLaunchMethod.Detached, InstallerLaunch.Plan(true, flags)[0]);
        }

        [Fact]
        public void AJobWithoutBreakawayGoesThroughWmiFirst()
        {
            var plan = InstallerLaunch.Plan(true, InstallerLaunch.JobLimitKillOnJobClose);
            Assert.Equal(new[] { InstallerLaunchMethod.Wmi, InstallerLaunchMethod.Detached, InstallerLaunchMethod.Child }, plan);
        }

        [Fact]
        public void ChildStartIsAlwaysTheLastResort()
        {
            foreach (bool inJob in new[] { false, true })
                foreach (uint flags in new uint[] { 0, InstallerLaunch.JobLimitBreakawayOk, InstallerLaunch.JobLimitKillOnJobClose })
                {
                    var plan = InstallerLaunch.Plan(inJob, flags);
                    Assert.Equal(InstallerLaunchMethod.Child, plan[plan.Count - 1]);
                }
        }

        [Fact]
        public void QuotesPathsWithSpaces()
        {
            Assert.Equal("\"C:\\Program Files\\VS\\VSIXInstaller.exe\" \"C:\\Users\\a b\\UEToVS-0.5.16.vsix\"",
                InstallerLaunch.CommandLine(@"C:\Program Files\VS\VSIXInstaller.exe", @"C:\Users\a b\UEToVS-0.5.16.vsix"));
        }
    }
}
