using System;
using System.Collections.Generic;
using System.Threading;

namespace UEToVS.Host.Logic.Shaders
{
    /// <summary>着色器文件的读取方式（宿主：已打开的缓冲区优先，其次磁盘；单测：内存）。</summary>
    public interface IShaderFileSource
    {
        /// <summary>文件的版本戳（大小 + 修改时间等），不存在时返回 null。</summary>
        string Stamp(string path);

        /// <summary>文件内容，读不到时返回 null。</summary>
        string Read(string path);
    }

    /// <summary>
    /// 最近解析过的着色器文件（LRU，按路径 + 版本戳失效）。#include 链上的 Common.ush 之类会被很多文件用到，留着就不用每次重新解析。线程安全。
    /// </summary>
    public sealed class ShaderDocumentCache
    {
        private readonly int _capacity;
        private readonly object _gate = new object();
        private readonly LinkedList<(string Path, string Stamp, HlslDocument Doc)> _lru = new LinkedList<(string, string, HlslDocument)>();
        private readonly Dictionary<string, LinkedListNode<(string Path, string Stamp, HlslDocument Doc)>> _map =
            new Dictionary<string, LinkedListNode<(string, string, HlslDocument)>>(StringComparer.OrdinalIgnoreCase);

        public ShaderDocumentCache(int capacity = 48) => _capacity = Math.Max(1, capacity);

        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        /// <summary>解析次数（单测和日志用）。</summary>
        public int Parses { get; private set; }

        /// <summary>已解析并且版本没变就直接返回，否则读文件重新解析；读不到返回 null。</summary>
        public HlslDocument Get(string path, IShaderFileSource source)
        {
            if (string.IsNullOrEmpty(path) || source == null) return null;
            string stamp = source.Stamp(path);
            if (stamp == null) return null;
            lock (_gate)
            {
                if (_map.TryGetValue(path, out var node) && node.Value.Stamp == stamp)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Doc;
                }
            }
            string text = source.Read(path);
            if (text == null) return null;
            var doc = HlslParser.Parse(text);
            Put(path, stamp, doc);
            return doc;
        }

        public void Put(string path, string stamp, HlslDocument doc)
        {
            lock (_gate)
            {
                Parses++;
                if (_map.TryGetValue(path, out var old))
                {
                    _lru.Remove(old);
                    _map.Remove(path);
                }
                _map[path] = _lru.AddFirst((path, stamp, doc));
                while (_map.Count > _capacity)
                {
                    var last = _lru.Last;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Path);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _lru.Clear();
                _map.Clear();
            }
        }
    }

    /// <summary>找到的定义：文件、行列和声明。</summary>
    public sealed class ShaderDefinition
    {
        public string File { get; set; }
        public HlslSymbol Symbol { get; set; }

        /// <summary>离当前文件隔了几层 #include（0 = 当前文件）。</summary>
        public int Depth { get; set; }
    }

    /// <summary>
    /// 沿 #include 链找名字的定义：当前文件优先，然后按 include 顺序一层层往下（广度优先），每个文件只看一次，最多看
    /// <see cref="MaxFiles"/> 个文件。解析结果走 <see cref="ShaderDocumentCache"/>。纯逻辑（路径解析和读文件由调用方给）。
    /// </summary>
    public static class ShaderDefinitionFinder
    {
        public const int MaxFiles = 160;

        /// <summary>
        /// <paramref name="resolve"/>：(include 文本, 所在文件) → 实际文件路径（找不到返回 null）。
        /// 结果按“同名不同种类”排序：先函数 / 结构体 / 宏，同层次里按出现顺序。
        /// </summary>
        public static List<ShaderDefinition> Find(string word, string currentFile, HlslDocument current, ShaderDocumentCache cache,
                                                  IShaderFileSource source, Func<string, string, string> resolve, CancellationToken ct,
                                                  bool memberOnly = false)
        {
            var hits = new List<ShaderDefinition>();
            if (string.IsNullOrEmpty(word)) return hits;
            Walk(currentFile, current, cache, source, resolve, ct, (file, doc, depth) =>
            {
                foreach (var s in doc.Symbols)
                {
                    if (!string.Equals(s.Name, word, StringComparison.Ordinal)) continue;
                    if (memberOnly && s.Container == null) continue;
                    hits.Add(new ShaderDefinition { File = file, Symbol = s, Depth = depth });
                }
                // 当前文件里找到顶层定义就够了，不再往 include 里翻
                return depth == 0 && hits.Count > 0;
            });
            hits.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : Rank(a.Symbol).CompareTo(Rank(b.Symbol)));
            return hits;
        }

        /// <summary>include 链上所有文件的声明（补全用），每个名字只留离当前文件最近的那个。</summary>
        public static List<ShaderDefinition> All(string currentFile, HlslDocument current, ShaderDocumentCache cache, IShaderFileSource source,
                                                 Func<string, string, string> resolve, CancellationToken ct, int maxSymbols = 4000)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<ShaderDefinition>();
            Walk(currentFile, current, cache, source, resolve, ct, (file, doc, depth) =>
            {
                foreach (var s in doc.Symbols)
                {
                    if (s.Container != null || !seen.Add(s.Name)) continue;
                    list.Add(new ShaderDefinition { File = file, Symbol = s, Depth = depth });
                    if (list.Count >= maxSymbols) return true;
                }
                return false;
            });
            return list;
        }

        /// <summary>广度优先遍历 include 链；<paramref name="visit"/> 返回 true 时停。</summary>
        public static int Walk(string currentFile, HlslDocument current, ShaderDocumentCache cache, IShaderFileSource source,
                               Func<string, string, string> resolve, CancellationToken ct, Func<string, HlslDocument, int, bool> visit)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string File, HlslDocument Doc, int Depth)>();
            if (current == null && currentFile != null) current = cache?.Get(currentFile, source);
            if (current == null) return 0;
            if (currentFile != null) visited.Add(currentFile);
            queue.Enqueue((currentFile, current, 0));
            int files = 0;
            while (queue.Count > 0 && files < MaxFiles)
            {
                ct.ThrowIfCancellationRequested();
                var (file, doc, depth) = queue.Dequeue();
                files++;
                if (visit(file, doc, depth)) break;
                if (resolve == null || cache == null) continue;
                foreach (var inc in doc.Includes)
                {
                    string path = resolve(inc.Path, file);
                    if (path == null || !visited.Add(path)) continue;
                    var child = cache.Get(path, source);
                    if (child != null) queue.Enqueue((path, child, depth + 1));
                }
            }
            return files;
        }

        private static int Rank(HlslSymbol s)
        {
            switch (s.Kind)
            {
                case HlslSymbolKind.Function: return 0;
                case HlslSymbolKind.Struct: return 1;
                case HlslSymbolKind.Macro: return 2;
                case HlslSymbolKind.Typedef: return 3;
                case HlslSymbolKind.CBuffer: return 4;
                default: return 5;
            }
        }
    }
}
