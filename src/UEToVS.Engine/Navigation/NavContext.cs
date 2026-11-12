using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Index;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Syntax;

namespace UEToVS.Engine.Navigation
{
    /// <summary>
    /// Per-request view of the index for resolution: memoised name / type / base lookups (thread-safe, so a references
    /// scan can share it across files) with an "overlay" document whose declarations replace the index's copy of that
    /// file (the editor buffer may be unsaved).
    /// </summary>
    public sealed class NavContext
    {
        private readonly ISymbolIndexBackend _backend;
        private readonly FileTable _files;
        private readonly ConcurrentDictionary<string, List<SymbolInfo>> _byName = new ConcurrentDictionary<string, List<SymbolInfo>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TypeEntry> _types = new ConcurrentDictionary<string, TypeEntry>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<TypeEntry>> _derived = new ConcurrentDictionary<string, List<TypeEntry>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<SymbolInfo>> _members = new ConcurrentDictionary<string, List<SymbolInfo>>(StringComparer.Ordinal);
        private readonly string _overlayKey;
        private int _indexQueries;

        public NavContext(ISymbolIndexBackend backend, FileTable files, DocumentModel overlay, CancellationToken ct)
        {
            _backend = backend;
            _files = files;
            Overlay = overlay;
            _overlayKey = overlay != null ? NormalizePath(overlay.Path) : null;
            Ct = ct;
        }

        public DocumentModel Overlay { get; }
        public CancellationToken Ct { get; }
        public int IndexQueries => Volatile.Read(ref _indexQueries);
        public ISymbolIndexBackend Backend => _backend;
        public FileTable Files => _files;

        public static string NormalizePath(string p) => (p ?? string.Empty).Replace('\\', '/').ToLowerInvariant();

        public bool IsOverlayPath(string path) => _overlayKey != null && NormalizePath(path) == _overlayKey;

        // ------------------------------------------------------------------ symbols

