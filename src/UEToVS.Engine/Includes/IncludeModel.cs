using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Unreal;

namespace UEToVS.Engine.Includes
{
    public enum IncludeStatus : byte
    {
        /// <summary>Resolved to <see cref="IncludeResolution.Path"/>.</summary>
        Found = 0,

        /// <summary>No file anywhere (index or disk under the include roots) ends with the include path.</summary>
        NotFound = 1,

        /// <summary>Files ending with the include path exist (<see cref="IncludeResolution.Candidates"/>) but none is under an include root of the module.</summary>
        NotOnIncludePath = 2,

        /// <summary>Not looked at: macro include, generated header, system header, no module…</summary>
        Unknown = 3,
    }

    /// <summary>How one <c>#include</c> resolves from a file.</summary>
    public sealed class IncludeResolution
    {
        public IncludeStatus Status { get; set; }

        /// <summary>The resolved file (Found).</summary>
        public string Path { get; set; }

        /// <summary>
        /// Whether a negative answer (NotFound / NotOnIncludePath) can be trusted: the including module is known, its include roots
        /// are fully known (no unevaluated Build.cs include path), the engine is indexed and the include is a quoted literal.
        /// </summary>
        public bool Certain { get; set; }

        /// <summary>How it was decided (logs and tests): relative, root, legacy-public, legacy-parent, build-cs, rsp, disk, suffix, …</summary>
        public string Reason { get; set; }

        /// <summary>NotOnIncludePath: the files ending with the include path.</summary>
        public IReadOnlyList<string> Candidates { get; set; } = Array.Empty<string>();

        /// <summary>NotOnIncludePath: every candidate lies in the Private folder (or module root) of another module.</summary>
        public bool CandidatesPrivate { get; set; }

        /// <summary>NotOnIncludePath: module of the first candidate.</summary>
        public string CandidateModule { get; set; }
    }

    /// <summary>The include roots one module compiles with.</summary>
    public sealed class ModuleIncludeView
    {
        public ModuleInfo Module { get; set; }

        /// <summary>Roots in search order (normalized, no trailing slash): own Private / module folder / Public / Classes / Internal, then the visible modules' Public / Classes.</summary>
        public List<string> StrictRoots { get; } = new List<string>();

