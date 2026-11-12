using System;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// VA's '.' ↔ '-&gt;' correction (§7.6 自动纠正): the engine reports, with a member completion, that the accessor the user
    /// typed does not fit the left-hand type (pointer, TObjectPtr … → '-&gt;'). On commit the host replaces it. Before
    /// editing, the fix is validated against the current line text so a stale or bogus fix never corrupts code. Pure.
    /// </summary>
    public static class AccessorFixes
    {
        public static bool IsAccessor(string s) => s == "." || s == "->";

        /// <summary>
        /// Is <paramref name="fix"/> applicable to <paramref name="lineText"/> (the text of line <c>fix.Line</c>)? The
        /// replaced text must be exactly '.' or '-&gt;', the replacement the other one.
        /// </summary>
        public static bool IsValid(AccessorFix fix, string lineText)
        {
            if (fix == null || lineText == null) return false;
            if (fix.Column < 0 || fix.Length <= 0 || fix.Column + fix.Length > lineText.Length) return false;
            string current = lineText.Substring(fix.Column, fix.Length);
            return IsAccessor(current) && IsAccessor(fix.Replacement) && current != fix.Replacement;
        }

        /// <summary>Applies a valid fix to the line text; false (and the line unchanged) otherwise.</summary>
        public static bool TryApply(AccessorFix fix, string lineText, out string newLine)
        {
            newLine = lineText;
            if (!IsValid(fix, lineText)) return false;
            newLine = lineText.Substring(0, fix.Column) + fix.Replacement + lineText.Substring(fix.Column + fix.Length);
            return true;
        }

        /// <summary>Columns at or after the fix shift by this much once it is applied.</summary>
        public static int Delta(AccessorFix fix) => fix == null ? 0 : (fix.Replacement?.Length ?? 0) - fix.Length;

        /// <summary>
        /// Where the fix lands relative to the session's replaced word: it must be on the same line and end at or before the
        /// word start (the accessor directly precedes the member name, maybe with spaces).
        /// </summary>
        public static bool PrecedesWord(AccessorFix fix, int wordLine, int wordStartColumn) =>
            fix != null && fix.Line == wordLine && fix.Column + fix.Length <= wordStartColumn;
    }
}
