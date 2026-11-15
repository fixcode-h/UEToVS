using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Search;
using UiBadge = UEToVS.UI.Logic.BadgeKind;
using UiGlyph = UEToVS.UI.Logic.SymbolKind;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>Which count of a Code Vision row was clicked.</summary>
    public enum CodeVisionSegment
    {
        DerivedTypes,
        Blueprints,
    }

    /// <summary>One clickable piece of a Code Vision row ("2 个派生类", "3 个蓝图").</summary>
    public sealed class CodeVisionPart
    {
        public CodeVisionPart(CodeVisionSegment segment, string text)
        {
            Segment = segment;
            Text = text;
        }

        public CodeVisionSegment Segment { get; }
        public string Text { get; }
    }

    /// <summary>
    /// v0.7.6 Code Vision click (Rider): the row's pieces and the list shown when one is clicked. Pure: the editor draws
    /// <see cref="Parts"/>, asks the engine for <see cref="CodeVisionItemsResponse"/> and shows <see cref="Rows"/> in a list
    /// popup. Blueprint rows behave like Search Everywhere's asset rows (copy object path + select the .uasset in Explorer).
    /// </summary>
    public static class CodeVisionModel
    {
        public const string Separator = " · ";

        /// <summary>The counts of a Usages hint, in display order; empty when every count is 0.</summary>
        public static IReadOnlyList<CodeVisionPart> Parts(InlayHint h)
        {
            var parts = new List<CodeVisionPart>(2);
            if (h == null) return parts;
            if (h.DerivedTypes > 0) parts.Add(new CodeVisionPart(CodeVisionSegment.DerivedTypes, NavStrings.CodeVisionDerived(h.DerivedTypes)));
            if (h.Blueprints > 0) parts.Add(new CodeVisionPart(CodeVisionSegment.Blueprints, NavStrings.CodeVisionBlueprints(h.Blueprints)));
            return parts;
        }

        /// <summary>"AMyActor::Fire" is a member hint; "AMyActor" a class hint.</summary>
        public static bool IsMember(string symbolName) => symbolName != null && symbolName.IndexOf("::", StringComparison.Ordinal) >= 0;

        public static string Title(CodeVisionSegment segment, string symbolName, int count) =>
            segment == CodeVisionSegment.DerivedTypes ? NavStrings.CodeVisionDerivedTitle(symbolName, count)
            : IsMember(symbolName) ? NavStrings.CodeVisionUsersTitle(symbolName, count)
            : NavStrings.CodeVisionBlueprintsTitle(symbolName, count);

        /// <summary>Rows of the popup for the clicked piece; a disabled hint row explains an empty or cut list.</summary>
        public static List<SearchRow> Rows(CodeVisionSegment segment, CodeVisionItemsResponse resp)
        {
            var rows = new List<SearchRow>();
            if (resp == null) return rows;
            if (segment == CodeVisionSegment.DerivedTypes)
            {
                foreach (var s in resp.DerivedTypes ?? new List<SymbolInfo>())
                    if (s != null) rows.Add(SearchEverywhereModel.FromSymbol(new SearchHit { Symbol = s, Category = ResultCategory.Type }, null, true));
            }
            else
            {
                foreach (var b in resp.Blueprints ?? new List<CodeVisionBlueprint>())
                    if (b?.Asset != null) rows.Add(BlueprintRow(b));
            }
            if (rows.Count == 0)
                rows.Add(Hint(segment == CodeVisionSegment.Blueprints && !resp.BlueprintIndexReady ? NavStrings.CodeVisionIndexLoading : NavStrings.CodeVisionNothing));
            else if (resp.Truncated)
                rows.Add(Hint(NavStrings.CodeVisionTruncated(rows.Count)));
            return rows;
        }

        /// <summary>
        /// Blueprint row: name, object path below it, "← parent" on the right. A Blueprint child of another Blueprint is
        /// indented under it (the engine sends parents before children), like Rider's derived Blueprint list.
        /// </summary>
        public static SearchRow BlueprintRow(CodeVisionBlueprint b)
        {
            var a = b.Asset;
            int depth = Math.Max(0, Math.Min(b.Depth, 6));
            string indent = depth == 0 ? string.Empty : new string(' ', (depth - 1) * 3) + "└ ";
            var row = new SearchRow
            {
                Kind = SearchRowKind.Asset,
                Title = indent + (a.Name ?? string.Empty),
                Detail = a.ObjectPath ?? a.FilePath,
                Right = string.IsNullOrEmpty(b.ParentClass) ? null : "← " + b.ParentClass,
                Glyph = UiGlyph.Asset,
                Asset = a,
            };
            row.Badges.Add(new KeyValuePair<string, UiBadge>(b.IsLevel ? NavStrings.KindLevel : NavStrings.KindBlueprint, UiBadge.Asset));
            return row;
        }

        /// <summary>Text matched by the popup's filter box (the name without the tree indent, plus the object path).</summary>
        public static string FilterText(SearchRow r) => r == null ? string.Empty : (r.Asset?.Name ?? r.Title ?? string.Empty) + " " + (r.Detail ?? string.Empty);

        private static SearchRow Hint(string text) => new SearchRow { Kind = SearchRowKind.Hint, Title = text };

        /// <summary>Joined text of the parts (logs, tooltips, tests).</summary>
        public static string Text(InlayHint h) => string.Join(Separator, Parts(h).Select(p => p.Text));
    }
}
