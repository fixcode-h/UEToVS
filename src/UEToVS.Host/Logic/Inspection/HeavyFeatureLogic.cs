using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Refactoring;

namespace UEToVS.Host.Logic.Inspection
{
    public enum InspectionGrouping
    {
        Rule = 0,
        File = 1,
    }

    /// <summary>One group of the inspection result tree (a rule, or a file).</summary>
    public sealed class InspectionGroup
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public List<InspectionFinding> Items { get; } = new List<InspectionFinding>();
        public int Fixable => Items.Count(i => i.Fix != null && i.Fix.Count > 0);
    }

    /// <summary>
    /// v0.4.5 whole-project inspection, host side: grouping / filtering of one results page, the batch-fix plan and the
    /// polling cadence. The engine holds the run; the host shows at most one page (<see cref="PageSize"/>).
    /// </summary>
    public static class InspectionView
    {
        /// <summary>Findings fetched for the popup at once (filters narrow the rest down engine-side).</summary>
        public const int PageSize = 2000;

        public static bool IsFinished(HeavyJobState s) => s == HeavyJobState.Completed || s == HeavyJobState.Cancelled || s == HeavyJobState.Failed;

        /// <summary>Poll interval while a job runs: quick at first, then every 2 s (the work is in the engine, polls are cheap).</summary>
        public static int PollDelayMs(TimeSpan running) => running < TimeSpan.FromSeconds(5) ? 500 : running < TimeSpan.FromSeconds(30) ? 1000 : 2000;

        public static List<InspectionGroup> Group(IEnumerable<InspectionFinding> items, InspectionGrouping by)
        {
            var groups = new Dictionary<string, InspectionGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in items ?? Enumerable.Empty<InspectionFinding>())
            {
                string key = by == InspectionGrouping.Rule ? f.Code ?? "?" : f.FilePath ?? "?";
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new InspectionGroup { Key = key };
                g.Items.Add(f);
            }
            foreach (var g in groups.Values)
            {
                g.Items.Sort((a, b) =>
                {
                    int c = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column);
                });
                g.Title = by == InspectionGrouping.Rule
                    ? HeavyFeatureStrings.RuleGroup(g.Key, HeavyFeatureStrings.RuleName(g.Key), g.Items.Count)
                    : System.IO.Path.GetFileName(g.Key) + "  (" + g.Items.Count + ")";
            }
            return by == InspectionGrouping.Rule
                ? groups.Values.OrderByDescending(g => g.Items.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList()
                : groups.Values.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Client-side text filter of the shown page: file name, rule id, message arguments or the source line.</summary>
        public static bool MatchesText(InspectionFinding f, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            text = text.Trim();
            bool Has(string s) => s != null && s.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has(f.Code) || Has(System.IO.Path.GetFileName(f.FilePath ?? string.Empty)) || Has(f.LineText) || Has(f.Message) || (f.Args != null && f.Args.Any(Has));
        }

        public static TextEdit ToTextEdit(FileTextEdit e) => new TextEdit
        {
            FilePath = e.FilePath, Line = e.Line, Column = e.Column, Length = e.Length, OldText = e.OldText, NewText = e.NewText,
        };

        /// <summary>
        /// The edits of every fixable finding, one undo step for the host: duplicates (same file / position / text) once,
        /// and an edit overlapping an earlier one in the same file is dropped (counted in <paramref name="dropped"/>).
        /// </summary>
        public static List<TextEdit> PlanFixes(IEnumerable<InspectionFinding> findings, out int dropped)
        {
            dropped = 0;
            var result = new List<TextEdit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var byFile = new Dictionary<string, List<TextEdit>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in findings ?? Enumerable.Empty<InspectionFinding>())
            {
                if (f.Fix == null) continue;
                foreach (var e in f.Fix)
                {
                    if (e == null || string.IsNullOrEmpty(e.FilePath)) continue;
                    string key = e.FilePath.ToUpperInvariant() + "|" + e.Line + "|" + e.Column + "|" + e.Length + "|" + e.NewText;
                    if (!seen.Add(key)) continue;
                    var t = ToTextEdit(e);
                    if (!byFile.TryGetValue(t.FilePath, out var list)) byFile[t.FilePath] = list = new List<TextEdit>();
                    if (list.Any(o => Overlaps(o, t))) { dropped++; continue; }
                    list.Add(t);
                    result.Add(t);
                }
            }
            return result;
        }

        private static bool Overlaps(TextEdit a, TextEdit b)
        {
            if (a.Line != b.Line) return false;
            if (a.Length == 0 && b.Length == 0) return a.Column == b.Column;
            return a.Column < b.Column + Math.Max(1, b.Length) && b.Column < a.Column + Math.Max(1, a.Length);
        }

        /// <summary>Status bar / popup header line for a job snapshot.</summary>
        public static string StatusText(ProjectInspectionResponse r)
        {
            if (r == null) return HeavyFeatureStrings.EngineUnavailable;
            switch (r.State)
            {
                case HeavyJobState.Running: return HeavyFeatureStrings.InspectionProgress(r.FilesDone, r.FilesTotal, r.Findings);
                case HeavyJobState.Completed: return HeavyFeatureStrings.InspectionDone(r.FilesDone, r.Findings, r.ElapsedMs / 1000.0);
                case HeavyJobState.Cancelled: return HeavyFeatureStrings.InspectionCancelled(r.FilesDone, r.FilesTotal);
                case HeavyJobState.Disabled: return HeavyFeatureStrings.InspectionDisabled;
                case HeavyJobState.Failed: return r.Error == "index-not-ready" ? HeavyFeatureStrings.IndexNotReady : HeavyFeatureStrings.InspectionFailed(r.Error);
                default: return HeavyFeatureStrings.NoResultsYet;
            }
        }
    }

    /// <summary>One deprecation version group of the upgrade assistant (newest first).</summary>
    public sealed class DeprecationVersionGroup
    {
        public string Version { get; set; }
        public string Title { get; set; }
        public List<DeprecatedApi> Apis { get; } = new List<DeprecatedApi>();
    }

    /// <summary>v0.4.5 engine upgrade assistant, host side: version groups and the batch replacement plan.</summary>
    public static class UpgradeView
    {
        public static List<DeprecationVersionGroup> GroupByVersion(IEnumerable<DeprecatedApi> apis)
        {
            var groups = new Dictionary<string, DeprecationVersionGroup>(StringComparer.Ordinal);
            foreach (var a in apis ?? Enumerable.Empty<DeprecatedApi>())
            {
                string v = a.Version ?? string.Empty;
                if (!groups.TryGetValue(v, out var g)) groups[v] = g = new DeprecationVersionGroup { Version = v };
                g.Apis.Add(a);
            }
            foreach (var g in groups.Values) g.Title = HeavyFeatureStrings.VersionGroup(g.Version, g.Apis.Count);
            return groups.Values.OrderByDescending(g => g.Version, Comparer<string>.Create(CompareVersions)).ToList();
        }

        /// <summary>Numeric UE version order ("4.27" &lt; "5.0" &lt; "5.10"); empty sorts first (same rule as the engine's DeprecationParser).</summary>
        public static int CompareVersions(string a, string b)
        {
            var pa = Parts(a);
            var pb = Parts(b);
            if (pa.Length == 0 || pb.Length == 0) return pa.Length.CompareTo(pb.Length);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        private static int[] Parts(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return new int[0];
            var list = new List<int>();
            foreach (var p in v.Split('.', '_', '-'))
            {
                if (!int.TryParse(p.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n)) break;
                list.Add(n);
            }
            return list.ToArray();
        }

        /// <summary>Usages the batch replacement may rename: APIs with a compatible replacement, Exact usages only.</summary>
        public static int Replaceable(IEnumerable<DeprecatedApi> apis) =>
            (apis ?? Enumerable.Empty<DeprecatedApi>()).Where(a => !string.IsNullOrEmpty(a.Replacement)).Sum(a => a.Usages.Count(u => u.Confidence == ResolutionConfidence.Exact));

        /// <summary>
        /// Rename edits for the selected APIs (identifier → replacement); OldText = the deprecated name, so a usage whose text
        /// changed since the scan is skipped. NameOnly usages are never renamed automatically.
        /// </summary>
        public static List<TextEdit> PlanReplacements(IEnumerable<DeprecatedApi> apis)
        {
            var edits = new List<TextEdit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in apis ?? Enumerable.Empty<DeprecatedApi>())
            {
                if (string.IsNullOrEmpty(a.Replacement) || a.Symbol == null) continue;
                foreach (var u in a.Usages)
                {
                    if (u.Confidence != ResolutionConfidence.Exact) continue;
                    if (!seen.Add((u.FilePath ?? string.Empty).ToUpperInvariant() + "|" + u.Line + "|" + u.Column)) continue;
                    edits.Add(new TextEdit { FilePath = u.FilePath, Line = u.Line, Column = u.Column, Length = a.Symbol.Name.Length, OldText = a.Symbol.Name, NewText = a.Replacement });
                }
            }
            return edits;
        }

        public static string StatusText(DeprecationScanResponse r)
        {
            if (r == null) return HeavyFeatureStrings.EngineUnavailable;
            switch (r.State)
            {
                case HeavyJobState.Running: return HeavyFeatureStrings.UpgradeProgress(r.SymbolsDone, r.DeprecatedSymbols, r.Usages);
                case HeavyJobState.Completed: return r.IndexHasDeprecations ? HeavyFeatureStrings.UpgradeDone(r.Apis.Count, r.Usages) : HeavyFeatureStrings.UpgradeNoIndexData;
                case HeavyJobState.Cancelled: return HeavyFeatureStrings.UpgradeCancelled;
                case HeavyJobState.Disabled: return HeavyFeatureStrings.UpgradeDisabled;
                case HeavyJobState.Failed: return r.Error == "index-not-ready" ? HeavyFeatureStrings.IndexNotReady : HeavyFeatureStrings.UpgradeFailed(r.Error);
                default: return HeavyFeatureStrings.NoResultsYet;
            }
        }
    }
}
