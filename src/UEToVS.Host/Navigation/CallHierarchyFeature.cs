using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Editor;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Navigation;
using UEToVS.Host.Shell;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Shells;
using BadgeKind = UEToVS.UI.Logic.BadgeKind;
using Glyph = UEToVS.UI.Logic.SymbolKind;

namespace UEToVS.Host.Navigation
{
    // ---- v0.4.5 navigation gaps
    /// <summary>
    /// Call Hierarchy (Ctrl+Shift+Alt+U, H; Rider Ctrl+Alt+H) in a ResultsPopup like Find References: callers / callees of the
    /// function at the caret, one level per engine call (a level is asked for when its row is expanded). Delegate
    /// bindings are callers with a "delegate binding" badge; UFUNCTION _Implementation / _Validate count as the function.
    /// Type to filter, preview at the right, Enter jumps (the popup stays open), → / ← expand and collapse.
    /// Replaces VS's View Call Hierarchy, which needs VS's C++ IntelliSense.
    /// </summary>
    internal static class CallHierarchyFeature
    {
        private static readonly Logger L = Log.For("Navigation");
        private static readonly LatestRequestGate Gate = new LatestRequestGate();
        private static readonly HashSet<CallNode> Pending = new HashSet<CallNode>();

        private static ResultsPopup<CallNode> _popup;
        private static SegmentedControl _direction;
        private static Chip _includeEngine, _showUncertain;
        private static SearchInput _filter;

        private static CallNode _root;
        private static CallHierarchyResponse _rootResponse;
        private static CaretRequest _caret;
        private static CallHierarchyDirection _dir = CallHierarchyDirection.Callers;
        private static bool _includeEngineOn, _showUncertainOn;
        private static long _session;

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
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            L.Info("navigation.callHierarchy.start", null, ("source", source), ("direction", _dir.ToString()), ("includeEngine", _includeEngineOn));
            _caret = caret;
            await LoadRootAsync(caret, null, clearFilter: true);
        }

