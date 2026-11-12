using System;
using UEToVS.Host.Logic.Update;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class UpdateCheckTests
    {
        [Theory]
        [InlineData("v0.4.4", "0.4.4")]
        [InlineData("0.4.4+3f2a1c", "0.4.4")]
        [InlineData("0.4", "0.4.0")]
        [InlineData("1.2.3-beta", "1.2.3")]
        public void ParsesVersions(string text, string expected) => Assert.Equal(Version.Parse(expected), UpdateCheck.ParseVersion(text));

        [Fact]
        public void BadVersionsAreNull()
        {
            Assert.Null(UpdateCheck.ParseVersion(null));
            Assert.Null(UpdateCheck.ParseVersion("latest"));
        }

        [Fact]
        public void ParsesTheLatestReleaseAndPicksTheUEToVSVsix()
        {
            const string json = @"{""tag_name"":""v0.4.5"",""html_url"":""https://github.com/fixcode-h/UEToVS/releases/tag/v0.4.5"",""draft"":false,""prerelease"":false,
              ""assets"":[{""name"":""notes.txt"",""browser_download_url"":""https://x/notes.txt"",""size"":1},
                          {""name"":""UEToVS-0.4.5.vsix"",""browser_download_url"":""https://x/UEToVS-0.4.5.vsix"",""size"":123456}]}";
            var r = UpdateCheck.Parse(json);
            Assert.Equal(new Version(0, 4, 5), r.Version);
            Assert.Equal("UEToVS-0.4.5.vsix", r.VsixName);
            Assert.Equal("https://x/UEToVS-0.4.5.vsix", r.VsixUrl);
            Assert.Equal(123456, r.VsixSize);
            Assert.True(UpdateCheck.IsNewer(r.Version, UpdateCheck.ParseVersion("0.4.4+abc")));
            Assert.False(UpdateCheck.IsNewer(r.Version, UpdateCheck.ParseVersion("0.4.5")));
            Assert.False(UpdateCheck.IsNewer(r.Version, null));
        }

        [Fact]
        public void ReleasesWithoutVsixOrDraftsAreIgnored()
        {
            Assert.Null(UpdateCheck.Parse(@"{""tag_name"":""v0.4.5"",""assets"":[]}"));
            Assert.Null(UpdateCheck.Parse(@"{""tag_name"":""v0.4.5"",""draft"":true,""assets"":[{""name"":""UEToVS-0.4.5.vsix"",""browser_download_url"":""u""}]}"));
            Assert.Null(UpdateCheck.Parse("not json"));
        }
    }
}
