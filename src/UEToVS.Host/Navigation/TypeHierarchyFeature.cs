using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Navigation
{
    /// <summary>
    /// Type Hierarchy popup (TabbedTreePopup): "base types" and "derived types" tabs, each a tree rooted at the type at the
    /// caret (or the class of the member at the caret). Types not in the index (template arguments, external types) are
    /// grayed; cut subtrees end with a "… (truncated)" row. Enter / double-click jumps; the popup stays open.
    /// </summary>
    internal static class TypeHierarchyFeature
    {
        private const string TabBases = "bases";
        private const string TabDerived = "derived";
        private const int ExpandDepth = 3;

        private static readonly Logger L = Log.For("Navigation");
        private static readonly LatestRequestGate Gate = new LatestRequestGate();
        private static TabbedTreePopup<HierarchyRow> _popup;

        public static async Task ExecuteAsync(string source)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorServices.ActiveView();
            if (view == null || view.IsClosed || !EditorServices.IsCpp(view.TextBuffer))
            {
                VsStatusBar.SetText(NavStrings.NoActiveCppEditor);
                return;
            }
            var caret = await EditorServices.CaretRequestAsync(view);
            if (caret == null) return;
            var (gen, ct) = Gate.Begin(NavigationContext.Disposal);
            var engine = NavigationContext.Engine;
            var r = engine == null
                ? EngineCallResult<TypeHierarchyResponse>.Fail(EngineCallStatus.Unavailable, 0)
                : await engine.GetTypeHierarchyAsync(new TypeHierarchyRequest { Caret = caret, Direction = HierarchyDirection.Both }, ct);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Gate.IsCurrent(gen)) return;
            if (r.Status != EngineCallStatus.Ok)
            {
                string msg = EngineCallText.ForStatus(r.Status);
                if (msg != null) VsStatusBar.SetText(msg);
                return;
            }
            var resp = r.Value;
            if (resp.Root == null)
            {
                VsStatusBar.SetText(NavStrings.NoTypeAtCaret + (resp.IndexReady ? string.Empty : NavStrings.IndexNotReady));
                L.Info("nav.hierarchy.noType", null, ("source", source), ("indexReady", resp.IndexReady));
                return;
            }
            Show(resp, source);
        }

        private static void Show(TypeHierarchyResponse resp, string source)
        {
            if (_popup == null)
            {
                var tabs = new[]
                {
                    new TabDefinition(TabBases, () => NavStrings.HierarchyBases),
                    new TabDefinition(TabDerived, () => NavStrings.HierarchyDerived),
                };
                _popup = new TabbedTreePopup<HierarchyRow>("uetovs.typeHierarchy", NavigationPresenters.Hierarchy, tabs, new System.Windows.Size(600, 480));
                _popup.ItemInvoked += (s, e) => Diagnostics.Safe.Execute(L, "nav.hierarchy.open", () =>
                {
                    var loc = NavigationPresenters.ToLocation(e.Item.Symbol?.Location);
                    if (loc != null) NavigationService.JumpTo(loc, "hierarchy");
                    else VsStatusBar.SetText(NavStrings.HierarchyUnresolved);
                });
            }
            _popup.OwnerHandle = VsMainWindow.Handle;
            string name = SymbolPresentation.QualifiedName(resp.Root);
            _popup.Title = NavStrings.HierarchyTitle(name);
            int baseCount = Count(resp.Bases), derivedCount = Count(resp.Derived);
            _popup.SetTabRoots(TabBases, new[] { RootNode(resp.Root, resp.Bases) }, baseCount);
            _popup.SetTabRoots(TabDerived, new[] { RootNode(resp.Root, resp.Derived) }, derivedCount);
            // Open on the tab with content (bases first, like Rider); derived when the type has no bases.
            _popup.SelectedTabId = baseCount == 0 && derivedCount > 0 ? TabDerived : TabBases;
            _popup.Footer.Status = NavStrings.HierarchySummary(resp.NodeCount, resp.ElapsedMs) + (resp.IndexReady ? string.Empty : NavStrings.IndexNotReady);
            _popup.ShowAt(PopupAnchor.CenterOf(VsMainWindow.Handle));
            L.Info("nav.hierarchy.shown", null, ("source", source), ("type", name), ("bases", baseCount), ("derived", derivedCount), ("nodes", resp.NodeCount));
        }

        private static TreeNode<HierarchyRow> RootNode(SymbolInfo root, List<TypeHierarchyNode> children)
        {
            var kids = new List<TreeNode<HierarchyRow>>();
            foreach (var c in children ?? new List<TypeHierarchyNode>()) kids.Add(Convert(c, 1));
            return new TreeNode<HierarchyRow>(new HierarchyRow { Symbol = root, IsRoot = true }, kids, true);
        }

        private static TreeNode<HierarchyRow> Convert(TypeHierarchyNode n, int depth)
        {
            var kids = new List<TreeNode<HierarchyRow>>();
            foreach (var c in n.Children ?? new List<TypeHierarchyNode>())
                if (c != null) kids.Add(Convert(c, depth + 1));
            if (n.Truncated) kids.Add(new TreeNode<HierarchyRow>(new HierarchyRow { IsTruncatedMarker = true }));
            var row = new HierarchyRow { Node = n, Symbol = n.Symbol, Display = n.Symbol == null ? n.DisplayName : null };
            return new TreeNode<HierarchyRow>(row, kids, depth < ExpandDepth);
        }

        private static int Count(List<TypeHierarchyNode> nodes)
        {
            int n = 0;
            if (nodes == null) return 0;
            foreach (var c in nodes)
                if (c != null) n += 1 + Count(c.Children);
            return n;
        }
    }
}
