using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Index;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.IntelliSense
{
    /// <summary>A member of a type as seen from a derived type: the declaring document and declaration, and the inheritance depth.</summary>
    public sealed class MemberEntry
    {
        public DocumentModel Doc { get; set; }
        public int Index { get; set; }

        /// <summary>Qualified name of the declaring type.</summary>
        public string DeclaringType { get; set; }

        /// <summary>0 = the type itself, 1 = direct base...</summary>
        public int Depth { get; set; }

        public Declaration Decl => Doc.Decls[Index];
        public override string ToString() => DeclaringType + "::" + Decl.Name + " d" + Depth;
    }

    /// <summary>Members of a type and its bases (most derived declaration first; a re-declared member hides the base one).</summary>
    public sealed class TypeMembers
    {
        public TypeEntry Root { get; set; }
        public string QualifiedName { get; set; }
        public List<MemberEntry> Members { get; } = new List<MemberEntry>();

        /// <summary>Qualified names of the type and all of its bases (resolved ones).</summary>
        public HashSet<string> Lineage { get; } = new HashSet<string>(StringComparer.Ordinal);

        internal List<(string Path, DocumentModel Doc)> Sources { get; } = new List<(string, DocumentModel)>();
        internal bool UsesOverlay { get; set; }
    }

    /// <summary>
    /// Shared, bounded cache of <see cref="TypeMembers"/> keyed by qualified type name. An entry is valid while every
    /// document it was built from is still the cached parse of that file (a save re-parses and invalidates it).
    /// </summary>
    public sealed class MemberTableCache
    {
        private readonly ConcurrentDictionary<string, TypeMembers> _map = new ConcurrentDictionary<string, TypeMembers>(StringComparer.Ordinal);
        private readonly int _max;
        private long _hits, _misses;

        public MemberTableCache(int max = 256) => _max = Math.Max(8, max);

        public long Hits => Interlocked.Read(ref _hits);
        public long Misses => Interlocked.Read(ref _misses);
        public int Count => _map.Count;

        internal bool TryGet(string key, DocumentCache docs, out TypeMembers members)
        {
            if (_map.TryGetValue(key, out members))
            {
                foreach (var (path, doc) in members.Sources)
                {
                    if (!ReferenceEquals(docs.GetFile(path), doc))
                    {
                        _map.TryRemove(key, out _);
                        members = null;
                        Interlocked.Increment(ref _misses);
                        return false;
                    }
                }
                Interlocked.Increment(ref _hits);
                return true;
            }
            Interlocked.Increment(ref _misses);
            return false;
        }

        internal void Add(string key, TypeMembers members)
        {
            if (members.UsesOverlay) return;
            if (_map.Count >= _max) _map.Clear();
            _map[key] = members;
        }

        public void Clear() => _map.Clear();
    }

    /// <summary>
    /// Per-request semantic view used by every IntelliSense feature: the <see cref="NavContext"/> (memoised index lookups +
    /// the editor buffer as overlay), access to declaring documents and member tables.
    /// </summary>
    public sealed class IsSession
    {
        public IsSession(NavContext ctx, DocumentModel doc, DocumentCache docs, MemberTableCache members, UeMacroKnowledge knowledge)
        {
            Ctx = ctx;
            Doc = doc;
            Docs = docs;
            MemberCache = members;
            Knowledge = knowledge ?? UeMacroKnowledge.Default;
        }

        public NavContext Ctx { get; }
        public DocumentModel Doc { get; }
        public DocumentCache Docs { get; }
        public MemberTableCache MemberCache { get; }
        public UeMacroKnowledge Knowledge { get; }
        public CancellationToken Ct => Ctx.Ct;

        private readonly Dictionary<string, TypeMembers> _local = new Dictionary<string, TypeMembers>(StringComparer.Ordinal);

        /// <summary>The overlay document for its own path, otherwise the cached parse of the file on disk.</summary>
        public DocumentModel DocFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Ctx.IsOverlayPath(path)) return Doc;
            return Docs.GetFile(path);
        }

        /// <summary>The declaration behind an index symbol (same file, line and column), or (null, -1).</summary>
        public (DocumentModel Doc, int Index) DeclOf(SymbolInfo s)
        {
            if (s?.Location == null) return (null, -1);
            var d = DocFor(s.Location.FilePath);
            if (d == null) return (null, -1);
            int fallback = -1;
            for (int i = 0; i < d.Decls.Count; i++)
            {
                var x = d.Decls[i];
                if (x.Name != s.Name) continue;
                if (x.Line == s.Location.Line && x.Column == s.Location.Column) return (d, i);
                if (x.Line == s.Location.Line && fallback < 0) fallback = i;
            }
            return (fallback >= 0 ? d : null, fallback);
        }

        /// <summary>Index of the class / struct body declaration of a type entry in its document.</summary>
        public (DocumentModel Doc, int Index) ClassDecl(TypeEntry t)
        {
            var doc = DocFor(t?.Symbol?.Location?.FilePath);
            if (doc == null) return (null, -1);
            string q = TypeText.Qualified(t.Symbol.Container, t.Symbol.Name);
            int found = -1;
            for (int i = 0; i < doc.Decls.Count; i++)
            {
                var d = doc.Decls[i];
                if (!TypeText.IsRecordKind(d.Kind) || d.Name != t.Symbol.Name || d.Body.IsNone || d.QualifiedName != q) continue;
                found = i;
                if (d.Line == t.Symbol.Location.Line) break;
            }
            return (found >= 0 ? doc : null, found);
        }

        /// <summary>Member key: overloads with different parameters are different members; a derived re-declaration hides the base one.</summary>
        public static string MemberKey(Declaration d)
        {
            if (TypeText.IsCallableKind(d.Kind))
            {
                string pk = TypeText.ParameterKey("(" + (d.Parameters ?? string.Empty) + ")");
                if (d.Kind == SymbolKind.Constructor || d.Kind == SymbolKind.Destructor) return "c:" + d.Name + "/" + pk;
                return "f:" + d.Name + "/" + pk + ((d.Modifiers & DeclModifiers.Const) != 0 ? "c" : string.Empty);
            }
            return "v:" + d.Name;
        }

        /// <summary>Record type for a (possibly qualified / templated / aliased) type name, or null.</summary>
        public TypeEntry FindRecord(string typeName, int offset = -1)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            var t = Ctx.FindType(typeName);
            if (t != null) return t;
            // relative to the enclosing namespaces of the caret
            if (offset >= 0 && !typeName.Contains("::"))
            {
                for (string ns = Doc.EnclosingNamespace(offset); !string.IsNullOrEmpty(ns); ns = TypeText.Parent(ns))
                {
                    t = Ctx.FindType(ns + "::" + typeName);
                    if (t != null) return t;
                }
            }
            string alias = ResolveAlias(typeName);
            return alias != null && alias != typeName ? Ctx.FindType(alias) : null;
        }

        /// <summary>Target type text of a typedef / using alias named <paramref name="name"/> (one level), or null.</summary>
        public string ResolveAlias(string name)
        {
            string simple = BaseNames.StripTemplateArgs(name);
            if (string.IsNullOrEmpty(simple)) return null;
            string last = TypeText.LastSegment(simple);
            foreach (var s in Ctx.ByName(last))
            {
                if (s.Kind != SymbolKind.Typedef) continue;
                string qual = TypeText.Parent(simple);
                if (qual != null && !TypeText.ContainerMatches(s.Container, qual)) continue;
                var (d, i) = DeclOf(s);
                if (d != null && !string.IsNullOrEmpty(d.Decls[i].Type)) return d.Decls[i].Type;
                string sig = s.Signature ?? string.Empty;
                if (sig.StartsWith("typedef ", StringComparison.Ordinal)) return TypeText.TypeFromSignature(sig.Substring(8), s.Name);
                int eq = sig.IndexOf('=');
                if (sig.StartsWith("using ", StringComparison.Ordinal) && eq > 0) return sig.Substring(eq + 1).Trim();
            }
            return null;
        }

        /// <summary>Members of a type and its bases (cached across requests).</summary>
        public TypeMembers MembersOf(TypeEntry root)
        {
            if (root?.Symbol == null) return null;
            string key = TypeText.Qualified(root.Symbol.Container, root.Symbol.Name);
            if (_local.TryGetValue(key, out var local)) return local;
            if (MemberCache != null && MemberCache.TryGet(key, Docs, out var cached))
            {
                _local[key] = cached;
                return cached;
            }
            var result = new TypeMembers { Root = root, QualifiedName = key };
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var level = new List<TypeEntry> { root };
            for (int depth = 0; depth < 24 && level.Count > 0; depth++)
            {
                var next = new List<TypeEntry>();
                foreach (var t in level)
                {
                    Ct.ThrowIfCancellationRequested();
                    string q = TypeText.Qualified(t.Symbol.Container, t.Symbol.Name);
                    if (!result.Lineage.Add(q)) continue;
                    var (doc, cls) = ClassDecl(t);
                    if (doc != null)
                    {
                        if (ReferenceEquals(doc, Doc) && Ctx.IsOverlayPath(doc.Path)) result.UsesOverlay = true;
                        else result.Sources.Add((doc.Path, doc));
                        for (int i = cls + 1; i < doc.Decls.Count; i++)
                        {
                            var d = doc.Decls[i];
                            if (d.Parent != cls || string.IsNullOrEmpty(d.Name)) continue;
                            if (keys.Add(MemberKey(d))) result.Members.Add(new MemberEntry { Doc = doc, Index = i, DeclaringType = q, Depth = depth });
                        }
                    }
                    foreach (var b in t.Bases)
                    {
                        var be = Ctx.FindType(b);
                        if (be != null) next.Add(be);
                    }
                }
                level = next;
            }
            _local[key] = result;
            MemberCache?.Add(key, result);
            return result;
        }

        /// <summary>Whether <paramref name="derived"/> is <paramref name="type"/> or (transitively) derives from it.</summary>
        public bool IsSameOrDerived(string derived, string type)
        {
            if (string.IsNullOrEmpty(derived) || string.IsNullOrEmpty(type)) return false;
            var t = FindRecord(derived);
            if (t == null) return NavContext.SameScope(derived, type);
            var m = MembersOf(t);
            foreach (var q in m.Lineage)
                if (NavContext.SameScope(q, type)) return true;
            return false;
        }
    }
}
