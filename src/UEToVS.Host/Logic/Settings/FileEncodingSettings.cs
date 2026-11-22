using System;
using System.Collections.Generic;
using UEToVS.Core.Text;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Settings
{
    /// <summary>
    /// v0.9.13 设置 > 编辑 >“文件编码”（用户 2026-11-22 要求，默认 UTF-8 无 BOM）。和 EditorFeatureSettings 一样自成一处：
    /// HostSettingsSchema 调 <see cref="AddDescriptors"/> / <see cref="ToValues"/> / <see cref="TrySet"/>。
    /// </summary>
    public static class FileEncodingSettings
    {
        public const string DefaultKey = "files.encoding";
        public const string ExistingKey = "files.existingEncoding";
        public const string OnSaveKey = "files.encodingOnSave";

        public static void AddDescriptors(List<SettingDescriptor> list, string category)
        {
            const string g = "文件编码", gEn = "File encoding";
            var def = new SettingDescriptor
            {
                Category = category, Key = DefaultKey, Kind = SettingKind.Choice, TitleZh = "默认编码", TitleEn = "Default encoding",
                DescriptionZh = "UEToVS 新建的文件（新建 UE 类、.mcp.json 等）用这个编码；“改已有文件时”选了转换也转成它。",
                DescriptionEn = "Files UEToVS creates (New UE Class, .mcp.json…) use this encoding; existing files are converted to it when \"When editing existing files\" says so.",
                Default = nameof(FileEncodingChoice.Utf8NoBom), GroupZh = g, GroupEn = gEn, Keywords = "encoding utf-8 utf8 bom gbk ansi 编码 字符集",
                Options = new[]
                {
                    new SettingOption(nameof(FileEncodingChoice.Utf8NoBom), "UTF-8（无 BOM）", "UTF-8 (no BOM)"),
                    new SettingOption(nameof(FileEncodingChoice.Utf8Bom), "UTF-8（带 BOM）", "UTF-8 with BOM"),
                    new SettingOption(nameof(FileEncodingChoice.Gbk), "GBK", "GBK"),
                },
            };
            list.Add(def);
            list.Add(new SettingDescriptor
            {
                Category = category, Key = ExistingKey, Kind = SettingKind.Choice, TitleZh = "改已有文件时", TitleEn = "When editing existing files",
                DescriptionZh = "重命名、加 Build.cs 依赖、写 Core Redirects、MCP 重命名等改动已有文件时：保留原编码（只改那几行，diff 干净），或转成默认编码（转换会丢字时仍保留原编码）。",
                DescriptionEn = "When rename, Build.cs dependencies, Core Redirects, MCP rename… change an existing file: keep its encoding (only those lines change), or convert it to the default encoding (kept when converting would lose characters).",
                Default = nameof(ExistingFileEncodingMode.Preserve), GroupZh = g, GroupEn = gEn, Keywords = "encoding preserve convert 编码 保留 转换",
                Options = new[]
                {
                    new SettingOption(nameof(ExistingFileEncodingMode.Preserve), "保留原编码", "Keep its encoding"),
                    new SettingOption(nameof(ExistingFileEncodingMode.ConvertToDefault), "转为默认编码", "Convert to the default"),
                },
            });
            list.Add(new SettingDescriptor
            {
                Category = category, Key = OnSaveKey, Kind = SettingKind.Choice, TitleZh = "VS 保存时统一编码", TitleEn = "Normalize encoding when VS saves",
                DescriptionZh = "在 VS 里保存 C++ / ini / Build.cs 等文本文件时：不改；只去掉 UTF-8 的 BOM；或能无损转换时全部转成默认编码（GBK 文件会变成整文件改动，确认团队都要统一再开）。只在 UE 解决方案里生效。",
                DescriptionEn = "When VS saves C++ / ini / Build.cs and other text files: leave them; only drop the UTF-8 BOM; or convert everything to the default encoding when that loses nothing (a GBK file then changes as a whole; turn on when the team agrees). UE solutions only.",
                Default = nameof(SaveEncodingMode.Off), GroupZh = g, GroupEn = gEn, Keywords = "encoding save bom 编码 保存 统一",
                Options = new[]
                {
                    new SettingOption(nameof(SaveEncodingMode.Off), "不改", "Off"),
                    new SettingOption(nameof(SaveEncodingMode.StripUtf8Bom), "只去掉 UTF-8 BOM", "Drop the UTF-8 BOM only"),
                    new SettingOption(nameof(SaveEncodingMode.ConvertToDefault), "转为默认编码", "Convert to the default"),
                },
            });
        }

        public static void ToValues(HostSettings s, IDictionary<string, string> d)
        {
            d[DefaultKey] = s.FileEncoding.ToString();
            d[ExistingKey] = s.ExistingFileEncoding.ToString();
            d[OnSaveKey] = s.SaveEncoding.ToString();
        }

        public static bool TrySet(HostSettings s, string key, string v)
        {
            switch (key)
            {
                case DefaultKey: s.FileEncoding = Parse(v, FileEncodingChoice.Utf8NoBom); return true;
                case ExistingKey: s.ExistingFileEncoding = Parse(v, ExistingFileEncodingMode.Preserve); return true;
                case OnSaveKey: s.SaveEncoding = Parse(v, SaveEncodingMode.Off); return true;
                default: return false;
            }
        }

        public static void Describe(HostSettings s, IDictionary<string, object> d)
        {
            d["fileEncoding"] = s.FileEncoding.ToString();
            d["existingFileEncoding"] = s.ExistingFileEncoding.ToString();
            d["saveEncoding"] = s.SaveEncoding.ToString();
        }

        private static T Parse<T>(string v, T fallback) where T : struct =>
            Enum.TryParse(v, true, out T r) && Enum.IsDefined(typeof(T), r) ? r : fallback;
    }
}
