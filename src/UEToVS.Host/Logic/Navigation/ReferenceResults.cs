using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>Toolbar state of the Find References popup.</summary>
    public sealed class ReferenceFilter
    {
        public bool WritesOnly { get; set; }

        /// <summary>Show hits in engine / engine-plugin / third-party code (also sent to the engine as IncludeNonProject).</summary>
        public bool IncludeEngine { get; set; }

        /// <summary>Show name-only matches (grayed). Off = hide them.</summary>
        public bool ShowUncertain { get; set; } = true;

        public ReferenceFilter Clone() => (ReferenceFilter)MemberwiseClone();
    }

    /// <summary>One file of results: header row + its hits sorted by position.</summary>
    public sealed class ReferenceFileGroup
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Directory { get; set; }
        public string Module { get; set; }
        public ItemOrigin Origin { get; set; }
        public List<ReferenceHit> Hits { get; } = new List<ReferenceHit>();
        public int WriteCount { get; set; }
    }

    public sealed class ReferenceView
    {
        public List<ReferenceFileGroup> Groups { get; } = new List<ReferenceFileGroup>();
        public int Shown { get; set; }
        public int Hidden { get; set; }
    }

    /// <summary>Filtering, grouping, ordering and texts of Find References results. Pure.</summary>
    public static class ReferenceResults
    {
        public static bool IsWrite(ReferenceHit h) => h != null && (h.Kind & ReferenceKind.Write) != 0;

        /// <summary>Name-only (or unresolved) matches may be a different symbol: shown grayed.</summary>
        public static bool IsUncertain(ReferenceHit h) => h != null && h.Confidence <= ResolutionConfidence.NameOnly;

        public static bool Passes(ReferenceHit h, ReferenceFilter f)
        {
            if (h?.Location == null || string.IsNullOrEmpty(h.Location.FilePath)) return false;
            if (f == null) return true;
            if (f.WritesOnly && !IsWrite(h)) return false;
            if (!f.IncludeEngine && SymbolPresentation.IsNonProject(h.Origin)) return false;
            if (!f.ShowUncertain && IsUncertain(h)) return false;
            return true;
        }

        /// <summary>
        /// Groups by file: project files first, then by path; hits by line/column. Duplicate hits (same position) are dropped.
        /// </summary>
        public static ReferenceView Build(IEnumerable<ReferenceHit> hits, ReferenceFilter filter)
        {
            var view = new ReferenceView();
            var byFile = new Dictionary<string, ReferenceFileGroup>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<(string, int, int)>();
            foreach (var h in hits ?? Enumerable.Empty<ReferenceHit>())
            {
                if (!Passes(h, filter))
                {
                    if (h?.Location != null) view.Hidden++;
                    continue;
                }
                var loc = h.Location;
                if (!seen.Add((loc.FilePath.ToLowerInvariant(), loc.Line, loc.Column))) continue;
                if (!byFile.TryGetValue(loc.FilePath, out var g))
                {
                    g = new ReferenceFileGroup
                    {
                        FilePath = loc.FilePath,
                        FileName = SymbolPresentation.SafeFileName(loc.FilePath),
                        Directory = SymbolPresentation.SafeDirectory(loc.FilePath),
                        Module = h.Module,
                        Origin = h.Origin,
                    };
                    byFile.Add(loc.FilePath, g);
                }
                g.Hits.Add(h);
                if (IsWrite(h)) g.WriteCount++;
                view.Shown++;
            }
            foreach (var g in byFile.Values)
                g.Hits.Sort((a, b) => a.Location.Line != b.Location.Line ? a.Location.Line.CompareTo(b.Location.Line) : a.Location.Column.CompareTo(b.Location.Column));
            view.Groups.AddRange(byFile.Values
                .OrderBy(g => SymbolPresentation.IsNonProject(g.Origin) ? 1 : 0)
                .ThenBy(g => g.FilePath, StringComparer.OrdinalIgnoreCase));
            return view;
        }

        /// <summary>Range of the match inside <see cref="ReferenceHit.LineText"/>, clamped; null when it does not fit.</summary>
        public static (int Start, int Length)? MatchRange(ReferenceHit h)
        {
            if (h == null || string.IsNullOrEmpty(h.LineText) || h.Length <= 0) return null;
            int start = h.LineTextColumn;
            if (start < 0 || start >= h.LineText.Length) return null;
            int len = Math.Min(h.Length, h.LineText.Length - start);
            return len > 0 ? (start, len) : ((int, int)?)null;
        }

        /// <summary>
        /// Row text "123  line text" (1-based line number, leading indentation removed) and the match range inside it
        /// (length 0 when the match is not inside the text).
        /// </summary>
        public static string DisplayLine(ReferenceHit h, out int matchStart, out int matchLength)
        {
            matchStart = 0;
            matchLength = 0;
            if (h?.Location == null) return string.Empty;
            string prefix = (h.Location.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "  ";
            string text = h.LineText ?? string.Empty;
            int indent = 0;
            while (indent < text.Length && char.IsWhiteSpace(text[indent])) indent++;
            var m = MatchRange(h);
            if (m.HasValue && m.Value.Start < indent) indent = m.Value.Start; // never cut into the match
            string body = text.Substring(indent).TrimEnd();
            if (m.HasValue && m.Value.Start - indent < body.Length)
            {
                matchStart = prefix.Length + m.Value.Start - indent;
                matchLength = Math.Min(m.Value.Length, body.Length - (m.Value.Start - indent));
            }
            return prefix + body;
        }

        /// <summary>Most telling kind badge: Write &gt; Definition &gt; Declaration &gt; Call &gt; TypeUse &gt; Read.</summary>
        public static string KindText(ReferenceKind k)
        {
            if ((k & ReferenceKind.Write) != 0) return NavStrings.KindWrite;
            if ((k & ReferenceKind.Definition) != 0) return NavStrings.KindDefinition;
            if ((k & ReferenceKind.Declaration) != 0) return NavStrings.KindDeclaration;
            if ((k & ReferenceKind.Call) != 0) return NavStrings.KindCall;
            if ((k & ReferenceKind.TypeUse) != 0) return NavStrings.KindTypeUse;
            if ((k & ReferenceKind.Read) != 0) return NavStrings.KindRead;
            return null;
        }

        public static string Summary(ReferencesResponse r, ReferenceView v)
        {
            int hits = v?.Shown ?? 0;
            int files = v?.Groups.Count ?? 0;
            string s = NavStrings.ReferencesSummary(hits, files, r?.ElapsedMs ?? 0, r?.Truncated == true);
            if (v != null && v.Hidden > 0) s += NavStrings.ReferencesHidden(v.Hidden);
            if (r != null && !r.IndexReady) s += NavStrings.IndexNotReady;
            return s;
        }
    }
}
