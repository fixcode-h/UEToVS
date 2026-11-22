using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Host.Logic.Files
{
    /// <summary>v0.9.13 “VS 保存时统一编码”只管这些扩展名：C++ / C# 构建脚本 / shader / ini / UE 描述文件。别的（.resx、.xml…）不碰。</summary>
    public static class SaveEncodingPolicy
    {
        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".h", ".hpp", ".hh", ".hxx", ".inl", ".c", ".cc", ".cpp", ".cxx", ".ipp",
            ".cs", ".usf", ".ush", ".hlsl", ".ini", ".uproject", ".uplugin",
        };

        public static bool AppliesTo(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return Extensions.Contains(Path.GetExtension(path)); }
            catch (ArgumentException) { return false; }
        }
    }
}
