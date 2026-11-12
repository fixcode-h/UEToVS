using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UEToVS.Host.Logic.Refactoring
{
    /// <summary>
    /// One text change: replace <see cref="Length"/> characters at 0-based (<see cref="Line"/>, <see cref="Column"/>) with
    /// <see cref="NewText"/>. <see cref="OldText"/> (when set) must be what is there, otherwise the edit is skipped (the file
    /// changed since the plan was made). A line equal to the line count inserts at the end of the text.
    /// </summary>
    public sealed class TextEdit
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
        public string OldText { get; set; }
        public string NewText { get; set; }

        public static TextEdit Insert(string file, int line, string text) => new TextEdit { FilePath = file, Line = line, Column = 0, Length = 0, NewText = text };

        public override string ToString() => $"{System.IO.Path.GetFileName(FilePath)}:{Line + 1}:{Column + 1} -{Length} +\"{NewText}\"";
    }

    /// <summary>Where a generated block goes: inserted before <see cref="Line"/> (column 0).</summary>
    public sealed class InsertPoint
    {
        public int Line { get; set; }

        /// <summary>Text to insert (complete lines, ending with a newline).</summary>
        public string Text { get; set; }

        /// <summary>Why this place was chosen (logs): "after:Name", "class-end", "section:public", "end-of-file"…</summary>
        public string Reason { get; set; }
    }

    /// <summary>Applies line/column edits to a string (disk files and tests; open documents are edited through the editor buffer).</summary>
    public static class TextEditApplier
    {
        /// <summary>Offset of each line start in <paramref name="text"/> (works for \n and \r\n).</summary>
        public static int[] LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        /// <summary>Offset of (line, column), or -1 when outside the text. line == line count → end of text.</summary>
        public static int OffsetOf(string text, int[] starts, int line, int column)
        {
            if (line == starts.Length && column == 0) return text.Length;
            if (line < 0 || line >= starts.Length) return -1;
            int lineEnd = line + 1 < starts.Length ? starts[line + 1] : text.Length;
            int off = starts[line] + column;
            return column < 0 || off > lineEnd ? -1 : off;
        }

        /// <summary>
        /// Applies the edits (any order; they must not overlap). Edits whose <see cref="TextEdit.OldText"/> does not match, or
        /// that fall outside the text, are skipped and counted in <paramref name="skipped"/>.
        /// </summary>
        public static string Apply(string text, IEnumerable<TextEdit> edits, out int skipped)
        {
            text = text ?? string.Empty;
            skipped = 0;
            var starts = LineStarts(text);
            var resolved = new List<(int Offset, TextEdit Edit)>();
            foreach (var e in edits)
            {
                int off = OffsetOf(text, starts, e.Line, e.Column);
                if (off < 0 || off + e.Length > text.Length || (e.OldText != null && !string.Equals(text.Substring(off, e.Length), e.OldText, StringComparison.Ordinal)))
                {
                    skipped++;
                    continue;
                }
                resolved.Add((off, e));
            }
            var sb = new StringBuilder(text);
            foreach (var (off, e) in resolved.OrderByDescending(r => r.Offset).ThenByDescending(r => r.Edit.Length))
            {
                string insert = e.NewText ?? string.Empty;
                // inserting at the very end of a text without a final newline: start a new line first
                if (off == text.Length && e.Column == 0 && e.Length == 0 && text.Length > 0 && text[text.Length - 1] != '\n' && insert.Length > 0)
                    insert = (text.Contains("\r\n") ? "\r\n" : "\n") + insert.TrimEnd('\r', '\n');
                sb.Remove(off, e.Length).Insert(off, insert);
            }
            return sb.ToString();
        }
    }
}
