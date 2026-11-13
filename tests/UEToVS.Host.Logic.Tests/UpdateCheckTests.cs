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

        [Fact]
        public void ReadsTheReleaseBody()
        {
            var r = UpdateCheck.Parse(@"{""tag_name"":""v0.5.3"",""body"":""### 改进\n- **A** — b"",""assets"":[{""name"":""UEToVS-0.5.3.vsix"",""browser_download_url"":""u""}]}");
            Assert.Equal("### 改进\n- **A** — b", r.Notes);
        }

        [Fact]
        public void NotesMarkdownBecomesPlainLines()
        {
            const string md = "## v0.5.3\r\n\r\n### 改进\r\n- **全项目检查** — 默认包含 `Plugins`，见 [说明](https://x)\r\n* 第二条\r\n\r\n---\r\n普通文字";
            var lines = UpdateCheck.NotesLines(md);
            Assert.Equal(5, lines.Count);
            Assert.Equal(NoteLineKind.Heading, lines[0].Kind);
            Assert.Equal("v0.5.3", lines[0].Text);
            Assert.Equal("改进", lines[1].Text);
            Assert.Equal(NoteLineKind.Bullet, lines[2].Kind);
            Assert.Equal("全项目检查 — 默认包含 Plugins，见 说明", lines[2].Text);
            Assert.Equal("第二条", lines[3].Text);
            Assert.Equal(NoteLineKind.Text, lines[4].Kind);
            Assert.Empty(UpdateCheck.NotesLines(null));
            Assert.Equal(2, UpdateCheck.NotesLines("- a\n- b\n- c", maxLines: 2).Count);
            // scripts/release.py appends install instructions under "### 下载": not shown.
            var release = UpdateCheck.NotesLines("## UEToVS v0.5.3 更新\n\n### 改进\n- a\n\n### 下载\n\n- Windows — x.vsix\n完整改动");
            Assert.Equal(new[] { "UEToVS v0.5.3 更新", "改进", "a" }, System.Linq.Enumerable.Select(release, l => l.Text));
        }
    }
}
