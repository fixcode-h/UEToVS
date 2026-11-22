using System;
using System.Collections.Generic;

namespace UEToVS.Host.Logic.Vcs
{
    public enum ChangeKind
    {
        Added = 0,
        Modified = 1,
        Deleted = 2,
    }

    /// <summary>
    /// 一块改动（行号从 0 开始）：基准版本的 [OldStart, OldStart+OldCount) 换成了当前的 [NewStart, NewStart+NewCount)。
    /// 删除块的 NewCount 是 0，NewStart 是删除位置后面那一行。
    /// </summary>
    public sealed class DiffHunk
    {
        public DiffHunk(int oldStart, int oldCount, int newStart, int newCount)
        {
            OldStart = oldStart;
            OldCount = oldCount;
            NewStart = newStart;
            NewCount = newCount;
        }

        public int OldStart { get; }
        public int OldCount { get; }
        public int NewStart { get; }
        public int NewCount { get; }

        public ChangeKind Kind => OldCount == 0 ? ChangeKind.Added : NewCount == 0 ? ChangeKind.Deleted : ChangeKind.Modified;

        /// <summary>当前文本里这块占的最后一行（删除块是删除位置那一行）。</summary>
        public int NewEnd => NewCount == 0 ? NewStart : NewStart + NewCount - 1;

        public override string ToString() => $"-{OldStart},{OldCount} +{NewStart},{NewCount}";
    }

    /// <summary>
    /// 行级 diff（Myers O(ND)，先去掉相同的头尾）：改动色条、改动块、追溯行号映射都用它。编辑时在后台线程上对比内存里缓存的
    /// 基准文本，不再调 git / p4。差异太大（编辑距离超过 <see cref="MaxEditDistance"/>）时中间整段算一块“修改”，保证耗时和内存有上限。
    /// 行比较忽略行尾的 \r（基准是 LF、编辑器里是 CRLF 时不会整篇标红）。
    /// </summary>
    public static class LineDiff
    {
        public const int MaxEditDistance = 1000;

