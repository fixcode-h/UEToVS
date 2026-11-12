using System;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>
    /// Builds <see cref="CaretRequest"/> from editor facts without touching VS types (the host passes snapshot numbers).
    /// Line/column are 0-based, the column counts UTF-16 code units (= ITextSnapshot positions). The full buffer text is
    /// sent only for dirty documents (otherwise the engine reads the file on disk).
    /// </summary>
    public static class CaretRequestBuilder
    {
        /// <summary>Largest buffer sent inline (UTF-16 chars). Bigger dirty buffers are sent as null (engine reads disk).</summary>
        public const int MaxInlineChars = 16 * 1024 * 1024;

        /// <param name="filePath">Full path of the document; null/empty → null request.</param>
        /// <param name="lineNumber">0-based line of the caret (ITextSnapshotLine.LineNumber).</param>
        /// <param name="lineStart">Snapshot position where that line starts.</param>
        /// <param name="lineLength">Length of the line without its line break.</param>
        /// <param name="position">Caret snapshot position.</param>
        /// <param name="isDirty">Document has unsaved changes.</param>
        /// <param name="getText">Lazily returns the whole buffer (only called when dirty).</param>
        /// <param name="textLength">Snapshot length, to skip huge buffers without materializing them.</param>
        /// <param name="version">ITextSnapshot.Version.VersionNumber.</param>
        public static CaretRequest Build(string filePath, int lineNumber, int lineStart, int lineLength, int position,
                                         bool isDirty, Func<string> getText, int textLength, long version)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            int column = position - lineStart;
            if (column < 0) column = 0;
            if (lineLength >= 0 && column > lineLength) column = lineLength;
            string text = null;
            if (isDirty && getText != null && textLength <= MaxInlineChars) text = getText();
            return new CaretRequest
            {
                FilePath = filePath,
                Line = Math.Max(0, lineNumber),
                Column = column,
                Text = text,
                TextVersion = version,
            };
        }

        /// <summary>Same caret (file, line, column, buffer version): the previous answer is still valid.</summary>
        public static bool SameCaret(CaretRequest a, CaretRequest b) =>
            a != null && b != null && a.Line == b.Line && a.Column == b.Column && a.TextVersion == b.TextVersion &&
            string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);

        /// <summary>Copy with Text stripped, for logs (never log buffer contents).</summary>
        public static System.Collections.Generic.Dictionary<string, object> Describe(CaretRequest r) =>
            r == null ? null : new System.Collections.Generic.Dictionary<string, object>
            {
                ["file"] = r.FilePath, ["line"] = r.Line, ["col"] = r.Column, ["version"] = r.TextVersion,
                ["dirty"] = r.Text != null, ["textChars"] = r.Text?.Length ?? 0,
            };
    }
}
