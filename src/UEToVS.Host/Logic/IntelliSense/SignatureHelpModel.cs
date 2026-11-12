using System;
using System.Collections.Generic;
using UEToVS.Contracts;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// Local parts of parameter info (§7.6): which parameter the caret is in (top-level commas between the call's '(' and
    /// the caret, ignoring nested brackets, template arguments in brackets, strings and chars), whether the caret has
    /// left the call, and the label range of each parameter (engine ranges, or split from the label when missing). Pure.
    /// </summary>
    public static class SignatureHelpModel
    {
        /// <summary>
        /// Scans <paramref name="text"/> (from just after the call's '(' to the caret). Returns the 0-based active parameter,
        /// or -1 when the call was closed before the caret (session should end).
        /// </summary>
        public static int ActiveParameter(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int depth = 0, commas = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\') i++;
                    else if (c == quote) quote = '\0';
                    continue;
                }
                switch (c)
                {
                    case '"':
                    case '\'':
                        quote = c;
                        break;
                    case '(':
                    case '[':
                    case '{':
                        depth++;
                        break;
                    case ')':
                    case ']':
                    case '}':
                        if (depth == 0) return -1; // the call's own ')' — caret is past it
                        depth--;
                        break;
                    case ',':
                        if (depth == 0) commas++;
                        break;
                }
            }
            return commas;
        }

        /// <summary>
        /// Finds the innermost unclosed '(' before <paramref name="caret"/> in <paramref name="text"/> (several lines joined),
        /// returning its index or -1. Strings and chars are skipped; scanning stops at ';' / '{' / '}' at depth 0.
        /// </summary>
        public static int FindOpenParen(string text, int caret)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            caret = Math.Min(caret, text.Length);
            // Forward pass with a stack (robust against quotes, unlike scanning backwards).
            var stack = new Stack<int>();
            char quote = '\0';
            for (int i = 0; i < caret; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\') i++;
                    else if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '/' && i + 1 < caret && text[i + 1] == '/')
                {
                    int nl = text.IndexOf('\n', i);
                    if (nl < 0 || nl >= caret) break;
                    i = nl;
                    continue;
                }
                switch (c)
                {
                    case '"':
                    case '\'':
                        quote = c;
                        break;
                    case '(':
                        stack.Push(i);
                        break;
                    case ')':
                        if (stack.Count > 0) stack.Pop();
                        break;
                    case ';':
                    case '{':
                    case '}':
                        stack.Clear();
                        break;
                }
            }
            return stack.Count > 0 ? stack.Peek() : -1;
        }

        /// <summary>
        /// (start, length) of every parameter inside <paramref name="sig"/>'s label: the engine's LabelStart/LabelLength when
        /// valid, otherwise split at the top-level commas of the outermost parentheses.
        /// </summary>
        public static List<(int Start, int Length)> ParameterRanges(SignatureInfo sig)
        {
            var result = new List<(int, int)>();
            if (sig?.Label == null) return result;
            string label = sig.Label;
            bool engineOk = sig.Parameters != null && sig.Parameters.Count > 0;
            if (engineOk)
                foreach (var p in sig.Parameters)
                    if (p == null || p.LabelStart < 0 || p.LabelLength <= 0 || p.LabelStart + p.LabelLength > label.Length)
                    {
                        engineOk = false;
                        break;
                    }
            if (engineOk)
            {
                foreach (var p in sig.Parameters) result.Add((p.LabelStart, p.LabelLength));
                return result;
            }
            int open = label.IndexOf('(');
            if (open < 0) return result;
            int depth = 0, start = open + 1;
            for (int i = open + 1; i < label.Length; i++)
            {
                char c = label[i];
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if ((c == ')' || c == '>' || c == ']' || c == '}') && depth > 0) depth--;
                else if (c == ')' && depth == 0)
                {
                    Add(result, label, start, i);
                    return result;
                }
                else if (c == ',' && depth == 0)
                {
                    Add(result, label, start, i);
                    start = i + 1;
                }
            }
            return result;
        }

        private static void Add(List<(int, int)> result, string label, int start, int end)
        {
            while (start < end && char.IsWhiteSpace(label[start])) start++;
            while (end > start && char.IsWhiteSpace(label[end - 1])) end--;
            if (end > start) result.Add((start, end - start));
        }

        /// <summary>
        /// Best signature for <paramref name="argCount"/> arguments: the engine's active one if it accepts them, else the
        /// first that has enough parameters (variadic labels with "..." accept any count).
        /// </summary>
        public static int PickSignature(SignatureHelpResponse r, int activeParameter)
        {
            if (r?.Signatures == null || r.Signatures.Count == 0) return -1;
            int engine = Math.Max(0, Math.Min(r.ActiveSignature, r.Signatures.Count - 1));
            if (Accepts(r.Signatures[engine], activeParameter)) return engine;
            for (int i = 0; i < r.Signatures.Count; i++)
                if (Accepts(r.Signatures[i], activeParameter)) return i;
            return engine;
        }

        private static bool Accepts(SignatureInfo s, int active)
        {
            if (s == null) return false;
            if (s.Label != null && s.Label.Contains("...")) return true;
            int n = s.Parameters != null && s.Parameters.Count > 0 ? s.Parameters.Count : ParameterRanges(s).Count;
            return active < Math.Max(1, n) || (n == 0 && active == 0);
        }
    }
}
