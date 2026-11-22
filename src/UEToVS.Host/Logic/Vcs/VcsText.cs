using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UEToVS.Host.Logic.Vcs
{
    /// <summary>“3 天前”这类相对时间（IDEA 行尾追溯的写法）。</summary>
    public static class RelativeTime
    {
        public static string Format(DateTimeOffset time, DateTimeOffset now, bool english, bool dateOnly = false)
        {
            if (time == default) return string.Empty;
            var span = now - time;
            if (dateOnly)
            {
                // p4 annotate 只有日期：按天算，当天就是“今天”
                int days = (int)Math.Floor((now.Date - time.Date).TotalDays);
                if (days <= 0) return english ? "today" : "今天";
                if (days == 1) return english ? "yesterday" : "昨天";
                if (days < 7) return english ? days + " days ago" : days + " 天前";
                return Coarse(span, english);
            }
            if (span.TotalSeconds < 0) span = TimeSpan.Zero; // 时钟差：当作刚刚
            if (span.TotalMinutes < 1) return english ? "just now" : "刚刚";
            if (span.TotalHours < 1) return Unit((int)span.TotalMinutes, "minute", "分钟", english);
            if (span.TotalDays < 1) return Unit((int)span.TotalHours, "hour", "小时", english);
            if (span.TotalDays < 7) return Unit((int)span.TotalDays, "day", "天", english);
            return Coarse(span, english);
        }

        private static string Coarse(TimeSpan span, bool english)
        {
            if (span.TotalDays < 31) return Unit((int)(span.TotalDays / 7), "week", "周", english);
            if (span.TotalDays < 365) return Unit(Math.Max(1, (int)(span.TotalDays / 30.4)), "month", "个月", english);
            return Unit(Math.Max(1, (int)(span.TotalDays / 365.25)), "year", "年", english);
        }

        private static string Unit(int n, string en, string zh, bool english) =>
            english ? n + " " + en + (n == 1 ? string.Empty : "s") + " ago" : n + " " + zh + "前";

        /// <summary>卡片上的绝对时间：2026-11-22 14:05（只有日期时不带时间）。</summary>
        public static string Absolute(DateTimeOffset time, bool dateOnly = false) =>
            time == default ? string.Empty : time.ToString(dateOnly ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>行尾灰字和追溯栏的文字。</summary>
    public static class BlameText
    {
        public const int MaxSummary = 80;

        public static string Inline(VcsCommit c, DateTimeOffset now, bool english)
        {
            if (c == null) return string.Empty;
            if (c.IsUncommitted) return english ? "Not committed yet" : "未提交";
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(c.Author) ? "?" : c.Author);
            string when = RelativeTime.Format(c.Time, now, english, c.DateOnly);
            if (when.Length > 0) sb.Append(" · ").Append(when);
            string summary = c.Summary;
            if (!string.IsNullOrEmpty(summary)) sb.Append(" · ").Append(Truncate(summary, MaxSummary));
            return sb.ToString();
        }

        /// <summary>追溯栏一行：作者（截短）+ 日期。</summary>
        public static string Gutter(VcsCommit c, bool english, int authorWidth = 12)
        {
            if (c == null) return string.Empty;
            if (c.IsUncommitted) return english ? "Not committed" : "未提交";
            string author = Truncate(string.IsNullOrEmpty(c.Author) ? "?" : c.Author, authorWidth);
            return author.PadRight(authorWidth + 1) + RelativeTime.Absolute(c.Time, true);
        }

        public static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
            return s.Substring(0, Math.Max(1, max - 1)) + "…";
        }
    }

    /// <summary>
    /// 追溯栏的颜色：同一提交同一色相（由提交号算出，固定），越新越亮（按文件里所有提交的时间排名）。返回 0–1 的亮度，
    /// 由界面层按深浅主题换成画刷。
    /// </summary>
    public static class BlameColors
    {
        /// <summary>提交 → 0–1 的“新旧”：最新的 1，最旧的 0；只有一个提交时 1。</summary>
        public static Dictionary<string, double> AgeRanks(IEnumerable<VcsCommit> commits)
        {
            var list = new List<VcsCommit>();
            foreach (var c in commits) if (c != null && !c.IsUncommitted) list.Add(c);
            list.Sort((x, y) => x.Time.CompareTo(y.Time));
            var r = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++) r[list[i].Id] = list.Count == 1 ? 1.0 : (double)i / (list.Count - 1);
            return r;
        }

        /// <summary>色相 0–360，按提交号散开。</summary>
        public static double Hue(string commitId)
        {
            if (string.IsNullOrEmpty(commitId)) return 0;
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in commitId) h = (h ^ c) * 16777619;
                return h % 360;
            }
        }

        /// <summary>HSL → RGB（0–255）。</summary>
        public static (byte R, byte G, byte B) FromHsl(double h, double s, double l)
        {
            h = (h % 360 + 360) % 360 / 360.0;
            s = Math.Max(0, Math.Min(1, s));
            l = Math.Max(0, Math.Min(1, l));
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            double Ch(double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }
            return ((byte)Math.Round(Ch(h + 1.0 / 3) * 255), (byte)Math.Round(Ch(h) * 255), (byte)Math.Round(Ch(h - 1.0 / 3) * 255));
        }

        /// <summary>
        /// 追溯栏底色：深色主题上越新越亮，浅色主题上越新越饱和；alpha 也随新旧变化（旧提交几乎透明，不抢正文）。
        /// </summary>
        public static (byte A, byte R, byte G, byte B) Background(string commitId, double rank, bool dark)
        {
            rank = Math.Max(0, Math.Min(1, rank));
            double l = dark ? 0.30 + 0.25 * rank : 0.80 - 0.15 * rank;
            var (r, g, b) = FromHsl(Hue(commitId), 0.45 + 0.25 * rank, l);
            byte a = (byte)Math.Round(40 + 140 * rank);
            return (a, r, g, b);
        }
    }

    /// <summary>
    /// 提交说明里的链接（设置 vcs.issueLinks）：每条 "正则 => URL 模板"，多条用 ; 或换行分开；模板里 $0 是整个匹配、$1… 是分组。
    /// 只生成 http / https 链接；正则写错的那条跳过（返回给设置页显示）。
    /// </summary>
    public sealed class IssueLinkRule
    {
        public IssueLinkRule(Regex pattern, string template)
        {
            Pattern = pattern;
            Template = template;
        }

        public Regex Pattern { get; }
        public string Template { get; }
    }

    public sealed class IssueLinkMatch
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public string Url { get; set; }
    }

    public static class IssueLinks
    {
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

        public static List<IssueLinkRule> Parse(string setting, List<string> errors = null)
        {
            var rules = new List<IssueLinkRule>();
            if (string.IsNullOrWhiteSpace(setting)) return rules;
            // 先按换行分，再按 ; 分；没有 "=>" 的片段是上一条正则里的 ;，接回去
            var entries = new List<string>();
            foreach (var line in setting.Replace("\r", string.Empty).Split('\n'))
            {
                int first = entries.Count;
                foreach (var piece in line.Split(';'))
                {
                    if (piece.Trim().Length == 0) continue;
                    bool hasArrow = piece.IndexOf("=>", StringComparison.Ordinal) >= 0;
                    if (entries.Count > first && (!hasArrow || entries[entries.Count - 1].IndexOf("=>", StringComparison.Ordinal) < 0))
                        entries[entries.Count - 1] += ";" + piece;
                    else entries.Add(piece);
                }
            }
            foreach (var e in entries)
            {
                int arrow = e.LastIndexOf("=>", StringComparison.Ordinal);
                if (arrow <= 0) { if (e.Trim().Length > 0) errors?.Add(e.Trim()); continue; }
                string pattern = e.Substring(0, arrow).Trim(), template = e.Substring(arrow + 2).Trim();
                if (pattern.Length == 0 || !IsHttp(template)) { errors?.Add(e.Trim()); continue; }
                try
                {
                    rules.Add(new IssueLinkRule(new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout), template));
                }
                catch (ArgumentException)
                {
                    errors?.Add(e.Trim());
                }
            }
            return rules;
        }

        /// <summary>文本里的链接，按位置排序、互不重叠（前面的规则优先）。</summary>
        public static List<IssueLinkMatch> Find(string text, IReadOnlyList<IssueLinkRule> rules)
        {
            var found = new List<IssueLinkMatch>();
            if (string.IsNullOrEmpty(text) || rules == null) return found;
            foreach (var rule in rules)
            {
                try
                {
                    foreach (Match m in rule.Pattern.Matches(text))
                    {
                        if (m.Length == 0) continue;
                        bool overlaps = false;
                        foreach (var f in found)
                            if (m.Index < f.Start + f.Length && f.Start < m.Index + m.Length) { overlaps = true; break; }
                        if (overlaps) continue;
                        string url = m.Result(rule.Template);
                        if (!IsHttp(url)) continue;
                        found.Add(new IssueLinkMatch { Start = m.Index, Length = m.Length, Url = url });
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // 写得太慢的正则：这条不管了
                }
            }
            found.Sort((x, y) => x.Start.CompareTo(y.Start));
            return found;
        }

        private static bool IsHttp(string url) =>
            url != null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>只保留最近用过的 N 项（版本信息缓存最多 32 个文件）。线程安全。</summary>
    public sealed class LruCache<TKey, TValue>
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
        private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new LinkedList<KeyValuePair<TKey, TValue>>();
        private readonly object _lock = new object();

        public LruCache(int capacity, IEqualityComparer<TKey> comparer = null)
        {
            _capacity = Math.Max(1, capacity);
            _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer ?? EqualityComparer<TKey>.Default);
        }

        public int Count
        {
            get { lock (_lock) return _map.Count; }
        }

        public bool TryGet(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var old))
                {
                    _order.Remove(old);
                    _map.Remove(key);
                }
                var node = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
                _map[key] = node;
                while (_map.Count > _capacity)
                {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }

        public void Remove(TKey key)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _map.Remove(key);
                }
            }
        }

        public void RemoveWhere(Func<TKey, bool> predicate)
        {
            lock (_lock)
            {
                var keys = new List<TKey>();
                foreach (var k in _map.Keys) if (predicate(k)) keys.Add(k);
                foreach (var k in keys) Remove(k);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _order.Clear();
            }
        }
    }

    /// <summary>“最多每 N 秒一次”：VS 激活触发的 git status / HEAD 检查。</summary>
    public sealed class RefreshThrottle
    {
        private readonly TimeSpan _interval;
        private DateTime _last = DateTime.MinValue;

        public RefreshThrottle(TimeSpan interval) => _interval = interval;

        /// <summary>距上次放行够久了就放行并记下时间。</summary>
        public bool TryEnter(DateTime nowUtc)
        {
            if (_last != DateTime.MinValue && nowUtc - _last < _interval && nowUtc >= _last) return false;
            _last = nowUtc;
            return true;
        }

        /// <summary>别的原因（保存、打开解决方案）刚刷新过，也算一次。</summary>
        public void Mark(DateTime nowUtc) => _last = nowUtc;
    }

    /// <summary>
    /// 函数的行区间：从代码引擎给的函数声明行开始，找到函数体的 { 和配对的 }（跳过注释、字符串、字符字面量）。
    /// 声明行后先遇到 ; 说明是声明不是定义，只返回声明那几行。
    /// </summary>
    public static class FunctionRange
    {
        public static (int Start, int End)? Find(IReadOnlyList<string> lines, int startLine, int maxLines = 20000)
        {
            if (lines == null || startLine < 0 || startLine >= lines.Count) return null;
            int depth = 0;
            bool opened = false, blockComment = false;
            int parens = 0;
            for (int i = startLine; i < lines.Count && i < startLine + maxLines; i++)
            {
                string s = lines[i] ?? string.Empty;
                for (int j = 0; j < s.Length; j++)
                {
                    char c = s[j];
                    char next = j + 1 < s.Length ? s[j + 1] : '\0';
                    if (blockComment)
                    {
                        if (c == '*' && next == '/') { blockComment = false; j++; }
                        continue;
                    }
                    if (c == '/' && next == '/') break;
                    if (c == '/' && next == '*') { blockComment = true; j++; continue; }
                    if (c == '"' || c == '\'')
                    {
                        // R"( … )" 原始字符串只处理单行的情况，多行的极少出现在函数头里
                        j = SkipLiteral(s, j, c);
                        continue;
                    }
                    if (c == '(') parens++;
                    else if (c == ')') parens = Math.Max(0, parens - 1);
                    else if (c == ';' && !opened && parens == 0) return (startLine, i);
                    else if (c == '{') { depth++; opened = true; }
                    else if (c == '}' && opened)
                    {
                        depth--;
                        if (depth == 0) return (startLine, i);
                    }
                }
            }
            return null;
        }

        private static int SkipLiteral(string s, int start, char quote)
        {
            for (int j = start + 1; j < s.Length; j++)
            {
                if (s[j] == '\\') { j++; continue; }
                if (s[j] == quote) return j;
            }
            return s.Length;
        }
    }

    /// <summary>基准版本的字节 → 文本：先看 BOM，再试严格 UTF-8，不行就用本机 ANSI 代码页（由调用方给）。</summary>
    public static class TextDecoding
    {
        public static string Decode(byte[] bytes, Encoding fallback)
        {
            if (bytes == null) return null;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return (fallback ?? Encoding.UTF8).GetString(bytes);
            }
        }

        /// <summary>看着像二进制（前 8000 字节里有 NUL）：不做改动色条和追溯。</summary>
        public static bool LooksBinary(byte[] bytes)
        {
            if (bytes == null) return false;
            if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF)) return false;
            int n = Math.Min(bytes.Length, 8000);
            for (int i = 0; i < n; i++) if (bytes[i] == 0) return true;
            return false;
        }
    }

    /// <summary>判断目录在 Git 还是 Perforce 里（最近一层，<see cref="Mcp.VcsRoot.Find"/>）；手动指定时按设置。</summary>
    public static class VcsDetector
    {
        public static (VcsKind Kind, string Root) Detect(string startDir, VcsProviderChoice choice = VcsProviderChoice.Auto, string p4ConfigName = null)
        {
            if (choice == VcsProviderChoice.Off) return (VcsKind.None, null);
            string root = Mcp.VcsRoot.Find(startDir, p4ConfigName);
            if (root == null) return (VcsKind.None, null);
            var kind = Classify(root);
            if (choice == VcsProviderChoice.Git && kind != VcsKind.Git) return (VcsKind.None, null);
            if (choice == VcsProviderChoice.Perforce && kind != VcsKind.Perforce) return (VcsKind.None, null);
            return (kind, root);
        }

        /// <summary>根目录上的标记：.git（文件夹或 worktree 的文件）→ Git；P4CONFIG 文件 → Perforce；其它不支持。</summary>
        public static VcsKind Classify(string root)
        {
            if (string.IsNullOrEmpty(root)) return VcsKind.None;
            string git = Path.Combine(root, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return VcsKind.Git;
            if (Directory.Exists(Path.Combine(root, ".svn")) || Directory.Exists(Path.Combine(root, ".hg")) || Directory.Exists(Path.Combine(root, ".plastic")))
                return VcsKind.None;
            return VcsKind.Perforce; // VcsRoot 只认这几种标记，剩下的就是 P4CONFIG 文件
        }

        /// <summary>路径是不是在 Engine\ 下（引擎文件默认不做行尾追溯）。没有引擎根时按路径里的 \Engine\Source\ 等判断。</summary>
        public static bool IsEngineFile(string path, string engineRoot)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!string.IsNullOrEmpty(engineRoot)) return Mcp.VcsRoot.IsSameOrUnder(Path.GetDirectoryName(path) ?? path, engineRoot);
            string p = path.Replace('/', '\\');
            return p.IndexOf("\\Engine\\Source\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("\\Engine\\Plugins\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("\\Engine\\Shaders\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>设置 vcs.provider。</summary>
    public enum VcsProviderChoice
    {
        Auto = 0,
        Git = 1,
        Perforce = 2,
        Off = 3,
    }

    /// <summary>设置 vcs.inlineBlameScope。</summary>
    public enum InlineBlameScope
    {
        CurrentLine = 0,
        AllLines = 1,
    }
}
