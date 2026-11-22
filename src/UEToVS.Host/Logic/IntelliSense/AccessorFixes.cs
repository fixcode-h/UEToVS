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

        /// <summary>
        /// v0.9.12 输入即纠正：是否在打出 '.' / '-&gt;' 的那一刻就改（而不是等提交）。要求：设置开着；引擎有把握
        /// （<see cref="AccessorFix.Confident"/>，纠正后的类型确实列出了成员）；这次补全正是由这个访问符触发的
        /// （'.' 改成 '-&gt;'，或 '&gt;' 结尾的 '-&gt;' 改成 '.'）；访问符就在光标前（之间没有已输入的成员名）。
        /// </summary>
        public static bool ShouldApplyOnType(AccessorFix fix, char triggerChar, int caretLine, int caretColumn, bool enabled)
        {
            if (!enabled || fix == null || !fix.Confident || !IsAccessor(fix.Replacement)) return false;
            if (fix.Line != caretLine || fix.Column + fix.Length != caretColumn) return false;
            if (triggerChar == '.') return fix.Length == 1 && fix.Replacement == "->";
            if (triggerChar == '>') return fix.Length == 2 && fix.Replacement == ".";
            return false;
        }

        /// <summary>
        /// v0.10.1：输入即纠正把 '.' 换成 '-&gt;' 时，VS 会把会话的适用范围（空的、贴着访问符）一起撑大，范围文字变成
        /// "-&gt;"：过滤串成了 "-&gt;"、没有默认选中项，提交时还把 "-&gt;" 一起替换掉（"PlayerInputComponentBindKey"）。
        /// 成员补全的范围开头若是访问符，返回要跳过的字符数（"-&gt;" 2、"." 1），否则 0。
        /// </summary>
        public static int LeadingAccessorLength(string spanText)
        {
            if (string.IsNullOrEmpty(spanText)) return 0;
            if (spanText.StartsWith("->", StringComparison.Ordinal)) return 2;
            return spanText[0] == '.' ? 1 : 0;
        }

        /// <summary>
        /// v0.10.1 一次退格删掉纠正出来的 "-&gt;"：用户只打了一个 '.'，退格也只该按一次。要求光标紧跟在纠正出来的
        /// 访问符后面（<paramref name="fixEnd"/>），那里仍是 "-&gt;"，且没有选中文字。
        /// </summary>
        public static bool BackspaceRemovesFix(string fixText, int fixEnd, int caret, bool hasSelection) =>
            !hasSelection && fixText == "->" && caret == fixEnd;
    }
}
