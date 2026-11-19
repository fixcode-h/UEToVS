using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>
    /// v0.9.4 引擎代码只读（参考 IDEA 的库源码只读）：打开引擎目录下的文件时编辑器默认不让改，顶部提示条点“启用编辑”才能改。
    /// 这里只放判断，不碰 VS 类型：哪些文件算引擎代码、什么时候该锁。只在编辑器层锁（视图禁止输入），不改磁盘文件的只读属性：
    /// 改属性会让 git / p4 / UBT 看到变化，也会让 VS 在保存时弹“文件只读”。
    /// </summary>
    public static class EngineReadOnlyPolicy
    {
        /// <summary>
        /// 规范化成比较用的目录前缀：反斜杠、末尾带分隔符、去掉多余的分隔符。null / 空 = 没有引擎根。
        /// 传入的可以是引擎根（含 Engine\ 的那层，例如 D:\UE_5.4）或 Engine 目录本身，两种都落到 …\Engine\。
        /// </summary>
        public static string EngineDirPrefix(string engineRoot)
        {
            if (string.IsNullOrWhiteSpace(engineRoot)) return null;
            string p = Normalize(engineRoot.Trim()).TrimEnd('\\');
            if (p.Length == 0) return null;
            if (!p.EndsWith("\\Engine", StringComparison.OrdinalIgnoreCase) && !string.Equals(p, "Engine", StringComparison.OrdinalIgnoreCase))
                p += "\\Engine";
            return p + "\\";
        }

        /// <summary>文件在引擎的 Engine\ 目录下（源码、插件、Shaders、Config、Build.cs 都算）。大小写不敏感，/ 和 \ 都认。</summary>
        public static bool IsEngineFile(string filePath, string engineRoot)
        {
            string prefix = EngineDirPrefix(engineRoot);
            if (prefix == null || string.IsNullOrWhiteSpace(filePath)) return false;
            string f = Normalize(filePath.Trim());
            return f.Length > prefix.Length && f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 打开（或设置变化后重新判断）时这个文件该不该锁。
        /// <paramref name="engineSolution"/>：打开的是引擎自己的解决方案（UE5.sln 旁边没有游戏项目），这时就是在改引擎，不锁。
        /// <paramref name="unlockedThisSession"/>：这次 VS 会话里已经点过“启用编辑”的文件，重新打开不再锁（和 IDEA 一样）。
        /// </summary>
        public static bool ShouldLock(bool enabled, bool unrealSolution, bool engineSolution, string filePath, string engineRoot,
                                      ICollection<string> unlockedThisSession)
        {
            if (!enabled || !unrealSolution || engineSolution) return false;
            if (!IsEngineFile(filePath, engineRoot)) return false;
            return unlockedThisSession == null || !unlockedThisSession.Contains(Key(filePath));
        }

        /// <summary>会话内“已解锁”集合的键：规范化后的完整路径（集合用 OrdinalIgnoreCase 比较）。</summary>
        public static string Key(string filePath) => string.IsNullOrWhiteSpace(filePath) ? string.Empty : Normalize(filePath.Trim());

        /// <summary>日志里的相对路径：Engine\ 之后的部分，方便看又不暴露盘符和用户目录。</summary>
        public static string RelativeToEngine(string filePath, string engineRoot)
        {
            string prefix = EngineDirPrefix(engineRoot);
            string f = Key(filePath);
            if (prefix == null || !f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return f;
            return "Engine\\" + f.Substring(prefix.Length);
        }

        private static string Normalize(string p)
        {
            var sb = new System.Text.StringBuilder(p.Length);
            char last = '\0';
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i] == '/' ? '\\' : p[i];
                // 合并重复分隔符，但保留 UNC 开头的 \\server
                if (c == '\\' && last == '\\' && i > 1) continue;
                sb.Append(c);
                last = c;
            }
            return sb.ToString();
        }
    }
}
