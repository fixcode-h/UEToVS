using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.Link
{
    public enum AutomationNodeState
    {
        NotRun = 0,
        Queued = 1,
        Running = 2,
        Passed = 3,
        Failed = 4,
        Skipped = 5,
    }

    /// <summary>
    /// One node of the automation test tree: a path segment ("Project" / "Feature") or a test (leaf with
    /// <see cref="Test"/>). Spec children reported by the editor ("Spec.Describe it works") hang below their spec.
    /// </summary>
    public sealed class AutomationTestNode
    {
        private readonly List<AutomationTestNode> _children = new List<AutomationTestNode>();

        public AutomationTestNode(string segment, string fullPath, AutomationTestNode parent)
        {
            Segment = segment;
            FullPath = fullPath;
            Parent = parent;
        }

        public string Segment { get; }

        /// <summary>Dot path from the root ("Project.Feature.Case"): what "Automation RunTests" receives.</summary>
        public string FullPath { get; }

        public AutomationTestNode Parent { get; }
        public IReadOnlyList<AutomationTestNode> Children => _children;

        /// <summary>The declaration when this node is a test found in the source; null for a pure path segment.</summary>
        public AutomationTestInfo Test { get; set; }

        /// <summary>A test the editor reported that is not in the source scan (spec "It" blocks, engine tests).</summary>
        public bool ReportedOnly { get; set; }

        public AutomationNodeState State { get; set; }
        public double DurationMs { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Logs { get; } = new List<string>();

        public bool IsTest => Test != null || ReportedOnly;

        internal List<AutomationTestNode> MutableChildren => _children;

        /// <summary>Source location of this node or its nearest ancestor test (spec children jump to their spec).</summary>
        public SourceLocation SourceLocation
        {
            get
            {
                for (var n = this; n != null; n = n.Parent)
                    if (n.Test?.Location != null) return n.Test.Location;
                return null;
            }
        }

        public int CountTests()
        {
            int n = IsTest ? 1 : 0;
            foreach (var c in _children) n += c.CountTests();
            return n;
        }

        /// <summary>Worst state below (and at) this node: Failed &gt; Running &gt; Queued &gt; Passed &gt; Skipped &gt; NotRun.</summary>
        public AutomationNodeState AggregateState()
        {
            var s = State;
            foreach (var c in _children) s = Worse(s, c.AggregateState());
            return s;
        }

        internal static AutomationNodeState Worse(AutomationNodeState a, AutomationNodeState b) => Rank(a) >= Rank(b) ? a : b;

        private static int Rank(AutomationNodeState s)
        {
            switch (s)
            {
                case AutomationNodeState.Failed: return 5;
                case AutomationNodeState.Running: return 4;
                case AutomationNodeState.Queued: return 3;
                case AutomationNodeState.Passed: return 2;
                case AutomationNodeState.Skipped: return 1;
                default: return 0;
            }
        }

        public override string ToString() => FullPath + " [" + State + "]";
    }

    /// <summary>
    /// The automation test tree of the test runner window: built from the engine's source scan (test names split on
    /// '.'), then updated from the editor's <c>tests</c> events (queued / running / passed / failed, errors and log
    /// lines). Pure; the window renders it with UEToVS.UI's TreeList.
    /// </summary>
    public sealed class AutomationTestTree
    {
        private readonly Dictionary<string, AutomationTestNode> _byPath = new Dictionary<string, AutomationTestNode>(StringComparer.OrdinalIgnoreCase);
        private readonly List<AutomationTestNode> _roots = new List<AutomationTestNode>();

        public IReadOnlyList<AutomationTestNode> Roots => _roots;

        public int TestCount { get; private set; }

        /// <summary>Duplicate test names (two macros with the same path) seen while building.</summary>
        public int Duplicates { get; private set; }

        public static AutomationTestTree Build(IEnumerable<AutomationTestInfo> tests)
        {
            var tree = new AutomationTestTree();
            if (tests != null)
                foreach (var t in tests.Where(t => !string.IsNullOrWhiteSpace(t?.Name)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var node = tree.GetOrAdd(t.Name.Trim());
                    if (node.Test != null) tree.Duplicates++;
                    else
                    {
                        node.Test = t;
                        tree.TestCount++;
                    }
                }
            return tree;
        }

        public static IEnumerable<string> Split(string path) =>
            (path ?? string.Empty).Split('.').Select(p => p.Trim()).Where(p => p.Length > 0);

        public AutomationTestNode Find(string path) => path != null && _byPath.TryGetValue(path.Trim(), out var n) ? n : null;

        private AutomationTestNode GetOrAdd(string path)
        {
            if (_byPath.TryGetValue(path, out var existing)) return existing;
            AutomationTestNode parent = null;
            string prefix = null;
            foreach (var seg in Split(path))
            {
                prefix = prefix == null ? seg : prefix + "." + seg;
                if (!_byPath.TryGetValue(prefix, out var node))
                {
                    node = new AutomationTestNode(seg, prefix, parent);
                    _byPath[prefix] = node;
                    if (parent == null) _roots.Add(node);
                    else parent.MutableChildren.Add(node);
                }
                parent = node;
            }
            if (parent != null && !string.Equals(parent.FullPath, path, StringComparison.OrdinalIgnoreCase)) _byPath[path] = parent;
            return parent ?? new AutomationTestNode(path, path, null);
        }

        /// <summary>
        /// The node a reported test name belongs to: the exact path, else a new child under the longest known prefix
        /// that is a test (spec "It" blocks: "MySpec.Describe should work" under "MySpec"), else a new path.
        /// </summary>
        public AutomationTestNode ForReported(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            var exact = Find(name);
            if (exact != null) return exact;
            var node = GetOrAdd(name);
            node.ReportedOnly = node.Test == null;
            return node;
        }

        /// <summary>Marks every test at or below <paramref name="root"/> (null = all) as queued and clears old results.</summary>
        public int MarkQueued(AutomationTestNode root)
        {
            int n = 0;
            foreach (var node in root == null ? _roots.SelectMany(Walk) : Walk(root))
            {
                node.Errors.Clear();
                node.Warnings.Clear();
                node.Logs.Clear();
                node.DurationMs = 0;
                node.State = node.IsTest ? AutomationNodeState.Queued : AutomationNodeState.NotRun;
                if (node.IsTest) n++;
            }
            return n;
        }

        /// <summary>A run ended: whatever is still queued / running did not report (not matched, editor stopped).</summary>
        public int FinishRun()
        {
            int n = 0;
            foreach (var node in _roots.SelectMany(Walk))
                if (node.State == AutomationNodeState.Queued || node.State == AutomationNodeState.Running)
                {
                    node.State = node.State == AutomationNodeState.Running ? AutomationNodeState.Failed : AutomationNodeState.NotRun;
                    n++;
                }
            return n;
        }

        /// <summary>Applies a tests event; returns the node it changed (null when none).</summary>
        public AutomationTestNode Apply(TestEvent e)
        {
            if (e == null) return null;
            switch (e.Phase)
            {
                case TestEventPhase.TestStarted:
                {
                    var n = ForReported(e.Name);
                    if (n != null) n.State = AutomationNodeState.Running;
                    return n;
                }
                case TestEventPhase.TestFinished:
                {
                    var n = ForReported(e.Name);
                    if (n == null) return null;
                    n.State = e.Result == AutomationTestOutcome.Passed ? AutomationNodeState.Passed
                        : e.Result == AutomationTestOutcome.Skipped ? AutomationNodeState.Skipped
                        : AutomationNodeState.Failed;
                    n.DurationMs = e.DurationMs;
                    n.Errors.Clear();
                    n.Errors.AddRange(e.Errors);
                    n.Warnings.Clear();
                    n.Warnings.AddRange(e.Warnings);
                    n.Logs.Clear();
                    n.Logs.AddRange(e.Logs);
                    return n;
                }
                default:
                    return null;
            }
        }

        public static IEnumerable<AutomationTestNode> Walk(AutomationTestNode root)
        {
            var stack = new Stack<AutomationTestNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }

        /// <summary>Counts of the test leaves: (total, passed, failed, running/queued).</summary>
        public (int Total, int Passed, int Failed, int Pending) Summary()
        {
            int total = 0, passed = 0, failed = 0, pending = 0;
            foreach (var n in _roots.SelectMany(Walk))
            {
                if (!n.IsTest) continue;
                total++;
                if (n.State == AutomationNodeState.Passed) passed++;
                else if (n.State == AutomationNodeState.Failed) failed++;
                else if (n.State == AutomationNodeState.Queued || n.State == AutomationNodeState.Running) pending++;
            }
            return (total, passed, failed, pending);
        }

        /// <summary>Nodes whose path or file name contains every search word (and their ancestors), for the filter box.</summary>
        public HashSet<AutomationTestNode> Matching(string search)
        {
            var words = (search ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var keep = new HashSet<AutomationTestNode>();
            foreach (var n in _roots.SelectMany(Walk))
            {
                bool all = true;
                foreach (var w in words)
                    if (n.FullPath.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (n.Test?.ClassName == null || n.Test.ClassName.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        all = false;
                        break;
                    }
                if (!all) continue;
                for (var p = n; p != null && keep.Add(p); p = p.Parent) { }
                foreach (var d in Walk(n)) keep.Add(d);
            }
            return keep;
        }
    }
}
