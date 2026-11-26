using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UEToVS.Core.Unreal
{
    /// <summary>Reads and edits the dependency lists of a UE module rules file (Foo.Build.cs).</summary>
    public sealed class BuildCsFile
    {
        private static readonly Regex AddRange = new Regex(
            @"(?<list>PublicDependencyModuleNames|PrivateDependencyModuleNames|PublicIncludePathModuleNames|PrivateIncludePathModuleNames)\s*\.\s*AddRange\s*\(\s*new\s+(?:string\s*)?\[\s*\]\s*\{(?<items>[^}]*)\}",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex AddOne = new Regex(
            @"(?<list>PublicDependencyModuleNames|PrivateDependencyModuleNames|PublicIncludePathModuleNames|PrivateIncludePathModuleNames)\s*\.\s*Add\s*\(\s*""(?<name>[^""]+)""\s*\)",
            RegexOptions.Compiled);

        private static readonly Regex QuotedName = new Regex(@"""(?<name>[^""]+)""", RegexOptions.Compiled);

        public BuildCsFile(string moduleName, string text)
        {
            ModuleName = moduleName;
            Text = text ?? string.Empty;
            Parse();
        }

        public string ModuleName { get; }
        public string Text { get; private set; }
        public HashSet<string> PublicDependencies { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PrivateDependencies { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PublicIncludePathModules { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PrivateIncludePathModules { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool DependsOn(string module) => PublicDependencies.Contains(module) || PrivateDependencies.Contains(module);

        /// <summary>
        /// v0.11.0: the expressions (C# source text) added to PublicIncludePaths / PublicSystemIncludePaths, e.g.
        /// <c>Path.Combine(ModuleDirectory, "Public/Sub")</c> or <c>"Runtime/Engine/Private"</c>. The engine's include model
        /// evaluates the common shapes; one it cannot evaluate makes "header not found" uncertain for this module.
        /// </summary>
        public List<string> PublicIncludePathExpressions { get; } = new List<string>();

        /// <summary>v0.11.0: expressions added to PrivateIncludePaths / PrivateSystemIncludePaths (see <see cref="PublicIncludePathExpressions"/>).</summary>
        public List<string> PrivateIncludePathExpressions { get; } = new List<string>();

        /// <summary>v0.11.0: <c>bLegacyPublicIncludePaths = true/false</c>; null when the rules file does not set it (UBT default by build settings version).</summary>
        public bool? LegacyPublicIncludePaths { get; private set; }

        /// <summary>v0.11.0: <c>bLegacyParentIncludePaths = true/false</c>; null when not set.</summary>
        public bool? LegacyParentIncludePaths { get; private set; }

        /// <summary>v0.11.0: <c>PrivatePCHHeaderFile = "…"</c> (relative to the module folder), or null.</summary>
        public string PrivatePchHeader { get; private set; }

        /// <summary>v0.11.0: <c>SharedPCHHeaderFile = "…"</c> (relative to the module folder), or null.</summary>
        public string SharedPchHeader { get; private set; }

        private static readonly Regex IncludePathCall = new Regex(
            @"(?<list>PublicIncludePaths|PrivateIncludePaths|PublicSystemIncludePaths|PrivateSystemIncludePaths)\s*\.\s*(?<op>AddRange|Add)\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex BoolSetting = new Regex(@"\b(?<name>bLegacyPublicIncludePaths|bLegacyParentIncludePaths)\s*=\s*(?<value>true|false)\b", RegexOptions.Compiled);

        private static readonly Regex PchSetting = new Regex(@"\b(?<name>PrivatePCHHeaderFile|SharedPCHHeaderFile)\s*=\s*""(?<value>[^""]+)""", RegexOptions.Compiled);

        /// <summary>
        /// Every module whose headers <paramref name="root"/> can include: itself, its direct dependencies and, transitively,
        /// the public dependencies of those (UBT propagates public include paths).
        /// </summary>
        public static HashSet<string> VisibleModules(string root, Func<string, BuildCsFile> load)
        {
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            var rootRules = load(root);
            if (rootRules == null) return visible;
            var queue = new Queue<string>();
            foreach (var m in rootRules.DirectlyVisibleModules())
                if (visible.Add(m)) queue.Enqueue(m);
            while (queue.Count > 0)
            {
                var rules = load(queue.Dequeue());
                if (rules == null) continue;
                foreach (var m in rules.PublicDependencies)
                    if (visible.Add(m)) queue.Enqueue(m);
                foreach (var m in rules.PublicIncludePathModules)
                    if (visible.Add(m)) queue.Enqueue(m);
            }
            return visible;
        }

        public static string ModuleNameFromPath(string buildCsPath)
        {
            string file = Path.GetFileName(buildCsPath);
            const string suffix = ".Build.cs";
            return file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - suffix.Length) : null;
        }

        private void Parse()
        {
            PublicDependencies.Clear();
            PrivateDependencies.Clear();
            PublicIncludePathModules.Clear();
            PrivateIncludePathModules.Clear();
            string code = StripComments(Text);
            foreach (Match m in AddRange.Matches(code))
            {
                var set = SetFor(m.Groups["list"].Value);
                foreach (Match n in QuotedName.Matches(m.Groups["items"].Value)) set.Add(n.Groups["name"].Value);
            }
            foreach (Match m in AddOne.Matches(code))
                SetFor(m.Groups["list"].Value).Add(m.Groups["name"].Value);
            ParseIncludePaths(code);
        }

        private void ParseIncludePaths(string code)
        {
            PublicIncludePathExpressions.Clear();
            PrivateIncludePathExpressions.Clear();
            LegacyPublicIncludePaths = null;
            LegacyParentIncludePaths = null;
            PrivatePchHeader = null;
            SharedPchHeader = null;
            foreach (Match m in IncludePathCall.Matches(code))
            {
                int open = m.Index + m.Length - 1;
                int close = MatchParen(code, open);
                if (close < 0) continue;
                string arg = code.Substring(open + 1, close - open - 1).Trim();
                var list = m.Groups["list"].Value.StartsWith("Public", StringComparison.Ordinal) ? PublicIncludePathExpressions : PrivateIncludePathExpressions;
                if (m.Groups["op"].Value == "AddRange")
                {
                    // AddRange(new string[] { a, b }) / AddRange(new[] { … }); anything else (a variable, a method call) stays one opaque item
                    int lb = arg.IndexOf('{');
                    int rb = arg.LastIndexOf('}');
                    if (arg.StartsWith("new", StringComparison.Ordinal) && lb >= 0 && rb > lb)
                    {
                        foreach (var item in SplitTopLevel(arg.Substring(lb + 1, rb - lb - 1)))
                            if (item.Length > 0) list.Add(item);
                    }
                    else list.Add("?" + arg);
                }
                else if (arg.Length > 0) list.Add(arg);
            }
            foreach (Match m in BoolSetting.Matches(code))
            {
                bool v = m.Groups["value"].Value == "true";
                if (m.Groups["name"].Value == "bLegacyPublicIncludePaths") LegacyPublicIncludePaths = v;
                else LegacyParentIncludePaths = v;
            }
            foreach (Match m in PchSetting.Matches(code))
            {
                if (m.Groups["name"].Value == "PrivatePCHHeaderFile") PrivatePchHeader = m.Groups["value"].Value;
                else SharedPchHeader = m.Groups["value"].Value;
            }
        }

        /// <summary>Index of the ')' closing the '(' at <paramref name="open"/> (strings skipped), or -1.</summary>
        public static int MatchParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    i = SkipString(s, i);
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Index of the closing quote of the string literal opening at <paramref name="quote"/> (@"…" verbatim: no escapes).</summary>
        public static int SkipString(string s, int quote)
        {
            bool verbatim = quote > 0 && s[quote - 1] == '@';
            for (int i = quote + 1; i < s.Length; i++)
            {
                if (s[i] == '\\' && !verbatim) { i++; continue; }
                if (s[i] == '"')
                {
                    if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { i++; continue; }
                    return i;
                }
            }
            return s.Length;
        }

        /// <summary>Splits on commas outside parentheses, brackets, braces and strings; items trimmed.</summary>
        public static List<string> SplitTopLevel(string s)
        {
            var list = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    i = SkipString(s, i);
                    continue;
                }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    list.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            if (start < s.Length) list.Add(s.Substring(start).Trim());
            return list;
        }

        private HashSet<string> SetFor(string list)
        {
            switch (list)
            {
                case "PublicDependencyModuleNames": return PublicDependencies;
                case "PrivateDependencyModuleNames": return PrivateDependencies;
                case "PublicIncludePathModuleNames": return PublicIncludePathModules;
                default: return PrivateIncludePathModules;
            }
        }

        /// <summary>Modules whose public headers this module may include (direct dependencies and include-path modules).</summary>
        public IEnumerable<string> DirectlyVisibleModules()
        {
            foreach (var m in PublicDependencies) yield return m;
            foreach (var m in PrivateDependencies) yield return m;
            foreach (var m in PublicIncludePathModules) yield return m;
            foreach (var m in PrivateIncludePathModules) yield return m;
        }

        /// <summary>
        /// Adds <paramref name="module"/> to the public or private list. Returns false when it is already a dependency.
        /// </summary>
        public bool AddDependency(string module, bool isPublic)
        {
            if (DependsOn(module) || string.Equals(module, ModuleName, StringComparison.OrdinalIgnoreCase)) return false;
            string listName = isPublic ? "PublicDependencyModuleNames" : "PrivateDependencyModuleNames";

            // 1) Append to an existing AddRange list (uncommented occurrence).
            string code = StripComments(Text);
            foreach (Match m in AddRange.Matches(code))
            {
                if (m.Groups["list"].Value != listName) continue;
                var items = m.Groups["items"];
                string inner = items.Value;
                int closeIndex = items.Index + items.Length; // index of '}' in Text (StripComments keeps offsets)
                string trimmed = inner.TrimEnd();
                bool multiLine = inner.IndexOf('\n') >= 0;
                string insertion;
                if (trimmed.Trim().Length == 0)
                {
                    insertion = " \"" + module + "\" ";
                    Text = Text.Substring(0, items.Index) + insertion + Text.Substring(closeIndex);
                }
                else
                {
                    int lastItemEnd = items.Index + trimmed.Length;
                    bool trailingComma = trimmed.EndsWith(",", StringComparison.Ordinal);
                    if (multiLine)
                    {
                        string indent = DetectItemIndent(inner);
                        insertion = (trailingComma ? "" : ",") + Environment.NewLine + indent + "\"" + module + "\"" + (trailingComma ? "," : "");
                    }
                    else
                    {
                        insertion = (trailingComma ? " " : ", ") + "\"" + module + "\"";
                    }
                    Text = Text.Substring(0, lastItemEnd) + insertion + Text.Substring(lastItemEnd);
                }
                Parse();
                return true;
            }

            // 2) No list: add a statement at the end of the constructor body.
            int ctor = FindConstructorBodyEnd(code);
            if (ctor < 0) throw new InvalidOperationException("Cannot find the module rules constructor in " + ModuleName + ".Build.cs");
            string lineIndent = "\t\t";
            string stmt = lineIndent + listName + ".Add(\"" + module + "\");" + Environment.NewLine;
            int lineStart = Text.LastIndexOf('\n', ctor) + 1;
            Text = Text.Substring(0, lineStart) + stmt + Text.Substring(lineStart);
            Parse();
            return true;
        }

        private static string DetectItemIndent(string inner)
        {
            foreach (var line in inner.Split('\n'))
            {
                int q = line.IndexOf('"');
                if (q > 0 && line.Substring(0, q).Trim().Length == 0) return line.Substring(0, q).TrimStart('\r');
            }
            return "\t\t\t\t";
        }

        private int FindConstructorBodyEnd(string code)
        {
            var ctor = new Regex(@"public\s+" + Regex.Escape(ModuleName) + @"\s*\(\s*ReadOnlyTargetRules\s+\w+\s*\)\s*:\s*base\s*\(\s*\w+\s*\)\s*\{");
            var m = ctor.Match(code);
            if (!m.Success) return -1;
            int depth = 1;
            for (int i = m.Index + m.Length; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Replaces comments and string contents (except quotes) with spaces, keeping offsets.</summary>
        internal static string StripComments(string text)
        {
            var chars = text.ToCharArray();
            int i = 0;
            while (i < chars.Length)
            {
                if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
                {
                    while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
                }
                else if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
                {
                    while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                    {
                        if (chars[i] != '\n') chars[i] = ' ';
                        i++;
                    }
                    if (i < chars.Length) { chars[i++] = ' '; chars[i++] = ' '; }
                }
                else if (chars[i] == '"')
                {
                    i++;
                    while (i < chars.Length && chars[i] != '"' && chars[i] != '\n') i++;
                    i++;
                }
                else i++;
            }
            return new string(chars);
        }
    }
}
