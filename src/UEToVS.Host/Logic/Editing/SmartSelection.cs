using System;
using System.Collections.Generic;
using UEToVS.Host.Logic.Generate;

namespace UEToVS.Host.Logic.Editing
{
    /// <summary>
    /// Smart Select (VA Extend Selection / Rider Ctrl+W): the next larger syntactic range around a selection — word, string
    /// contents, string, argument, bracket contents, brackets, statement, block contents, block with its header (if / for /
    /// function signature). Bracket structure on comment- and string-blanked text; local to the enclosing top-level block. Pure.
    /// </summary>
    public static class SmartSelection
    {
        /// <summary>The smallest candidate range strictly containing [start, end), or null when there is none.</summary>
        public static (int Start, int End)? Expand(string text, int start, int end)
        {
            if (string.IsNullOrEmpty(text)) return null;
            start = Math.Max(0, Math.Min(start, text.Length));
            end = Math.Max(start, Math.Min(end, text.Length));
            string b = UeCodeGenerator.Blank(text);
            var cands = new List<(int, int)>();
            Word(text, start, end, cands);
            StringLiteral(text, b, start, end, cands);
            Brackets(text, b, start, end, cands);
            (int, int)? best = null;
            foreach (var c in cands)
            {
                if (c.Item1 > start || c.Item2 < end || c.Item2 - c.Item1 <= end - start) continue;
                if (best == null || c.Item2 - c.Item1 < best.Value.Item2 - best.Value.Item1) best = c;
            }
            if (best == null && (start > 0 || end < text.Length)) best = (0, text.Length);
            return best;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static void Word(string t, int s, int e, List<(int, int)> cands)
        {
            if (s != e && !(s < t.Length && IsWord(t[s]))) return;
            int a = s, z = e;
            while (a > 0 && IsWord(t[a - 1])) a--;
            while (z < t.Length && IsWord(t[z])) z++;
            if (z > a) cands.Add((a, z));
        }

        private static void StringLiteral(string t, string b, int s, int e, List<(int, int)> cands)
        {
            // blanked text keeps the quotes: find the quote pair on the line around the selection
            int ls = t.LastIndexOf('\n', Math.Max(0, s - 1)) + 1;
            int le = t.IndexOf('\n', e);
            if (le < 0) le = t.Length;
            for (int i = ls; i < le; i++)
            {
                if (b[i] != '"' || t[i] != '"') continue;
                int j = i + 1;
                while (j < le && !(t[j] == '"' && b[j] == '"')) j++;
                if (j >= le) return;
                if (i < s && j >= e - 1 && j + 1 >= e)
                {
                    if (i + 1 <= s && j >= e) cands.Add((i + 1, j));
                    cands.Add((i, j + 1));
                    return;
                }
                i = j;
            }
        }

        private static void Brackets(string t, string b, int s, int e, List<(int, int)> cands)
        {
            // walk outwards from the selection over enclosing (), [], {}
            var stack = new Stack<char>();
            int pos = s - 1;
            // the selection is exactly a bracket pair: still offer what surrounds that pair (callee, block header)
            if (s < e && s < b.Length && (b[s] == '(' || b[s] == '[' || b[s] == '{') &&
                Match(b, s, b[s], b[s] == '(' ? ')' : b[s] == '[' ? ']' : '}') == e - 1) pos = s;
            while (pos >= 0)
            {
                char c = b[pos];
                if (c == ')' || c == ']' || c == '}') stack.Push(c);
                else if (c == '(' || c == '[' || c == '{')
                {
                    char want = c == '(' ? ')' : c == '[' ? ']' : '}';
                    if (stack.Count > 0)
                    {
                        if (stack.Peek() == want) stack.Pop();
                        pos--;
                        continue;
                    }
                    int close = Match(b, pos, c, want);
                    if (close < 0 || close + 1 < e) { pos--; continue; }
                    AddPair(t, b, pos, close, s, e, cands);
                }
                pos--;
            }
            // statement at top level (outside any brace)
            Statement(t, b, -1, b.Length, s, e, cands);
        }

        private static void AddPair(string t, string b, int open, int close, int s, int e, List<(int, int)> cands)
        {
            var (ia, iz) = Trim(t, open + 1, close);
            if (iz > ia) cands.Add((ia, iz));
            cands.Add((open, close + 1));
            char kind = b[open];
            if (kind == '(' || kind == '[')
            {
                // the argument containing the selection
                int depth = 0, segStart = open + 1;
                for (int i = open + 1; i <= close; i++)
                {
                    char c = b[i];
                    if (c == '(' || c == '[' || c == '{') depth++;
                    else if ((c == ')' || c == ']' || c == '}') && i != close) depth--;
                    if (i == close || c == ',' && depth == 0)
                    {
                        if (segStart <= s && i >= e)
                        {
                            var (a, z) = Trim(t, segStart, i);
                            if (z > a) cands.Add((a, z));
                        }
                        segStart = i + 1;
                    }
                }
                // callee + call: Foo(…), Obj->Foo(…)
                int k = open;
                while (k > 0 && char.IsWhiteSpace(b[k - 1])) k--;
                int n = k;
                while (n > 0 && (IsWord(b[n - 1]) || b[n - 1] == '.' || b[n - 1] == ':' || b[n - 1] == '>' && n > 1 && b[n - 2] == '-' || b[n - 1] == '-' && n < b.Length && b[n] == '>')) n--;
                if (n < k) cands.Add((n, close + 1));
            }
            else if (kind == '{')
            {
                Statement(t, b, open, close, s, e, cands);
                // the block with its header: "if (x) { … }", "void F() const { … }", "class A : B { … }"
                int h = open;
                while (h > 0 && b[h - 1] != ';' && b[h - 1] != '{' && b[h - 1] != '}' && !(b[h - 1] == ':' && IsAccessLabel(b, h - 1))) h--;
                var (ha, _) = Trim(t, h, open);
                if (ha < open) cands.Add((ha, close + 1));
            }
        }

        /// <summary>The statement inside the block (open, close) that contains the selection: from after the previous ';' / '{' / '}' to the next ';'.</summary>
        private static void Statement(string t, string b, int open, int close, int s, int e, List<(int, int)> cands)
        {
            int depth = 0, segStart = open + 1;
            for (int i = open + 1; i < close && i < b.Length; i++)
            {
                char c = b[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (segStart <= s && i + 1 >= e) { var (a, z) = Trim(t, segStart, i + 1); if (z > a) cands.Add((a, z)); return; }
                        segStart = i + 1;
                    }
                    continue;
                }
                if (depth == 0 && c == ';')
                {
                    if (segStart <= s && i + 1 >= e)
                    {
                        var (a, z) = Trim(t, segStart, i + 1);
                        if (z > a) cands.Add((a, z));
                        return;
                    }
                    segStart = i + 1;
                }
            }
        }

        private static bool IsAccessLabel(string b, int colon)
        {
            int s = colon;
            while (s > 0 && char.IsLetter(b[s - 1])) s--;
            string w = b.Substring(s, colon - s);
            return w == "public" || w == "protected" || w == "private";
        }

        private static (int, int) Trim(string t, int a, int z)
        {
            while (a < z && char.IsWhiteSpace(t[a])) a++;
            while (z > a && char.IsWhiteSpace(t[z - 1])) z--;
            return (a, z);
        }

        private static int Match(string b, int open, char o, char c)
        {
            int depth = 0;
            for (int i = open; i < b.Length; i++)
            {
                if (b[i] == o) depth++;
                else if (b[i] == c && --depth == 0) return i;
            }
            return -1;
        }
    }
}
