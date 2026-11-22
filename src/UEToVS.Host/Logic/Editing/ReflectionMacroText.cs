using System;
using UEToVS.Host.Logic.Generate;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>源码里的一个反射宏调用：<c>UPROPERTY(EditAnywhere, meta=(ClampMin="0"))</c>。偏移量都是原文里的字符偏移。</summary>
    public sealed class ReflectionMacroCall
    {
        /// <summary>UPROPERTY / UFUNCTION / UCLASS / USTRUCT。</summary>
        public string Name { get; set; }

        /// <summary>宏名第一个字符。</summary>
        public int Start { get; set; }

        public int OpenParen { get; set; }
        public int CloseParen { get; set; }

        /// <summary>括号里的原文（不含括号）。</summary>
        public string Arguments { get; set; }

        /// <summary>宏调用的结尾（')' 之后）。</summary>
        public int End => CloseParen + 1;
    }

    /// <summary>
    /// 在文本里找反射宏（说明符编辑器、UE0009 / UE0010 的“去掉 UPROPERTY / UFUNCTION”共用）。注释和字符串先抹掉再找，
    /// 所以注释里写的 UPROPERTY( 不算；括号按配对找，meta=(…) 里的括号不影响。纯逻辑。
    /// </summary>
    public static class ReflectionMacroText
    {
        /// <summary>说明符编辑器支持的宏。</summary>
        public static readonly string[] EditableMacros = { "UPROPERTY", "UFUNCTION", "UCLASS", "USTRUCT" };

        /// <summary>只往回看这么远（宏不会离光标 / 声明太远，大文件里也不全文扫描）。</summary>
        private const int MaxLookBack = 4000;

        /// <summary>光标在宏名上或括号里（含右括号之后紧挨着的位置）时，返回这个宏；否则 null。</summary>
        public static ReflectionMacroCall FindAt(string text, int caret)
        {
            if (string.IsNullOrEmpty(text) || caret < 0 || caret > text.Length) return null;
            int from = Math.Max(0, caret - MaxLookBack);
            string b = UeCodeGenerator.Blank(text.Substring(from, Math.Min(text.Length - from, caret - from + MaxLookBack)));
            int local = caret - from;
            foreach (var macro in EditableMacros)
            {
                int search = Math.Min(b.Length - 1, local + macro.Length);
                while (search >= 0)
                {
                    int at = b.LastIndexOf(macro, search, StringComparison.Ordinal);
                    if (at < 0) break;
                    search = at - 1;
                    var call = Parse(b, text, from, at, macro);
                    if (call == null) continue;
                    if (caret >= call.Start && caret <= call.End) return call;
                    if (call.End < caret) break; // 更早的同名宏只会更远
                }
            }
            return null;
        }

        /// <summary>
        /// 声明（<paramref name="declOffset"/> 是声明里任意位置，通常是名字）前面最近的 <paramref name="macro"/>：
        /// 宏的右括号和声明之间不能有 ';'、'{'、'}'（否则那个宏属于别的声明）。
        /// </summary>
        public static ReflectionMacroCall FindBefore(string text, int declOffset, string macro)
        {
            if (string.IsNullOrEmpty(text) || declOffset <= 0 || declOffset > text.Length) return null;
            int from = Math.Max(0, declOffset - MaxLookBack);
            string b = UeCodeGenerator.Blank(text.Substring(from, declOffset - from));
            int search = b.Length - 1;
            while (search >= 0)
            {
                int at = b.LastIndexOf(macro, search, StringComparison.Ordinal);
                if (at < 0) return null;
                search = at - 1;
                var call = Parse(b, text, from, at, macro);
                if (call == null) continue;
                int localEnd = call.End - from;
                if (localEnd > b.Length) return null;
                string between = b.Substring(localEnd);
                if (between.IndexOfAny(new[] { ';', '{', '}' }) >= 0) return null;
                return call;
            }
            return null;
        }

        /// <summary>
        /// 删掉整个宏调用的编辑范围：宏独占一行时连同这一行（含换行）一起删，否则只删宏和它后面的空白。
        /// </summary>
        public static (int Start, int Length) RemovalSpan(string text, ReflectionMacroCall call)
        {
            int lineStart = call.Start == 0 ? 0 : text.LastIndexOf('\n', call.Start - 1) + 1;
            bool aloneBefore = text.Substring(lineStart, call.Start - lineStart).Trim().Length == 0;
            int e = call.End;
            while (e < text.Length && (text[e] == ' ' || text[e] == '\t')) e++;
            bool aloneAfter = e >= text.Length || text[e] == '\r' || text[e] == '\n';
            if (aloneBefore && aloneAfter)
            {
                if (e < text.Length && text[e] == '\r') e++;
                if (e < text.Length && text[e] == '\n') e++;
                return (lineStart, e - lineStart);
            }
            return (call.Start, e - call.Start);
        }

        private static ReflectionMacroCall Parse(string blank, string text, int from, int at, string macro)
        {
            if (at > 0 && IsIdent(blank[at - 1])) return null;
            int k = at + macro.Length;
            if (k < blank.Length && IsIdent(blank[k])) return null;
            while (k < blank.Length && char.IsWhiteSpace(blank[k])) k++;
            if (k >= blank.Length || blank[k] != '(') return null;
            int close = UeCodeGenerator.MatchParen(blank, k);
            if (close < 0) return null;
            return new ReflectionMacroCall
            {
                Name = macro, Start = from + at, OpenParen = from + k, CloseParen = from + close,
                Arguments = text.Substring(from + k + 1, close - k - 1),
            };
        }

        private static bool IsIdent(char c) => c == '_' || char.IsLetterOrDigit(c);
    }
}