        public static string[] SplitLines(string text)
        {
            if (text == null) return Array.Empty<string>();
            var lines = new List<string>(text.Length / 32 + 1);
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                    lines.Add(text.Substring(start, end - start));
                    start = i + 1;
                }
            }
            // 最后一行（没有换行结尾时）；有换行结尾时编辑器里也是多一行空行，和 ITextSnapshot 的行数一致
            string last = text.Substring(start);
            if (last.EndsWith("\r", StringComparison.Ordinal)) last = last.Substring(0, last.Length - 1);
            lines.Add(last);
            return lines.ToArray();
        }

        public static List<DiffHunk> Compute(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
        {
            var hunks = new List<DiffHunk>();
            if (oldLines == null || newLines == null) return hunks;
            // 行 → 整数编号，比较只比整数
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] a = Encode(oldLines, ids), b = Encode(newLines, ids);
            int n = a.Length, m = b.Length;
            int pre = 0;
            while (pre < n && pre < m && a[pre] == b[pre]) pre++;
            int suf = 0;
            while (suf < n - pre && suf < m - pre && a[n - 1 - suf] == b[m - 1 - suf]) suf++;
            int aLen = n - pre - suf, bLen = m - pre - suf;
            if (aLen == 0 && bLen == 0) return hunks;
            if (aLen == 0 || bLen == 0)
            {
                hunks.Add(new DiffHunk(pre, aLen, pre, bLen));
                return hunks;
            }
            var ops = Myers(a, pre, aLen, b, pre, bLen);
            if (ops == null)
            {
                hunks.Add(new DiffHunk(pre, aLen, pre, bLen));
                return hunks;
            }
            // ops：按顺序的 (oldIndex, newIndex) 相同行对；相邻相同行之间的空隙就是改动块
            int ai = pre, bi = pre;
            foreach (var (x, y) in ops)
            {
                if (x > ai || y > bi) hunks.Add(new DiffHunk(ai, x - ai, bi, y - bi));
                ai = x + 1;
                bi = y + 1;
            }
            int aEnd = pre + aLen, bEnd = pre + bLen;
            if (aEnd > ai || bEnd > bi) hunks.Add(new DiffHunk(ai, aEnd - ai, bi, bEnd - bi));
            return hunks;
        }

        private static int[] Encode(IReadOnlyList<string> lines, Dictionary<string, int> ids)
        {
            var r = new int[lines.Count];
            for (int i = 0; i < r.Length; i++)
            {
                string s = lines[i] ?? string.Empty;
                if (s.EndsWith("\r", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
                if (!ids.TryGetValue(s, out int id))
                {
                    id = ids.Count;
                    ids[s] = id;
                }
                r[i] = id;
            }
            return r;
        }

        /// <summary>Myers 贪心算法，返回相同行对；编辑距离超过上限时 null。</summary>
        private static List<(int, int)> Myers(int[] a, int aOff, int n, int[] b, int bOff, int m)
        {
            int max = Math.Min(n + m, MaxEditDistance);
            int offset = max + 1;
            var v = new int[2 * max + 3];
            var trace = new List<int[]>();
            int found = -1;
            for (int d = 0; d <= max; d++)
            {
                // 只保存本轮用到的 [-d, d] 区间，回溯时用
                var snap = new int[2 * d + 1];
                for (int k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || k != d && v[offset + k - 1] < v[offset + k + 1]) x = v[offset + k + 1];
                    else x = v[offset + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && a[aOff + x] == b[bOff + y])
                    {
                        x++;
                        y++;
                    }
                    v[offset + k] = x;
                    if (x >= n && y >= m)
                    {
                        found = d;
                        break;
                    }
                }
                for (int k = -d; k <= d; k++) snap[k + d] = v[offset + k];
                trace.Add(snap);
                if (found >= 0) break;
            }
            if (found < 0) return null;

            var pairs = new List<(int, int)>();
            int cx = n, cy = m;
            for (int d = found; d > 0; d--)
            {
                var prev = trace[d - 1];
                int k = cx - cy;
                int prevK;
                if (k == -d || k != d && Get(prev, d - 1, k - 1) < Get(prev, d - 1, k + 1)) prevK = k + 1;
                else prevK = k - 1;
                int prevX = Get(prev, d - 1, prevK);
                int prevY = prevX - prevK;
                while (cx > prevX && cy > prevY)
                {
                    cx--;
                    cy--;
                    pairs.Add((aOff + cx, bOff + cy));
                }
                cx = prevX;
                cy = prevY;
            }
            while (cx > 0 && cy > 0)
            {
                cx--;
                cy--;
                pairs.Add((aOff + cx, bOff + cy));
            }
            pairs.Reverse();
            return pairs;
        }

        private static int Get(int[] snap, int d, int k)
        {
            int i = k + d;
            return i < 0 || i >= snap.Length ? -1 : snap[i];
        }
    }

    /// <summary>改动块上的计算：当前行 → 基准行、上一处 / 下一处、光标所在的块。</summary>
    public static class HunkMath
    {
        /// <summary>当前第 <paramref name="line"/> 行在基准里的行号；新加或改过的行是 -1。</summary>
        public static int ToBaseLine(IReadOnlyList<DiffHunk> hunks, int line)
        {
            if (hunks == null) return line;
            int shift = 0;
            foreach (var h in hunks)
            {
                if (line < h.NewStart) break;
                if (h.NewCount > 0 && line < h.NewStart + h.NewCount) return -1;
                shift += h.OldCount - h.NewCount;
            }
            return line + shift;
        }

        /// <summary>每个当前行 → 基准行（-1 = 未提交的改动）。</summary>
        public static int[] BaseLineMap(IReadOnlyList<DiffHunk> hunks, int lineCount)
        {
            var map = new int[Math.Max(0, lineCount)];
            int next = 0, shift = 0;
            if (hunks != null)
                foreach (var h in hunks)
                {
                    for (; next < h.NewStart && next < map.Length; next++) map[next] = next + shift;
                    for (int i = 0; i < h.NewCount && next < map.Length; i++, next++) map[next] = -1;
                    shift += h.OldCount - h.NewCount;
                }
            for (; next < map.Length; next++) map[next] = next + shift;
            return map;
        }

        /// <summary>包含第 <paramref name="line"/> 行的块（删除块算在它标记的那一行）。</summary>
        public static DiffHunk At(IReadOnlyList<DiffHunk> hunks, int line)
        {
            if (hunks == null) return null;
            foreach (var h in hunks)
            {
                if (h.NewCount == 0)
                {
                    if (DeletionMarkLine(h) == line) return h;
                }
                else if (line >= h.NewStart && line < h.NewStart + h.NewCount) return h;
            }
            return null;
        }

        /// <summary>删除块画在哪一行：删除位置后面那一行的顶上；删到文件末尾时画在最后一行。</summary>
        public static int DeletionMarkLine(DiffHunk h) => h.NewStart;

        /// <summary>光标行之后的下一块（到底了从头开始，<paramref name="wrapped"/> 为 true）。</summary>
        public static DiffHunk Next(IReadOnlyList<DiffHunk> hunks, int line, out bool wrapped)
        {
            wrapped = false;
            if (hunks == null || hunks.Count == 0) return null;
            var cur = At(hunks, line);
            foreach (var h in hunks)
                if (h.NewStart > line && !ReferenceEquals(h, cur)) return h;
            wrapped = true;
            return ReferenceEquals(hunks[0], cur) && hunks.Count == 1 ? null : hunks[0];
        }

        public static DiffHunk Previous(IReadOnlyList<DiffHunk> hunks, int line, out bool wrapped)
        {
            wrapped = false;
            if (hunks == null || hunks.Count == 0) return null;
            var cur = At(hunks, line);
            int anchor = cur != null ? cur.NewStart : line;
            for (int i = hunks.Count - 1; i >= 0; i--)
                if (hunks[i].NewStart < anchor && !ReferenceEquals(hunks[i], cur)) return hunks[i];
            wrapped = true;
            var last = hunks[hunks.Count - 1];
            return ReferenceEquals(last, cur) && hunks.Count == 1 ? null : last;
        }

        /// <summary>
        /// 当前行区间 [first, last] 对应的基准行区间（从 1 开始，给 git log -L）。两端是新加的行时往里收；整段都是未提交的行时 null。
        /// </summary>
        public static (int Start, int End)? BaseRange(IReadOnlyList<DiffHunk> hunks, int first, int last)
        {
            if (last < first) (first, last) = (last, first);
            int s = -1, e = -1;
            for (int i = first; i <= last; i++)
            {
                int b = ToBaseLine(hunks, i);
                if (b >= 0) { s = b; break; }
            }
            for (int i = last; i >= first; i--)
            {
                int b = ToBaseLine(hunks, i);
                if (b >= 0) { e = b; break; }
            }
            if (s < 0 || e < 0) return null;
            return (s + 1, e + 1);
        }
    }
}
