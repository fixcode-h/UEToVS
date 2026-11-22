using System.Text;
using UEToVS.Core.Text;
using Xunit;

namespace UEToVS.Core.Tests
{
    public class TextFileCodecTests
    {
        private static readonly Encoding Gbk;

        static TextFileCodecTests()
        {
            TextFileCodec.Detect(new byte[] { 0x41 }); // 触发静态构造，注册代码页
            Gbk = Encoding.GetEncoding(936);
        }

        [Fact]
        public void Utf8WithoutBom_RoundTripsWithoutBom()
        {
            var bytes = new UTF8Encoding(false).GetBytes("// 中文注释\nint X;\n");
            string text = TextFileCodec.Decode(bytes, out var f);
            Assert.Equal(TextFileFormat.Utf8, f);
            Assert.True(TextFileCodec.TryEncode(text.Replace("X", "Y"), f, out var outBytes));
            Assert.NotEqual(0xEF, outBytes[0]);
            Assert.Equal("// 中文注释\nint Y;\n", new UTF8Encoding(false).GetString(outBytes));
        }

        [Fact]
        public void Utf8Bom_KeepsBom()
        {
            var bytes = new UTF8Encoding(true).GetPreamble();
            bytes = Concat(bytes, Encoding.UTF8.GetBytes("int X;"));
            string text = TextFileCodec.Decode(bytes, out var f);
            Assert.Equal(TextFileFormat.Utf8Bom, f);
            Assert.Equal("int X;", text);
            Assert.True(TextFileCodec.TryEncode(text, f, out var outBytes));
            Assert.Equal(bytes, outBytes);
        }

        [Fact]
        public void Gbk_IsDetectedAndWrittenBackAsGbk()
        {
            var bytes = Gbk.GetBytes("// 血量\nfloat Health;\n");
            string text = TextFileCodec.Decode(bytes, out var f, 936);
            Assert.Equal(TextFileEncodingKind.CodePage, f.Kind);
            Assert.Equal(936, f.CodePage);
            Assert.Contains("血量", text);
            Assert.True(TextFileCodec.TryEncode(text.Replace("Health", "HitPoints"), f, out var outBytes));
            Assert.Equal(Gbk.GetBytes("// 血量\nfloat HitPoints;\n"), outBytes);
        }

        [Fact]
        public void Gbk_RejectsCharactersItCannotStore()
        {
            var f = new TextFileFormat(TextFileEncodingKind.CodePage, 936);
            Assert.False(TextFileCodec.TryEncode("emoji 😀", f, out var bytes));
            Assert.Null(bytes);
        }

        [Fact]
        public void Utf16Le_IsPreserved()
        {
            var bytes = Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("int X;"));
            string text = TextFileCodec.Decode(bytes, out var f);
            Assert.Equal(TextFileEncodingKind.Utf16LE, f.Kind);
            Assert.True(TextFileCodec.TryEncode(text, f, out var outBytes));
            Assert.Equal(bytes, outBytes);
        }

        [Fact]
        public void TruncatedUtf8_IsNotUtf8()
        {
            Assert.False(TextFileCodec.IsValidUtf8(new byte[] { 0x41, 0xE4, 0xB8 }, 0));
            Assert.True(TextFileCodec.IsValidUtf8(Encoding.UTF8.GetBytes("中"), 0));
            Assert.False(TextFileCodec.IsValidUtf8(new byte[] { 0xC0, 0x80 }, 0)); // 过长编码
        }

        [Fact]
        public void ChooseForExisting_PreservesByDefault_ConvertsWhenAsked()
        {
            var gbk = new TextFileFormat(TextFileEncodingKind.CodePage, 936);
            Assert.Equal(gbk, TextFileCodec.ChooseForExisting(gbk, "中文", FileEncodingChoice.Utf8NoBom, ExistingFileEncodingMode.Preserve));
            Assert.Equal(TextFileFormat.Utf8, TextFileCodec.ChooseForExisting(gbk, "中文", FileEncodingChoice.Utf8NoBom, ExistingFileEncodingMode.ConvertToDefault));
            // 转成 GBK 会丢字：保留原编码
            Assert.Equal(TextFileFormat.Utf8, TextFileCodec.ChooseForExisting(TextFileFormat.Utf8, "😀", FileEncodingChoice.Gbk, ExistingFileEncodingMode.ConvertToDefault));
        }

        [Fact]
        public void ChooseOnSave_FollowsMode()
        {
            Assert.Null(TextFileCodec.ChooseOnSave(TextFileFormat.Utf8Bom, "x", FileEncodingChoice.Utf8NoBom, SaveEncodingMode.Off));
            Assert.Equal(TextFileFormat.Utf8, TextFileCodec.ChooseOnSave(TextFileFormat.Utf8Bom, "x", FileEncodingChoice.Utf8NoBom, SaveEncodingMode.StripUtf8Bom));
            Assert.Null(TextFileCodec.ChooseOnSave(TextFileFormat.Utf8, "x", FileEncodingChoice.Utf8NoBom, SaveEncodingMode.StripUtf8Bom));
            var gbk = new TextFileFormat(TextFileEncodingKind.CodePage, 936);
            Assert.Null(TextFileCodec.ChooseOnSave(gbk, "中", FileEncodingChoice.Utf8NoBom, SaveEncodingMode.StripUtf8Bom));
            Assert.Equal(TextFileFormat.Utf8, TextFileCodec.ChooseOnSave(gbk, "中", FileEncodingChoice.Utf8NoBom, SaveEncodingMode.ConvertToDefault));
            Assert.Null(TextFileCodec.ChooseOnSave(TextFileFormat.Utf8, "x", FileEncodingChoice.Utf8NoBom, SaveEncodingMode.ConvertToDefault));
        }

        [Fact]
        public void EmptyFile_IsUtf8WithoutBom()
        {
            Assert.Equal(string.Empty, TextFileCodec.Decode(new byte[0], out var f));
            Assert.Equal(TextFileFormat.Utf8, f);
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }
    }
}
