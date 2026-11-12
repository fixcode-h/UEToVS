using System;
using System.Collections.Generic;
using System.Windows;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.UI.Molecules
{
    /// <summary>
    /// Virtualized tree (grouped results: file → matches, module → symbols). Same engine and row as
    /// <see cref="VirtualList{T}"/>; nodes are flattened lazily on expand/collapse.
    /// Keys: → expand / go to first child, ← collapse / go to parent, Enter invokes a leaf or toggles a group.
    /// Group rows show their leaf count at the right unless the presenter sets <see cref="RowData.Right"/>.
    /// </summary>
    public class TreeList<T> : VirtualListCore
    {
        private readonly TreeFlattener<T> _flat = new TreeFlattener<T>();
        private readonly RowData _scratch = new RowData();
        private IReadOnlyList<TreeNode<T>> _roots = Array.Empty<TreeNode<T>>();

        public TreeList(IItemPresenter<T> presenter = null)
        {
            Presenter = presenter;
        }

        public IItemPresenter<T> Presenter { get; set; }

        public event EventHandler<ListInvokeEventArgs<T>> ItemInvoked;

        /// <summary>A node was expanded or collapsed by the user (→ / ← / chevron / Enter); lazy trees load its children here.</summary>
        public event EventHandler<TreeNode<T>> NodeToggled;

        /// <summary>
        /// Enter / double-click on a node with children raises <see cref="ItemInvoked"/> (jump) instead of toggling it;
        /// → / ← and the chevron still expand / collapse. For trees whose inner nodes are targets too (call hierarchy).
        /// </summary>
        public bool InvokeExpandableItems { get; set; }

        public IReadOnlyList<TreeNode<T>> Roots => _roots;

        public void SetRoots(IReadOnlyList<TreeNode<T>> roots)
        {
            _roots = roots ?? Array.Empty<TreeNode<T>>();
            _flat.SetRoots(_roots);
            ResetCount(_flat.Rows.Count);
        }

        public TreeNode<T> SelectedNode
        {
            get
            {
                int i = SelectedIndex;
                return i >= 0 && i < _flat.Rows.Count ? _flat.Rows[i].Node : null;
            }
        }

        public void ExpandAll(bool expanded)
        {
            var sel = SelectedNode;
            _flat.SetAllExpanded(_roots, expanded);
            ResetCount(_flat.Rows.Count, true);
            if (sel != null)
                for (int i = 0; i < _flat.Rows.Count; i++)
                    if (_flat.Rows[i].Node == sel) { SelectedIndex = i; break; }
        }

        public void Toggle(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _flat.Rows.Count) return;
            var node = _flat.Rows[rowIndex].Node;
            if (!node.HasChildren) return;
            _flat.SetExpanded(rowIndex, !node.IsExpanded);
            UpdateCount(_flat.Rows.Count);
            SelectedIndex = rowIndex;
            NodeToggled?.Invoke(this, node);
        }

        /// <summary>Selects (and scrolls to) the first visible row whose item matches; false when none does.</summary>
        public bool SelectItem(Func<T, bool> match)
        {
            if (match == null) return false;
            for (int i = 0; i < _flat.Rows.Count; i++)
                if (match(_flat.Rows[i].Node.Item))
                {
                    SelectedIndex = i;
                    ScrollIntoView(i);
                    return true;
                }
            return false;
        }

        protected override FrameworkElement CreateRow() => new ListRow();

        protected override void BindRow(FrameworkElement row, int index)
        {
            var d = _scratch;
            d.Reset();
            var fr = _flat.Rows[index];
            try
            {
                Presenter?.Present(fr.Node.Item, d);
            }
            catch (Exception ex)
            {
                Ui.Logger.Error("ui.tree.present_failed", "Tree presenter threw", ex, ("index", index));
                d.Reset();
                d.Primary = "?";
            }
            d.IsGroupHeader = false; // groups are selectable in a tree
            d.Indent = fr.Depth;
            d.Expanded = fr.Node.HasChildren ? fr.Node.IsExpanded : (bool?)null;
            if (fr.Node.HasChildren && string.IsNullOrEmpty(d.Right)) d.Right = PerfMath.FormatCount(fr.Node.LeafCount);
            ((ListRow)row).Bind(d, index, _flat.Rows.Count);
        }

        protected override bool IsSelectableCore(int index) => index >= 0 && index < _flat.Rows.Count;

        protected override void OnInvoke(int index, InvokeKind kind)
        {
            var node = _flat.Rows[index].Node;
            if (node.HasChildren && kind == InvokeKind.Open && !InvokeExpandableItems)
            {
                Toggle(index);
                return;
            }
            ItemInvoked?.Invoke(this, new ListInvokeEventArgs<T>(node.Item, index, kind));
        }

        protected override bool OnHorizontalKey(bool right)
        {
            int i = SelectedIndex;
            if (i < 0) return false;
            var fr = _flat.Rows[i];
            if (right)
            {
                if (fr.Node.HasChildren && !fr.Node.IsExpanded) Toggle(i);
                else if (fr.Node.HasChildren && i + 1 < _flat.Rows.Count) SelectedIndex = i + 1;
                return true;
            }
            if (fr.Node.HasChildren && fr.Node.IsExpanded) { Toggle(i); return true; }
            int p = _flat.ParentOf(i);
            if (p >= 0) SelectedIndex = p;
            return true;
        }

        protected override bool OnRowClick(FrameworkElement row, int index, Point posInRow)
        {
            if (row is ListRow lr && lr.IsOverChevron(posInRow))
            {
                Toggle(index);
                return true;
            }
            return false;
        }
    }
}
