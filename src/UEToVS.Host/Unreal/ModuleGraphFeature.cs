using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Unreal;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Integration;
using UEToVS.Host.Logic.Actions;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Unreal;
using UEToVS.Host.Navigation;
using UEToVS.Host.Search;
using UEToVS.Host.Shell;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Unreal
{
    /// <summary>
    /// 项目自己的模块（项目 Source 和 Plugins 下的 Build.cs）。只在打开依赖图、检查 ini 时找一次，60 秒内复用；不读引擎目录。
    /// </summary>
    internal static class ProjectModules
    {
        private static readonly Logger L = Log.For("BuildCs");
        private static readonly object Gate = new object();
        private static Task<List<ModuleNode>> _task;
        private static DateTime _at;
        private static string _key;

        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Intermediate", "Binaries", "Content", "Saved", "ThirdParty", "Resources", "Shaders", "Config", "DerivedDataCache", ".git", ".vs",
        };

        public static string ProjectDir
        {
            get
            {
                string p = HostPackage.Instance?.Unreal?.ProjectFile;
                return string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            }
        }

        public static Task<List<ModuleNode>> LoadAsync(bool refresh = false)
        {
            string dir = ProjectDir;
            lock (Gate)
            {
                if (!refresh && _task != null && _key == dir && DateTime.UtcNow - _at < TimeSpan.FromSeconds(60) && !_task.IsFaulted) return _task;
                _key = dir;
                _at = DateTime.UtcNow;
                _task = Task.Run(() => Scan(dir));
                return _task;
            }
        }

        public static async Task<HashSet<string>> NamesAsync()
        {
            var list = await LoadAsync().ConfigureAwait(false);
            return new HashSet<string>(list.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        }

        private static List<ModuleNode> Scan(string projectDir)
        {
            var sw = Stopwatch.StartNew();
            var list = new List<ModuleNode>();
            if (string.IsNullOrEmpty(projectDir)) return list;
            var stack = new Stack<(string Dir, string Plugin)>();
            foreach (var sub in new[] { "Source", "Plugins" })
            {
                string d = Path.Combine(projectDir, sub);
                if (Directory.Exists(d)) stack.Push((d, null));
            }
            int visited = 0;
            while (stack.Count > 0 && visited < 20000)
            {
                var (dir, plugin) = stack.Pop();
                visited++;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.uplugin")) plugin = Path.GetFileNameWithoutExtension(f);
                    foreach (var f in Directory.EnumerateFiles(dir, "*.Build.cs"))
                    {
                        try
                        {
                            list.Add(ModuleNode.FromBuildCs(f, File.ReadAllText(f), true, plugin));
                        }
                        catch (IOException ex)
                        {
                            L.Warn("modules.read.failed", null, ex, ("file", f));
                        }
                    }
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        if (!Skip.Contains(Path.GetFileName(sub))) stack.Push((sub, plugin));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 目录在扫描时被删 / 没权限：跳过
                }
            }
            L.Info("modules.project.scan", null, ("modules", list.Count), ("dirs", visited), ("ms", sw.ElapsedMilliseconds));
            return list;
        }
    }

    /// <summary>依赖图窗口要显示的全部数据（后台算好，UI 线程只负责画）。</summary>
    internal sealed class ModuleGraphData
    {
        public ModuleGraph Graph;
        public List<ModuleNode> Project = new List<ModuleNode>();

        /// <summary>当前模块（命令执行时光标所在文件的模块）；全项目视图时为 null。</summary>
        public string Focus;

        /// <summary>依赖里找不到 Build.cs 的模块名。</summary>
        public HashSet<string> Unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<ModuleFinding> Findings = new List<ModuleFinding>();
        public bool AuditDone;
    }

    /// <summary>
    /// 命令“模块依赖图”：读项目模块的 Build.cs、按需找依赖的引擎模块，弹出窗口（树 / 图 / 问题）；include 检查在后台跑，窗口关掉就停。
    /// </summary>
    internal static class ModuleGraphFeature
    {
        private static readonly Logger L = Log.For("BuildCs");
        public const string ActionId = "uetovs.unreal.moduleGraph";
        private const int MaxFilesPerModule = 4000;
        private static ModuleGraphWindow _window;

        public static void Register(ActionRegistry registry, HostPackage package) =>
            registry.Register(new DelegateAction(ActionId, UnrealFileStrings.CmdModuleGraph, ActionCategory.Unreal, ctx => ShowAsync(package), ctx => ctx.IsUnrealSolution, null,
                "module", "dependency", "graph", "build.cs", "模块", "依赖", "依赖图", "循环"));

        public static async Task ShowAsync(HostPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (package.Unreal?.IsUnreal != true)
            {
                VsStatusBar.SetText(UnrealFileStrings.GraphNoUnreal);
                return;
            }
            string current = VsEditor.CurrentLocation()?.FilePath;
            if (_window == null)
            {
                _window = new ModuleGraphWindow(package);
                _window.Closed += (s, e) => _window = null;
            }
            _window.ShowOwned(VsMainWindow.Handle);
            _window.Load(current, refresh: false);
        }

        /// <summary>
        /// 读数据：项目模块 + 当前文件的模块（引擎模块也行）+ 它们依赖的模块的 Build.cs（只为知道目录，不再往下展开）。
        /// </summary>
        public static async Task<ModuleGraphData> LoadAsync(HostPackage package, string currentFile, bool refresh, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var data = new ModuleGraphData();
            data.Project = await ProjectModules.LoadAsync(refresh).ConfigureAwait(false);
            var nodes = data.Project.ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(currentFile))
            {
                string buildCs = await Task.Run(() => ModuleLocator.ForFileSystem().FindBuildCs(currentFile), ct).ConfigureAwait(false);
                if (buildCs != null)
                {
                    string name = BuildCsFile.ModuleNameFromPath(buildCs);
                    if (!nodes.ContainsKey(name))
                    {
                        string text = await Task.Run(() => SafeRead(buildCs), ct).ConfigureAwait(false);
                        if (text != null) nodes[name] = ModuleNode.FromBuildCs(buildCs, text, false);
                    }
                    if (nodes.ContainsKey(name)) data.Focus = name;
                }
            }
            // 被依赖的、还不认识的模块：找它的 Build.cs（索引里按文件名找，找不到再看引擎 Source 下的常见位置）
            var wanted = nodes.Values.Where(n => n.IsProject || n.Name == data.Focus)
                              .SelectMany(n => n.Dependencies.Concat(n.PublicIncludePaths).Concat(n.PrivateIncludePaths))
                              .Where(d => !nodes.ContainsKey(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string engineSource = string.IsNullOrEmpty(package.Unreal?.EngineRoot) ? null : Path.Combine(package.Unreal.EngineRoot, "Engine", "Source");
            var engine = package.Engine;
            foreach (var dep in wanted)
            {
                ct.ThrowIfCancellationRequested();
                string path = null;
                if (engine != null && engine.IsConnected)
                {
                    var files = await EngineQueries.FindFilesByNameAsync(engine, dep + ".Build.cs", ct).ConfigureAwait(false);
                    path = files.FirstOrDefault();
                }
                if (path == null && engineSource != null)
                    path = new[] { "Runtime", "Editor", "Developer", "Programs" }.Select(k => Path.Combine(engineSource, k, dep, dep + ".Build.cs")).FirstOrDefault(File.Exists);
                string text = path == null ? null : await Task.Run(() => SafeRead(path), ct).ConfigureAwait(false);
                if (text == null)
                {
                    data.Unresolved.Add(dep);
                    continue;
                }
                nodes[dep] = ModuleNode.FromBuildCs(path, text, false);
            }
            data.Graph = ModuleGraph.Build(nodes.Values);
            data.Findings.AddRange(ModuleDependencyAudit.CycleFindings(data.Graph));
            L.Info("moduleGraph.load", null, ("projectModules", data.Project.Count), ("nodes", nodes.Count), ("unresolved", data.Unresolved.Count),
                ("cycles", data.Graph.Cycles.Count), ("focus", data.Focus), ("ms", sw.ElapsedMilliseconds));
            return data;
        }

        /// <summary>对要检查的模块逐个读源文件的 #include 跑 <see cref="ModuleDependencyAudit"/>（后台线程）。</summary>
        public static Task AuditAsync(ModuleGraphData data, IReadOnlyList<string> modules, IProgress<int> progress, CancellationToken ct) => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var exists = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool Exists(string p) => exists.GetOrAdd(p, File.Exists);
            int done = 0, files = 0;
            foreach (var name in modules)
            {
                ct.ThrowIfCancellationRequested();
                var m = data.Graph.Get(name);
                if (m?.Directory == null || !Directory.Exists(m.Directory)) continue;
                var sources = new List<ModuleSourceFile>();
                foreach (var f in SourceFiles(m.Directory).Take(MaxFilesPerModule))
                {
                    ct.ThrowIfCancellationRequested();
                    string text = SafeRead(f);
                    if (text == null) continue;
                    sources.Add(new ModuleSourceFile { Path = f, Includes = IncludeDependencyChecker.ParseIncludes(text) });
                }
                files += sources.Count;
                var found = ModuleDependencyAudit.Run(m, sources, data.Graph.Get, Exists, ct);
                lock (data.Findings) data.Findings.AddRange(found);
                progress?.Report(++done);
            }
            data.AuditDone = true;
            L.Info("moduleGraph.audit", null, ("modules", modules.Count), ("files", files), ("findings", data.Findings.Count), ("ms", sw.ElapsedMilliseconds));
        }, ct);

        private static IEnumerable<string> SourceFiles(string dir)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                string[] files, subs;
                try
                {
                    files = Directory.GetFiles(d);
                    subs = Directory.GetDirectories(d);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var f in files)
                    if (IncludeDependencyChecker.IsCppFile(f)) yield return f;
                foreach (var s in subs)
                {
                    string n = Path.GetFileName(s);
                    if (n.Equals("Intermediate", StringComparison.OrdinalIgnoreCase) || n.Equals("ThirdParty", StringComparison.OrdinalIgnoreCase)) continue;
                    // 子模块（自己有 Build.cs）的文件不算这个模块的
                    bool nested;
                    try { nested = Directory.EnumerateFiles(s, "*.Build.cs").Any(); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                    if (!nested) stack.Push(s);
                }
            }
        }

        private static string SafeRead(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