        /// <summary>Asks for the first level (by caret, or by function when the direction / scope changes) and shows it.</summary>
        private static async Task LoadRootAsync(CaretRequest caret, SymbolInfo function, bool clearFilter)
        {
            EnsurePopup();
            if (clearFilter && !string.IsNullOrEmpty(_filter.Text)) _filter.Text = string.Empty;
            var (gen, ct) = Gate.Begin(NavigationContext.Disposal);
            _session++;
            Pending.Clear();
            var engine = NavigationContext.Engine;
            var request = new CallHierarchyRequest { Caret = function == null ? caret : null, Function = function, Direction = _dir, IncludeNonProject = _includeEngineOn };
            bool wasVisible = _popup.IsVisible;
            if (wasVisible)
            {
                _popup.IsBusy = true;
                _popup.SetResults(Array.Empty<TreeNode<CallNode>>(), NavGapStrings.Loading);
            }
            var r = engine == null
                ? EngineCallResult<CallHierarchyResponse>.Fail(EngineCallStatus.Unavailable, 0)
                : await engine.GetCallHierarchyAsync(request, ct);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Gate.IsCurrent(gen)) return;
            _popup.IsBusy = false;
            if (r.Status != EngineCallStatus.Ok)
            {
                string msg = EngineCallText.ForStatus(r.Status) ?? NavGapStrings.LevelFailed;
                VsStatusBar.SetText(msg);
                if (wasVisible) _popup.SetResults(Array.Empty<TreeNode<CallNode>>(), msg);
                return;
            }
            var resp = r.Value;
            if (resp.Root == null)
            {
                string msg = NavGapStrings.NoFunctionAtCaret + (resp.IndexReady ? string.Empty : NavStrings.IndexNotReady);
                VsStatusBar.SetText(msg);
                if (wasVisible) _popup.SetResults(Array.Empty<TreeNode<CallNode>>(), msg);
                return;
            }
            _rootResponse = resp;
            _root = CallHierarchyModel.CreateRoot(resp);
            _popup.Title = NavGapStrings.CallHierarchyTitle(SymbolPresentation.QualifiedName(resp.Root));
            Render(null);
            // First child (the first caller / callee), not the root: Enter on it jumps right away.
            if (_popup.Tree.Count > 1) _popup.Tree.SelectedIndex = 1;
            if (!_popup.IsVisible) _popup.ShowAt(PopupAnchor.CenterOf(VsMainWindow.Handle));
            L.Info("navigation.callHierarchy.shown", null, ("function", SymbolPresentation.QualifiedName(resp.Root)), ("direction", _dir.ToString()),
                   ("items", resp.Items.Count), ("names", string.Join(",", resp.Names ?? new List<string>())), ("truncated", resp.Truncated));
        }

        /// <summary>One more level below <paramref name="node"/> (expanded for the first time).</summary>
        private static async Task LoadLevelAsync(CallNode node)
        {
            if (node == null || !node.CanLoad || !Pending.Add(node)) return;
            long session = _session;
            var engine = NavigationContext.Engine;
            var request = new CallHierarchyRequest { Function = node.Function, Direction = _dir, IncludeNonProject = _includeEngineOn };
            var r = engine == null
                ? EngineCallResult<CallHierarchyResponse>.Fail(EngineCallStatus.Unavailable, 0)
                : await engine.GetCallHierarchyAsync(request, NavigationContext.Disposal);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Pending.Remove(node);
            if (session != _session || _popup == null) return; // a new root replaced this tree
            if (r.Status == EngineCallStatus.Ok) CallHierarchyModel.ApplyLevel(node, r.Value);
            else CallHierarchyModel.FailLevel(node, EngineCallText.ForStatus(r.Status));
            node.IsExpanded = true;
            Render(_popup.Tree.SelectedNode?.Item);
        }

        // ------------------------------------------------------------------ tree

        /// <summary>Rebuilds the visible tree from the model (filter, uncertain toggle) and keeps the selection.</summary>
        private static void Render(CallNode keepSelected)
        {
            if (_popup == null || _root == null) return;
            using (L.Time("navigation.callHierarchy.render", 16))
            {
                string filter = _filter.Text;
                var top = Convert(_root, filter);
                _popup.SetResults(new[] { top }, CallHierarchyModel.Summary(_rootResponse) + (_rootResponse.IndexReady ? string.Empty : NavStrings.IndexNotReady));
                if (keepSelected != null) _popup.Tree.SelectItem(n => ReferenceEquals(n, keepSelected));
            }
        }

        private static TreeNode<CallNode> Convert(CallNode n, string filter)
        {
            List<TreeNode<CallNode>> kids = null;
            foreach (var c in n.Children)
            {
                if (!CallHierarchyModel.IsVisible(c, filter, _showUncertainOn)) continue;
                (kids ?? (kids = new List<TreeNode<CallNode>>())).Add(Convert(c, filter));
            }
            // While filtering, loaded levels open so matches below them are visible.
            bool expanded = n.IsExpanded || (!string.IsNullOrWhiteSpace(filter) && n.Loaded);
            return new TreeNode<CallNode>(n, kids, expanded);
        }

        private static void OnToggled(TreeNode<CallNode> t)
        {
            var n = t.Item;
            if (n == null) return;
            n.IsExpanded = t.IsExpanded;
            if (t.IsExpanded && n.CanLoad) Diagnostics.Safe.RunAsync(L, "navigation.callHierarchy.expand", () => LoadLevelAsync(n));
        }

        private static void Jump(CallNode n)
        {
            if (n == null) return;
            if (n.Kind == CallNodeKind.Loading)
            {
                Diagnostics.Safe.RunAsync(L, "navigation.callHierarchy.expand", () => LoadLevelAsync(n.Parent));
                return;
            }
            var loc = NavigationPresenters.ToLocation(CallHierarchyModel.TargetOf(n, _dir));
            if (loc != null) NavigationService.Instance.JumpTo(loc, "callHierarchy");
        }

        // ------------------------------------------------------------------ popup

        private static void EnsurePopup()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_popup != null)
            {
                _popup.OwnerHandle = VsMainWindow.Handle;
                return;
            }
            _popup = new ResultsPopup<CallNode>("uetovs.callHierarchy", Presenter, new Size(900, 520)) { OwnerHandle = VsMainWindow.Handle };
            _popup.Tree.InvokeExpandableItems = true;

            _direction = new SegmentedControl(NavGapStrings.Callers, NavGapStrings.Callees) { SelectedIndex = _dir == CallHierarchyDirection.Callees ? 1 : 0 };
            _includeEngine = new Chip(() => NavGapStrings.IncludeEngine, _includeEngineOn);
            _showUncertain = new Chip(() => NavGapStrings.ShowUncertain, _showUncertainOn);
            Tip.Set(_showUncertain, () => NavGapStrings.ShowUncertainTip);
            _filter = new SearchInput(embedded: false) { Width = 200, Placeholder = NavGapStrings.FilterPlaceholder };
            _popup.AddToolbarItem(_direction);
            _popup.AddToolbarItem(_includeEngine);
            _popup.AddToolbarItem(_showUncertain);
            _popup.AddToolbarItem(_filter, right: true);

            _direction.SelectionChanged += (s, e) => Diagnostics.Safe.RunAsync(L, "navigation.callHierarchy.direction", async () =>
            {
                var dir = _direction.SelectedIndex == 1 ? CallHierarchyDirection.Callees : CallHierarchyDirection.Callers;
                if (dir == _dir) return;
                _dir = dir;
                _includeEngine.IsEnabled = dir == CallHierarchyDirection.Callers;
                if (_root?.Function != null) await LoadRootAsync(_caret, _root.Function, clearFilter: false);
            });
            _includeEngine.Click += (s, e) => Diagnostics.Safe.RunAsync(L, "navigation.callHierarchy.includeEngine", async () =>
            {
                _includeEngineOn = _includeEngine.IsChecked == true;
                if (_root?.Function != null) await LoadRootAsync(_caret, _root.Function, clearFilter: false);
            });
            _showUncertain.Click += (s, e) => Diagnostics.Safe.Execute(L, "navigation.callHierarchy.uncertain", () =>
            {
                _showUncertainOn = _showUncertain.IsChecked == true;
                Render(_popup.Tree.SelectedNode?.Item);
            });
            _filter.TextChanged += (s, text) => Diagnostics.Safe.Execute(L, "navigation.callHierarchy.filter", () =>
            {
                Render(_popup.Tree.SelectedNode?.Item);
                if (!string.IsNullOrWhiteSpace(text)) _popup.Tree.SelectItem(n => n.Kind != CallNodeKind.Root && CallHierarchyModel.Matches(n, text));
            });
            // Filter box keys: ↑ / ↓ move the tree, Enter jumps, Esc clears the filter first.
            _filter.InputBox.PreviewKeyDown += (s, e) => Diagnostics.Safe.Execute(L, "navigation.callHierarchy.filterKey", () =>
            {
                var tree = _popup.Tree;
                switch (e.Key)
                {
                    case Key.Down: tree.SelectedIndex = Math.Min(tree.SelectedIndex + 1, tree.Count - 1); tree.ScrollIntoView(tree.SelectedIndex); e.Handled = true; break;
                    case Key.Up: tree.SelectedIndex = Math.Max(tree.SelectedIndex - 1, 0); tree.ScrollIntoView(tree.SelectedIndex); e.Handled = true; break;
                    case Key.Enter: Jump(tree.SelectedNode?.Item); e.Handled = true; break;
                    case Key.Escape when !string.IsNullOrEmpty(_filter.Text): _filter.Text = string.Empty; Render(tree.SelectedNode?.Item); tree.Focus(); e.Handled = true; break;
                }
            });
            // Type to filter: printable keys typed in the tree go to the filter box.
            _popup.Tree.PreviewTextInput += (s, e) => Diagnostics.Safe.Execute(L, "navigation.callHierarchy.typeToFilter", () =>
            {
                if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
                _filter.Text = (_filter.Text ?? string.Empty) + e.Text;
                _filter.InputBox.Focus();
                _filter.InputBox.CaretIndex = _filter.Text.Length;
                Render(_popup.Tree.SelectedNode?.Item);
                _popup.Tree.SelectItem(n => n.Kind != CallNodeKind.Root && CallHierarchyModel.Matches(n, _filter.Text));
                e.Handled = true;
            });
            _popup.Tree.NodeToggled += (s, t) => Diagnostics.Safe.Execute(L, "navigation.callHierarchy.toggle", () => OnToggled(t));
            // "Expand all" shows "Loading…" rows without toggling them: load when one gets selected.
            _popup.Tree.SelectionChanged += (s, e) => Diagnostics.Safe.Execute(L, "navigation.callHierarchy.select", () =>
            {
                var n = _popup.Tree.SelectedNode?.Item;
                if (n?.Kind == CallNodeKind.Loading && n.Parent != null && n.Parent.CanLoad)
                    Diagnostics.Safe.RunAsync(L, "navigation.callHierarchy.expand", () => LoadLevelAsync(n.Parent));
            });
            _popup.ItemInvoked += (s, e) => Diagnostics.Safe.Execute(L, "navigation.callHierarchy.open", () => Jump(e.Item));

            Diagnostics.Safe.Execute(L, "navigation.callHierarchy.preview.attach", () => Search.EditorCodePreview.Attach(_popup.Panel.Preview));
            // CodePreviewRequest.Line is 1-based; contract lines are 0-based.
            _popup.Panel.PreviewProvider = n =>
            {
                var loc = CallHierarchyModel.TargetOf(n, _dir);
                return loc == null || string.IsNullOrEmpty(loc.FilePath) ? null
                    : new CodePreviewRequest { FilePath = loc.FilePath, Line = loc.Line + 1, Column = loc.Column, Length = CallHierarchyModel.TargetLength(n, _dir) };
            };
        }

        // ------------------------------------------------------------------ rows

        private static readonly IItemPresenter<CallNode> Presenter = new DelegatePresenter<CallNode>((n, row) =>
        {
            switch (n.Kind)
            {
                case CallNodeKind.Loading:
                case CallNodeKind.Message:
                    row.Primary = CallHierarchyModel.Title(n);
                    row.IsDimmed = true;
                    return;
                case CallNodeKind.Site:
                    var s = n.Site;
                    var hit = new ReferenceHit { Location = s?.Location, Length = s?.Length ?? 0, LineText = s?.LineText, LineTextColumn = s?.LineTextColumn ?? 0 };
                    row.Primary = ReferenceResults.DisplayLine(hit, out int start, out int len);
                    if (len > 0) row.PrimaryRanges = new[] { new TextRange(start, len) };
                    row.Secondary = CallHierarchyModel.Detail(n, _dir);
                    row.Right = SymbolPresentation.ShortLocation(s?.Location);
                    if (s?.Kind == CallSiteKind.DelegateBinding) row.AddBadge(NavGapStrings.Binding, BadgeKind.Delegate);
                    row.IsDimmed = CallHierarchyModel.IsUncertain(n);
                    row.ToolTip = SymbolPresentation.ConfidenceText(s?.Confidence ?? ResolutionConfidence.None);
                    return;
            }
            var f = n.Function;
            row.Glyph = f != null ? SymbolPresentation.GlyphFor(f) : n.Item?.Sites?.Count > 0 && _dir == CallHierarchyDirection.Callers ? Glyph.File : Glyph.Function;
            row.Primary = CallHierarchyModel.Title(n);
            row.Secondary = CallHierarchyModel.Detail(n, _dir);
            row.Right = CallHierarchyModel.Right(n, _dir);
            if (n.Item?.Sites != null && n.Item.Sites.Exists(x => x.Kind == CallSiteKind.DelegateBinding)) row.AddBadge(NavGapStrings.Binding, BadgeKind.Delegate);
            if (f != null)
            {
                var b = SymbolPresentation.UeBadge(f.Ue);
                if (b.HasValue) row.AddBadge(b.Value.Text, b.Value.Kind);
            }
            row.IsDimmed = CallHierarchyModel.IsUncertain(n) || (f != null && SymbolPresentation.IsNonProject(f.Origin));
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(f?.Signature)) parts.Add(f.Signature);
            if (f?.Location != null) parts.Add(f.Location.FilePath + ":" + (f.Location.Line + 1));
            if (n.Item != null) parts.Add(SymbolPresentation.ConfidenceText(n.Item.Confidence));
            if (CallHierarchyModel.IsUncertain(n)) parts.Add(NavGapStrings.ShowUncertainTip);
            row.ToolTip = parts.Count == 0 ? null : string.Join("\n", parts);
        }, n => true);
    }
}
