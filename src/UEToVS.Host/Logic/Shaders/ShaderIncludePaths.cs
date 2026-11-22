using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace UEToVS.Host.Logic.Shaders
{
    /// <summary>光标在 <c>#include "…"</c> 引号里时：已输入的路径、要替换的那一段（最后一个 / 之后）。</summary>
    public sealed class IncludeCompletionContext
    {
        /// <summary>引号里光标前的全部文本，如 "/Engine/Private/Co"。</summary>
        public string Typed { get; set; }

        /// <summary>最后一个 '/' 之前（含 '/'）的目录部分，如 "/Engine/Private/"；没有 '/' 时为空串。</summary>
        public string Directory { get; set; }

        /// <summary>要被补全项替换的列范围：最后一个 '/' 之后到光标。</summary>
        public int ReplaceStart { get; set; }
        public int ReplaceLength { get; set; }
    }

    /// <summary>
    /// 着色器 #include 的虚拟路径（UE 的 shader 目录映射）：/Engine/ → Engine/Shaders/，/Plugin/&lt;名字&gt;/ → 插件的 Shaders 目录，
    /// /Project/ → 项目的 Shaders 目录（项目里自己 AddShaderSourceDirectoryMapping 的其他映射不认）。纯逻辑：列目录由调用方做。
    /// </summary>
    public static class ShaderIncludePaths
    {
        public static readonly IReadOnlyList<string> VirtualRoots = new[] { "/Engine/", "/Plugin/", "/Project/" };

        /// <summary>补全列出的文件扩展名。</summary>
        public static bool IsShaderInclude(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? string.Empty);
            return ext.Equals(".ush", StringComparison.OrdinalIgnoreCase) || ext.Equals(".usf", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".h", StringComparison.OrdinalIgnoreCase) || ext.Equals(".hlsl", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>光标（列）在 #include 的 "…" 里时返回上下文，否则 null。</summary>
        public static IncludeCompletionContext Analyze(string line, int column)
        {
            if (string.IsNullOrEmpty(line) || column < 0 || column > line.Length) return null;
            string t = line.TrimStart();
            if (!t.StartsWith("#", StringComparison.Ordinal) || !t.Substring(1).TrimStart().StartsWith("include", StringComparison.Ordinal)) return null;
            int q1 = line.IndexOf('"');
            if (q1 < 0 || column <= q1) return null;
            int q2 = line.IndexOf('"', q1 + 1);
            if (q2 >= 0 && column > q2) return null;
            string typed = line.Substring(q1 + 1, column - q1 - 1);
            int slash = typed.LastIndexOfAny(new[] { '/', '\\' });
            return new IncludeCompletionContext
            {
                Typed = typed,
                Directory = slash < 0 ? string.Empty : typed.Substring(0, slash + 1).Replace('\\', '/'),
                ReplaceStart = q1 + 1 + slash + 1,
                ReplaceLength = column - (q1 + 1 + slash + 1),
            };
        }

        /// <summary>
        /// 虚拟目录（以 / 结尾，如 "/Engine/Private/"）对应的磁盘目录；"/Plugin/X/…" 用 <paramref name="pluginShaderDirs"/>（插件名 → Shaders 目录）。
        /// 相对路径按 <paramref name="currentFile"/> 所在目录。映射不到返回 null；"/" 和 "/Plugin/" 本身没有磁盘目录，也返回 null。
        /// </summary>
        public static string MapDirectory(string virtualDir, string currentFile, string engineDir, string projectDir, IReadOnlyDictionary<string, string> pluginShaderDirs)
        {
            string d = (virtualDir ?? string.Empty).Replace('\\', '/');
            if (d.StartsWith("/", StringComparison.Ordinal))
            {
                if (d.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase))
                    return engineDir == null ? null : Join(Path.Combine(engineDir, "Shaders"), d.Substring("/Engine/".Length));
                if (d.StartsWith("/Project/", StringComparison.OrdinalIgnoreCase))
                    return projectDir == null ? null : Join(Path.Combine(projectDir, "Shaders"), d.Substring("/Project/".Length));
                if (d.StartsWith("/Plugin/", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = d.Substring("/Plugin/".Length);
                    int slash = rest.IndexOf('/');
                    if (slash <= 0 || pluginShaderDirs == null) return null;
                    string name = rest.Substring(0, slash);
                    foreach (var kv in pluginShaderDirs)
                        if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return Join(kv.Value, rest.Substring(slash + 1));
                }
                return null;
            }
            string baseDir = Path.GetDirectoryName(currentFile ?? string.Empty);
            return string.IsNullOrEmpty(baseDir) ? null : Join(baseDir, d);
        }

        /// <summary>
        /// "/Plugin/X/…" 也走插件名映射的 include 解析（<see cref="HlslLexer.ResolveInclude"/> 要的是虚拟根 → 目录）。
        /// </summary>
        public static Dictionary<string, string> AsVirtualRoots(IReadOnlyDictionary<string, string> pluginShaderDirs)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (pluginShaderDirs != null)
                foreach (var kv in pluginShaderDirs) d["/Plugin/" + kv.Key] = kv.Value;
            return d;
        }

        private static string Join(string dir, string rel)
        {
            rel = (rel ?? string.Empty).Trim('/');
            return rel.Length == 0 ? dir : Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    /// <summary>列目录的方式（宿主：磁盘；单测：内存）。</summary>
    public interface IDirectoryLister
    {
        IEnumerable<string> Directories(string dir);
        IEnumerable<string> Files(string dir, string pattern);
    }

    /// <summary>
    /// 找 Plugins 目录下的插件（.uplugin）：按目录往下走，遇到含 .uplugin 的目录就停（插件里不再套插件），跳过 Source / Content /
    /// Binaries / Intermediate 之类的大目录，所以引擎几百个插件也只看几千个目录。只在第一次用到时跑一次，结果由调用方缓存。纯逻辑。
    /// </summary>
    public static class PluginDiscovery
    {
        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Source", "Content", "Binaries", "Intermediate", "Saved", "Resources", "Shaders", "Config", "ThirdParty", "Build", "Docs",
            "Documentation", "Extras", "Tests", "DerivedDataCache", ".git", "Localization",
        };

        public const int MaxDirectories = 20000;

        /// <summary>插件名 → .uplugin 路径（同名的先到先得：调用方先给项目再给引擎）。</summary>
        public static Dictionary<string, string> Find(IEnumerable<string> pluginRoots, IDirectoryLister fs, CancellationToken ct)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int visited = 0;
            foreach (var root in pluginRoots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0 && visited < MaxDirectories)
                {
                    ct.ThrowIfCancellationRequested();
                    string dir = stack.Pop();
                    visited++;
                    bool isPlugin = false;
                    foreach (var f in Safe(() => fs.Files(dir, "*.uplugin")))
                    {
                        isPlugin = true;
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (!found.ContainsKey(name)) found[name] = f;
                    }
                    if (isPlugin) continue;
                    foreach (var sub in Safe(() => fs.Directories(dir)))
                        if (!Skip.Contains(Path.GetFileName(sub))) stack.Push(sub);
                }
            }
            return found;
        }

        private static IEnumerable<string> Safe(Func<IEnumerable<string>> list)
        {
            try
            {
                return new List<string>(list());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