        /// <summary>Normalized roots that also resolve includes but are not used to spell new ones: Build.cs include paths, response files, dependency Internal folders.</summary>
        public HashSet<string> ExtraRoots { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Original-cased strict roots (for disk probes and spelling).</summary>
        public List<string> StrictRootsOriginal { get; } = new List<string>();

        /// <summary>Modules whose headers this module may include (itself, direct dependencies, public dependencies of those…).</summary>
        public HashSet<string> Visible { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The module's own Build.cs adds an include path we could not evaluate.</summary>
        public bool OpaquePaths { get; set; }

        /// <summary>Include roots read from a UBT response file (the exact compile command line).</summary>
        public bool FromResponseFile { get; set; }

        /// <summary>Modules (visible) whose Build.cs leaves the legacy include path switches at their default (UBT may add Public subfolders / the parent folder).</summary>
        public HashSet<string> LegacyPublic { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> LegacyParent { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// v0.11.0 include path model (design: 后续规划-代码诊断与提示 §4.1): the one place that knows which folders a module's
    /// <c>#include</c> searches and how a header should be spelled. Replaces the four ad-hoc guesses (completion, Add Include,
    /// go to include, include walk) that each matched path suffixes over the whole file table.
    /// <para>
    /// Roots come from the module catalog (UBT conventions: own Public / Classes / Private / Internal / module folder, the
    /// dependencies' Public / Classes), the Build.cs include path expressions we can evaluate, the legacy switches
    /// (bLegacyPublicIncludePaths adds every Public subfolder, bLegacyParentIncludePaths the module's parent folder) and, when the
    /// module was compiled before, UBT's response files (<c>Intermediate/Build/…/&lt;Module&gt;/*.rsp</c>: the exact /I list).
    /// </para>
    /// Thread-safe. Lookups are dictionary based (file name → paths); the tables are rebuilt when the file table or the catalog changes.
    /// </summary>
    public sealed class IncludeModel
    {
        private static readonly Logger L = Log.For("Includes");

        private static readonly string[] OwnFolders = { "Private", "Public", "Classes", "Internal" };
        private static readonly string[] PublicFolders = { "Public", "Classes" };

        private readonly FileTable _files;
        private readonly Func<ModuleCatalog> _modules;
        private readonly Func<Workspace> _workspace;
        private readonly object _gate = new object();
        private FileEntry[] _source;
        private ModuleCatalog _catalog;
        private Dictionary<string, string[]> _byName = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _paths = new HashSet<string>(StringComparer.Ordinal);
        private bool _engineIndexed;
        private readonly ConcurrentDictionary<string, ModuleIncludeView> _views = new ConcurrentDictionary<string, ModuleIncludeView>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _disk = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _moduleIndexed = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public IncludeModel(FileTable files, Func<ModuleCatalog> modules, Func<Workspace> workspace = null)
        {
            _files = files;
            _modules = modules ?? (() => null);
            _workspace = workspace ?? (() => null);
            Graph = new IncludeGraph(this);
        }

        /// <summary>Include closures over the index's include lists (set <see cref="IncludeGraph.IncludesOf"/> to enable).</summary>
        public IncludeGraph Graph { get; }

        /// <summary>Probe for files outside the index (generated / excluded folders); replaceable in tests.</summary>
        public Func<string, bool> FileExists { get; set; } = SafeExists;

        /// <summary>Reads a UBT response file's lines; null when absent (tests replace it).</summary>
        public Func<ModuleInfo, IReadOnlyList<string>> ResponseFileLines { get; set; }

        /// <summary>v0.11.0: include folders from UBT's JsonExport (null = not available).</summary>
        public UbtExport Export { get; set; }

        private int _exportVersion = -1;

        public ModuleCatalog Catalog
        {
            get
            {
                Refresh();
                return _catalog;
            }
        }

        /// <summary>Whether engine sources are in the file table (otherwise "not found" can never be certain).</summary>
        public bool EngineIndexed
        {
            get
            {
                Refresh();
                return _engineIndexed;
            }
        }

        /// <summary>Files (C++ headers and sources) with this file name.</summary>
        public IReadOnlyList<string> FilesNamed(string fileName)
        {
            Refresh();
            return fileName != null && _byName.TryGetValue(fileName, out var list) ? list : Array.Empty<string>();
        }

        public bool IsIndexed(string path)
        {
            Refresh();
            return _paths.Contains(NavContext.NormalizePath(path));
        }

        public ModuleInfo ModuleOf(string file) => Catalog?.FindForFile(file);

        /// <summary>Whether any source file of the module is in the file table (cached per file-table snapshot).</summary>
        public bool IsModuleIndexed(ModuleInfo module)
        {
            if (module?.Directory == null) return false;
            Refresh();
            return _moduleIndexed.GetOrAdd(module.BuildCsPath ?? module.Name, _ =>
            {
                string dir = NavContext.NormalizePath(module.Directory).TrimEnd('/') + "/";
                foreach (var p in _paths)
                    if (p.StartsWith(dir, StringComparison.Ordinal)) return true;
                return false;
            });
        }

        /// <summary>
        /// The module and every module it can see are indexed: only then does "declared nowhere" mean what it says (an engine plugin
        /// left out of the index would make all of its names look unknown).
        /// </summary>
        public bool DependenciesIndexed(ModuleInfo module)
        {
            if (module == null || Catalog == null) return false;
            foreach (var name in View(module).Visible)
            {
                var m = _catalog.Get(name);
                if (m == null || !IsModuleIndexed(m)) return false;
            }
            return true;
        }

        // ================================================================== resolve

        /// <summary>Resolves <c>#include "include"</c> (or &lt;include&gt;) written in <paramref name="fromFile"/>.</summary>
        public IncludeResolution Resolve(string include, bool angled, string fromFile)
        {
            var r = new IncludeResolution { Status = IncludeStatus.Unknown };
            if (string.IsNullOrWhiteSpace(include)) { r.Reason = "empty"; return r; }
            string inc = include.Trim().Replace('\\', '/');
            if (inc.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)) { r.Reason = "generated"; return r; }
            if (System.IO.Path.IsPathRooted(inc) || inc.Length > 1 && inc[1] == ':')
            {
                bool exists = IsIndexed(inc) || Probe(inc);
                r.Status = exists ? IncludeStatus.Found : IncludeStatus.NotFound;
                r.Path = exists ? inc : null;
                r.Reason = "absolute";
                r.Certain = !angled;
                return r;
            }
            Refresh();
            string fromDir = SafeDirectory(fromFile);
            // 1) next to the including file (quoted includes)
            if (!angled && fromDir != null)
            {
                string local = Combine(fromDir, inc);
                if (local != null && (_paths.Contains(NavContext.NormalizePath(local)) || Probe(local)))
                {
                    r.Status = IncludeStatus.Found;
                    r.Path = Original(local);
                    r.Reason = "relative";
                    return r;
                }
            }
            var module = _catalog?.FindForFile(fromFile);
            var view = module != null ? View(module) : null;
            string fileName = System.IO.Path.GetFileName(inc);
            string suffix = "/" + NavContext.NormalizePath(inc).TrimStart('/');
            var candidates = new List<string>();
            if (_byName.TryGetValue(fileName, out var named))
                foreach (var p in named)
                {
                    string n = NavContext.NormalizePath(p);
                    if (n.EndsWith(suffix, StringComparison.Ordinal)) candidates.Add(p);
                }
            if (view == null)
            {
                // no module (loose file, program without Build.cs): the old behaviour, best suffix match
                r.Reason = "no-module";
                if (candidates.Count > 0)
                {
                    r.Status = IncludeStatus.Found;
                    r.Path = Best(candidates, fromDir);
                }
                return r;
            }
            // 2) a candidate whose prefix is an include root of the module
            string best = null;
            string bestReason = null;
            int bestRank = int.MaxValue;
            foreach (var c in candidates)
            {
                string n = NavContext.NormalizePath(c);
                string root = n.Substring(0, n.Length - suffix.Length);
                int rank = RootRank(view, root, out string reason);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = c;
                    bestReason = reason;
                }
            }
            if (best != null && bestRank < int.MaxValue)
            {
                r.Status = IncludeStatus.Found;
                r.Path = best;
                r.Reason = bestReason;
                return r;
            }
            // 3) not indexed: generated / excluded folders on disk under the strict roots
            foreach (var root in view.StrictRootsOriginal)
            {
                string p = Combine(root, inc);
                if (p != null && Probe(p))
                {
                    r.Status = IncludeStatus.Found;
                    r.Path = p;
                    r.Reason = "disk";
                    return r;
                }
            }
            foreach (var root in view.ExtraRoots)
            {
                string p = Combine(root, inc);
                if (p != null && Probe(p))
                {
                    r.Status = IncludeStatus.Found;
                    r.Path = p;
                    r.Reason = "disk-extra";
                    return r;
                }
            }
            r.Certain = !angled && _engineIndexed && !view.OpaquePaths;
            if (candidates.Count == 0)
            {
                r.Status = IncludeStatus.NotFound;
                r.Reason = "nowhere";
                return r;
            }
            r.Status = IncludeStatus.NotOnIncludePath;
            r.Reason = "not-on-path";
            r.Candidates = candidates;
            bool allPrivate = true;
            foreach (var c in candidates)
            {
                var owner = _catalog.FindForFile(c);
                if (r.CandidateModule == null && owner != null) r.CandidateModule = owner.Name;
                if (owner == null || string.Equals(owner.Name, module.Name, StringComparison.OrdinalIgnoreCase) || !IsPrivatePart(owner, c)) allPrivate = false;
                // the candidate's module adds include paths we cannot see: it might expose this folder
                if (owner != null && View(owner).OpaquePaths) r.Certain = false;
            }
            r.CandidatesPrivate = allPrivate;
            return r;
        }

        /// <summary>0 = strict root (own first), larger = looser; int.MaxValue = not a root of the module.</summary>
        private int RootRank(ModuleIncludeView view, string normRoot, out string reason)
        {
            reason = null;
            int i = view.StrictRoots.IndexOf(normRoot);
            if (i >= 0) { reason = view.FromResponseFile ? "rsp" : "root"; return i; }
            if (view.ExtraRoots.Contains(normRoot)) { reason = view.FromResponseFile ? "rsp" : "build-cs"; return 1000; }
            // bLegacyPublicIncludePaths: every subfolder of a visible module's Public folder is a root
            foreach (var name in view.LegacyPublic)
            {
                var m = _catalog.Get(name);
                if (m == null) continue;
                string pub = NavContext.NormalizePath(System.IO.Path.Combine(m.Directory, "Public"));
                if (normRoot.StartsWith(pub + "/", StringComparison.Ordinal)) { reason = "legacy-public"; return 2000; }
            }
            // bLegacyParentIncludePaths: the module's parent folder (includes written as "Module/Public/X.h")
            foreach (var name in view.LegacyParent)
            {
                var m = _catalog.Get(name);
                if (m == null) continue;
                string parent = NavContext.NormalizePath(System.IO.Path.GetDirectoryName(m.Directory));
                if (normRoot == parent) { reason = "legacy-parent"; return 3000; }
            }
            return int.MaxValue;
        }

        /// <summary>Whether <paramref name="file"/> is outside the module's Public / Classes folders (dependents cannot include it).</summary>
        public static bool IsPrivatePart(ModuleInfo module, string file)
        {
            string n = NavContext.NormalizePath(file);
            foreach (var f in PublicFolders)
                if (n.StartsWith(NavContext.NormalizePath(System.IO.Path.Combine(module.Directory, f)) + "/", StringComparison.Ordinal)) return false;
            return true;
        }

        // ================================================================== spelling

        /// <summary>
        /// How <paramref name="fromFile"/> should write the include of <paramref name="header"/>: the shortest path below one of the
        /// module's strict roots (or, for a header of a module that is not a dependency yet, below that module's Public / Classes
        /// folder) that resolves back to the same file; the file name when it sits next to the including file. Null when unknown.
        /// </summary>
        public string Spelling(string header, string fromFile)
        {
            if (string.IsNullOrEmpty(header)) return null;
            Refresh();
            string h = NavContext.NormalizePath(header);
            var options = new List<string>();
            string fromDir = SafeDirectory(fromFile);
            if (fromDir != null && NavContext.NormalizePath(System.IO.Path.GetDirectoryName(header)) == NavContext.NormalizePath(fromDir))
                options.Add(System.IO.Path.GetFileName(header));
            var module = _catalog?.FindForFile(fromFile);
            var view = module != null ? View(module) : null;
            if (view != null)
                foreach (var root in view.StrictRoots)
                    if (h.StartsWith(root + "/", StringComparison.Ordinal)) options.Add(Tail(header, root.Length + 1));
            var owner = _catalog?.FindForFile(header);
            if (owner != null)
                foreach (var f in OwnFolders.Concat(new[] { "" }))
                {
                    string root = NavContext.NormalizePath(f.Length == 0 ? owner.Directory : System.IO.Path.Combine(owner.Directory, f));
                    if (h.StartsWith(root + "/", StringComparison.Ordinal)) options.Add(Tail(header, root.Length + 1));
                }
            if (options.Count == 0) return LegacySpelling(header);
            foreach (var o in options.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o.Length))
            {
                if (fromFile == null) return o;
                var r = Resolve(o, false, fromFile);
                if (r.Status == IncludeStatus.Found && NavContext.NormalizePath(r.Path) == h) return o;
                // not visible yet (the module dependency is missing): still the right spelling once the dependency is added
                if (r.Status == IncludeStatus.NotOnIncludePath && r.Candidates.Any(c => NavContext.NormalizePath(c) == h)) return o;
            }
            return options.OrderBy(o => o.Length).First();
        }

        /// <summary>The pre-v0.11 guess (first Public / Classes / Private / Internal folder), for files outside every module.</summary>
        public static string LegacySpelling(string header)
        {
            if (string.IsNullOrEmpty(header)) return null;
            var parts = header.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
                foreach (var f in OwnFolders)
                    if (string.Equals(parts[i], f, StringComparison.OrdinalIgnoreCase)) return string.Join("/", parts.Skip(i + 1));
            return parts[parts.Length - 1];
        }

        /// <summary>Whether a header of <paramref name="headerModule"/> may be included from <paramref name="fromModule"/> (dependency visibility only).</summary>
        public bool IsVisible(string fromModule, string headerModule)
        {
            if (fromModule == null || headerModule == null) return true;
            var m = Catalog?.Get(fromModule);
            if (m == null) return true;
            return View(m).Visible.Contains(headerModule);
        }

        // ================================================================== module views

        public ModuleIncludeView View(ModuleInfo module)
        {
            Refresh();
            string key = module.BuildCsPath ?? module.Name;
            return _views.GetOrAdd(key, _ => BuildView(module));
        }

        private ModuleIncludeView BuildView(ModuleInfo module)
        {
            var v = new ModuleIncludeView { Module = module };
            void AddStrict(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                string n = NavContext.NormalizePath(dir).TrimEnd('/');
                if (v.StrictRoots.Contains(n)) return;
                v.StrictRoots.Add(n);
                v.StrictRootsOriginal.Add(dir);
            }
            // own module: Private first (UBT puts private paths before public ones), then the module folder, Public, Classes, Internal
            AddStrict(System.IO.Path.Combine(module.Directory, "Private"));
            AddStrict(module.Directory);
            foreach (var f in new[] { "Public", "Classes", "Internal" }) AddStrict(System.IO.Path.Combine(module.Directory, f));
            v.Visible.UnionWith(VisibleModules(module));
            foreach (var name in v.Visible)
            {
                var d = _catalog?.Get(name);
                if (d == null || ReferenceEquals(d, module)) continue;
                foreach (var f in PublicFolders) AddStrict(System.IO.Path.Combine(d.Directory, f));
                v.ExtraRoots.Add(NavContext.NormalizePath(System.IO.Path.Combine(d.Directory, "Internal")));
                if (d.LegacyPublicIncludePaths != false) v.LegacyPublic.Add(d.Name);
                if (d.LegacyParentIncludePaths != false) v.LegacyParent.Add(d.Name);
                foreach (var e in d.PublicIncludePathExpressions)
                {
                    var dirs = Evaluate(e, d);
                    if (dirs != null) foreach (var x in dirs) v.ExtraRoots.Add(NavContext.NormalizePath(x).TrimEnd('/'));
                }
                var dx = Export?.Get(d.Name);
                if (dx != null) foreach (var x in dx.Public) AddExported(v, x);
            }
            if (module.LegacyPublicIncludePaths != false) v.LegacyPublic.Add(module.Name);
            if (module.LegacyParentIncludePaths != false) v.LegacyParent.Add(module.Name);
            foreach (var e in module.PublicIncludePathExpressions.Concat(module.PrivateIncludePathExpressions))
            {
                var dirs = Evaluate(e, module);
                if (dirs == null) { v.OpaquePaths = true; continue; }
                foreach (var x in dirs) v.ExtraRoots.Add(NavContext.NormalizePath(x).TrimEnd('/'));
            }
            // UBT's JsonExport (run by the host when a Build.cs changes): the module's include folders as UBT evaluated them
            var own = Export?.Get(module.Name);
            if (own != null)
            {
                foreach (var x in own.Public) AddExported(v, x);
                foreach (var x in own.Private) AddExported(v, x);
                v.FromResponseFile = true;
                v.OpaquePaths = false;
            }
            // UBT's response file: the exact /I list of the last compile. It settles everything the Build.cs might add.
            var rsp = ReadResponseRoots(module);
            if (rsp != null && rsp.Count > 0)
            {
                foreach (var x in rsp) v.ExtraRoots.Add(NavContext.NormalizePath(x).TrimEnd('/'));
                v.FromResponseFile = true;
                v.OpaquePaths = false;
            }
            L.Debug("includes.view", null, ("module", module.Name), ("strict", v.StrictRoots.Count), ("extra", v.ExtraRoots.Count), ("visible", v.Visible.Count),
                ("opaque", v.OpaquePaths), ("rsp", v.FromResponseFile));
            return v;
        }

        private void AddExported(ModuleIncludeView v, string dir)
        {
            string p = dir.Replace('\\', '/');
            if (!(System.IO.Path.IsPathRooted(p) || p.Length > 1 && p[1] == ':'))
            {
                string src = EngineSource();
                if (src == null) return;
                p = src.Replace('\\', '/').TrimEnd('/') + "/" + p;
            }
            v.ExtraRoots.Add(NavContext.NormalizePath(CollapseOriginal(p)).TrimEnd('/'));
        }

        /// <summary>The module itself, its direct dependencies (public, private, include-path-only) and, transitively, the public ones of those.</summary>
        private HashSet<string> VisibleModules(ModuleInfo root)
        {
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Name };
            var queue = new Queue<string>();
            foreach (var m in root.PublicDependencies.Concat(root.PrivateDependencies).Concat(root.PublicIncludePathModules).Concat(root.PrivateIncludePathModules))
                if (visible.Add(m)) queue.Enqueue(m);
            while (queue.Count > 0 && visible.Count < 4096)
            {
                string next = queue.Dequeue(); // not inside ?. : a null catalog would skip the Dequeue and spin forever
                var m = _catalog?.Get(next);
                if (m == null) continue;
                foreach (var d in m.PublicDependencies.Concat(m.PublicIncludePathModules))
                    if (visible.Add(d)) queue.Enqueue(d);
            }
            return visible;
        }

        // ================================================================== Build.cs expressions

        /// <summary>
        /// Folders an include path expression of <paramref name="module"/>'s Build.cs denotes, or null when it cannot be evaluated.
        /// Understood: string literals (relative ones are tried against Engine/Source and the module folder, like UBT's legacy
        /// rule), ModuleDirectory / PluginDirectory / EngineDirectory / Target.UEThirdPartySourceDirectory, Path.Combine(…),
        /// Path.GetFullPath(…) and string concatenation with +.
        /// </summary>
        public List<string> Evaluate(string expression, ModuleInfo module)
        {
            if (string.IsNullOrWhiteSpace(expression) || expression.StartsWith("?", StringComparison.Ordinal)) return null;
            string value = Eval(expression.Trim(), module);
            if (value == null) return null;
            value = value.Replace('\\', '/');
            if (System.IO.Path.IsPathRooted(value) || value.Length > 1 && value[1] == ':') return new List<string> { CollapseOriginal(value) };
            var list = new List<string>();
            string engineSource = EngineSource();
            if (engineSource != null) list.Add(CollapseOriginal(engineSource.Replace('\\', '/') + "/" + value));
            list.Add(CollapseOriginal(module.Directory.Replace('\\', '/') + "/" + value));
            return list;
        }

        private string Eval(string e, ModuleInfo module)
        {
            e = StripParens(e.Trim());
            // concatenation: a + b (top level)
            var parts = SplitPlus(e);
            if (parts.Count > 1)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var p in parts)
                {
                    string v = Eval(p, module);
                    if (v == null) return null;
                    sb.Append(v);
                }
                return sb.ToString();
            }
            if (e.StartsWith("@\"", StringComparison.Ordinal) && e.EndsWith("\"", StringComparison.Ordinal) && e.Length >= 3) return e.Substring(2, e.Length - 3).Replace("\"\"", "\"");
            if (e.StartsWith("\"", StringComparison.Ordinal) && e.EndsWith("\"", StringComparison.Ordinal) && e.Length >= 2) return e.Substring(1, e.Length - 2).Replace("\\\\", "\\").Replace("\\\"", "\"");
            string call = CallName(e, out string args);
            if (call != null)
            {
                switch (call)
                {
                    case "Path.Combine":
                    case "System.IO.Path.Combine":
                    {
                        string acc = null;
                        foreach (var a in UEToVS.Core.Unreal.BuildCsFile.SplitTopLevel(args))
                        {
                            string v = Eval(a, module);
                            if (v == null) return null;
                            acc = acc == null ? v : (System.IO.Path.IsPathRooted(v) ? v : acc.TrimEnd('/', '\\') + "/" + v.TrimStart('/', '\\'));
                        }
                        return acc;
                    }
                    case "Path.GetFullPath":
                    case "System.IO.Path.GetFullPath":
                        return Eval(args, module);
                    default:
                        return null;
                }
            }
            switch (e)
            {
                case "ModuleDirectory":
                case "this.ModuleDirectory":
                    return module.Directory;
                case "PluginDirectory":
                case "this.PluginDirectory":
                    return PluginDirectory(module);
                case "EngineDirectory":
                case "Target.RelativeEnginePath":
                    return _workspace()?.EngineDir;
                case "Target.UEThirdPartySourceDirectory":
                case "UEThirdPartySourceDirectory":
                {
                    string src = EngineSource();
                    return src == null ? null : src + "/ThirdParty/";
                }
                case "EngineSourceDirectory":
                    return EngineSource();
                default:
                    return null;
            }
        }

