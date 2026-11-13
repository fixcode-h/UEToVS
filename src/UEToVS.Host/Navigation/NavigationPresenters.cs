using System.Collections.Generic;
using UEToVS.Contracts;
using UEToVS.Core.Navigation;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using BadgeKind = UEToVS.UI.Logic.BadgeKind;
using Glyph = UEToVS.UI.Logic.SymbolKind;

namespace UEToVS.Host.Navigation
{
    /// <summary>A row of the Find References tree: a file group or one hit.</summary>
    internal sealed class ReferenceRow
    {
        public ReferenceFileGroup Group;
        public ReferenceHit Hit;
    }

    /// <summary>A row of the type hierarchy tree.</summary>
    internal sealed class HierarchyRow
    {
        public TypeHierarchyNode Node;
        public SymbolInfo Symbol;
        public string Display;
        public bool IsRoot;
        public bool IsTruncatedMarker;
    }

    /// <summary>Maps contract data to UEToVS.UI rows (glyphs, badges, dimming, tooltips). No logic beyond presentation.</summary>
    internal static class NavigationPresenters
    {
        public static CodeLocation ToLocation(SourceLocation l) =>
            l == null || string.IsNullOrEmpty(l.FilePath) ? null : new CodeLocation(l.FilePath, l.Line, l.Column);

        private static void AddUeBadge(RowData row, UeFlags ue)
        {
            var b = SymbolPresentation.UeBadge(ue);
            if (b.HasValue) row.AddBadge(b.Value.Text, b.Value.Kind);
        }

        private static string Tooltip(SymbolInfo s, string extra = null)
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(s?.Signature)) parts.Add(s.Signature);
            if (!string.IsNullOrEmpty(s?.Specifiers)) parts.Add(s.Specifiers);
            if (s?.Location != null) parts.Add(s.Location.FilePath + ":" + (s.Location.Line + 1));
            if (!string.IsNullOrEmpty(extra)) parts.Add(extra);
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }

        /// <summary>当前选择列表里同名文件的区分目录（<see cref="NavigationOutcome.LocationHints"/>），弹列表前设置；只在 UI 线程用。</summary>
        public static IReadOnlyDictionary<NavigationTarget, string> TargetHints = new Dictionary<NavigationTarget, string>();

        /// <summary>Go to definition / implementation chooser: "Class::Member", file:line, role badge, UE badge, module/origin.</summary>
        public static readonly IItemPresenter<NavigationTarget> Target = new DelegatePresenter<NavigationTarget>((t, row) =>
        {
            var s = t.Symbol;
            row.Glyph = SymbolPresentation.GlyphFor(s);
            row.Primary = SymbolPresentation.QualifiedName(s);
            row.Secondary = SymbolPresentation.ModuleAndOrigin(s.Module, s.Origin);
            row.Right = TargetHints.TryGetValue(t, out var hint)
                ? hint + " › " + SymbolPresentation.ShortLocation(s.Location)
                : SymbolPresentation.ShortLocation(s.Location);
            row.AddBadge(SymbolPresentation.RoleText(t.Role), t.Role == NavigationTargetRole.Definition ? BadgeKind.Module : BadgeKind.Neutral);
            AddUeBadge(row, s.Ue);
            row.IsDimmed = SymbolPresentation.IsNonProject(s.Origin);
            row.ToolTip = Tooltip(s);
        }, t => true);

        /// <summary>Member lists (Alt+M): glyph by kind, UE badge, signature or "from Base", line.</summary>
        /// <param name="ownerType">Type whose members are listed (null = file list); read per row so one popup serves every type.</param>
        public static IItemPresenter<SymbolInfo> Member(System.Func<SymbolInfo> ownerType) => new DelegatePresenter<SymbolInfo>((s, row) =>
        {
            var owner = ownerType?.Invoke();
            row.Glyph = SymbolPresentation.GlyphFor(s);
            row.Primary = s.Name;
            bool inherited = owner != null && ScopeModel.IsInherited(s, owner);
            row.Secondary = inherited ? NavStrings.InheritedFrom(s.Container) : !string.IsNullOrEmpty(s.Signature) ? s.Signature : s.Container;
            row.Right = owner == null ? (s.Location == null ? null : (s.Location.Line + 1).ToString()) : SymbolPresentation.ShortLocation(s.Location);
            AddUeBadge(row, s.Ue);
            row.IsDimmed = inherited;
            row.ToolTip = Tooltip(s);
        }, s => true);

        /// <summary>Navigation bar drop-downs (types).</summary>
        public static readonly IItemPresenter<ScopeEntry> Scope = new DelegatePresenter<ScopeEntry>((e, row) =>
        {
            row.Glyph = e.IsGlobal ? Glyph.Namespace : e.Symbol != null ? SymbolPresentation.GlyphFor(e.Symbol) : Glyph.Class;
            row.Primary = e.Display;
            row.Secondary = e.Container;
            if (e.Symbol != null) AddUeBadge(row, e.Symbol.Ue);
            if (e.Location != null) row.Right = (e.Location.Line + 1).ToString();
        }, e => true);

        public static readonly IItemPresenter<ReferenceRow> Reference = new DelegatePresenter<ReferenceRow>((r, row) =>
        {
            if (r.Group != null)
            {
                var g = r.Group;
                row.Glyph = Glyph.File;
                row.Primary = g.FileName;
                row.Secondary = g.Directory;
                if (!string.IsNullOrEmpty(g.Module)) row.AddBadge(g.Module, BadgeKind.Module);
                row.IsDimmed = SymbolPresentation.IsNonProject(g.Origin);
                row.ToolTip = g.FilePath;
                return;
            }
            var h = r.Hit;
            row.Primary = ReferenceResults.DisplayLine(h, out int start, out int len);
            if (len > 0) row.PrimaryRanges = new[] { new TextRange(start, len) };
            row.Secondary = h.Context;
            string kind = ReferenceResults.KindText(h.Kind);
            if (kind != null) row.AddBadge(kind, ReferenceResults.IsWrite(h) ? BadgeKind.UProperty : BadgeKind.Neutral, ReferenceResults.IsWrite(h));
            bool uncertain = ReferenceResults.IsUncertain(h);
            row.IsDimmed = uncertain;
            row.ToolTip = SymbolPresentation.ConfidenceText(h.Confidence) + (uncertain ? "\n" + NavStrings.ReferencesUncertainTip : string.Empty) +
                          (string.IsNullOrEmpty(h.Context) ? string.Empty : "\n" + h.Context);
        }, r => true);

        public static readonly IItemPresenter<HierarchyRow> Hierarchy = new DelegatePresenter<HierarchyRow>((r, row) =>
        {
            if (r.IsTruncatedMarker)
            {
                row.Primary = NavStrings.HierarchyTruncated;
                row.IsDimmed = true;
                return;
            }
            var s = r.Symbol;
            row.Glyph = s != null ? SymbolPresentation.GlyphFor(s) : Glyph.Class;
            row.Primary = !string.IsNullOrEmpty(r.Display) ? r.Display : SymbolPresentation.QualifiedName(s);
            if (s != null)
            {
                row.Secondary = SymbolPresentation.ModuleAndOrigin(s.Module, s.Origin);
                row.Right = SymbolPresentation.ShortLocation(s.Location);
                AddUeBadge(row, s.Ue);
                row.ToolTip = Tooltip(s);
            }
            else
            {
                row.IsDimmed = true;
                row.ToolTip = NavStrings.HierarchyUnresolved;
            }
        }, r => !r.IsTruncatedMarker);
    }
}
