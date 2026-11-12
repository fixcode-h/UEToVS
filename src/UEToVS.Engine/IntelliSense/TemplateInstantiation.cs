using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Index;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>One template parameter: <c>typename T = int</c> → (T, int), <c>class... Ts</c> → (Ts, pack).</summary>
    public sealed class TemplateParam
    {
        public TemplateParam(string name, string defaultText, bool isPack)
        {
            Name = name;
            Default = defaultText;
            IsPack = isPack;
        }

        public string Name { get; }
        public string Default { get; }
        public bool IsPack { get; }
        public override string ToString() => Name + (IsPack ? "..." : string.Empty) + (Default != null ? " = " + Default : string.Empty);
    }

    /// <summary>Pure text helpers for template parameter lists and argument substitution (no index access).</summary>
    public static class TemplateText
    {
        /// <summary>Parameters of a template parameter list text (without the angle brackets), in order. Unnamed ones get null names.</summary>
        public static List<TemplateParam> ParseParameters(string tpl)
        {
            var list = new List<TemplateParam>();
            if (string.IsNullOrWhiteSpace(tpl)) return list;
            foreach (var raw in SplitTopLevel(tpl, ','))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                string def = null;
                int eq = IndexOfTopLevel(p, '=');
                if (eq >= 0)
                {
                    def = p.Substring(eq + 1).Trim();
                    p = p.Substring(0, eq).Trim();
                }
                bool pack = p.Contains("...");
                p = p.Replace("...", " ").Trim();
                // the name is the trailing identifier after a type / typename / class keyword: "typename T", "int32 N", "ESPMode Mode"
                int end = p.Length;
                int s = end;
                while (s > 0 && TypeText.IsIdentPart(p[s - 1])) s--;
                string name = s < end ? p.Substring(s, end - s) : null;
                string before = p.Substring(0, s).Trim();
                // a single word is an unnamed parameter ("typename", "int32")
                if (name != null && (before.Length == 0 || IsKeyword(name))) name = null;
                list.Add(new TemplateParam(name, string.IsNullOrEmpty(def) ? null : def, pack));
            }
            return list;
        }

        private static bool IsKeyword(string s) => s == "typename" || s == "class" || s == "struct" || s == "auto";

        /// <summary>
        /// Replaces whole-identifier occurrences of the map's keys (template parameter names) in <paramref name="text"/>.
        /// Identifiers after <c>::</c>, <c>.</c> or <c>-&gt;</c> are member names, not parameters, and are kept.
        /// </summary>
        public static string Substitute(string text, IReadOnlyDictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(text) || map == null || map.Count == 0) return text;
            StringBuilder sb = null;
            int i = 0, copied = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (!TypeText.IsIdentStart(c) || (i > 0 && TypeText.IsIdentPart(text[i - 1])))
                {
                    i++;
                    continue;
                }
                int j = i;
                while (j < text.Length && TypeText.IsIdentPart(text[j])) j++;
                string word = text.Substring(i, j - i);
                if (map.TryGetValue(word, out var repl) && !AfterMemberAccess(text, i))
                {
                    sb ??= new StringBuilder(text.Length + 16);
                    sb.Append(text, copied, i - copied);
                    sb.Append(repl);
                    copied = j;
                }
                i = j;
            }
            if (sb == null) return text;
            sb.Append(text, copied, text.Length - copied);
            return Normalize(sb.ToString());
        }

        /// <summary>Whether any of <paramref name="names"/> occurs as a whole identifier.</summary>
        public static bool Mentions(string text, IReadOnlyCollection<string> names)
        {
            if (string.IsNullOrEmpty(text) || names == null || names.Count == 0) return false;
            int i = 0;
            while (i < text.Length)
            {
                if (!TypeText.IsIdentStart(text[i]) || (i > 0 && TypeText.IsIdentPart(text[i - 1]))) { i++; continue; }
                int j = i;
                while (j < text.Length && TypeText.IsIdentPart(text[j])) j++;
                string w = text.Substring(i, j - i);
                foreach (var n in names) if (n == w) return true;
                i = j;
            }
            return false;
        }

        private static bool AfterMemberAccess(string text, int i)
        {
            int k = i - 1;
            while (k >= 0 && text[k] == ' ') k--;
            if (k >= 1 && text[k] == ':' && text[k - 1] == ':') return true;
            if (k >= 0 && text[k] == '.') return true;
            return k >= 1 && text[k] == '>' && text[k - 1] == '-';
        }

        /// <summary>Tidies a substituted type: <c>UFoo* *</c> → <c>UFoo**</c>, <c>const const</c> collapsed, "&gt;&gt;" kept apart.</summary>
        public static string Normalize(string t)
        {
            if (t == null) return null;
            t = t.Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            t = t.Replace(" *", "*").Replace(" &", "&").Replace("const const ", "const ");
            return t;
        }

        public static List<string> SplitTopLevel(string s, char sep)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth--;
                else if (c == sep && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        private static int IndexOfTopLevel(string s, char ch)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth--;
                else if (c == ch && depth == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// Splits a dependent member type <c>typename TBox&lt;UFoo&gt;::ValueType*</c> into (<c>TBox&lt;UFoo&gt;</c>, <c>ValueType</c>, <c>*</c>);
        /// false when the text has no <c>X&lt;…&gt;::Name</c> shape.
        /// </summary>
        public static bool SplitDependentMember(string text, out string owner, out string member, out string suffix)
        {
            owner = member = suffix = null;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();
            if (t.StartsWith("typename ", StringComparison.Ordinal)) t = t.Substring(9).TrimStart();
            if (t.StartsWith("const ", StringComparison.Ordinal)) t = t.Substring(6).TrimStart();
            int depth = 0, lastGt = -1;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '<') depth++;
                else if (c == '>') { if (--depth == 0) lastGt = i; }
                else if (c == ':' && depth == 0 && i + 1 < t.Length && t[i + 1] == ':' && lastGt >= 0 && lastGt == i - 1)
                {
                    owner = t.Substring(0, i).Trim();
                    int j = i + 2;
                    while (j < t.Length && TypeText.IsIdentPart(t[j])) j++;
                    member = t.Substring(i + 2, j - i - 2);
                    suffix = t.Substring(j).Trim();
                    return member.Length > 0;
                }
            }
            return false;
        }
    }

    /// <summary>Template parameter → argument map of one instantiation (<c>TBox&lt;UFoo&gt;</c> → {T: UFoo}).</summary>
    public sealed class TemplateBinding
    {
        internal TemplateBinding(string template, Dictionary<string, string> map, DocumentModel source, string sourcePath)
        {
            Template = template;
            Map = map;
            Source = source;
            SourcePath = sourcePath;
        }

        public string Template { get; }
        public IReadOnlyDictionary<string, string> Map { get; }
        internal DocumentModel Source { get; }
        internal string SourcePath { get; }

        public static readonly TemplateBinding Empty = new TemplateBinding(null, new Dictionary<string, string>(StringComparer.Ordinal), null, null);
        public bool IsEmpty => Map.Count == 0;
        public string Apply(string text) => TemplateText.Substitute(text, Map);
        public override string ToString() => Template + "{" + string.Join(", ", EnumerateMap()) + "}";

        private IEnumerable<string> EnumerateMap()
        {
            foreach (var kv in Map) yield return kv.Key + "=" + kv.Value;
        }
    }

    /// <summary>
    /// Bounded LRU of <see cref="TemplateBinding"/>s keyed by (template, arguments). Capacity is a setting; hits, misses and
    /// evictions are counted and logged (<c>intellisense.templates</c>) at most once a minute while there is traffic.
    /// An entry is valid while the template's declaring document is still the cached parse of its file. Thread-safe.
    /// </summary>
    public sealed class TemplateInstanceCache
    {
        private static readonly Logger L = Log.For("IS");
        private readonly object _gate = new object();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, TemplateBinding>>> _map =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, TemplateBinding>>>(StringComparer.Ordinal);
        private readonly LinkedList<KeyValuePair<string, TemplateBinding>> _lru = new LinkedList<KeyValuePair<string, TemplateBinding>>();
        private long _hits, _misses, _evictions, _stale, _depthLimited;
        private long _lastLogTicks = DateTime.UtcNow.Ticks;
        private long _loggedLookups;
        private int _capacity;

        public TemplateInstanceCache(int capacity = DefaultCapacity) => _capacity = Clamp(capacity);

        public const int DefaultCapacity = 2048;
        public const int MinCapacity = 64;
        public const int MaxCapacity = 65536;

        /// <summary>Interval between <c>intellisense.templates</c> log lines (only when there were lookups).</summary>
        public TimeSpan LogInterval { get; set; } = TimeSpan.FromSeconds(60);

        private static int Clamp(int c) => Math.Max(MinCapacity, Math.Min(MaxCapacity, c <= 0 ? DefaultCapacity : c));

        public int Capacity
        {
            get { lock (_gate) return _capacity; }
            set
            {
                lock (_gate)
                {
                    _capacity = Clamp(value);
                    Trim();
                }
            }
        }

        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        public long Hits => Interlocked.Read(ref _hits);
        public long Misses => Interlocked.Read(ref _misses);
        public long Evictions => Interlocked.Read(ref _evictions);
        public long DepthLimited => Interlocked.Read(ref _depthLimited);

        public double HitRate
        {
            get
            {
                long h = Hits, m = Misses;
                return h + m == 0 ? 0 : Math.Round(100.0 * h / (h + m), 1);
            }
        }

        internal void NoteDepthLimit() => Interlocked.Increment(ref _depthLimited);

        internal TemplateBinding Get(string key, Func<DocumentModel, string, bool> valid, Func<TemplateBinding> create)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    var b = node.Value.Value;
                    if (b.Source == null || valid(b.Source, b.SourcePath))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        _hits++;
                        MaybeLog();
                        return b;
                    }
                    _lru.Remove(node);
                    _map.Remove(key);
                    _stale++;
                }
                _misses++;
            }
            var created = create();
            if (created == null) return null;
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _lru.Remove(existing);
                    _map.Remove(key);
                }
                _map[key] = _lru.AddFirst(new KeyValuePair<string, TemplateBinding>(key, created));
                Trim();
                MaybeLog();
            }
            return created;
        }

        private void Trim()
        {
            while (_map.Count > _capacity && _lru.Last != null)
            {
                _map.Remove(_lru.Last.Value.Key);
                _lru.RemoveLast();
                _evictions++;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
                _lru.Clear();
            }
        }

        /// <summary>Called under the lock: one log line per interval while the cache is used.</summary>
        private void MaybeLog()
        {
            long now = DateTime.UtcNow.Ticks;
            long lookups = _hits + _misses;
            if (now - _lastLogTicks < LogInterval.Ticks || lookups == _loggedLookups) return;
            _lastLogTicks = now;
            long delta = lookups - _loggedLookups;
            _loggedLookups = lookups;
            L.Info("intellisense.templates", null, ("hits", _hits), ("misses", _misses), ("hitRate", lookups == 0 ? 0 : Math.Round(100.0 * _hits / lookups, 1)),
                ("lookupsSinceLast", delta), ("entries", _map.Count), ("capacity", _capacity), ("evictions", _evictions), ("stale", _stale),
                ("depthLimited", _depthLimited));
        }

        /// <summary>Stats for the performance dump / tests.</summary>
        public override string ToString() => $"entries={Count} capacity={Capacity} hits={Hits} misses={Misses} hitRate={HitRate}% evictions={Evictions}";
    }

    /// <summary>
    /// Generic template instantiation for IntelliSense (v0.4.5): binds a class template's parameters to the arguments of an
    /// instance (<c>TBox&lt;UFoo&gt;</c>), follows them into base templates, member typedefs / using aliases, alias templates
    /// and function templates with explicit arguments (<c>Cast&lt;UFoo&gt;(x)</c>), and substitutes them into member return /
    /// field types. Every walk is bounded by <see cref="MaxDepth"/>; bindings are cached in a <see cref="TemplateInstanceCache"/>.
    /// Turned off in the settings → <see cref="IsSession.Templates"/> is null and the rule-based inference applies alone.
    /// </summary>
    public sealed class TemplateInstantiator
    {
        public TemplateInstantiator(TemplateInstanceCache cache = null, int maxDepth = 8)
        {
            Cache = cache ?? new TemplateInstanceCache();
            MaxDepth = Math.Max(1, maxDepth);
        }

        public TemplateInstanceCache Cache { get; }
        public int MaxDepth { get; }

        private static bool StillCurrent(IsSession s, DocumentModel doc, string path) => ReferenceEquals(s.DocFor(path), doc);

        /// <summary>Binding of a class template record for <paramref name="args"/>; <see cref="TemplateBinding.Empty"/> when the type is not a template.</summary>
        public TemplateBinding Bind(IsSession s, TypeEntry record, IReadOnlyList<string> args)
        {
            if (record?.Symbol == null || args == null || args.Count == 0) return TemplateBinding.Empty;
            string q = TypeText.Qualified(record.Symbol.Container, record.Symbol.Name);
            string key = q + "<" + string.Join(",", args) + ">";
            return Cache.Get(key, (doc, path) => StillCurrent(s, doc, path), () =>
            {
                var (doc, idx) = s.ClassDecl(record);
                if (doc == null) return TemplateBinding.Empty;
                var decl = doc.Decls[idx];
                var map = MapParameters(decl.TemplateParameters, args);
                return new TemplateBinding(q, map, doc, doc.Path);
            }) ?? TemplateBinding.Empty;
        }

        internal static Dictionary<string, string> MapParameters(string templateParameters, IReadOnlyList<string> args)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var ps = TemplateText.ParseParameters(templateParameters);
            for (int i = 0; i < ps.Count; i++)
            {
                var p = ps[i];
                if (p.Name == null) continue;
                if (p.IsPack)
                {
                    if (i < args.Count) map[p.Name] = string.Join(", ", Slice(args, i));
                    break;
                }
                string a = i < args.Count ? args[i] : p.Default != null ? TemplateText.Substitute(p.Default, map) : null;
                if (a != null) map[p.Name] = a;
            }
            return map;
        }

        private static IEnumerable<string> Slice(IReadOnlyList<string> a, int from)
        {
            for (int i = from; i < a.Count; i++) yield return a[i];
        }

        /// <summary>
        /// Binding of <paramref name="declaringType"/> as reached from <paramref name="root"/>&lt;<paramref name="args"/>&gt;
        /// through its base list (<c>TDerived&lt;T&gt; : TBase&lt;T*&gt;</c>), or null when it cannot be followed within the depth limit.
        /// </summary>
        public TemplateBinding BindDeclaring(IsSession s, TypeEntry root, IReadOnlyList<string> args, string declaringType)
        {
            if (root?.Symbol == null) return null;
            var rootBinding = Bind(s, root, args);
            string rootQ = TypeText.Qualified(root.Symbol.Container, root.Symbol.Name);
            if (string.Equals(rootQ, declaringType, StringComparison.Ordinal)) return rootBinding;
            var level = new List<(TypeEntry Type, TemplateBinding Binding)> { (root, rootBinding) };
            var seen = new HashSet<string>(StringComparer.Ordinal) { rootQ };
            for (int depth = 0; depth < MaxDepth && level.Count > 0; depth++)
            {
                var next = new List<(TypeEntry, TemplateBinding)>();
                foreach (var (t, b) in level)
                {
                    foreach (var baseText in t.Bases)
                    {
                        string concrete = b.Apply(baseText);
                        var shape = TypeShape.Parse(concrete);
                        var be = s.Ctx.FindType(shape.Head);
                        if (be?.Symbol == null) continue;
                        string bq = TypeText.Qualified(be.Symbol.Container, be.Symbol.Name);
                        if (!seen.Add(bq)) continue;
                        var bb = shape.Args.Count > 0 ? Bind(s, be, shape.Args) : TemplateBinding.Empty;
                        if (string.Equals(bq, declaringType, StringComparison.Ordinal)) return bb;
                        next.Add((be, bb));
                    }
                }
                level = next;
            }
            Cache.NoteDepthLimit();
            return null;
        }

        /// <summary>
        /// Type text of member <paramref name="e"/> (return type or field type, as written) on an instance
        /// <paramref name="root"/>&lt;<paramref name="args"/>&gt;: the declaring class's parameters substituted, member typedefs of the
        /// declaring class expanded, function template parameters bound to <paramref name="explicitArgs"/>.
        /// </summary>
        public string MemberType(IsSession s, MemberEntry e, TypeEntry root, IReadOnlyList<string> args, string typeText, IReadOnlyList<string> explicitArgs = null)
        {
            if (string.IsNullOrEmpty(typeText)) return typeText;
            string t = typeText;
            var decl = e.Decl;
            if (explicitArgs != null && explicitArgs.Count > 0 && !string.IsNullOrEmpty(decl.TemplateParameters))
                t = TemplateText.Substitute(t, MapParameters(decl.TemplateParameters, explicitArgs));
            TemplateBinding binding = null;
            if (args != null && args.Count > 0)
            {
                binding = BindDeclaring(s, root, args, e.DeclaringType);
                if (binding != null) t = binding.Apply(t);
            }
            return ExpandMemberAlias(s, e, binding, t, 0);
        }

        /// <summary><c>ValueType</c> declared as <c>using ValueType = T;</c> in the declaring class → its target (bound).</summary>
        private string ExpandMemberAlias(IsSession s, MemberEntry e, TemplateBinding binding, string t, int depth)
        {
            for (; depth < MaxDepth; depth++)
            {
                var shape = TypeShape.Parse(t);
                if (shape.Head == null || shape.Head.Contains("::")) return t;
                Declaration alias = null;
                var doc = e.Doc;
                int cls = doc.Decls[e.Index].Parent;
                if (cls < 0) return t;
                for (int i = cls + 1; i < doc.Decls.Count; i++)
                {
                    var d = doc.Decls[i];
                    if (d.Parent != cls) continue;
                    if (d.Kind == SymbolKind.Typedef && d.Name == shape.Head && !string.IsNullOrEmpty(d.Type)) { alias = d; break; }
                }
                if (alias == null) return t;
                string target = binding != null ? binding.Apply(alias.Type) : alias.Type;
                t = ReplaceHead(t, shape.Head, target);
            }
            Cache.NoteDepthLimit();
            return t;
        }

        /// <summary>Replaces the head word of <paramref name="t"/> (keeping cv / pointer / reference decorations) with <paramref name="target"/>.</summary>
        internal static string ReplaceHead(string t, string head, string target)
        {
            int i = IndexOfWord(t, head);
            if (i < 0) return t;
            int end = i + head.Length;
            // drop the head's own template arguments (alias templates are expanded elsewhere)
            if (end < t.Length && t[end] == '<')
            {
                int d = 0;
                for (int k = end; k < t.Length; k++)
                {
                    if (t[k] == '<') d++;
                    else if (t[k] == '>' && --d == 0) { end = k + 1; break; }
                }
            }
            return TemplateText.Normalize(t.Substring(0, i) + target + t.Substring(end));
        }

        private static int IndexOfWord(string t, string w)
        {
            int i = 0;
            while ((i = t.IndexOf(w, i, StringComparison.Ordinal)) >= 0)
            {
                bool startOk = i == 0 || !TypeText.IsIdentPart(t[i - 1]);
                bool endOk = i + w.Length >= t.Length || !TypeText.IsIdentPart(t[i + w.Length]);
                if (startOk && endOk) return i;
                i += w.Length;
            }
            return -1;
        }

        /// <summary>
        /// Expands aliases at the head of a type text, bounded by the depth limit: <c>FMyPtr</c> (<c>using FMyPtr = TSharedPtr&lt;FFoo&gt;</c>),
        /// alias templates (<c>TPtr&lt;FFoo&gt;</c> with <c>template&lt;class T&gt; using TPtr = TSharedPtr&lt;T&gt;</c>) and dependent member
        /// types (<c>typename TBox&lt;UFoo&gt;::ValueType</c>). Returns the input when nothing applies.
        /// </summary>
        public string ExpandAlias(IsSession s, string typeText)
        {
            if (string.IsNullOrEmpty(typeText)) return typeText;
            string t = typeText;
            for (int depth = 0; depth < MaxDepth; depth++)
            {
                if (TemplateText.SplitDependentMember(t, out string owner, out string member, out string suffix))
                {
                    string resolved = DependentMember(s, owner, member);
                    if (resolved == null) return t;
                    t = TemplateText.Normalize(resolved + (suffix.Length > 0 ? " " + suffix : string.Empty));
                    continue;
                }
                var shape = TypeShape.Parse(t);
                if (shape.Head == null || TypeText.IsBuiltin(shape.Head) || UeBuiltins.ApiOf(shape.Head) != null) return t;
                if (s.Ctx.FindType(shape.Head) != null) return t;
                var alias = FindAlias(s, shape.Head);
                if (alias == null) return t;
                string target = alias.Type;
                if (!string.IsNullOrEmpty(alias.TemplateParameters) && shape.Args.Count > 0)
                    target = TemplateText.Substitute(target, MapParameters(alias.TemplateParameters, shape.Args));
                t = ReplaceHead(t, shape.Head, target);
            }
            Cache.NoteDepthLimit();
            return t;
        }

        private string DependentMember(IsSession s, string owner, string member)
        {
            var os = TypeShape.Parse(ExpandAlias(s, owner));
            var rec = s.FindRecord(os.Head);
            if (rec == null) return null;
            var members = s.MembersOf(rec);
            if (members == null) return null;
            foreach (var e in members.Members)
            {
                var d = e.Decl;
                if (d.Name != member || string.IsNullOrEmpty(d.Type)) continue;
                if (d.Kind != SymbolKind.Typedef) continue;
                return MemberType(s, e, rec, os.Args, d.Type);
            }
            return null;
        }

        /// <summary>The typedef / using declaration named <paramref name="name"/> (possibly qualified), from its declaring document.</summary>
        private static Declaration FindAlias(IsSession s, string name)
        {
            string last = TypeText.LastSegment(name);
            string qual = TypeText.Parent(name);
            foreach (var sym in s.Ctx.ByName(last))
            {
                if (sym.Kind != SymbolKind.Typedef) continue;
                if (qual != null && !TypeText.ContainerMatches(sym.Container, qual)) continue;
                var (d, i) = s.DeclOf(sym);
                if (d != null && !string.IsNullOrEmpty(d.Decls[i].Type)) return d.Decls[i];
            }
            return null;
        }

        /// <summary>
        /// Return type of a call of function (template) <paramref name="fn"/> with explicit template arguments: the
        /// function's template parameters bound in order, defaults applied. Null when the return type does not depend on them.
        /// </summary>
        public static string FunctionReturn(Declaration fn, IReadOnlyList<string> explicitArgs)
        {
            if (fn == null || string.IsNullOrEmpty(fn.Type) || string.IsNullOrEmpty(fn.TemplateParameters) || explicitArgs == null || explicitArgs.Count == 0) return null;
            var map = MapParameters(fn.TemplateParameters, explicitArgs);
            if (map.Count == 0 || !TemplateText.Mentions(fn.Type, map.Keys)) return null;
            return TemplateText.Substitute(fn.Type, map);
        }
    }
}
