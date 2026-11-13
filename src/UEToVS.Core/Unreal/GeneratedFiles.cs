using System;

namespace UEToVS.Core.Unreal
{
    /// <summary>
    /// UE 生成的 C++ 文件（UHT 的 X.generated.h / X.gen.cpp、Intermediate 下的其他产物）。跳转、查找引用、悬停排序时
    /// 手写的 .h / .cpp 排在它们前面：生成文件只是宏展开，用户想看的几乎总是手写的那一处。
    /// </summary>
    public static class GeneratedFiles
    {
        /// <summary>路径是 UE 生成的文件：*.generated.h、*.gen.cpp（含 .init.gen.cpp），或位于某一级 Intermediate 目录下。</summary>
        public static bool IsGenerated(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gen.cpp", StringComparison.OrdinalIgnoreCase))
                return true;
            // 手写代码不会放在 Intermediate 下（UBT 每次编译都可能重写这个目录）
            return path.IndexOf("\\Intermediate\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("/Intermediate/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>排序键：手写文件 0，生成文件 1。</summary>
        public static int Rank(string path) => IsGenerated(path) ? 1 : 0;
    }
}
