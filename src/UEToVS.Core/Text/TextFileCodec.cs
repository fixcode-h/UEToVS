using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace UEToVS.Core.Text
{
    /// <summary>v0.9.13 UEToVS 新建 / 写入文件用的编码（设置 > 编辑 > 文件编码）。</summary>
    public enum FileEncodingChoice
    {
        Utf8NoBom,
        Utf8Bom,
        /// <summary>GBK（代码页 936）。国内老项目和 VS 早期默认保存出来的“ANSI”文件多是它。</summary>
        Gbk,
    }

    /// <summary>改写已有文件时用什么编码。</summary>
    public enum ExistingFileEncodingMode
    {
        /// <summary>保留原编码（默认）：只改几行时不会让整个文件在 diff 里变成整文件改动。</summary>
        Preserve,
        /// <summary>写回时转成默认编码；转换会丢字时仍保留原编码。</summary>
        ConvertToDefault,
    }

    /// <summary>在 VS 里保存文件时要不要统一编码。</summary>
    public enum SaveEncodingMode
    {
        Off,
        /// <summary>只把带 BOM 的 UTF-8 改成不带 BOM。</summary>
        StripUtf8Bom,
        /// <summary>能无损转换时全部转成默认编码。</summary>
        ConvertToDefault,
    }

    public enum TextFileEncodingKind
    {
        Utf8,
        Utf8Bom,
        Utf16LE,
        Utf16BE,
        /// <summary>单 / 双字节代码页（GBK、1252…），见 <see cref="TextFileFormat.CodePage"/>。</summary>
        CodePage,
    }

    /// <summary>一个文件在磁盘上的编码：种类 + 代码页（只对 <see cref="TextFileEncodingKind.CodePage"/> 有意义）。</summary>
    public readonly struct TextFileFormat : IEquatable<TextFileFormat>
    {
        public TextFileFormat(TextFileEncodingKind kind, int codePage = 0)
        {
            Kind = kind;
            CodePage = kind == TextFileEncodingKind.CodePage ? codePage : 0;
        }

        public TextFileEncodingKind Kind { get; }
        public int CodePage { get; }

        public static readonly TextFileFormat Utf8 = new TextFileFormat(TextFileEncodingKind.Utf8);
        public static readonly TextFileFormat Utf8Bom = new TextFileFormat(TextFileEncodingKind.Utf8Bom);

        public bool IsUtf8 => Kind == TextFileEncodingKind.Utf8 || Kind == TextFileEncodingKind.Utf8Bom;

        /// <summary>日志和提示里用的名字，例如 utf-8、utf-8-bom、cp936。</summary>
        public string Name
        {
            get
            {
                switch (Kind)
                {
                    case TextFileEncodingKind.Utf8: return "utf-8";
                    case TextFileEncodingKind.Utf8Bom: return "utf-8-bom";
                    case TextFileEncodingKind.Utf16LE: return "utf-16le";
                    case TextFileEncodingKind.Utf16BE: return "utf-16be";
                    default: return "cp" + CodePage.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        public bool Equals(TextFileFormat other) => Kind == other.Kind && CodePage == other.CodePage;
        public override bool Equals(object obj) => obj is TextFileFormat f && Equals(f);
        public override int GetHashCode() => ((int)Kind * 397) ^ CodePage;
        public override string ToString() => Name;
    }

    /// <summary>
    /// v0.9.13 文本文件的编码识别与无损读写。UEToVS 自己改写文件（MCP 重命名、Build.cs、ini、新建类）都走这里：
    /// 以前一律按 UTF-8 解码再写回，GBK 文件里的中文注释会被写成乱码且无法恢复。规则：
    /// <list type="bullet">
    /// <item>BOM 优先（UTF-8 / UTF-16 LE / BE）；没有 BOM 时严格按 UTF-8 解码，成功就是 UTF-8（纯 ASCII 也算，不带 BOM）；</item>
    /// <item>不是合法 UTF-8 时按系统 ANSI 代码页（中文 Windows 是 936 = GBK）解码；</item>
    /// <item>写回时编码不了的字符不替换成“?”，而是报告失败，由调用方决定不写这个文件。</item>
    /// </list>
    /// </summary>
    public static class TextFileCodec
    {
        public const int GbkCodePage = 936;

        static TextFileCodec()
        {
            // .NET 8（MCP、单测）默认不带 GBK 等代码页，要注册 CodePagesEncodingProvider（运行时自带这个程序集）；
            // .NET Framework（VS 宿主）本来就有，找不到这个类型时什么都不做。
            try
            {
                var type = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages", false);
                var instance = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as EncodingProvider;
                if (instance != null) Encoding.RegisterProvider(instance);
            }
            catch (Exception)
            {
                // 注册失败只影响非 UTF-8 文件：读不了时调用方会拿到错误而不是乱码
            }
        }

        /// <summary>没有 BOM、又不是合法 UTF-8 时按哪个代码页读：系统 ANSI 代码页，取不到（或是 UTF-8 本身）时用 GBK。</summary>
        public static int SystemAnsiCodePage
        {
            get
            {
                int cp = 0;
                try { cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage; }
                catch (Exception) { } // 取不到区域设置：下面按 GBK
                if (cp <= 0 || cp == 65001) return GbkCodePage;
                if (cp == 1252 && !LooksWestern()) return GbkCodePage;
                return cp;
            }
        }

        // 1252 是英文 / 西欧系统的 ANSI 代码页；Linux 上的 .NET 也报 1252。我们的用户主要是中文 Windows，
        // 只有当前界面语言确实是西欧语言时才信 1252，否则按 GBK 读，避免把 GBK 的中文当成 1252 的拉丁字母写坏
        private static bool LooksWestern()
        {
            try
            {
                string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                return lang != "zh" && lang != "iv";
            }
            catch (Exception) { return false; }
        }

        public static TextFileFormat FromChoice(FileEncodingChoice choice)
        {
            switch (choice)
            {
                case FileEncodingChoice.Utf8Bom: return TextFileFormat.Utf8Bom;
                case FileEncodingChoice.Gbk: return new TextFileFormat(TextFileEncodingKind.CodePage, GbkCodePage);
                default: return TextFileFormat.Utf8;
            }
        }

        /// <summary>识别编码（不解码全文以外的工作）。<paramref name="fallbackCodePage"/> 为 0 时用 <see cref="SystemAnsiCodePage"/>。</summary>
        public static TextFileFormat Detect(byte[] bytes, int fallbackCodePage = 0)
        {
            if (bytes == null || bytes.Length == 0) return TextFileFormat.Utf8;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return TextFileFormat.Utf8Bom;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return new TextFileFormat(TextFileEncodingKind.Utf16LE);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return new TextFileFormat(TextFileEncodingKind.Utf16BE);
            if (IsValidUtf8(bytes, 0)) return TextFileFormat.Utf8;
            return new TextFileFormat(TextFileEncodingKind.CodePage, fallbackCodePage > 0 ? fallbackCodePage : SystemAnsiCodePage);
        }

        /// <summary>按识别出的编码解码（去掉 BOM）。</summary>
        public static string Decode(byte[] bytes, out TextFileFormat format, int fallbackCodePage = 0)
        {
            format = Detect(bytes, fallbackCodePage);
            if (bytes == null || bytes.Length == 0) return string.Empty;
            switch (format.Kind)
            {
                case TextFileEncodingKind.Utf8Bom: return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
                case TextFileEncodingKind.Utf16LE: return new UnicodeEncoding(false, false).GetString(bytes, 2, bytes.Length - 2);
                case TextFileEncodingKind.Utf16BE: return new UnicodeEncoding(true, false).GetString(bytes, 2, bytes.Length - 2);
                case TextFileEncodingKind.Utf8: return new UTF8Encoding(false).GetString(bytes);
                default: return GetCodePage(format.CodePage, strict: false).GetString(bytes);
            }
        }

        /// <summary>
        /// 编码成要写盘的字节（带 BOM 的格式写 BOM）。有字符在目标代码页里没有（会变成“?”）时返回 false，
        /// <paramref name="bytes"/> 为 null。
        /// </summary>
        public static bool TryEncode(string text, TextFileFormat format, out byte[] bytes)
        {
            text = text ?? string.Empty;
            bytes = null;
            try
            {
                switch (format.Kind)
                {
                    case TextFileEncodingKind.Utf8: bytes = new UTF8Encoding(false, true).GetBytes(text); return true;
                    case TextFileEncodingKind.Utf8Bom: bytes = WithPreamble(new byte[] { 0xEF, 0xBB, 0xBF }, new UTF8Encoding(false, true).GetBytes(text)); return true;
                    case TextFileEncodingKind.Utf16LE: bytes = WithPreamble(new byte[] { 0xFF, 0xFE }, new UnicodeEncoding(false, false, true).GetBytes(text)); return true;
                    case TextFileEncodingKind.Utf16BE: bytes = WithPreamble(new byte[] { 0xFE, 0xFF }, new UnicodeEncoding(true, false, true).GetBytes(text)); return true;
                    default: bytes = GetCodePage(format.CodePage, strict: true).GetBytes(text); return true;
                }
            }
            catch (EncoderFallbackException) { bytes = null; return false; }
            catch (ArgumentException) { bytes = null; return false; } // 代码页不可用
            catch (NotSupportedException) { bytes = null; return false; }
        }

        /// <summary>
        /// 改写已有文件时用哪个编码：保留原编码，或按设置转成默认编码（转换会丢字时退回原编码）。
        /// </summary>
        public static TextFileFormat ChooseForExisting(TextFileFormat original, string text, FileEncodingChoice defaultChoice, ExistingFileEncodingMode mode)
        {
            if (mode != ExistingFileEncodingMode.ConvertToDefault) return original;
            var target = FromChoice(defaultChoice);
            return TryEncode(text, target, out _) ? target : original;
        }

        /// <summary>
        /// VS 保存时要换成的编码；null = 不换。只认识 UTF-8 / UTF-16 / 代码页三类，判断逻辑和 <see cref="ChooseForExisting"/> 一致。
        /// </summary>
        public static TextFileFormat? ChooseOnSave(TextFileFormat current, string text, FileEncodingChoice defaultChoice, SaveEncodingMode mode)
        {
            switch (mode)
            {
                case SaveEncodingMode.StripUtf8Bom:
                    return current.Kind == TextFileEncodingKind.Utf8Bom ? TextFileFormat.Utf8 : (TextFileFormat?)null;
                case SaveEncodingMode.ConvertToDefault:
                    var target = FromChoice(defaultChoice);
                    if (target.Equals(current)) return null;
                    return TryEncode(text, target, out _) ? target : (TextFileFormat?)null;
                default:
                    return null;
            }
        }

        /// <summary>读文件：文本 + 编码。读不了时抛 IO 异常（调用方照常处理）。</summary>
        public static string ReadFile(string path, out TextFileFormat format) => Decode(File.ReadAllBytes(path), out format);

        /// <summary>
        /// 写文件；编码不了时不写、返回 false。不改只读属性：P4 未签出的文件只读，这里会抛 UnauthorizedAccessException，
        /// 由调用方提示“先签出”，而不是偷偷去掉只读。
        /// </summary>
        public static bool TryWriteFile(string path, string text, TextFileFormat format)
        {
            if (!TryEncode(text, format, out var bytes)) return false;
            File.WriteAllBytes(path, bytes);
            return true;
        }

        /// <summary>严格的 UTF-8 校验（拒绝过长编码、代理区、超出 U+10FFFF）。</summary>
        public static bool IsValidUtf8(byte[] b, int start)
        {
            int i = start, n = b.Length;
            while (i < n)
            {
                byte c = b[i];
                if (c < 0x80) { i++; continue; }
                int need;
                if (c >= 0xC2 && c <= 0xDF) need = 1;
                else if (c >= 0xE0 && c <= 0xEF) need = 2;
                else if (c >= 0xF0 && c <= 0xF4) need = 3;
                else return false;
                if (i + need >= n) return false; // 截断的多字节序列
                byte c1 = b[i + 1];
                if ((c1 & 0xC0) != 0x80) return false;
                if (c == 0xE0 && c1 < 0xA0) return false;          // 过长
                if (c == 0xED && c1 > 0x9F) return false;          // 代理区
                if (c == 0xF0 && c1 < 0x90) return false;          // 过长
                if (c == 0xF4 && c1 > 0x8F) return false;          // > U+10FFFF
                for (int k = 2; k <= need; k++)
                    if ((b[i + k] & 0xC0) != 0x80) return false;
                i += need + 1;
            }
            return true;
        }

        private static Encoding GetCodePage(int codePage, bool strict) =>
            strict
                ? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback)
                : Encoding.GetEncoding(codePage);

        private static byte[] WithPreamble(byte[] preamble, byte[] body)
        {
            var all = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
            return all;
        }
    }
}
