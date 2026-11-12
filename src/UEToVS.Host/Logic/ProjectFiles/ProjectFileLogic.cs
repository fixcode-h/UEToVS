using System;
using System.Collections.Generic;
using System.IO;

namespace UEToVS.Host.Logic.ProjectFiles
{
    public enum DescriptorTargetKind
    {
        None,
        /// <summary>A module name: open &lt;Name&gt;.Build.cs.</summary>
        Module,
        /// <summary>A plugin name: open &lt;Name&gt;.uplugin.</summary>
        Plugin,
    }

    /// <summary>Where the caret is inside a .uproject / .uplugin JSON document.</summary>
    public sealed class JsonCaret
    {
        /// <summary>Key of the value the caret is in (e.g. "Type"); for a string directly in an array, the array's key.</summary>
        public string Property { get; set; }
        /// <summary>Key of the closest enclosing array ("Modules", "Plugins", "AdditionalDependencies" …), or null.</summary>
        public string Array { get; set; }
        /// <summary>True when the caret is inside a string value (not a key).</summary>
        public bool InValue { get; set; }
        /// <summary>The string's content and its span (excluding the quotes).</summary>
        public string Value { get; set; }
        public int ValueStart { get; set; }
        public int ValueLength { get; set; }
    }

    /// <summary>
    /// Pure helpers for UE descriptor files (.uproject / .uplugin, JSON) and config files (.ini): value completion lists,
    /// what a name navigates to, and the class a <c>[/Script/Module.Class]</c> section configures.
    /// </summary>
    public static class ProjectFileLogic
    {
        public static readonly IReadOnlyList<string> ModuleTypes = new[]
        {
            "Runtime", "RuntimeNoCommandlet", "RuntimeAndProgram", "CookedOnly", "UncookedOnly", "Developer", "DeveloperTool",
            "Editor", "EditorNoCommandlet", "EditorAndProgram", "Program", "ServerOnly", "ClientOnly", "ClientOnlyNoCommandlet",
        };

        public static readonly IReadOnlyList<string> LoadingPhases = new[]
        {
            "EarliestPossible", "PostConfigInit", "PostSplashScreen", "PreEarlyLoadingScreen", "PreLoadingScreen", "PreDefault",
            "Default", "PostDefault", "PostEngineInit", "None",
        };

        public static readonly IReadOnlyList<string> Platforms = new[]
        {
            "Win64", "Mac", "Linux", "LinuxArm64", "Android", "IOS", "TVOS", "VisionOS",
        };

        public static readonly IReadOnlyList<string> TargetTypes = new[] { "Game", "Editor", "Client", "Server", "Program" };

        public static readonly IReadOnlyList<string> TargetConfigurations = new[] { "Debug", "DebugGame", "Development", "Shipping", "Test" };

