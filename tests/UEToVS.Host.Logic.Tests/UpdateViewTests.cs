using System;
using UEToVS.Host.Logic.Update;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class UpdateViewTests
    {
        private static ReleaseInfo Release(string v, long size = 12 * 1048576) =>
            new ReleaseInfo { Version = Version.Parse(v), Tag = "v" + v, VsixName = "UEToVS-" + v + ".vsix", VsixUrl = "https://x/UEToVS-" + v + ".vsix", VsixSize = size };

        [Fact]
        public void IdleShowsTheInstalledVersionWithoutBuildMetadata()
        {
            var s = new UpdateState(UpdatePhase.Idle, "0.4.5+9079d2c4c4cb");
            Assert.Equal("v0.4.5", s.InstalledDisplay);
            Assert.Contains("v0.4.5", UpdateView.Text(s));
            Assert.DoesNotContain("9079d2c", UpdateView.Text(s));
            Assert.Equal(UpdateCommand.Check, UpdateView.Command(s));
        }

        [Fact]
        public void ButtonFollowsTheFlow()
        {
            const string installed = "0.4.5";
            Assert.Equal(UpdateCommand.None, UpdateView.Command(new UpdateState(UpdatePhase.Checking, installed)));
            Assert.Equal(UpdateCommand.DownloadAndInstall, UpdateView.Command(new UpdateState(UpdatePhase.Available, installed, Release("0.4.6"))));
            Assert.Equal(UpdateCommand.Cancel, UpdateView.Command(new UpdateState(UpdatePhase.Downloading, installed, Release("0.4.6"))));
            Assert.Equal(UpdateCommand.Check, UpdateView.Command(new UpdateState(UpdatePhase.UpToDate, installed, Release("0.4.5"))));
            Assert.Equal(UpdateCommand.Check, UpdateView.Command(new UpdateState(UpdatePhase.InstallerStarted, installed, Release("0.4.6"))));
            // A failed check checks again; a failed download downloads again.
            Assert.Equal(UpdateCommand.Check, UpdateView.Command(new UpdateState(UpdatePhase.Failed, installed, message: "x")));
            Assert.Equal(UpdateCommand.DownloadAndInstall,
                         UpdateView.Command(new UpdateState(UpdatePhase.Failed, installed, Release("0.4.6"), message: "x", failedWhileDownloading: true)));
            Assert.True(new UpdateState(UpdatePhase.Downloading, installed).IsBusy);
            Assert.False(new UpdateState(UpdatePhase.Failed, installed).IsBusy);
        }

        [Fact]
        public void AvailableNamesBothVersionsAndTheSize()
        {
            string text = UpdateView.Text(new UpdateState(UpdatePhase.Available, "0.4.5+abc", Release("0.4.6")));
            Assert.Contains("v0.4.6", text);
            Assert.Contains("v0.4.5", text);
            Assert.Contains("12.0 MB", text);
        }

        [Fact]
        public void DownloadingShowsProgress()
        {
            string text = UpdateView.Text(new UpdateState(UpdatePhase.Downloading, "0.4.5", Release("0.4.6"), 3 * 1048576, 12 * 1048576));
            Assert.Contains("v0.4.6", text);
            Assert.Contains("3.0 / 12.0 MB (25%)", text);
            Assert.Equal("1.5 MB", UpdateView.Progress(1572864, 0));
            Assert.Equal("?", UpdateView.Size(0));
        }

        [Fact]
        public void FailedShowsTheMessage()
        {
            Assert.Equal("boom", UpdateView.Text(new UpdateState(UpdatePhase.Failed, "0.4.5", message: "boom")));
        }
    }
}
