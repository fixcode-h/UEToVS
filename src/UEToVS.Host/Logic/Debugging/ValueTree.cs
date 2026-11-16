using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Debugging
{
    /// <summary>A debugger value (local, argument, member, expression result). In VS it wraps EnvDTE.Expression.</summary>
    public interface IValueNode
    {
        string Name { get; }
        string Type { get; }
        string Value { get; }
        bool IsValid { get; }

        /// <summary>Members as the debugger shows them (natvis applies: FString, TArray, UObject* read like in the Watch window).</summary>
        IEnumerable<IValueNode> Children();
    }

    /// <summary>
    /// Writes values as compact JSON within a character budget so a locals dump of a big UObject never floods the agent:
    /// <c>{"n":"Health","t":"float","v":"100.0","c":[…]}</c>, children only down to <c>depth</c>, at most
    /// <c>maxChildren</c> per node, long values cut. When the budget runs out the rest is dropped and the result says so.
    /// </summary>
    public sealed class ValueTreeWriter
    {
        public const int MaxValueChars = 400;

        private readonly int _maxChildren;
        private int _budget;

        public ValueTreeWriter(int budgetChars, int maxChildren = 25)
        {
            _budget = budgetChars;
            _maxChildren = maxChildren;
        }

        /// <summary>True once something was dropped (budget or child limit).</summary>
        public bool Truncated { get; private set; }

        public int Remaining => _budget;

        /// <summary>Top-level nodes; <paramref name="filter"/> keeps names containing it (case-insensitive), children are not filtered.</summary>
        public JArray WriteAll(IEnumerable<IValueNode> nodes, int depth, string filter = null)
        {
            var arr = new JArray();
            if (nodes == null) return arr;
            foreach (var n in nodes)
            {
                if (n == null) continue;
                if (!string.IsNullOrEmpty(filter) && (n.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (_budget <= 0) { Truncated = true; break; }
                arr.Add(Write(n, depth));
            }
            return arr;
        }

        public JObject Write(IValueNode n, int depth)
        {
            var o = new JObject { ["n"] = n.Name ?? "" };
            string type = n.Type ?? "";
            string value = Cut(n.Value ?? "");
            if (type.Length > 0) o["t"] = Cut(type);
            o["v"] = value;
            if (!n.IsValid) o["err"] = true;
            _budget -= 16 + (n.Name?.Length ?? 0) + Math.Min(type.Length, MaxValueChars) + value.Length;
            if (depth <= 0 || !n.IsValid || _budget <= 0) return o;

            var kids = new JArray();
            int count = 0, skipped = 0;
            IEnumerable<IValueNode> children;
            try { children = n.Children(); }
            catch (Exception) { return o; } // the debugger refuses to expand (e.g. invalid pointer): show the value only
            if (children == null) return o;
            foreach (var c in children)
            {
                if (c == null) continue;
                if (count >= _maxChildren || _budget <= 0) { skipped++; continue; }
                kids.Add(Write(c, depth - 1));
                count++;
            }
            if (kids.Count > 0) o["c"] = kids;
            if (skipped > 0)
            {
                o["more"] = skipped;
                Truncated = true;
            }
            return o;
        }

        private static string Cut(string s) => s.Length <= MaxValueChars ? s : s.Substring(0, MaxValueChars) + "…";
    }
}