        public static bool IsDescriptor(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty);
            return ext.Equals(".uproject", StringComparison.OrdinalIgnoreCase) || ext.Equals(".uplugin", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsIni(string path) => Path.GetExtension(path ?? string.Empty).Equals(".ini", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Locates <paramref name="offset"/> in a JSON document: the enclosing key / array and the string under the caret.
        /// A tolerant single pass (descriptors are small and often mid-edit); returns null outside any string.
        /// </summary>
        public static JsonCaret At(string text, int offset)
        {
            if (string.IsNullOrEmpty(text) || offset < 0 || offset > text.Length) return null;
            // stack of containers: ('{' or '[', key that introduced it)
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
                    bool closed = e < text.Length && text[e] == '"';
                    int end = Math.Min(e, text.Length);
                    string str = text.Substring(s, end - s);
                    bool inObject = stack.Count > 0 && stack[stack.Count - 1].Kind == '{';
                    bool isKey = inObject && expectKey;
                    if (offset >= s && offset <= end)
                    {
                        if (isKey) return new JsonCaret { Property = str, Array = EnclosingArray(stack), InValue = false, Value = str, ValueStart = s, ValueLength = end - s };
                        string prop = inObject ? lastKey : stack.Count > 0 ? stack[stack.Count - 1].Key : null;
                        return new JsonCaret { Property = prop, Array = EnclosingArray(stack), InValue = true, Value = str, ValueStart = s, ValueLength = end - s };
                    }
                    if (isKey)
                    {
                        lastKey = str;
                        expectKey = false;
                    }
                    i = closed ? e + 1 : end;
                    continue;
                }
                if (i >= offset) return null;
                switch (c)
                {
                    case '{':
                        stack.Add(('{', inObjectKey(stack, lastKey)));
                        expectKey = true;
                        lastKey = null;
                        break;
                    case '[':
                        stack.Add(('[', inObjectKey(stack, lastKey)));
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

            string inObjectKey(List<(char Kind, string Key)> st, string key) =>
                st.Count > 0 && st[st.Count - 1].Kind == '{' ? key : st.Count > 0 ? st[st.Count - 1].Key : null;
        }

        private static string EnclosingArray(List<(char Kind, string Key)> stack)
        {
            for (int k = stack.Count - 1; k >= 0; k--)
                if (stack[k].Kind == '[') return stack[k].Key;
            return null;
        }

        /// <summary>The fixed value list for a descriptor key, or null when the key takes free text.</summary>
        public static IReadOnlyList<string> ValuesFor(JsonCaret caret)
        {
            if (caret == null || !caret.InValue) return null;
            switch (caret.Property)
            {
                case "Type" when caret.Array == "Modules": return ModuleTypes;
                case "LoadingPhase": return LoadingPhases;
                case "PlatformAllowList":
                case "PlatformDenyList":
                case "WhitelistPlatforms":
                case "BlacklistPlatforms":
                case "SupportedTargetPlatforms":
                    return Platforms;
                case "TargetAllowList":
                case "TargetDenyList":
                case "WhitelistTargets":
                case "BlacklistTargets":
                    return TargetTypes;
                case "TargetConfigurationAllowList":
                case "TargetConfigurationDenyList":
                    return TargetConfigurations;
                default: return null;
            }
        }

        /// <summary>What the string under the caret names: a module ("Modules"[].Name, "AdditionalDependencies"[]) or a plugin ("Plugins"[].Name).</summary>
        public static DescriptorTargetKind TargetOf(JsonCaret caret)
        {
            if (caret == null || !caret.InValue || string.IsNullOrWhiteSpace(caret.Value)) return DescriptorTargetKind.None;
            if (caret.Property == "Name" && caret.Array == "Modules") return DescriptorTargetKind.Module;
            if (caret.Property == "AdditionalDependencies") return DescriptorTargetKind.Module;
            if (caret.Property == "Name" && caret.Array == "Plugins") return DescriptorTargetKind.Plugin;
            return DescriptorTargetKind.None;
        }

        /// <summary>Likely Build.cs locations for a module next to a descriptor (Source/Name/Name.Build.cs), most likely first.</summary>
        public static List<string> ModuleCandidates(string descriptorPath, string module)
        {
            var list = new List<string>();
            string dir = Path.GetDirectoryName(descriptorPath ?? string.Empty);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrWhiteSpace(module)) return list;
            list.Add(Path.Combine(dir, "Source", module, module + ".Build.cs"));
            list.Add(Path.Combine(dir, "Source", "Runtime", module, module + ".Build.cs"));
            list.Add(Path.Combine(dir, "Source", "Editor", module, module + ".Build.cs"));
            return list;
        }

        /// <summary>
        /// For an ini section header <c>[/Script/Module.Class]</c>: module and class (unprefixed, as UE writes it). Null for other
        /// sections such as "[Core.Log]" or "[SectionsToSave]".
        /// </summary>
        public static (string Module, string Class)? ScriptSection(string line)
        {
            if (line == null) return null;
            string t = line.Trim();
            if (!t.StartsWith("[/Script/", StringComparison.Ordinal)) return null;
            int close = t.IndexOf(']');
            if (close < 0) return null;
            string body = t.Substring("[/Script/".Length, close - "[/Script/".Length);
            int dot = body.IndexOf('.');
            if (dot <= 0 || dot == body.Length - 1) return null;
            string module = body.Substring(0, dot);
            string cls = body.Substring(dot + 1);
            if (!IsIdentifier(module) || !IsIdentifier(cls)) return null;
            return (module, cls);
        }

        /// <summary>C++ names a reflected class may have for its script name (UObject / AActor prefixes).</summary>
        public static IEnumerable<string> ClassCandidates(string scriptClass)
        {
            yield return "U" + scriptClass;
            yield return "A" + scriptClass;
        }

        /// <summary>The section header that governs <paramref name="lineIndex"/> (searching upwards), or null.</summary>
        public static string SectionAbove(IReadOnlyList<string> lines, int lineIndex)
        {
            for (int n = Math.Min(lineIndex, lines.Count - 1); n >= 0; n--)
            {
                string t = lines[n].Trim();
                if (t.StartsWith("[", StringComparison.Ordinal)) return t;
            }
            return null;
        }

        /// <summary>
        /// The key being typed at the start of an ini line: optional +/-/./! operator, then identifier characters up to the caret.
        /// Returns the key's start column, or -1 when the caret is past '=' or in a comment / header.
        /// </summary>
        public static int IniKeyStart(string line, int column, out string prefix)
        {
            prefix = string.Empty;
            if (line == null || column < 0 || column > line.Length) return -1;
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i < line.Length && (line[i] == ';' || line[i] == '[' || line[i] == '#')) return -1;
            if (i < line.Length && "+-.!".IndexOf(line[i]) >= 0) i++;
            if (column < i) return -1;
            for (int k = i; k < column; k++)
                if (!(char.IsLetterOrDigit(line[k]) || line[k] == '_')) return -1;
            prefix = line.Substring(i, column - i);
            return i;
        }

        /// <summary>True when the UPROPERTY specifier text marks the property as saved in config.</summary>
        public static bool IsConfigProperty(string specifiers)
        {
            if (string.IsNullOrEmpty(specifiers)) return false;
            foreach (var raw in specifiers.Split(','))
            {
                string s = raw.Trim();
                int eq = s.IndexOf('=');
                if (eq >= 0) s = s.Substring(0, eq).Trim();
                if (s.Equals("Config", StringComparison.OrdinalIgnoreCase) || s.Equals("GlobalConfig", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char c in s)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }
    }
}
