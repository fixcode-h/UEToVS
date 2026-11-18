using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UEToVS.Contracts;

namespace UEToVS.Engine.Navigation
{
    /// <summary>
    /// WITH_EDITOR、UE_BUILD_SHIPPING、&lt;MODULE&gt;_API 这类宏由 UBT 定义：写在编译命令行和 UE 5.x 的
    /// <c>Intermediate/Build/&lt;Platform&gt;/[&lt;Arch&gt;/]&lt;Target&gt;/&lt;Config&gt;/&lt;Module&gt;/Definitions.&lt;Module&gt;.h</c> 里，
    /// 手写头文件里通常只有 <c>#ifndef X / #define X 0</c> 的兜底（Build.h），有的连兜底都没有。
    /// 索引里找不到宏的 #define 时到这里找：只探几层目录（不扫整个 Intermediate），多份时取最近编译的那份。
    /// </summary>
    public static class UbtDefinitionLocator
    {
        private static readonly HashSet<string> KnownUbtMacros = new HashSet<string>(StringComparer.Ordinal)
        {
            "WITH_EDITOR", "WITH_EDITORONLY_DATA", "WITH_ENGINE", "WITH_UNREAL_DEVELOPER_TOOLS", "WITH_UNREAL_TARGET_DEVELOPER_TOOLS",
            "WITH_PLUGIN_SUPPORT", "WITH_SERVER_CODE", "WITH_DEV_AUTOMATION_TESTS", "WITH_PERF_AUTOMATION_TESTS", "WITH_LOW_LEVEL_TESTS",
            "WITH_APPLICATION_CORE", "WITH_COREUOBJECT", "WITH_VERSE_VM", "WITH_LIVE_CODING", "WITH_PUSH_MODEL", "WITH_IRIS",
            "UE_BUILD_DEBUG", "UE_BUILD_DEVELOPMENT", "UE_BUILD_TEST", "UE_BUILD_SHIPPING", "UE_BUILD_SHIPPING_WITH_EDITOR",
            "UE_BUILD_DOCS", "UE_GAME", "UE_EDITOR", "UE_SERVER", "UE_CLIENT", "UE_IS_ENGINE_MODULE", "UE_PROJECT_NAME",
            "UE_TARGET_NAME", "IS_MONOLITHIC", "IS_PROGRAM", "USE_STATS_WITHOUT_ENGINE", "USE_LOGGING_IN_SHIPPING",
            "UBT_COMPILED_PLATFORM", "UBT_COMPILED_TARGET", "UE_APP_NAME", "UE_ENABLE_ICU", "WITH_CLANG_ADDRESS_SANITIZER",
        };

        /// <summary>UBT 在命令行定义的宏（固定清单，加上模块导出宏 X_API）：找不到定义时用来给出说明。</summary>
        public static bool IsKnownUbtMacro(string name) =>
            !string.IsNullOrEmpty(name) && (KnownUbtMacros.Contains(name) || (name.Length > 4 && name.EndsWith("_API", StringComparison.Ordinal) && IsUpperIdent(name)));

        private static bool IsUpperIdent(string s)
        {
            foreach (char c in s)
                if (!(c == '_' || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
            return true;
        }

        /// <summary><paramref name="macro"/> 在 <paramref name="sourcePath"/> 所属模块最近一份 Definitions.&lt;Module&gt;.h 里的 #define；没有时 null。</summary>
        public static SymbolInfo Find(string sourcePath, string macro)
        {
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(macro)) return null;
            try
            {
                if (!GeneratedBodyLocator.TryOwningModule(sourcePath, out string module, out string root)) return null;
                foreach (var file in DefinitionFiles(Path.Combine(root, "Intermediate", "Build"), module))
                {
                    int line = FindDefine(File.ReadLines(file), macro, out string text);
                    if (line < 0) continue;
                    return new SymbolInfo
                    {
                        Name = macro,
                        Kind = SymbolKind.Macro,
                        Signature = text,
                        Container = Path.GetFileName(file),
                        IsDefinition = true,
                        Module = module,
                        Origin = string.Equals(Path.GetFileName(root), "Engine", StringComparison.OrdinalIgnoreCase) ? ItemOrigin.Engine : ItemOrigin.Project,
                        Location = new SourceLocation { FilePath = file, Line = line, Column = 0 },
                    };
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { }
            return null;
        }

        /// <summary>
        /// buildDir 下 4 层以内的 &lt;Module&gt;/Definitions.&lt;Module&gt;.h，最近写入的在前。
        /// UE 5.0–5.2 是 Win64/UnrealEditor/Development/Module，5.3 起多一层架构 Win64/x64/UnrealEditor/Development/Module。
        /// </summary>
        public static List<string> DefinitionFiles(string buildDir, string module)
        {
            var found = new List<(string Path, DateTime Time)>();
            string name = "Definitions." + module + ".h";
            var level = new List<string> { buildDir };
            for (int depth = 0; depth <= 4 && level.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var d in level)
                {
                    string candidate = Path.Combine(d, module, name);
                    try
                    {
                        if (File.Exists(candidate)) found.Add((candidate, File.GetLastWriteTimeUtc(candidate)));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    // 模块目录本身和 Inc（UHT 输出）里没有 Definitions，不往下走，免得把几百个模块目录都探一遍
                    if (depth < 4)
                        foreach (var sub in GeneratedBodyLocator.SafeDirs(d))
                        {
                            string n = Path.GetFileName(sub);
                            if (!string.Equals(n, "Inc", StringComparison.OrdinalIgnoreCase) && !string.Equals(n, module, StringComparison.OrdinalIgnoreCase))
                                next.Add(sub);
                        }
                }
                // 配置目录（Development 等）下面是一个个模块目录：Definitions 已经在这一层找到了，就不再往下展开
                if (found.Count > 0) break;
                level = next;
            }
            return found.OrderByDescending(f => f.Time).Select(f => f.Path).ToList();
        }

        /// <summary>0-based line of <c>#define macro</c> (text in <paramref name="text"/>), or -1.</summary>
        public static int FindDefine(IEnumerable<string> lines, string macro, out string text)
        {
            int i = 0;
            foreach (var raw in lines)
            {
                var t = raw.TrimStart();
                if (t.StartsWith("#", StringComparison.Ordinal))
                {
                    var rest = t.Substring(1).TrimStart();
                    if (rest.StartsWith("define", StringComparison.Ordinal))
                    {
                        var name = rest.Substring(6);
                        if (name.Length > 0 && (name[0] == ' ' || name[0] == '\t'))
                        {
                            name = name.TrimStart();
                            int end = 0;
                            while (end < name.Length && (char.IsLetterOrDigit(name[end]) || name[end] == '_')) end++;
                            if (end == macro.Length && string.CompareOrdinal(name, 0, macro, 0, end) == 0)
                            {
                                text = t.TrimEnd();
                                return i;
                            }
                        }
                    }
                }
                i++;
            }
            text = null;
            return -1;
        }
    }
}
