using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Navigation;

namespace UEToVS.Engine.Includes
{
    /// <summary>The headers one file sees through its #include lines (transitively).</summary>
    public sealed class IncludeClosure
    {
        /// <summary>Normalized paths (<see cref="NavContext.NormalizePath"/>) of every reached file, the root included.</summary>
        public HashSet<string> Files { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Reached file → the direct include text of the root it came through.</summary>
        public Dictionary<string, string> Via { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The walk stopped at <see cref="IncludeGraph.MaxFiles"/>: absence from <see cref="Files"/> proves nothing.</summary>
        public bool Capped { get; set; }

        /// <summary>Include lines that resolved to nothing (macro includes, system headers, missing files).</summary>
        public int Unresolved { get; set; }

        /// <summary>Files reached whose include list is unknown (not indexed yet): absence proves less.</summary>
        public int UnknownLists { get; set; }

        public double ElapsedMs { get; set; }

        public bool Contains(string path) => path != null && Files.Contains(NavContext.NormalizePath(path));
    }

    /// <summary>
    /// v0.11.0 include graph (design: 后续规划-代码诊断与提示 §4.4): walks the include lists the index stores per file, resolving
    /// each line with the <see cref="IncludeModel"/> from the including file. Resolutions are cached per (module, include text)
    /// — the same text resolves the same way for every file of a module, except next-to-the-file includes, checked first —
    /// and the closures of the last few roots are kept. Bounded: <see cref="MaxFiles"/> files per closure,
    /// <see cref="MaxResolutions"/> cached resolutions (dropped wholesale beyond that), <see cref="MaxClosures"/> closures.
    /// </summary>
    public sealed class IncludeGraph
    {
        private static readonly Logger L = Log.For("Includes");

        public const int MaxFiles = 20000;
        public const int MaxResolutions = 200000;
        public const int MaxClosures = 16;

        private readonly IncludeModel _model;
        private readonly ConcurrentDictionary<string, string> _resolved = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, object Source, IncludeClosure Closure)> _closures = new LinkedList<(string, object, IncludeClosure)>();
        private readonly object _gate = new object();
        private object _source;
        private long _hits, _misses;

        public IncludeGraph(IncludeModel model) => _model = model;

        /// <summary>The stored include texts of a file (index), or null when unknown.</summary>
        public Func<string, IReadOnlyList<string>> IncludesOf { get; set; }

        /// <summary>Changes whenever the index content changes (revision); a different value drops the caches.</summary>
        public Func<object> Revision { get; set; }

        public long ClosureHits => Interlocked.Read(ref _hits);
        public long ClosureMisses => Interlocked.Read(ref _misses);
        public int CachedResolutions => _resolved.Count;

        /// <summary>
        /// The closure of <paramref name="file"/> whose own include texts are <paramref name="directIncludes"/> (the editor buffer's,
        /// which may differ from the index).
        /// </summary>
        public IncludeClosure Closure(string file, IReadOnlyList<string> directIncludes, CancellationToken ct)
        {
            var source = Revision?.Invoke();
            string key = Key(file, directIncludes);
            lock (_gate)
            {
                if (!Equals(source, _source))
                {
                    // the older closures stay (with their revision) for Peek: completion may use one a revision old
                    _resolved.Clear();
                    _source = source;
                }
                for (var n = _closures.First; n != null; n = n.Next)
                {
                    if (n.Value.Key != key) continue;
                    _closures.Remove(n);
                    if (!Equals(n.Value.Source, source)) break; // stale: walk again below
                    _closures.AddFirst(n);
                    Interlocked.Increment(ref _hits);
                    return n.Value.Closure;
                }
            }
            Interlocked.Increment(ref _misses);
            var closure = Walk(file, directIncludes, ct);
            lock (_gate)
            {
                _closures.AddFirst((key, source, closure));
                while (_closures.Count > MaxClosures) _closures.RemoveLast();
            }
            L.Debug("includes.closure", null, ("file", System.IO.Path.GetFileName(file)), ("files", closure.Files.Count), ("capped", closure.Capped),
                ("unresolved", closure.Unresolved), ("unknownLists", closure.UnknownLists), ("cachedResolutions", _resolved.Count), ("ms", closure.ElapsedMs));
            return closure;
        }

        /// <summary>
        /// The cached closure for this root and include list without walking (completion must not wait for a cold walk of
        /// thousands of headers), or null. <paramref name="fresh"/> is false when it was built against an older index revision.
        /// </summary>
        public IncludeClosure Peek(string file, IReadOnlyList<string> directIncludes, out bool fresh)
        {
            fresh = false;
            var source = Revision?.Invoke();
            string key = Key(file, directIncludes);
            lock (_gate)
                for (var n = _closures.First; n != null; n = n.Next)
                {
                    if (n.Value.Key != key) continue;
                    fresh = Equals(n.Value.Source, source);
                    return n.Value.Closure;
                }
            return null;
        }

        private static string Key(string file, IReadOnlyList<string> directIncludes) =>
            NavContext.NormalizePath(file) + "\n" + string.Join("\n", directIncludes ?? Array.Empty<string>());

        private IncludeClosure Walk(string root, IReadOnlyList<string> direct, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var c = new IncludeClosure();
            string rootKey = NavContext.NormalizePath(root);
            c.Files.Add(rootKey);
            var queue = new Queue<(string Path, string Via)>();
            foreach (var text in direct ?? Array.Empty<string>())
            {
                string p = ResolveOne(text, root);
                if (p == null) { c.Unresolved++; continue; }
                string k = NavContext.NormalizePath(p);
                if (!c.Files.Add(k)) continue;
                c.Via[k] = text;
                queue.Enqueue((p, text));
            }
            int steps = 0;
            while (queue.Count > 0)
            {
                if ((++steps & 255) == 0) ct.ThrowIfCancellationRequested();
                var (path, via) = queue.Dequeue();
                var list = IncludesOf?.Invoke(path);
                if (list == null) { c.UnknownLists++; continue; }
                foreach (var text in list)
                {
                    string p = ResolveOne(text, path);
                    if (p == null) { c.Unresolved++; continue; }
                    string k = NavContext.NormalizePath(p);
                    if (!c.Files.Add(k)) continue;
                    c.Via[k] = via;
                    if (c.Files.Count >= MaxFiles)
                    {
                        c.Capped = true;
                        c.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                        return c;
                    }
                    queue.Enqueue((p, via));
                }
            }
            c.ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            return c;
        }

        /// <summary>The file an include text of <paramref name="from"/> reaches, or null.</summary>
        public string ResolveOne(string text, string from)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            // an absolute path (the module's PrivatePCHHeaderFile, added for .cpp files)
            if (System.IO.Path.IsPathRooted(text)) return _model.IsIndexed(text) ? text : null;
            // COMPILED_PLATFORM_HEADER(Platform.h) → "Windows/WindowsPlatform.h" (the editor builds Win64): without this the
            // platform layer of Core (FPlatformMisc, FPlatformAtomics…) would look unreachable
            text = PlatformHeader(text) ?? text;
            string dir = System.IO.Path.GetDirectoryName(from);
            if (dir != null)
            {
                string local = IncludeModel.Combine(dir, text);
                if (local != null && _model.IsIndexed(local)) return local;
            }
            var module = _model.ModuleOf(from);
            string key = (module?.BuildCsPath ?? ("dir:" + dir)) + "\n" + text;
            if (_resolved.TryGetValue(key, out var cached)) return cached.Length == 0 ? null : cached;
            var r = _model.Resolve(text, false, from);
            string path = r.Status == IncludeStatus.Found ? r.Path
                : r.Status == IncludeStatus.NotOnIncludePath && r.Candidates.Count > 0 ? r.Candidates[0]
                : null;
            if (_resolved.Count >= MaxResolutions) _resolved.Clear();
            _resolved[key] = path ?? string.Empty;
            return path;
        }

        private static readonly System.Text.RegularExpressions.Regex PlatformMacro = new System.Text.RegularExpressions.Regex(
            @"^\s*COMPILED_PLATFORM_HEADER(?<p>_WITH_PREFIX)?\s*\(\s*(?:(?<prefix>[\w/]+)\s*,\s*)?(?<name>[\w.]+)\s*\)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The Win64 header a COMPILED_PLATFORM_HEADER(_WITH_PREFIX) include names, or null for any other text.</summary>
        internal static string PlatformHeader(string text)
        {
            if (text == null || text.IndexOf("COMPILED_PLATFORM_HEADER", StringComparison.Ordinal) < 0) return null;
            var m = PlatformMacro.Match(text);
            if (!m.Success) return null;
            string name = "Windows/Windows" + m.Groups["name"].Value;
            return m.Groups["prefix"].Success ? m.Groups["prefix"].Value.TrimEnd('/') + "/" + name : name;
        }
    }
}
