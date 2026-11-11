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
            @"(?<list>PublicDependencyModuleNames|PrivateDependencyModuleNames)\s*\.\s*AddRange\s*\(\s*new\s+(?:string\s*)?\[\s*\]\s*\{(?<items>[^}]*)\}",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex AddOne = new Regex(
            @"(?<list>PublicDependencyModuleNames|PrivateDependencyModuleNames)\s*\.\s*Add\s*\(\s*""(?<name>[^""]+)""\s*\)",
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

        public bool DependsOn(string module) => PublicDependencies.Contains(module) || PrivateDependencies.Contains(module);

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
            string code = StripComments(Text);
            foreach (Match m in AddRange.Matches(code))
            {
                var set = m.Groups["list"].Value.StartsWith("Public", StringComparison.Ordinal) ? PublicDependencies : PrivateDependencies;
                foreach (Match n in QuotedName.Matches(m.Groups["items"].Value)) set.Add(n.Groups["name"].Value);
            }
            foreach (Match m in AddOne.Matches(code))
            {
                var set = m.Groups["list"].Value.StartsWith("Public", StringComparison.Ordinal) ? PublicDependencies : PrivateDependencies;
                set.Add(m.Groups["name"].Value);
            }
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
