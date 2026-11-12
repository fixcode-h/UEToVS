using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>
    /// A replacement of [<see cref="Start"/>, <see cref="End"/>) in one text whose new content is made of pieces: literal
    /// text or ranges of the ORIGINAL text. Pieces that copy original text keep the edits nested in them (a call inside the
    /// arguments of another call, a renamed parameter inside a reordered argument), so overlapping refactoring edits compose
    /// instead of clobbering each other (v0.4.5, Change Signature).
    /// </summary>
    public sealed class StructuredEdit
    {
        public StructuredEdit(int start, int end)
        {
            Start = start;
            End = end;
        }

        public int Start { get; }
        public int End { get; }

        /// <summary>Owner of the edit (a <see cref="SignatureSite"/>, …); free for the caller.</summary>
        public object Tag { get; set; }

        public List<EditPiece> Pieces { get; } = new List<EditPiece>();

        public StructuredEdit Literal(string text)
        {
            Pieces.Add(EditPiece.Text(text));
            return this;
        }

        public StructuredEdit Copy(int start, int end)
        {
            Pieces.Add(EditPiece.Range(start, end));
            return this;
        }

        /// <summary>A plain replacement by <paramref name="text"/>.</summary>
        public static StructuredEdit Replace(int start, int end, string text) => new StructuredEdit(start, end).Literal(text);

        public override string ToString() => $"[{Start},{End}) {Pieces.Count} pieces";
    }

    public readonly struct EditPiece
    {
        private EditPiece(string text, int start, int end)
        {
            LiteralText = text;
            Start = start;
            End = end;
        }

        /// <summary>Literal text, or null for a copy of the original [Start, End).</summary>
        public string LiteralText { get; }

        public int Start { get; }
        public int End { get; }
        public bool IsCopy => LiteralText == null;

        public static EditPiece Text(string text) => new EditPiece(text ?? string.Empty, 0, 0);

        public static EditPiece Range(int start, int end) => new EditPiece(null, start, end);
    }

    /// <summary>Renders <see cref="StructuredEdit"/>s of one text into non-overlapping <see cref="TextEdit"/>s (pure).</summary>
    public static class StructuredEditComposer
    {
        /// <summary>
        /// Top-level edits become <see cref="TextEdit"/>s (with OldText = the original text, so a changed file is detected);
        /// an edit inside a copied piece of another is applied inside that piece. Edits that partly overlap an earlier one, or
        /// that sit inside a dropped (non-copied) part of another edit, are returned in <paramref name="dropped"/>.
        /// </summary>
        public static List<TextEdit> Compose(string filePath, string text, IEnumerable<StructuredEdit> edits, out List<StructuredEdit> dropped)
        {
            dropped = new List<StructuredEdit>();
            text = text ?? string.Empty;
            var list = edits.Where(e => e != null && e.Start >= 0 && e.End <= text.Length && e.Start <= e.End)
                            .OrderBy(e => e.Start).ThenByDescending(e => e.End).ToList();
            // containment forest
            var roots = new List<Node>();
            var stack = new List<Node>();
            foreach (var e in list)
            {
                var node = new Node(e);
                while (stack.Count > 0 && !Contains(stack[stack.Count - 1].Edit, e)) stack.RemoveAt(stack.Count - 1);
                if (stack.Count > 0)
                {
                    var parent = stack[stack.Count - 1];
                    if (Overlaps(parent.Children.LastOrDefault()?.Edit, e) || !InsideCopiedPiece(parent.Edit, e))
                    {
                        dropped.Add(e);
                        continue;
                    }
                    parent.Children.Add(node);
                }
                else
                {
                    if (roots.Count > 0 && Overlaps(roots[roots.Count - 1].Edit, e))
                    {
                        dropped.Add(e);
                        continue;
                    }
                    roots.Add(node);
                }
                stack.Add(node);
            }
            var starts = TextEditApplier.LineStarts(text);
            var result = new List<TextEdit>();
            foreach (var r in roots)
            {
                int line = LineOf(starts, r.Edit.Start);
                result.Add(new TextEdit
                {
                    FilePath = filePath, Line = line, Column = r.Edit.Start - starts[line], Length = r.Edit.End - r.Edit.Start,
                    OldText = text.Substring(r.Edit.Start, r.Edit.End - r.Edit.Start), NewText = Render(text, r),
                });
            }
            return result;
        }

        /// <summary>Applies the edits to the text directly (tests, previews).</summary>
        public static string Apply(string text, IEnumerable<StructuredEdit> edits)
        {
            var composed = Compose(null, text, edits, out _);
            return TextEditApplier.Apply(text, composed, out _);
        }

        private sealed class Node
        {
            public Node(StructuredEdit e) => Edit = e;
            public StructuredEdit Edit { get; }
            public List<Node> Children { get; } = new List<Node>();
        }

        private static bool Contains(StructuredEdit outer, StructuredEdit inner) =>
            inner.Start >= outer.Start && inner.End <= outer.End && !(inner.Start == inner.End && (inner.Start == outer.Start || inner.Start == outer.End));

        private static bool Overlaps(StructuredEdit a, StructuredEdit b) => a != null && b.Start < a.End && a.Start < b.End;

        private static bool InsideCopiedPiece(StructuredEdit parent, StructuredEdit e) =>
            parent.Pieces.Any(p => p.IsCopy && e.Start >= p.Start && e.End <= p.End);

        private static string Render(string text, Node node)
        {
            var sb = new StringBuilder();
            foreach (var p in node.Edit.Pieces)
            {
                if (!p.IsCopy)
                {
                    sb.Append(p.LiteralText);
                    continue;
                }
                int pos = p.Start;
                foreach (var c in node.Children.Where(c => c.Edit.Start >= p.Start && c.Edit.End <= p.End).OrderBy(c => c.Edit.Start))
                {
                    if (c.Edit.Start < pos) continue;
                    sb.Append(text, pos, c.Edit.Start - pos);
                    sb.Append(Render(text, c));
                    pos = c.Edit.End;
                }
                sb.Append(text, pos, p.End - pos);
            }
            return sb.ToString();
        }

        internal static int LineOf(int[] starts, int offset)
        {
            int lo = 0, hi = starts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (starts[mid] <= offset) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }
    }
}