        private string EngineSource()
        {
            string engine = _workspace()?.EngineDir;
            return engine == null ? null : System.IO.Path.Combine(engine, "Source").Replace('\\', '/');
        }

        private string PluginDirectory(ModuleInfo module)
        {
            for (string d = module.Directory; !string.IsNullOrEmpty(d); d = System.IO.Path.GetDirectoryName(d))
            {
                try
                {
                    if (Directory.Exists(d) && Directory.EnumerateFiles(d, "*.uplugin").Any()) return d;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
                if (string.Equals(System.IO.Path.GetFileName(d), "Plugins", StringComparison.OrdinalIgnoreCase)) break;
            }
            return null;
        }

        private static string StripParens(string e)
        {
            while (e.Length > 1 && e[0] == '(' && UEToVS.Core.Unreal.BuildCsFile.MatchParen(e, 0) == e.Length - 1) e = e.Substring(1, e.Length - 2).Trim();
            return e;
        }

        private static List<string> SplitPlus(string e)
        {
            var list = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < e.Length; i++)
            {
                char c = e[i];
                if (c == '"') { i = UEToVS.Core.Unreal.BuildCsFile.SkipString(e, i); continue; }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == '+' && depth == 0)
                {
                    list.Add(e.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            list.Add(e.Substring(start).Trim());
            return list;
        }

        private static string CallName(string e, out string args)
        {
            args = null;
            int open = e.IndexOf('(');
            if (open <= 0 || !e.EndsWith(")", StringComparison.Ordinal)) return null;
            if (UEToVS.Core.Unreal.BuildCsFile.MatchParen(e, open) != e.Length - 1) return null;
            string name = e.Substring(0, open).Trim();
            foreach (char c in name)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.')) return null;
            args = e.Substring(open + 1, e.Length - open - 2);
            return name;
        }

        // ================================================================== UBT response files

        /// <summary>/I folders of the most recent UBT response file of the module (relative paths are relative to Engine/Source, UBT's working folder).</summary>
        private List<string> ReadResponseRoots(ModuleInfo module)
        {
            IReadOnlyList<string> lines = null;
            try
            {
                lines = ResponseFileLines != null ? ResponseFileLines(module) : DefaultResponseFile(module);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                L.Debug("includes.rsp.failed", null, ("module", module.Name), ("error", ex.Message));
            }
            if (lines == null) return null;
            return ParseResponseIncludes(lines, EngineSource());
        }

        /// <summary>Include folders of a compiler response file: <c>/I "x"</c>, <c>/I"x"</c>, <c>-I x</c>, <c>/external:I "x"</c>, <c>-isystem x</c>.</summary>
        public static List<string> ParseResponseIncludes(IEnumerable<string> lines, string engineSource)
        {
            var list = new List<string>();
            foreach (var raw in lines)
            {
                var tokens = Tokenize(raw);
                for (int i = 0; i < tokens.Count; i++)
                {
                    string t = tokens[i];
                    string value = null;
                    foreach (var flag in new[] { "/external:I", "-external:I", "/I", "-I", "-isystem", "-iquote", "/imsvc", "-imsvc" })
                    {
                        if (!t.StartsWith(flag, StringComparison.Ordinal)) continue;
                        // "/Iinclude" is "/I" + path, but "/Ifoo" must not match "/I" of "/imsvc" etc. (case-sensitive above)
                        value = t.Length > flag.Length ? t.Substring(flag.Length) : (i + 1 < tokens.Count ? tokens[++i] : null);
                        break;
                    }
                    if (string.IsNullOrEmpty(value)) continue;
                    value = value.Trim('"').Replace('\\', '/');
                    if (!(System.IO.Path.IsPathRooted(value) || value.Length > 1 && value[1] == ':'))
                    {
                        if (engineSource == null) continue;
                        value = engineSource.Replace('\\', '/').TrimEnd('/') + "/" + value;
                    }
                    list.Add(CollapseOriginal(value));
                }
            }
            return list;
        }

        private static List<string> Tokenize(string line)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(line)) return list;
            var sb = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char c in line)
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }

        private static IReadOnlyList<string> DefaultResponseFile(ModuleInfo module)
        {
            if (!GeneratedBodyLocator.TryOwningModule(System.IO.Path.Combine(module.Directory, "x.cpp"), out _, out string root)) return null;
            foreach (var defs in UbtDefinitionLocator.DefinitionFiles(System.IO.Path.Combine(root, "Intermediate", "Build"), module.Name))
            {
                string dir = System.IO.Path.GetDirectoryName(defs);
                var rsp = SafeFiles(dir, "*.rsp").Concat(SafeFiles(dir, "*.response"))
                    .OrderByDescending(f => SafeTime(f)).FirstOrDefault();
                if (rsp == null) continue;
                return File.ReadAllLines(rsp);
            }
            return null;
        }

        private static IEnumerable<string> SafeFiles(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).Take(64).ToList() : Enumerable.Empty<string>(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }

        private static DateTime SafeTime(string f)
        {
            try { return File.GetLastWriteTimeUtc(f); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return DateTime.MinValue; }
        }

        // ================================================================== tables

        private void Refresh()
        {
            var snap = _files?.Snapshot() ?? Array.Empty<FileEntry>();
            var catalog = _modules();
            int exportVersion = Export?.Version ?? 0;
            if (exportVersion != Volatile.Read(ref _exportVersion))
            {
                // a new UBT export: every module view may change
                _views.Clear();
                Volatile.Write(ref _exportVersion, exportVersion);
            }
            if (ReferenceEquals(snap, _source) && ReferenceEquals(catalog, _catalog)) return;
            lock (_gate)
            {
                if (!ReferenceEquals(catalog, _catalog))
                {
                    _views.Clear();
                    _catalog = catalog;
                }
                if (ReferenceEquals(snap, _source)) return;
                var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var paths = new HashSet<string>(StringComparer.Ordinal);
                bool engine = false;
                foreach (var e in snap)
                {
                    var f = e.File;
                    if (f == null || !f.IsCpp) continue;
                    if (f.Origin == ItemOrigin.Engine || f.Origin == ItemOrigin.EnginePlugin) engine = true;
                    string name = f.Name ?? System.IO.Path.GetFileName(f.Path);
                    if (!byName.TryGetValue(name, out var list)) byName[name] = list = new List<string>(1);
                    list.Add(f.Path);
                    paths.Add(NavContext.NormalizePath(f.Path));
                }
                _byName = byName.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                _paths = paths;
                _engineIndexed = engine;
                _disk.Clear();
                _moduleIndexed.Clear();
                _source = snap;
            }
        }

        private bool Probe(string path)
        {
            string key = NavContext.NormalizePath(path);
            if (_disk.Count > 8192) _disk.Clear();
            return _disk.GetOrAdd(key, _ => FileExists(path));
        }

        private string Original(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            string n = NavContext.NormalizePath(path);
            if (_byName.TryGetValue(name, out var list))
                foreach (var p in list)
                    if (NavContext.NormalizePath(p) == n) return p;
            return path;
        }

        private static string Best(List<string> candidates, string fromDir)
        {
            string fd = fromDir == null ? null : NavContext.NormalizePath(fromDir);
            return candidates.OrderByDescending(c => fd != null && NavContext.NormalizePath(c).StartsWith(fd + "/", StringComparison.Ordinal) ? 1 : 0)
                             .ThenBy(c => c.Length).First();
        }

        private static string Tail(string path, int start)
        {
            string p = path.Replace('\\', '/');
            return start <= p.Length ? p.Substring(start) : p;
        }

        private static string SafeDirectory(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            try { return System.IO.Path.GetDirectoryName(file); }
            catch (ArgumentException) { return null; }
        }

        /// <summary><paramref name="dir"/> + <paramref name="relative"/> with "." / ".." collapsed (original casing), or null.</summary>
        internal static string Combine(string dir, string relative)
        {
            if (dir == null || relative == null) return null;
            return CollapseOriginal(dir.Replace('\\', '/').TrimEnd('/') + "/" + relative.Replace('\\', '/'));
        }

        internal static string CollapseOriginal(string path)
        {
            var parts = path.Replace('\\', '/').Split('/');
            var stack = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p == "." || p.Length == 0 && i > 0 && i < parts.Length - 1) continue;
                if (p == ".." && stack.Count > 0 && stack[stack.Count - 1] != ".." && stack[stack.Count - 1].Length > 0 && !stack[stack.Count - 1].EndsWith(":", StringComparison.Ordinal))
                {
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                stack.Add(p);
            }
            string s = string.Join("/", stack).TrimEnd('/');
            return System.IO.Path.DirectorySeparatorChar == '\\' ? s.Replace('/', '\\') : s;
        }

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { return false; }
        }
    }
}
