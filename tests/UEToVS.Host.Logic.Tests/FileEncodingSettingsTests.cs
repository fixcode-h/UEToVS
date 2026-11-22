using System.Collections.Generic;
using UEToVS.Core.Text;
using UEToVS.Host.Logic.Files;
using UEToVS.Host.Logic.Settings;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class FileEncodingSettingsTests
    {
        [Fact]
        public void Defaults_AreUtf8WithoutBom_PreserveExisting_NoSaveConversion()
        {
            var s = new HostSettings();
            Assert.Equal(FileEncodingChoice.Utf8NoBom, s.FileEncoding);
            Assert.Equal(ExistingFileEncodingMode.Preserve, s.ExistingFileEncoding);
            Assert.Equal(SaveEncodingMode.Off, s.SaveEncoding);
        }

        [Fact]
        public void Values_RoundTrip_AndBadValuesFallBack()
        {
            var s = new HostSettings();
            Assert.True(FileEncodingSettings.TrySet(s, FileEncodingSettings.DefaultKey, "Gbk"));
            Assert.True(FileEncodingSettings.TrySet(s, FileEncodingSettings.ExistingKey, "ConvertToDefault"));
            Assert.True(FileEncodingSettings.TrySet(s, FileEncodingSettings.OnSaveKey, "StripUtf8Bom"));
            var d = new Dictionary<string, string>();
            FileEncodingSettings.ToValues(s, d);
            Assert.Equal("Gbk", d[FileEncodingSettings.DefaultKey]);
            Assert.Equal("ConvertToDefault", d[FileEncodingSettings.ExistingKey]);
            Assert.Equal("StripUtf8Bom", d[FileEncodingSettings.OnSaveKey]);
            FileEncodingSettings.TrySet(s, FileEncodingSettings.DefaultKey, "nonsense");
            Assert.Equal(FileEncodingChoice.Utf8NoBom, s.FileEncoding);
            Assert.False(FileEncodingSettings.TrySet(s, "other.key", "x"));
        }

        [Fact]
        public void SchemaRoundTrip_KeepsTheValues()
        {
            var s = new HostSettings { FileEncoding = FileEncodingChoice.Utf8Bom, SaveEncoding = SaveEncodingMode.ConvertToDefault };
            var values = HostSettingsSchema.Codec.ToValues(s);
            Assert.Equal("Utf8Bom", values[FileEncodingSettings.DefaultKey]);
        }

        [Theory]
        [InlineData(@"C:\P\Source\A.h", true)]
        [InlineData(@"C:\P\Source\A.CPP", true)]
        [InlineData(@"C:\P\Config\DefaultEngine.ini", true)]
        [InlineData(@"C:\P\Game.uproject", true)]
        [InlineData(@"C:\P\Shaders\X.usf", true)]
        [InlineData(@"C:\P\Some.resx", false)]
        [InlineData(null, false)]
        public void SavePolicy_OnlySourceAndConfigFiles(string path, bool expected) => Assert.Equal(expected, SaveEncodingPolicy.AppliesTo(path));
    }
}
