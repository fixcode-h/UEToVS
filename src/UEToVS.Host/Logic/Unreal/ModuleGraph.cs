using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Core.Unreal;

namespace UEToVS.Host.Logic.Unreal
{
    /// <summary>一个模块（一个 Build.cs）在依赖图里的样子。</summary>
    public sealed class ModuleNode
    {
        public string Name { get; set; }
        public string BuildCsPath { get; set; }

        /// <summary>Build.cs 所在目录（模块根）。</summary>
        public string Directory { get; set; }

        /// <summary>项目（含项目插件）里的模块；引擎模块只作为依赖的终点出现，不再往下展开。</summary>
        public bool IsProject { get; set; }

        /// <summary>所属插件名，不在插件里为 null。</summary>
        public string Plugin { get; set; }

        public HashSet<string> Public { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Private { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PublicIncludePaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PrivateIncludePaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Public / Private 依赖（包括只在 Private 里的），去掉自己。</summary>
        public IEnumerable<string> Dependencies => Public.Concat(Private.Where(p => !Public.Contains(p)))
                                                         .Where(d => !string.Equals(d, Name, StringComparison.OrdinalIgnoreCase));

        public static ModuleNode FromBuildCs(string buildCsPath, string text, bool isProject, string plugin = null)
        {
            string name = BuildCsFile.ModuleNameFromPath(buildCsPath);
            var rules = new BuildCsFile(name, text ?? string.Empty);
            return new ModuleNode
            {
                Name = name,
                BuildCsPath = buildCsPath,
                Directory = Path.GetDirectoryName(buildCsPath),
                IsProject = isProject,
                Plugin = plugin,
                Public = new HashSet<string>(rules.PublicDependencies, StringComparer.OrdinalIgnoreCase),
                Private = new HashSet<string>(rules.PrivateDependencies, StringComparer.OrdinalIgnoreCase),
                PublicIncludePaths = new HashSet<string>(rules.PublicIncludePathModules, StringComparer.OrdinalIgnoreCase),
                PrivateIncludePaths = new HashSet<string>(rules.PrivateIncludePathModules, StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    public sealed class ModuleEdge
    {
        public string From { get; set; }
        public string To { get; set; }
        public bool IsPublic { get; set; }

        /// <summary>这条边在某个循环里（两端在同一个强连通分量）。</summary>
        public bool InCycle { get; set; }
    }

    /// <summary>
    /// 模块依赖图：边（Public / Private）、循环依赖（Tarjan 强连通分量，只算已知 Build.cs 的模块之间）、给画布用的分层布局。纯逻辑。
    /// </summary>
    public sealed class ModuleGraph
    {
        private readonly Dictionary<string, ModuleNode> _nodes = new Dictionary<string, ModuleNode>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _component = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, ModuleNode> Nodes => _nodes;
        public List<ModuleEdge> Edges { get; } = new List<ModuleEdge>();

        /// <summary>每个循环（大于一个模块的强连通分量），模块名按字母排序。</summary>
        public List<List<string>> Cycles { get; } = new List<List<string>>();

        public static ModuleGraph Build(IEnumerable<ModuleNode> nodes)
        {
            var g = new ModuleGraph();
            foreach (var n in nodes)
                if (n?.Name != null && !g._nodes.ContainsKey(n.Name)) g._nodes[n.Name] = n;
            foreach (var n in g._nodes.Values)
            {
                foreach (var d in n.Public)
                    if (!string.Equals(d, n.Name, StringComparison.OrdinalIgnoreCase)) g.Edges.Add(new ModuleEdge { From = n.Name, To = d, IsPublic = true });
                foreach (var d in n.Private)
                    if (!n.Public.Contains(d) && !string.Equals(d, n.Name, StringComparison.OrdinalIgnoreCase))
                        g.Edges.Add(new ModuleEdge { From = n.Name, To = d, IsPublic = false });
            }
            g.FindCycles();
            foreach (var e in g.Edges)
                e.InCycle = g._component.TryGetValue(e.From, out int a) && g._component.TryGetValue(e.To, out int b) && a == b && g.CycleComponent(a);
            return g;
        }

        private readonly HashSet<int> _cyclic = new HashSet<int>();

        private bool CycleComponent(int c) => _cyclic.Contains(c);

        public bool InCycle(string module) => module != null && _component.TryGetValue(module, out int c) && _cyclic.Contains(c);

        public ModuleNode Get(string name) => name != null && _nodes.TryGetValue(name, out var n) ? n : null;

        /// <summary>依赖 <paramref name="module"/> 的已知模块。</summary>
        public IEnumerable<string> Dependents(string module) =>
            Edges.Where(e => string.Equals(e.To, module, StringComparison.OrdinalIgnoreCase)).Select(e => e.From).Distinct(StringComparer.OrdinalIgnoreCase);

        private void FindCycles()
        {
            // Tarjan（迭代写法，模块多时也不会栈溢出）
            int index = 0;
            var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            var adj = _nodes.Values.ToDictionary(n => n.Name, n => n.Dependencies.Where(_nodes.ContainsKey).ToList(), StringComparer.OrdinalIgnoreCase);
            int comp = 0;
            foreach (var root in _nodes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (idx.ContainsKey(root)) continue;
                var work = new Stack<(string Node, int Next)>();
                work.Push((root, 0));
                idx[root] = low[root] = index++;
                stack.Push(root);
                onStack.Add(root);
                while (work.Count > 0)
                {
                    var (v, next) = work.Pop();
                    var deps = adj[v];
                    if (next < deps.Count)
                    {
                        work.Push((v, next + 1));
                        string w = deps[next];
                        if (!idx.ContainsKey(w))
                        {
                            idx[w] = low[w] = index++;
                            stack.Push(w);
                            onStack.Add(w);
                            work.Push((w, 0));
                        }
                        else if (onStack.Contains(w)) low[v] = Math.Min(low[v], idx[w]);
                        continue;
                    }
                    if (work.Count > 0)
                    {
                        string parent = work.Peek().Node;
                        low[parent] = Math.Min(low[parent], low[v]);
                    }
                    if (low[v] != idx[v]) continue;
                    var members = new List<string>();
                    string x;
                    do
                    {
                        x = stack.Pop();
                        onStack.Remove(x);
                        members.Add(x);
                        _component[x] = comp;
                    } while (!string.Equals(x, v, StringComparison.OrdinalIgnoreCase));
                    if (members.Count > 1)
                    {
                        _cyclic.Add(comp);
                        members.Sort(StringComparer.OrdinalIgnoreCase);
                        Cycles.Add(members);
                    }
                    comp++;
                }
            }
        }

        /// <summary>
        /// 画布布局：每个模块一层（列），被依赖的在右边（层号 = 到“只被依赖、不依赖别人”的最长路径）；循环里的模块放同一层。
        /// 只排 <paramref name="visible"/> 里的模块，返回 模块 → (层, 层内序号)。
        /// </summary>
        public Dictionary<string, (int Layer, int Row)> Layout(IEnumerable<string> visible)
        {
            var set = new HashSet<string>(visible, StringComparer.OrdinalIgnoreCase);
            var layer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var deps = set.ToDictionary(m => m, m => Edges.Where(e => string.Equals(e.From, m, StringComparison.OrdinalIgnoreCase) && set.Contains(e.To) && !e.InCycle)
                                                         .Select(e => e.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            int Depth(string m, int guard)
            {
                if (layer.TryGetValue(m, out int l)) return l;
                if (guard > set.Count) return 0;
                int d = 0;
                foreach (var to in deps[m]) d = Math.Max(d, Depth(to, guard + 1) + 1);
                layer[m] = d;
                return d;
            }
            foreach (var m in set) Depth(m, 0);
            // 层号反过来：依赖别人的在左（层 0），被依赖的在右
            int max = layer.Count == 0 ? 0 : layer.Values.Max();
            var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in layer.GroupBy(kv => max - kv.Value).OrderBy(g => g.Key))
            {
                int row = 0;
                foreach (var kv in g.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) result[kv.Key] = (g.Key, row++);
            }
            return result;
        }
    }

    public enum ModuleFindingKind
    {
        /// <summary>Public 头文件 include 了只在 PrivateDependencyModuleNames 里的模块的头：依赖本模块的模块会编不过。</summary>
        PublicHeaderUsesPrivateDependency,

        /// <summary>声明了依赖，但模块里没有 include 它的头（可能不准：可能只链接、或通过别的头间接用到）。</summary>
        UnusedDependency,

        /// <summary>依赖循环。</summary>
        Cycle,
    }

    public sealed class ModuleFinding
    {
        public ModuleFindingKind Kind { get; set; }
        public string Module { get; set; }
        public string Dependency { get; set; }
        public string File { get; set; }

        /// <summary>0 起；没有具体行时为 -1（跳到 Build.cs）。</summary>
        public int Line { get; set; } = -1;
        public string Include { get; set; }

        public string Message(bool english)
        {
            switch (Kind)
            {
                case ModuleFindingKind.PublicHeaderUsesPrivateDependency:
                    return english
                        ? $"Public header includes \"{Include}\" from {Dependency}, which is only in PrivateDependencyModuleNames: modules that depend on {Module} will not compile. Move {Dependency} to PublicDependencyModuleNames or the include to a .cpp."
                        : $"Public 头文件 include 了 {Dependency} 的 \"{Include}\"，但 {Dependency} 只在 PrivateDependencyModuleNames 里：依赖 {Module} 的模块会编不过。把 {Dependency} 移到 PublicDependencyModuleNames，或者把 include 挪到 .cpp。";
                case ModuleFindingKind.UnusedDependency:
                    return english
                        ? $"{Module} declares {Dependency} but no file includes its headers (may be wrong: link-only use or headers pulled in through other modules are not seen)."
                        : $"{Module} 声明了依赖 {Dependency}，但没有文件 include 它的头（可能不准：只链接不 include、或通过其他模块的头间接用到时看不出来）。";
                default:
                    return english ? "Circular dependency between: " + Dependency : "这些模块之间有循环依赖：" + Dependency;
            }
        }
    }

    /// <summary>一个源文件和它的 #include（审查用）。</summary>
    public sealed class ModuleSourceFile
    {
        public string Path { get; set; }
        public List<IncludeRef> Includes { get; set; } = new List<IncludeRef>();
    }

    /// <summary>
    /// 一个模块的依赖审查：每个 include 先在本模块里找，再在声明的依赖模块的 Public / Classes / Internal 目录里找（只判断文件在不在），
    /// 据此报告“Public 头用到只在 Private 里的依赖”和“声明了但没用到的依赖”（后者只作提示）。不认识目录的依赖不下结论。纯逻辑。
    /// </summary>
    public static class ModuleDependencyAudit
    {
        /// <summary>几乎每个模块都要、只靠链接或宏也算“用到”的依赖，不报未使用。</summary>
        private static readonly HashSet<string> AlwaysUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Core", "CoreUObject" };

        public static bool IsPublicHeader(ModuleNode module, string file)
        {
            if (module?.Directory == null || file == null) return false;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".h" && ext != ".hpp" && ext != ".inl") return false;
            foreach (var sub in new[] { "Public", "Classes" })
            {
                string dir = Path.Combine(module.Directory, sub) + Path.DirectorySeparatorChar;
                if (file.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>依赖模块能被别人 include 的目录。</summary>
        public static IEnumerable<string> IncludeRoots(ModuleNode m)
        {
            if (m?.Directory == null) yield break;
            yield return Path.Combine(m.Directory, "Public");
            yield return Path.Combine(m.Directory, "Classes");
            yield return Path.Combine(m.Directory, "Internal");
        }

        public static List<ModuleFinding> Run(ModuleNode module, IReadOnlyList<ModuleSourceFile> files, Func<string, ModuleNode> lookup,
                                              Func<string, bool> fileExists, CancellationToken ct)
        {
            var findings = new List<ModuleFinding>();
            if (module == null || files == null) return findings;
            var declared = module.Dependencies.Concat(module.PublicIncludePaths).Concat(module.PrivateIncludePaths)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var known = declared.Select(d => lookup(d)).Where(n => n?.Directory != null).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // include → 所属依赖模块（"" = 本模块或找不到）
            int resolved = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                bool publicHeader = IsPublicHeader(module, f.Path);
                foreach (var inc in f.Includes)
                {
                    string path = inc.Path.Replace('\\', '/');
                    if (path.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!owner.TryGetValue(path, out string dep))
                    {
                        dep = Owner(module, f.Path, path, known, fileExists);
                        owner[path] = dep;
                    }
                    if (dep == null) continue;
                    resolved++;
                    if (dep.Length == 0) continue;
                    used.Add(dep);
                    if (publicHeader && module.Private.Contains(dep) && !module.Public.Contains(dep) && !module.PublicIncludePaths.Contains(dep))
                        findings.Add(new ModuleFinding
                        {
                            Kind = ModuleFindingKind.PublicHeaderUsesPrivateDependency, Module = module.Name, Dependency = dep, File = f.Path,
                            Line = inc.Line, Include = inc.Path,
                        });
                }
            }
            // 一个 include 都没认出来（读文件失败之类）时不报未使用，免得整片误报
            if (resolved > 0)
                foreach (var d in module.Dependencies)
                {
                    if (used.Contains(d) || AlwaysUsed.Contains(d)) continue;
                    if (!known.Any(n => string.Equals(n.Name, d, StringComparison.OrdinalIgnoreCase))) continue;
                    findings.Add(new ModuleFinding { Kind = ModuleFindingKind.UnusedDependency, Module = module.Name, Dependency = d, File = module.BuildCsPath });
                }
            return findings;
        }

        /// <summary>include 属于哪个声明的依赖：""= 本模块（或相对路径），null = 认不出来。</summary>
        private static string Owner(ModuleNode module, string file, string include, List<ModuleNode> deps, Func<string, bool> fileExists)
        {
            string rel = include.Replace('/', Path.DirectorySeparatorChar);
            string here = Path.GetDirectoryName(file);
            if (here != null && fileExists(Path.Combine(here, rel))) return string.Empty;
            foreach (var root in IncludeRoots(module).Concat(new[] { module.Directory, Path.Combine(module.Directory, "Private") }))
                if (fileExists(Path.Combine(root, rel))) return string.Empty;
            foreach (var d in deps)
                foreach (var root in IncludeRoots(d))
                    if (fileExists(Path.Combine(root, rel))) return d.Name;
            return null;
        }

        public static IEnumerable<ModuleFinding> CycleFindings(ModuleGraph graph)
        {
            foreach (var c in graph.Cycles)
            {
                var first = graph.Get(c[0]);
                yield return new ModuleFinding
                {
                    Kind = ModuleFindingKind.Cycle, Module = c[0], Dependency = string.Join(", ", c), File = first?.BuildCsPath,
                };
            }
        }
    }
}
