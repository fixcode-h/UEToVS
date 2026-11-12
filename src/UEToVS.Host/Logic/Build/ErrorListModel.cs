using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UEToVS.Host.Logic.Build
{
    /// <summary>Where an Error List row comes from.</summary>
    public enum ErrorListOrigin
    {
        /// <summary>UEToVS UE rule squiggles (engine DiagnosticsProvider + Build.cs include check) of an open document.</summary>
        UeRules = 0,

        /// <summary>The last build (UBT / MSVC / clang / UHT), parsed from the Build output.</summary>
        Build = 1,
    }

    /// <summary>One row UEToVS puts into the VS Error List. Line / Column 0-based (the Error List's convention).</summary>
    public sealed class ErrorListEntry
    {
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public BuildSeverity Severity { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public ErrorListOrigin Origin { get; set; }

        /// <summary>"UEToVS" (rules) or "UEToVS · msvc" (build), shown in the Tool column.</summary>
        public string Tool { get; set; }

        public static ErrorListEntry FromBuild(BuildDiagnostic d) => new ErrorListEntry
        {
            File = d.File,
            Line = Math.Max(0, d.Line - 1),
            Column = Math.Max(0, d.Column - 1),
            Severity = d.Severity,
            Code = d.Code,
            Message = d.Message,
            Origin = ErrorListOrigin.Build,
            Tool = string.IsNullOrEmpty(d.Tool) ? ErrorListModel.SourceName : ErrorListModel.SourceName + " · " + d.Tool,
        };
    }

    /// <summary>
    /// The UEToVS rows of the Error List (pure): UE rule diagnostics of open documents (replaced per file by the squiggle tagger)
    /// plus the last build's errors / warnings. Sorted by severity, file, line; identical rows (same file, line, code, message)
    /// appear once. Not thread-safe: the host owns it on the UI thread.
    /// </summary>
    public sealed class ErrorListModel
    {
        public const string SourceName = "UEToVS";

        /// <summary>Upper bound of rows (the Error List gets slow with tens of thousands).</summary>
        public const int MaxRows = 2000;

        private readonly Dictionary<string, List<ErrorListEntry>> _rules = new Dictionary<string, List<ErrorListEntry>>(StringComparer.OrdinalIgnoreCase);
        private BuildErrorSet _build = BuildErrorSet.Empty;

        public int Version { get; private set; }

        /// <summary>Replaces the rule rows of one file (empty / null removes them). Returns whether anything changed.</summary>
        public bool SetRules(string file, IReadOnlyList<ErrorListEntry> entries)
        {
            if (string.IsNullOrEmpty(file)) return false;
            string key = BuildErrorSet.NormalizePath(file);
            bool had = _rules.TryGetValue(key, out var old);
            if (entries == null || entries.Count == 0)
            {
                if (!had) return false;
                _rules.Remove(key);
                Version++;
                return true;
            }
            var list = entries.ToList();
            if (had && Same(old, list)) return false;
            _rules[key] = list;
            Version++;
            return true;
        }

        public bool SetBuild(BuildErrorSet build)
        {
            build = build ?? BuildErrorSet.Empty;
            if (ReferenceEquals(build, _build)) return false;
            _build = build;
            Version++;
            return true;
        }

        public void Clear()
        {
            _rules.Clear();
            _build = BuildErrorSet.Empty;
            Version++;
        }

        public int RuleFileCount => _rules.Count;

        /// <summary>Rows to show. <paramref name="includeBuild"/> = the build rows too (VS lists them already under "Build").</summary>
        public List<ErrorListEntry> Rows(bool includeRules, bool includeBuild)
        {
            var rows = new List<ErrorListEntry>();
            if (includeRules)
                foreach (var list in _rules.Values) rows.AddRange(list);
            if (includeBuild && _build.Record?.Items != null)
                rows.AddRange(_build.Record.Items.Where(d => d.Severity != BuildSeverity.Info).Select(ErrorListEntry.FromBuild));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return rows.Where(r => seen.Add(BuildErrorSet.NormalizePath(r.File) + "|" + r.Line + "|" + r.Code + "|" + r.Message))
                       .OrderByDescending(r => r.Severity)
                       .ThenBy(r => r.File == null ? 1 : 0)
                       .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(r => r.Line)
                       .ThenBy(r => r.Column)
                       .Take(MaxRows)
                       .ToList();
        }

        /// <summary>File name for the Error List's "File" column (it shows names, the path is the tooltip).</summary>
        public static string DisplayFile(string path) => string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);

        private static bool Same(List<ErrorListEntry> a, List<ErrorListEntry> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];
                if (x.Line != y.Line || x.Column != y.Column || x.Severity != y.Severity || x.Code != y.Code || x.Message != y.Message) return false;
            }
            return true;
        }
    }
}
