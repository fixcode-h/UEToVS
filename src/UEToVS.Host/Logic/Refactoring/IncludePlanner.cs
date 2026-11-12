using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>
    /// Add Include (UE style): the include path relative to the module's Public / Classes / Private folder (quotes), and the
    /// line to insert it at: in the existing include group, before the .generated.h include, after the last quoted include.
    /// </summary>
    public static class IncludePlanner
    {
        private static readonly Regex IncludeLine = new Regex(@"^\s*#\s*include\s*([""<])([^"">]+)["">]", RegexOptions.Compiled);
        private static readonly string[] RootFolders = { "Public", "Classes", "Private", "Internal" };

        /// <summary>
        /// <c>…/Engine/Source/Runtime/Engine/Classes/GameFramework/Actor.h</c> → <c>GameFramework/Actor.h</c>;
        /// <c>…/Source/MyGame/Public/Chars/Hero.h</c> → <c>Chars/Hero.h</c>. Without such a folder: relative to
        /// <paramref name="moduleDir"/> (the folder of the module's Build.cs) when given, else the file name.
        /// </summary>
        public static string UeIncludePath(string headerPath, string moduleDir = null)
        {
            if (string.IsNullOrEmpty(headerPath)) return null;
            string p = headerPath.Replace('\\', '/');
            var parts = p.Split('/');
            int root = -1;
            // the outermost Public/Classes/Private below the module (search from the module dir when known)
            int startAt = 0;
            if (!string.IsNullOrEmpty(moduleDir))
            {
                string m = moduleDir.Replace('\\', '/').TrimEnd('/');
                if (p.StartsWith(m + "/", StringComparison.OrdinalIgnoreCase)) startAt = m.Split('/').Length;
            }
            for (int i = startAt; i < parts.Length - 1; i++)
                if (RootFolders.Any(r => string.Equals(parts[i], r, StringComparison.OrdinalIgnoreCase))) { root = i; break; }
            if (root < 0 && startAt > 0) return string.Join("/", parts.Skip(startAt));
            if (root < 0) return parts[parts.Length - 1];
            return string.Join("/", parts.Skip(root + 1));
        }

        public sealed class IncludeInfo
        {
            public int Line { get; set; }
            public string Path { get; set; }
            public bool Angled { get; set; }
        }

        /// <summary>#include lines of a text (top-level only; conditional ones included).</summary>
        public static List<IncludeInfo> Includes(string text)
        {
            var list = new List<IncludeInfo>();
            var lines = CppTextScan.Lines(text);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = IncludeLine.Match(lines[i]);
                if (m.Success) list.Add(new IncludeInfo { Line = i, Path = m.Groups[2].Value.Trim(), Angled = m.Groups[1].Value == "<" });
            }
            return list;
        }

        /// <summary>Whether the text already includes <paramref name="include"/> (same path, or same file name with a path suffix match).</summary>
        public static bool AlreadyIncluded(string text, string include)
        {
            string want = include.Replace('\\', '/');
            foreach (var inc in Includes(text))
            {
                string have = inc.Path.Replace('\\', '/');
                if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) return true;
                if (have.EndsWith("/" + want, StringComparison.OrdinalIgnoreCase) || want.EndsWith("/" + have, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// The edit adding <c>#include "include"</c>: before X.generated.h when present; else after the last quoted include
        /// of the first include group (a .cpp keeps its own header first); else after the last include; else after
        /// <c>#pragma once</c> / the leading comment block.
        /// </summary>
        public static InsertPoint Plan(string text, string include)
        {
            string nl = CppTextScan.NewlineOf(text);
            string line = "#include \"" + include.Replace('\\', '/') + "\"" + nl;
            var incs = Includes(text);
            var generated = incs.FirstOrDefault(i => i.Path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase));
            if (generated != null) return new InsertPoint { Line = generated.Line, Text = line, Reason = "before-generated" };
            if (incs.Count > 0)
            {
                // first group: consecutive include lines (blank lines / comments allowed in between)
                var lines = CppTextScan.Lines(text);
                var group = new List<IncludeInfo> { incs[0] };
                for (int k = 1; k < incs.Count; k++)
                {
                    bool contiguous = true;
                    for (int l = group[group.Count - 1].Line + 1; l < incs[k].Line; l++)
                    {
                        string s = lines[l].Trim();
                        if (s.Length > 0 && !s.StartsWith("//", StringComparison.Ordinal)) { contiguous = false; break; }
                    }
                    if (!contiguous) break;
                    group.Add(incs[k]);
                }
                var lastQuoted = group.LastOrDefault(i => !i.Angled) ?? group[group.Count - 1];
                return new InsertPoint { Line = lastQuoted.Line + 1, Text = line, Reason = "include-group" };
            }
            var all = CppTextScan.Lines(text);
            for (int i = 0; i < all.Length; i++)
                if (Regex.IsMatch(all[i], @"^\s*#\s*pragma\s+once")) return new InsertPoint { Line = i + 1, Text = nl + line, Reason = "after-pragma-once" };
            // after a leading comment block
            int at = 0;
            bool inBlock = false;
            for (; at < all.Length; at++)
            {
                string s = all[at].Trim();
                if (inBlock) { if (s.Contains("*/")) inBlock = false; continue; }
                if (s.StartsWith("/*", StringComparison.Ordinal)) { inBlock = !s.Contains("*/"); continue; }
                if (s.StartsWith("//", StringComparison.Ordinal)) continue;
                break;
            }
            return new InsertPoint { Line = at, Text = line, Reason = "top" };
        }
    }
}
