using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;
using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Navigation
{
    public enum CallNodeKind
    {
        /// <summary>The function the hierarchy is about (top row).</summary>
        Root = 0,
        /// <summary>A caller / callee function (expandable when the engine resolved it).</summary>
        Function = 1,
        /// <summary>One call site of the parent function row (listed when it has several).</summary>
        Site = 2,
        /// <summary>Placeholder child of a level not loaded yet ("Loading…").</summary>
        Loading = 3,
        /// <summary>The level was loaded and is empty / failed / cut: a message row.</summary>
        Message = 4,
    }

    /// <summary>
    /// One row of the call hierarchy popup. The tree is built lazily: a function row gets a <see cref="CallNodeKind.Loading"/>
    /// child until it is expanded, then the engine is asked for that one level (<see cref="CallHierarchyModel.ApplyLevel"/>).
    /// Pure (no WPF); the host maps nodes to UEToVS.UI tree rows.
    /// </summary>
    public sealed class CallNode
    {
        public CallNodeKind Kind { get; set; }
        public CallNode Parent { get; set; }
        public List<CallNode> Children { get; } = new List<CallNode>();

        /// <summary>Root: the function. Function rows: Item.Function (null for file scope / unresolved callees).</summary>
        public SymbolInfo Function { get; set; }
        public CallHierarchyItem Item { get; set; }
        public CallSite Site { get; set; }
        public string Message { get; set; }

        public bool IsExpanded { get; set; }

        /// <summary>The level below this node was asked for (or cannot be: leaves, recursion).</summary>
        public bool Loaded { get; set; }

        /// <summary>This function already appears on the path from the root (a recursive call): not expanded again.</summary>
        public bool IsRecursive { get; set; }

        public bool CanLoad => (Kind == CallNodeKind.Function || Kind == CallNodeKind.Root) && !Loaded && !IsRecursive && Function != null;

        public int Depth
        {
            get
            {
                int d = 0;
                for (var p = Parent; p != null; p = p.Parent) d++;
                return d;
            }
        }
    }

    /// <summary>Builds and filters the lazily loaded call hierarchy tree; maps rows to texts and jump targets.</summary>
    public static class CallHierarchyModel
    {
        /// <summary>The root row with the first level (from the first engine answer) below it.</summary>
        public static CallNode CreateRoot(CallHierarchyResponse resp)
        {
            var root = new CallNode { Kind = CallNodeKind.Root, Function = resp?.Root, IsExpanded = true };
            ApplyLevel(root, resp);
            return root;
        }

        /// <summary>Puts the level answered by the engine under <paramref name="node"/> (replacing its "Loading…" child).</summary>
        public static void ApplyLevel(CallNode node, CallHierarchyResponse resp)
        {
            node.Loaded = true;
            node.Children.RemoveAll(c => c.Kind == CallNodeKind.Loading || c.Kind == CallNodeKind.Message);
            var items = resp?.Items ?? new List<CallHierarchyItem>();
            if (items.Count == 0)
            {
                string msg = resp?.Reason == "no-body" ? NavGapStrings.NoBody
                    : resp?.Direction == CallHierarchyDirection.Callees ? NavGapStrings.NoCallees : NavGapStrings.NoCallers;
                node.Children.Add(new CallNode { Kind = CallNodeKind.Message, Parent = node, Message = msg, Loaded = true });
                return;
            }
            foreach (var item in items) node.Children.Add(ItemNode(node, item));
            if (resp.Truncated) node.Children.Add(new CallNode { Kind = CallNodeKind.Message, Parent = node, Message = NavGapStrings.Truncated, Loaded = true });
        }

        /// <summary>The level could not be loaded: a message row instead of "Loading…" (expanding again retries).</summary>
        public static void FailLevel(CallNode node, string message)
        {
            node.Loaded = false;
            node.Children.RemoveAll(c => c.Kind == CallNodeKind.Loading || c.Kind == CallNodeKind.Message);
            node.Children.Add(new CallNode { Kind = CallNodeKind.Message, Parent = node, Message = message ?? NavGapStrings.LevelFailed, Loaded = true });
        }

        private static CallNode ItemNode(CallNode parent, CallHierarchyItem item)
        {
            var n = new CallNode { Kind = CallNodeKind.Function, Parent = parent, Item = item, Function = item.Function };
            if (item.Sites != null && item.Sites.Count > 1)
                foreach (var s in item.Sites) n.Children.Add(new CallNode { Kind = CallNodeKind.Site, Parent = n, Site = s, Loaded = true });
            n.IsRecursive = item.Function != null && OnPath(parent, item.Function);
            if (!item.CanExpand || item.Function == null || n.IsRecursive) n.Loaded = true;
            else n.Children.Add(new CallNode { Kind = CallNodeKind.Loading, Parent = n, Loaded = true });
            return n;
        }

        /// <summary>Same function as one of <paramref name="node"/> and its ancestors (UE _Implementation / _Validate count as the UFUNCTION).</summary>
        public static bool OnPath(CallNode node, SymbolInfo f)
        {
            string key = FunctionKey(f);
            for (var p = node; p != null; p = p.Parent)
                if (p.Function != null && FunctionKey(p.Function) == key) return true;
            return false;
        }

        public static string FunctionKey(SymbolInfo f)
        {
            if (f == null) return string.Empty;
            string name = f.Name ?? string.Empty;
            foreach (var suffix in new[] { "_Implementation", "_Validate" })
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)) name = name.Substring(0, name.Length - suffix.Length);
            return (f.Container ?? string.Empty) + "::" + name;
        }

        // ------------------------------------------------------------------ filter

        /// <summary>Rows to show: every row when the filter is empty; otherwise rows matching all words, with their ancestors and loaded subtrees.</summary>
        public static bool IsVisible(CallNode n, string filter, bool showUncertain)
        {
            if (n.Kind == CallNodeKind.Root) return true;
            if (!showUncertain && IsUncertain(n)) return false;
            if (string.IsNullOrWhiteSpace(filter)) return true;
            for (var p = n; p != null && p.Kind != CallNodeKind.Root; p = p.Parent)
                if (Matches(p, filter)) return true;
            return HasMatchingDescendant(n, filter, showUncertain);
        }

        private static bool HasMatchingDescendant(CallNode n, string filter, bool showUncertain)
        {
            foreach (var c in n.Children)
            {
                if (!showUncertain && IsUncertain(c)) continue;
                if (Matches(c, filter) || HasMatchingDescendant(c, filter, showUncertain)) return true;
            }
            return false;
        }

        /// <summary>Only name-matched (the engine could not tell whether it is this function).</summary>
        public static bool IsUncertain(CallNode n) =>
            n.Kind == CallNodeKind.Function && n.Item != null && n.Item.Confidence <= ResolutionConfidence.NameOnly && n.Item.Confidence != ResolutionConfidence.None ||
            n.Kind == CallNodeKind.Site && n.Site != null && n.Site.Confidence <= ResolutionConfidence.NameOnly;

        /// <summary>All words of the filter occur (case-insensitive) in the row's name, file or source line.</summary>
        public static bool Matches(CallNode n, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            string hay = SearchText(n);
            if (hay.Length == 0) return false;
            foreach (var word in filter.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (hay.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        private static string SearchText(CallNode n)
        {
            switch (n.Kind)
            {
                case CallNodeKind.Function:
                case CallNodeKind.Root:
                    string file = n.Item?.Sites?.FirstOrDefault()?.Location?.FilePath ?? n.Function?.Location?.FilePath;
                    return Title(n) + " " + SymbolPresentation.SafeFileName(file ?? string.Empty);
                case CallNodeKind.Site:
                    return (n.Site?.LineText ?? string.Empty) + " " + SymbolPresentation.SafeFileName(n.Site?.Location?.FilePath ?? string.Empty);
                default:
                    return string.Empty;
            }
        }

        // ------------------------------------------------------------------ presentation

        public static string Title(CallNode n)
        {
            switch (n.Kind)
            {
                case CallNodeKind.Root:
                case CallNodeKind.Function:
                    if (n.Function != null) return SymbolPresentation.QualifiedName(n.Function);
                    return n.Item?.DisplayName ?? "?";
                case CallNodeKind.Site:
                    return n.Site?.LineText ?? string.Empty;
                case CallNodeKind.Loading:
                    return NavGapStrings.Loading;
                default:
                    return n.Message ?? string.Empty;
            }
        }

        /// <summary>Second column: "file:line" of the first site (or of the function), the binding method, "recursive".</summary>
        public static string Detail(CallNode n, CallHierarchyDirection direction)
        {
            if (n.Kind == CallNodeKind.Site)
                return n.Site?.Kind == CallSiteKind.DelegateBinding ? NavGapStrings.BindingVia(n.Site.BindMethod) : null;
            if (n.Kind != CallNodeKind.Function) return null;
            var parts = new List<string>(3);
            var binding = n.Item?.Sites?.FirstOrDefault(s => s.Kind == CallSiteKind.DelegateBinding);
            if (binding != null) parts.Add(NavGapStrings.BindingVia(binding.BindMethod) + (string.IsNullOrEmpty(binding.Delegate) ? string.Empty : " " + binding.Delegate));
            if (n.Function == null && n.Item?.Confidence <= ResolutionConfidence.NameOnly && direction == CallHierarchyDirection.Callees) parts.Add(NavGapStrings.Unresolved);
            if (n.Function == null && direction == CallHierarchyDirection.Callers) parts.Add(NavGapStrings.FileScope);
            if (n.IsRecursive) parts.Add(NavGapStrings.Recursive);
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        /// <summary>Right column: where Enter goes ("Foo.cpp:42") and the number of sites when there are several.</summary>
        public static string Right(CallNode n, CallHierarchyDirection direction)
        {
            var target = TargetOf(n, direction);
            string where = target == null ? null : SymbolPresentation.ShortLocation(target);
            if (n.Kind == CallNodeKind.Function && n.Item?.Sites?.Count > 1) return NavGapStrings.Sites(n.Item.Sites.Count) + (where == null ? string.Empty : "  " + where);
            return where;
        }

        /// <summary>
        /// Where Enter (and the preview) goes: a site row → the site; a caller → its (first) call site; a callee → the callee
        /// itself (its declaration), or the call when it is unresolved; the root → the function.
        /// </summary>
        public static SourceLocation TargetOf(CallNode n, CallHierarchyDirection direction)
        {
            switch (n?.Kind)
            {
                case CallNodeKind.Root:
                    return n.Function?.Location;
                case CallNodeKind.Site:
                    return n.Site?.Location;
                case CallNodeKind.Function:
                    var first = n.Item?.Sites?.FirstOrDefault()?.Location;
                    if (direction == CallHierarchyDirection.Callees) return n.Function?.Location ?? first;
                    return first ?? n.Function?.Location;
                default:
                    return null;
            }
        }

        /// <summary>Length of the highlighted token at <see cref="TargetOf"/> (sites: the call; functions: their name).</summary>
        public static int TargetLength(CallNode n, CallHierarchyDirection direction)
        {
            if (n?.Kind == CallNodeKind.Site) return n.Site?.Length ?? 0;
            if (n?.Kind == CallNodeKind.Function && (direction == CallHierarchyDirection.Callers || n.Function == null)) return n.Item?.Sites?.FirstOrDefault()?.Length ?? 0;
            return n?.Function?.Name?.Length ?? 0;
        }

        public static string Summary(CallHierarchyResponse r)
        {
            if (r == null) return string.Empty;
            int sites = r.Items.Sum(i => i.Sites?.Count ?? 0);
            int bindings = r.Items.Sum(i => i.Sites?.Count(s => s.Kind == CallSiteKind.DelegateBinding) ?? 0);
            return NavGapStrings.Summary(r.Items.Count, sites, bindings, r.ElapsedMs, r.Truncated);
        }

        /// <summary>Every node of the tree, depth first (tests, logging).</summary>
        public static IEnumerable<CallNode> Walk(CallNode root)
        {
            var stack = new Stack<CallNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }
    }
}
