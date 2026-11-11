using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UEToVS.Core.Query
{
    public enum SearchTab
    {
        All,
        Types,
        Files,
        Symbols,
        Actions,
        Text,
        Assets,
    }

    /// <summary>
    /// Parsed Search Everywhere input. Handles "File.cpp:120:8", "AActor::Tick", "AActor.Tick",
    /// path fragments ("Engine/Ren/SceneRen") and "/types foo" style prefix commands.
    /// </summary>
    public sealed class SearchQuery
    {
        private static readonly Regex LineSuffix = new Regex(@"(?<!:):(\d+)(?::(\d+))?$", RegexOptions.Compiled);

        public static readonly IReadOnlyDictionary<string, SearchTab> PrefixCommands = new Dictionary<string, SearchTab>(StringComparer.OrdinalIgnoreCase)
        {
            ["/all"] = SearchTab.All,
            ["/types"] = SearchTab.Types,
            ["/classes"] = SearchTab.Types,
            ["/files"] = SearchTab.Files,
            ["/symbols"] = SearchTab.Symbols,
            ["/actions"] = SearchTab.Actions,
            ["/text"] = SearchTab.Text,
            ["/assets"] = SearchTab.Assets,
        };

        private static readonly HashSet<string> FileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "h", "hpp", "hh", "hxx", "inl", "c", "cc", "cpp", "cxx", "cs", "ini", "usf", "ush", "uproject", "uplugin",
            "json", "txt", "md", "xml", "py", "lua", "hlsl", "uasset", "umap", "generated", "natvis", "vsct", "xaml",
        };

        public string Raw { get; private set; }

        /// <summary>The tab requested by a "/xxx" prefix, if any.</summary>
        public SearchTab? TabOverride { get; private set; }

        /// <summary>True while the user has typed only "/" or a partial prefix command.</summary>
        public bool IsPrefixCommandListing { get; private set; }

        /// <summary>The name pattern to match (last segment).</summary>
        public string Pattern { get; private set; } = string.Empty;

        /// <summary>Qualifier before "::" or "." (e.g. "AActor" in "AActor::Tick"), or null.</summary>
        public string Container { get; private set; }

        /// <summary>Directory fragments when the input looks like a path.</summary>
        public IReadOnlyList<string> PathFragments { get; private set; } = Array.Empty<string>();

        public bool LooksLikeFile { get; private set; }

        /// <summary>1-based line, or null.</summary>
        public int? Line { get; private set; }

        /// <summary>1-based column, or null.</summary>
        public int? Column { get; private set; }

        public bool IsEmpty => Pattern.Length == 0 && Container == null && PathFragments.Count == 0;

        public static SearchQuery Parse(string text)
        {
            var q = new SearchQuery { Raw = text ?? string.Empty };
            string s = q.Raw.Trim();

            if (s.StartsWith("/", StringComparison.Ordinal))
            {
                int space = s.IndexOf(' ');
                string cmd = space < 0 ? s : s.Substring(0, space);
                if (PrefixCommands.TryGetValue(cmd, out var tab))
                {
                    q.TabOverride = tab;
                    s = space < 0 ? string.Empty : s.Substring(space + 1).Trim();
                }
                else if (space < 0)
                {
                    q.IsPrefixCommandListing = true;
                    return q;
                }
            }

            var m = LineSuffix.Match(s);
            if (m.Success && m.Index > 0)
            {
                q.Line = int.Parse(m.Groups[1].Value);
                if (m.Groups[2].Success) q.Column = int.Parse(m.Groups[2].Value);
                s = s.Substring(0, m.Index);
            }

            if (s.IndexOf('/') >= 0 || s.IndexOf('\\') >= 0)
            {
                var parts = s.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    q.Pattern = parts[parts.Length - 1];
                    var dirs = new string[parts.Length - 1];
                    Array.Copy(parts, dirs, dirs.Length);
                    q.PathFragments = dirs;
                    q.LooksLikeFile = true;
                }
                return q;
            }

            int colons = s.LastIndexOf("::", StringComparison.Ordinal);
            if (colons > 0)
            {
                q.Container = s.Substring(0, colons);
                q.Pattern = s.Substring(colons + 2);
                return q;
            }

            int dot = s.LastIndexOf('.');
            if (dot > 0 && dot < s.Length - 1)
            {
                string ext = s.Substring(dot + 1);
                if (FileExtensions.Contains(ext) || IsPartialExtension(ext, s))
                {
                    q.Pattern = s;
                    q.LooksLikeFile = true;
                }
                else
                {
                    q.Container = s.Substring(0, dot);
                    q.Pattern = s.Substring(dot + 1);
                }
                return q;
            }

            q.Pattern = s;
            return q;
        }

        // "MyActor.c" while typing "MyActor.cpp": treat a lowercase 1-3 char suffix that prefixes a known extension as a file.
        private static bool IsPartialExtension(string ext, string whole)
        {
            if (ext.Length > 3 || !char.IsLower(ext[0])) return false;
            foreach (var e in FileExtensions)
                if (e.StartsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
