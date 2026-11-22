using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Host.Logic.ProjectFiles
{
    /// <summary>JSON 里的一个字符串（键或值）和它所在的位置。</summary>
    public sealed class JsonStringToken
    {
        public string Value { get; set; }

        /// <summary>引号里内容的起点和长度。</summary>
        public int Start { get; set; }
        public int Length { get; set; }
        public bool IsKey { get; set; }

        /// <summary>是值时它所属的键（数组里的字符串为数组的键）；是键时为它自己。</summary>
        public string Property { get; set; }

        /// <summary>
        /// 从根往里的容器路径，对象写 "{"、数组写 "[键"，如 Modules 数组里对象的键为 ["{", "[Modules", "{"]。
        /// </summary>
        public IReadOnlyList<string> Path { get; set; }
    }

    /// <summary>
    /// .uproject / .uplugin 的补全和校验：容错的单遍 JSON 字符串扫描（描述文件不大，编辑中常常不合法）、各层对象可以写的键、
    /// Modules 里的模块名。纯逻辑。
    /// </summary>
    public static class DescriptorLogic
    {
        public static readonly IReadOnlyList<string> ProjectKeys = new[]
        {
            "FileVersion", "EngineAssociation", "Category", "Description", "Modules", "Plugins", "TargetPlatforms",
            "AdditionalRootDirectories", "AdditionalPluginDirectories", "DisableEnginePluginsByDefault", "IsEnterpriseProject",
            "EpicSampleNameHash", "PreBuildSteps", "PostBuildSteps",
        };

        public static readonly IReadOnlyList<string> PluginKeys = new[]
        {
            "FileVersion", "Version", "VersionName", "FriendlyName", "Description", "Category", "CreatedBy", "CreatedByURL", "DocsURL",
            "MarketplaceURL", "SupportURL", "EngineVersion", "EnabledByDefault", "CanContainContent", "CanContainVerse", "IsBetaVersion",
            "IsExperimentalVersion", "Installed", "IsHidden", "ExplicitlyLoaded", "HasExplicitPlatforms", "SupportedTargetPlatforms",
            "SupportedPrograms", "RequiresBuildPlatform", "IsSealed", "NoCode", "Modules", "Plugins", "LocalizationTargets",
            "PreBuildSteps", "PostBuildSteps",
        };

        public static readonly IReadOnlyList<string> ModuleKeys = new[]
        {
            "Name", "Type", "LoadingPhase", "AdditionalDependencies", "PlatformAllowList", "PlatformDenyList", "TargetAllowList",
            "TargetDenyList", "TargetConfigurationAllowList", "TargetConfigurationDenyList", "ProgramAllowList", "ProgramDenyList",
            "HasExplicitPlatforms",
        };

        public static readonly IReadOnlyList<string> PluginReferenceKeys = new[]
        {
            "Name", "Enabled", "Optional", "Description", "MarketplaceURL", "PlatformAllowList", "PlatformDenyList", "TargetAllowList",
            "TargetDenyList", "TargetConfigurationAllowList", "TargetConfigurationDenyList", "HasExplicitPlatforms", "Version",
        };

        public static readonly IReadOnlyList<string> LocalizationTargetKeys = new[] { "Name", "LoadingPolicy", "ConfigGenerationPolicy" };

        public static readonly IReadOnlyList<string> LoadingPolicies = new[] { "Never", "Always", "Editor", "Game", "PropertyNames", "ToolTips" };

        /// <summary>所有字符串，按出现顺序。</summary>
        public static List<JsonStringToken> Strings(string text)
        {
            var list = new List<JsonStringToken>();
            Walk(text, -1, list);
            return list;
        }

        /// <summary>光标所在的字符串（含刚输入、还没闭合的），不在字符串里时返回 null。</summary>
        public static JsonStringToken StringAt(string text, int offset)
        {
            if (string.IsNullOrEmpty(text) || offset < 0 || offset > text.Length) return null;
            var list = new List<JsonStringToken>();
            return Walk(text, offset, list);
        }

        private static JsonStringToken Walk(string text, int stopAt, List<JsonStringToken> output)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var stack = new List<(char Kind, string Key)>();
            string lastKey = null;
            bool expectKey = false;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '"')
                {
                    int s = i + 1;
                    int e = s;
                    while (e < text.Length && text[e] != '"' && text[e] != '\n')
                    {
                        if (text[e] == '\\') e++;
                        e++;
                    }
                    int end = Math.Min(e, text.Length);
                    bool closed = end < text.Length && text[end] == '"';
                    bool inObject = stack.Count > 0 && stack[stack.Count - 1].Kind == '{';
                    bool isKey = inObject && expectKey;
                    var tok = new JsonStringToken
                    {
                        Value = text.Substring(s, end - s), Start = s, Length = end - s, IsKey = isKey,
                        Property = isKey ? text.Substring(s, end - s) : inObject ? lastKey : stack.Count > 0 ? stack[stack.Count - 1].Key : null,
                        Path = PathOf(stack),
                    };
                    if (stopAt >= 0 && stopAt >= s && stopAt <= end) return tok;
                    output.Add(tok);
                    if (isKey)
                    {
                        lastKey = tok.Value;
                        expectKey = false;
                    }
                    i = closed ? end + 1 : end;
                    continue;
                }
                if (stopAt >= 0 && i >= stopAt) return null;
                switch (c)
                {
                    case '{':
                        stack.Add(('{', KeyFor(stack, lastKey)));
                        expectKey = true;
                        lastKey = null;
                        break;
                    case '[':
                        stack.Add(('[', KeyFor(stack, lastKey)));
                        break;
                    case '}':
                    case ']':
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        expectKey = false;
                        lastKey = null;
                        break;
                    case ',':
                        if (stack.Count > 0 && stack[stack.Count - 1].Kind == '{')
                        {
                            expectKey = true;
                            lastKey = null;
                        }
                        break;
                    case '/':
                        if (i + 1 < text.Length && text[i + 1] == '/')
                        {
                            while (i < text.Length && text[i] != '\n') i++;
                            continue;
                        }
                        break;
                }
                i++;
            }
            return null;
        }

        private static string KeyFor(List<(char Kind, string Key)> st, string key) =>
            st.Count > 0 && st[st.Count - 1].Kind == '{' ? key : st.Count > 0 ? st[st.Count - 1].Key : null;

        private static IReadOnlyList<string> PathOf(List<(char Kind, string Key)> stack)
        {
            var p = new string[stack.Count];
            for (int k = 0; k < stack.Count; k++) p[k] = stack[k].Kind == '{' ? "{" : "[" + stack[k].Key;
            return p;
        }

        /// <summary>光标在一个键里时，这一层对象可以写的键；不认识这一层时返回 null。</summary>
        public static IReadOnlyList<string> KeysFor(string descriptorPath, JsonStringToken token)
        {
            if (token == null || !token.IsKey || token.Path == null) return null;
            bool plugin = Path.GetExtension(descriptorPath ?? string.Empty).Equals(".uplugin", StringComparison.OrdinalIgnoreCase);
            var p = token.Path;
            if (p.Count == 1 && p[0] == "{") return plugin ? PluginKeys : ProjectKeys;
            if (p.Count == 3 && p[0] == "{" && p[2] == "{")
            {
                switch (p[1])
                {
                    case "[Modules": return ModuleKeys;
                    case "[Plugins": return PluginReferenceKeys;
                    case "[LocalizationTargets": return plugin ? LocalizationTargetKeys : null;
                }
            }
            return null;
        }

        /// <summary>光标在值里时可选的固定取值：先看已有的 <see cref="ProjectFileLogic.ValuesFor"/>，再补 LocalizationTargets 的 LoadingPolicy。</summary>
        public static IReadOnlyList<string> ValuesFor(JsonCaret caret)
        {
            var v = ProjectFileLogic.ValuesFor(caret);
            if (v != null) return v;
            if (caret != null && caret.InValue && caret.Property == "LoadingPolicy" && caret.Array == "LocalizationTargets") return LoadingPolicies;
            return null;
        }

        /// <summary>光标在 Plugins 数组里某个对象的 "Name" 值里时为 true（补全插件名）。</summary>
        public static bool IsPluginName(JsonStringToken token) =>
            token != null && !token.IsKey && token.Property == "Name" && token.Path != null && token.Path.Count == 3 && token.Path[1] == "[Plugins";

        /// <summary>Modules 数组里每个对象的 "Name" 值（模块名校验）。</summary>
        public static List<JsonStringToken> ModuleNames(string text)
        {
            var list = new List<JsonStringToken>();
            foreach (var t in Strings(text))
                if (!t.IsKey && t.Property == "Name" && t.Path.Count == 3 && t.Path[0] == "{" && t.Path[1] == "[Modules" && t.Path[2] == "{" &&
                    !string.IsNullOrWhiteSpace(t.Value))
                    list.Add(t);
            return list;
        }

        /// <summary>同一层对象里已经写过的键（补全时不再列出）。</summary>
        public static HashSet<string> ExistingKeys(string text, JsonStringToken token)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (token?.Path == null) return set;
            // 简化：同一路径、位置在同一个对象里（往前到上一个同深度 '{'、往后到对应的 '}'）的键
            int open = ObjectStart(text, token.Start - 2);
            int close = ObjectEnd(text, open);
            foreach (var t in Strings(text))
                if (t.IsKey && t.Start > open && t.Start < close && t.Start != token.Start && SamePath(t.Path, token.Path)) set.Add(t.Value);
            return set;
        }

        private static bool SamePath(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static int ObjectStart(string text, int from)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = Math.Min(from, text.Length - 1); i >= 0; i--)
            {
                char c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\')) inStr = !inStr;
                if (inStr) continue;
                if (c == '}' || c == ']') depth++;
                else if (c == '{' || c == '[')
                {
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return -1;
        }

        private static int ObjectEnd(string text, int open)
        {
            if (open < 0) return text.Length;
            int depth = 0;
            bool inStr = false;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' && text[i - 1] != '\\') inStr = !inStr;
                if (inStr) continue;
                if (c == '{' || c == '[') depth++;
                else if ((c == '}' || c == ']') && --depth == 0) return i;
            }
            return text.Length;
        }
    }
}