        /// <summary>Every declaration named <paramref name="name"/> (index + overlay document). Do not mutate the list.</summary>
        public List<SymbolInfo> ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return new List<SymbolInfo>();
            return _byName.GetOrAdd(name, n =>
            {
                Ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _indexQueries);
                var list = new List<SymbolInfo>();
                foreach (var s in _backend.FindSymbols(n, null))
                {
                    if (_overlayKey != null && IsOverlayPath(s.Location?.FilePath)) continue;
                    list.Add(s);
                }
                if (Overlay != null)
                {
                    for (int i = 0; i < Overlay.Decls.Count; i++)
                        if (string.Equals(Overlay.Decls[i].Name, n, StringComparison.Ordinal)) list.Add(ToSymbol(Overlay, i));
                }
                return list;
            });
        }

        public SymbolInfo ToSymbol(DocumentModel doc, int declIndex)
        {
            var d = doc.Decls[declIndex];
            var (module, origin) = FileOrigin(doc.Path);
            return new SymbolInfo
            {
                Name = d.Name,
                Container = string.IsNullOrEmpty(d.Container) ? null : d.Container,
                Kind = d.Kind,
                Ue = d.Ue,
                Specifiers = string.IsNullOrEmpty(d.Specifiers) ? null : d.Specifiers,
                Signature = d.Signature,
                IsDefinition = d.IsDefinition,
                Module = module,
                Origin = origin,
                Location = new SourceLocation { FilePath = doc.Path, Line = d.Line, Column = d.Column },
            };
        }

        public SymbolInfo LocalSymbol(DocumentModel doc, LocalVariable l)
        {
            var (line, col) = doc.PositionOf(l.Offset);
            var fn = doc.Decls[l.Function];
            var (module, origin) = FileOrigin(doc.Path);
            return new SymbolInfo
            {
                Name = l.Name,
                Container = fn.QualifiedName,
                Kind = SymbolKind.Variable,
                Signature = (l.Type + " " + l.Name).Trim(),
                IsDefinition = true,
                Module = module,
                Origin = origin,
                Location = new SourceLocation { FilePath = doc.Path, Line = line, Column = col },
            };
        }

        public (string Module, ItemOrigin Origin) FileOrigin(string path)
        {
            if (_files != null && !string.IsNullOrEmpty(path) && _files.TryGet(path, out var e)) return (e.File.Module, e.File.Origin);
            return (null, ItemOrigin.Project);
        }

        // ------------------------------------------------------------------ types and bases

        /// <summary>
        /// Best type entry for a (possibly qualified, possibly templated) type name: overlay document first, then the index
        /// (exact container, project items, entries with bases preferred). Null when unknown.
        /// </summary>
        public TypeEntry FindType(string typeName)
        {
            string name = BaseNames.StripTemplateArgs(typeName);
            if (string.IsNullOrEmpty(name)) return null;
            return _types.GetOrAdd(name, n =>
            {
                Ct.ThrowIfCancellationRequested();
                string simple = TypeText.LastSegment(n);
                string qual = TypeText.Parent(n);
                if (Overlay != null)
                {
                    for (int i = 0; i < Overlay.Decls.Count; i++)
                    {
                        var d = Overlay.Decls[i];
                        if (!TypeText.IsRecordKind(d.Kind) || d.IsImplicit || d.Name != simple) continue;
                        if (qual != null && !TypeText.ContainerMatches(d.Container, qual)) continue;
                        return new TypeEntry { Symbol = ToSymbol(Overlay, i), Bases = d.Bases ?? Array.Empty<string>() };
                    }
                }
                Interlocked.Increment(ref _indexQueries);
                var list = _backend.FindTypes(simple, qual);
                TypeEntry best = null;
                int bestScore = int.MinValue;
                foreach (var t in list)
                {
                    if (IsOverlayPath(t.Symbol.Location?.FilePath)) continue;
                    int score = 0;
                    if (qual != null && string.Equals(t.Symbol.Container, qual, StringComparison.Ordinal)) score += 8;
                    if (qual == null && string.IsNullOrEmpty(t.Symbol.Container)) score += 4;
                    if (t.Bases.Length > 0) score += 2;
                    if (TypeText.IsProject(t.Symbol.Origin)) score += 1;
                    if (t.Symbol.IsDefinition) score += 1;
                    if (score > bestScore)
                    {
                        best = t;
                        bestScore = score;
                    }
                }
                return best;
            });
        }

        /// <summary>Base class names (template arguments stripped) of a type; empty when unknown.</summary>
        public IReadOnlyList<string> BasesOf(string typeName)
        {
            var t = FindType(typeName);
            if (t == null || t.Bases.Length == 0) return Array.Empty<string>();
            var list = new List<string>(t.Bases.Length);
            foreach (var b in t.Bases)
            {
                string s = BaseNames.StripTemplateArgs(b);
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }

        public string FirstBase(string typeName)
        {
            var b = BasesOf(typeName);
            return b.Count > 0 ? b[0] : null;
        }

        /// <summary>Types deriving directly from <paramref name="type"/> (checked against the full base text when it is qualified).</summary>
        public List<TypeEntry> DerivedOf(TypeEntry type)
        {
            if (type?.Symbol == null) return new List<TypeEntry>();
            string qualified = TypeText.Qualified(type.Symbol.Container, type.Symbol.Name);
            return _derived.GetOrAdd(qualified, q =>
            {
                Ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _indexQueries);
                var result = new List<TypeEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                IEnumerable<TypeEntry> source = _backend.FindDerivedTypes(type.Symbol.Name).Where(t => !IsOverlayPath(t.Symbol.Location?.FilePath));
                if (Overlay != null)
                {
                    var extra = new List<TypeEntry>();
                    for (int i = 0; i < Overlay.Decls.Count; i++)
                    {
                        var d = Overlay.Decls[i];
                        if (!TypeText.IsRecordKind(d.Kind) || d.Bases == null || d.IsImplicit) continue;
                        if (d.Bases.Any(b => BaseNames.Simple(b) == type.Symbol.Name)) extra.Add(new TypeEntry { Symbol = ToSymbol(Overlay, i), Bases = d.Bases });
                    }
                    source = source.Concat(extra);
                }
                foreach (var t in source)
                {
                    bool matches = false;
                    foreach (var b in t.Bases)
                    {
                        string stripped = BaseNames.StripTemplateArgs(b);
                        if (BaseNames.Simple(b) != type.Symbol.Name) continue;
                        // "Detail::FBase" must match "Game::Detail::FBase"; an unqualified base matches any namespace
                        if (!stripped.Contains("::") || TypeText.ContainerMatches(q, stripped) || TypeText.ContainerMatches(stripped, q)) matches = true;
                    }
                    string key = TypeText.Qualified(t.Symbol.Container, t.Symbol.Name);
                    if (matches && !string.Equals(key, q, StringComparison.Ordinal) && seen.Add(key + "@" + t.Symbol.Location?.FilePath)) result.Add(t);
                }
                result.Sort((a, b) => string.CompareOrdinal(a.Symbol.Name, b.Symbol.Name));
                return result;
            });
        }

        // ------------------------------------------------------------------ member lookup

        /// <summary>Whether symbol container <paramref name="container"/> denotes type/namespace <paramref name="scope"/> (either may be less qualified).</summary>
        public static bool SameScope(string container, string scope) =>
            TypeText.ContainerMatches(container, scope) || (!string.IsNullOrEmpty(container) && TypeText.ContainerMatches(scope, container));

        /// <summary>
        /// Members named <paramref name="name"/> of <paramref name="type"/>, searching its bases breadth-first and stopping
        /// at the first level that has any (the most derived declaration hides the base ones). <paramref name="depth"/> is
        /// that level (0 = the type itself).
        /// </summary>
        public List<SymbolInfo> MemberLookup(string name, string type, out int depth)
        {
            depth = -1;
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(name)) return new List<SymbolInfo>();
            var all = ByName(name);
            var level = new List<string> { BaseNames.StripTemplateArgs(type) };
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int d = 0; d < 16 && level.Count > 0; d++)
            {
                Ct.ThrowIfCancellationRequested();
                var hits = new List<SymbolInfo>();
                foreach (var c in level)
                    foreach (var s in all)
                        if (SameScope(s.Container, c)) hits.Add(s);
                if (hits.Count > 0)
                {
                    depth = d;
                    return hits;
                }
                var next = new List<string>();
                foreach (var c in level)
                {
                    if (!visited.Add(c)) continue;
                    foreach (var b in BasesOf(c)) if (!visited.Contains(b)) next.Add(b);
                }
                level = next;
            }
            return new List<SymbolInfo>();
        }

        /// <summary>All members (incl. inherited) of a type in base → derived order, cached; used by GetMembers.</summary>
        public List<SymbolInfo> CachedMembers(string key, Func<List<SymbolInfo>> build) => _members.GetOrAdd(key, _ => build());

        // ------------------------------------------------------------------ includes

        /// <summary>Resolves <c>#include "X/Y.h"</c> against the file table: same directory first, then project files, shortest path.</summary>
        public string ResolveInclude(string includePath, string fromFile)
        {
            if (string.IsNullOrEmpty(includePath)) return null;
            string inc = includePath.Replace('\\', '/').TrimStart('.', '/');
            if (inc.Length == 0) return null;
            if (!string.IsNullOrEmpty(fromFile))
            {
                try
                {
                    string dir = Path.GetDirectoryName(fromFile);
                    if (dir != null)
                    {
                        string local = Path.GetFullPath(Path.Combine(dir, includePath));
                        if (File.Exists(local)) return local;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException || ex is UnauthorizedAccessException)
                {
                }
            }
            if (_files == null) return null;
            string suffix = "/" + inc.ToLowerInvariant();
            string fileName = Path.GetFileName(inc);
            string best = null;
            int bestScore = int.MinValue;
            string fromDir = fromFile != null ? NormalizePath(Path.GetDirectoryName(fromFile)) : null;
            foreach (var e in _files.Snapshot())
            {
                var f = e.File;
                if (!string.Equals(f.Name ?? Path.GetFileName(f.Path), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                string norm = NormalizePath(f.Path);
                if (!norm.EndsWith(suffix, StringComparison.Ordinal) && norm != suffix.Substring(1)) continue;
                int score = 0;
                if (fromDir != null && norm.StartsWith(fromDir + "/", StringComparison.Ordinal)) score += 100;
                if (TypeText.IsProject(f.Origin)) score += 50;
                score -= norm.Length / 8;
                if (score > bestScore)
                {
                    best = f.Path;
                    bestScore = score;
                }
            }
            return best;
        }
    }
}
