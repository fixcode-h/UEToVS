using System;
using System.Collections.Generic;

namespace UEToVS.UI.Logic
{
    /// <summary>A node of a lazily flattened tree (TreeList / ResultsPanel). Children may be null for leaves.</summary>
    public sealed class TreeNode<T>
    {
        public TreeNode(T item, IList<TreeNode<T>> children = null, bool isExpanded = true)
        {
            Item = item;
            Children = children;
            IsExpanded = isExpanded;
        }

        public T Item { get; }
        public IList<TreeNode<T>> Children { get; set; }
        public bool IsExpanded { get; set; }
        public bool HasChildren => Children != null && Children.Count > 0;

        /// <summary>Number of leaf descendants (cached lazily; call <see cref="InvalidateCount"/> after mutating Children).</summary>
        public int LeafCount
        {
            get
            {
                if (_leafCount >= 0) return _leafCount;
                if (!HasChildren) return _leafCount = 0;
                int n = 0;
                foreach (var c in Children) n += c.HasChildren ? c.LeafCount : 1;
                return _leafCount = n;
            }
        }

        private int _leafCount = -1;

        public void InvalidateCount() => _leafCount = -1;
    }

    /// <summary>A visible row of a flattened tree.</summary>
    public readonly struct FlatRow<T>
    {
        public readonly TreeNode<T> Node;
        public readonly int Depth;

        public FlatRow(TreeNode<T> node, int depth)
        {
            Node = node;
            Depth = depth;
        }
    }

    /// <summary>Flattens expanded nodes into a list of rows (depth-first, pre-order) and supports expand/collapse in place.</summary>
    public sealed class TreeFlattener<T>
    {
        private readonly List<FlatRow<T>> _rows = new List<FlatRow<T>>();

        public IReadOnlyList<FlatRow<T>> Rows => _rows;

        public void SetRoots(IEnumerable<TreeNode<T>> roots)
        {
            _rows.Clear();
            if (roots == null) return;
            foreach (var r in roots) Append(_rows, r, 0);
        }

        private static void Append(List<FlatRow<T>> list, TreeNode<T> node, int depth)
        {
            if (node == null) return;
            // Iterative to survive deep trees.
            var stack = new Stack<(TreeNode<T> Node, int Depth)>();
            stack.Push((node, depth));
            while (stack.Count > 0)
            {
                var (n, d) = stack.Pop();
                list.Add(new FlatRow<T>(n, d));
                if (n.IsExpanded && n.HasChildren)
                    for (int i = n.Children.Count - 1; i >= 0; i--)
                        if (n.Children[i] != null) stack.Push((n.Children[i], d + 1));
            }
        }

        /// <summary>Expands or collapses the node at <paramref name="rowIndex"/>. Returns false if nothing changed.</summary>
        public bool SetExpanded(int rowIndex, bool expanded)
        {
            if (rowIndex < 0 || rowIndex >= _rows.Count) return false;
            var row = _rows[rowIndex];
            var node = row.Node;
            if (!node.HasChildren || node.IsExpanded == expanded) return false;
            if (expanded)
            {
                node.IsExpanded = true;
                var inserted = new List<FlatRow<T>>();
                foreach (var c in node.Children) Append(inserted, c, row.Depth + 1);
                _rows.InsertRange(rowIndex + 1, inserted);
            }
            else
            {
                int end = rowIndex + 1;
                while (end < _rows.Count && _rows[end].Depth > row.Depth) end++;
                _rows.RemoveRange(rowIndex + 1, end - rowIndex - 1);
                node.IsExpanded = false;
            }
            return true;
        }

        /// <summary>Index of the parent row of <paramref name="rowIndex"/>, or -1 for a root.</summary>
        public int ParentOf(int rowIndex)
        {
            if (rowIndex <= 0 || rowIndex >= _rows.Count) return -1;
            int d = _rows[rowIndex].Depth;
            for (int i = rowIndex - 1; i >= 0; i--)
                if (_rows[i].Depth < d) return i;
            return -1;
        }

        public void SetAllExpanded(IEnumerable<TreeNode<T>> roots, bool expanded)
        {
            if (roots == null) return;
            var stack = new Stack<TreeNode<T>>(roots);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n == null || !n.HasChildren) continue;
                n.IsExpanded = expanded;
                foreach (var c in n.Children) stack.Push(c);
            }
            SetRoots(roots);
        }
    }
}
